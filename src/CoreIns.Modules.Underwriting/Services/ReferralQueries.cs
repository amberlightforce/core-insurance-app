using System.Globalization;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary>What a referral rule declares to be read and the fact value observed.</summary>
internal sealed record RuleReading(string? Observed, string Limit);

/// <summary>
/// <c>uw.Referral.list</c> and <c>uw.Referral.get</c> (D-USR-13): the referral workbench reads. UW owns the queues (issues and
/// decisions); the quote header comes from POL (<see cref="IPolicyJobService"/>) and the policyholder's display name from PTY's
/// masked view (<see cref="IPartyPartyService"/>, never a reveal), both in-process and in the caller's legal entity. The risk
/// facts are the derived facts UW recorded with POL's latest evaluation (no birth date: the youngest driver's age band).
/// A page joins at most <c>limit</c> jobs one by one (in-process calls; a batched read comes with pol.Job.list).
/// </summary>
internal sealed class ReferralQueries(
    ReferralReads reads,
    UnderwritingStore store,
    DecisionEligibility eligibility,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IPolicyJobService jobs,
    IPartyPartyService parties)
{
    /// <summary>The most open referrals the MINE view computes over (D-SL5-04); above it the caller narrows the view.</summary>
    internal const int MineCap = 500;

    private static readonly string[] ActiveStatuses = [IssueStatus.Open, IssueStatus.Rejected, "Approved", "ApprovedWithConditions"];

    public async Task<Result<ReferralListPage>> ListAsync(ReferralQueueCode? queue, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100 || !TryDecode(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..100.");
        }

        var legalEntity = LegalEntity();
        var me = context.Actor.ToString();
        var (dayStart, dayEnd) = Today();
        var size = limit ?? 25;
        var counts = await reads.CountsAsync(legalEntity, me, dayStart, dayEnd, cancellationToken).ConfigureAwait(false);
        var overCap = counts.Open > MineCap;
        if (queue == ReferralQueueCode.Mine && overCap)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", $"The legal entity has more than {MineCap} open referrals; narrow the view (use the Open queue).");
        }

        // MINE and counts.mine share one computation: the open referrals whose every Open issue the caller can decide now.
        var mine = overCap ? null : await MineAsync(legalEntity, cancellationToken).ConfigureAwait(false);
        List<ReferralJobRecord> rows;
        if (queue == ReferralQueueCode.Mine)
        {
            rows = [.. mine!.Where(m => after is not { } a || Compare(m, a) > 0).Take(size + 1)];
        }
        else
        {
            var which = queue switch
            {
                ReferralQueueCode.ApprovedToday => ReferralQueue.ApprovedToday,
                ReferralQueueCode.Rejected => ReferralQueue.Rejected,
                ReferralQueueCode.DecidedByMeToday => ReferralQueue.DecidedByMeToday,
                _ => ReferralQueue.Open,
            };
            rows = [.. await reads.JobsAsync(legalEntity, which, me, dayStart, dayEnd, after, size + 1, cancellationToken).ConfigureAwait(false)];
        }

        var page = rows.Take(size).ToList();
        var jobIds = page.Select(r => r.JobId).ToList();
        var issues = jobIds.Count == 0 ? [] : await reads.IssuesOfJobsAsync(legalEntity, jobIds, cancellationToken).ConfigureAwait(false);
        var participation = jobIds.Count == 0 ? new Dictionary<Guid, JobParticipation>() : await store.ParticipationAsync(legalEntity, jobIds, cancellationToken).ConfigureAwait(false);
        var explains = await ExplainsAsync(legalEntity, jobIds, cancellationToken).ConfigureAwait(false);

        var items = new List<ReferralListItem>(page.Count);
        foreach (var jobId in jobIds)
        {
            var job = await JobAsync(jobId, cancellationToken).ConfigureAwait(false);
            var customer = job is null ? null : await CustomerAsync(job.PolicyholderPartyId, cancellationToken).ConfigureAwait(false);
            var workedOn = eligibility.SodReasons(participation.GetValueOrDefault(jobId, JobParticipation.None)).Count > 0;
            items.Add(Summary(jobId, [.. issues.Where(i => i.JobId == jobId).Select(i => i.Issue)], workedOn, job, customer, explains.GetValueOrDefault(jobId)));
        }

        return new ReferralListPage
        {
            Items = items,
            NextCursor = rows.Count > size ? Encode(page[^1]) : null,
            Limit = size,
            Counts = new ReferralQueueCounts
            {
                Open = (int)counts.Open,
                ApprovedToday = (int)counts.ApprovedToday,
                Rejected = (int)counts.Rejected,
                DecidedByMeToday = (int)counts.DecidedByMeToday,
                Mine = mine?.Count,
            },
        };
    }

    public async Task<Result<ReferralGetResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var jobId))
        {
            return NotFound();
        }

        var legalEntity = LegalEntity();
        var issues = (await reads.IssuesOfJobsAsync(legalEntity, [jobId], cancellationToken).ConfigureAwait(false)).Select(i => i.Issue).ToList();
        if (issues.Count == 0)
        {
            // No issue of this legal entity: not a referral here (another entity's job is "not found" too).
            return NotFound();
        }

        var participation = (await store.ParticipationAsync(legalEntity, [jobId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(jobId, JobParticipation.None);
        var job = await JobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var customer = job is null ? null : await CustomerAsync(job.PolicyholderPartyId, cancellationToken).ConfigureAwait(false);
        var version = job?.Versions.FirstOrDefault(v => v.VersionNo == job.CurrentVersionNo);
        var facts = (await reads.LatestFactsAsync(legalEntity, [jobId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(jobId);
        var explains = await ExplainsAsync(legalEntity, [jobId], cancellationToken).ConfigureAwait(false);
        var zone = BusinessZone();

        // Decidability: the decide check run dry for every issue (nothing is recorded; uw.Issue.decide re-runs it at commit).
        var checks = await eligibility.DryChecksAsync(issues, cancellationToken).ConfigureAwait(false);
        var rows = issues.Select(issue => (Issue: issue, Eligibility: eligibility.Evaluate(issue, participation, checks[issue.IssueType]))).ToList();
        var open = rows.Where(r => r.Issue.Status == IssueStatus.Open).ToList();
        var viewReasons = open.Count == 0 ? [DecidabilityReason.NotOpen] : open.SelectMany(r => r.Eligibility.Reasons).Distinct().ToList();

        return new ReferralGetResponse
        {
            Referral = new ReferralView
            {
                Summary = Summary(jobId, issues, eligibility.SodReasons(participation).Count > 0, job, customer, explains.GetValueOrDefault(jobId)),
                ProductVersion = job?.ProductVersion.ToString(),
                ExpirationDate = job is null ? null : job.ExpirationAt.ToBusinessDate(zone),
                ProducerCode = job?.ProducerCode,
                QuoteVersionNo = version?.VersionNo,
                Premium = version?.Premium,
                Taxes = version?.Taxes,
                Facts = facts is null ? null : Facts(facts, job, version),
                Issues = [.. rows.Select(r => new ReferralIssue { Issue = UnderwritingIssueQueries.Item(r.Issue), Decidability = Decidability(r.Issue, r.Eligibility) })],
                Decidability = new ReferralDecidability { CanDecide = open.Count > 0 && open.All(r => r.Eligibility.CanDecide), Reasons = viewReasons },
            },
        };
    }

    private static IssueDecidability Decidability(IssueRecord issue, IssueEligibility result) => new()
    {
        CanDecide = result.CanDecide,
        Reasons = result.Reasons,
        Authority = new AuthorityPreview
        {
            Type = result.Authority.Type.ToString(),
            IssueType = issue.IssueType,
            Outcome = result.Authority.Decision switch
            {
                AuthorityDecision.Allow => AuthorityPreview.OutcomeValue.Allow,
                AuthorityDecision.Refer => AuthorityPreview.OutcomeValue.Refer,
                _ => AuthorityPreview.OutcomeValue.Deny,
            },
            SourceGrantId = result.Authority.SourceGrant,
        },
    };

    /// <summary>
    /// The open referrals of the legal entity the caller can decide now, oldest first (the order of the Open queue): a job qualifies
    /// when it has an Open issue and the dry decide check says yes for every Open issue. Computed set-wise: one read of the open
    /// issues, one of the participation, one dry authority check per issue type.
    /// </summary>
    private async Task<List<ReferralJobRecord>> MineAsync(Guid legalEntity, CancellationToken cancellationToken)
    {
        var issues = await reads.OpenReferralIssuesAsync(legalEntity, cancellationToken).ConfigureAwait(false);
        if (issues.Count == 0)
        {
            return [];
        }

        var jobIds = issues.Select(i => i.JobId).Distinct().ToList();
        var participation = await store.ParticipationAsync(legalEntity, jobIds, cancellationToken).ConfigureAwait(false);
        var checks = await eligibility.DryChecksAsync(issues.Where(i => i.Status == IssueStatus.Open), cancellationToken).ConfigureAwait(false);
        var mine = new List<ReferralJobRecord>();
        foreach (var group in issues.GroupBy(i => i.JobId))
        {
            var part = participation.GetValueOrDefault(group.Key, JobParticipation.None);
            var open = group.Where(i => i.Status == IssueStatus.Open).ToList();
            if (open.Count > 0 && open.All(i => eligibility.Evaluate(i, part, checks[i.IssueType]).CanDecide))
            {
                mine.Add(new ReferralJobRecord { JobId = group.Key, SortAt = group.Min(i => i.CreatedAt) });
            }
        }

        mine.Sort((a, b) => Compare(a, (b.SortAt, b.JobId)));
        return mine;
    }

    /// <summary>The queue order: oldest raised first, then job id (the order and the keyset of the cursor).</summary>
    private static int Compare(ReferralJobRecord row, (DateTime SortAt, Guid JobId) other)
    {
        var byTime = row.SortAt.CompareTo(other.SortAt);
        return byTime != 0 ? byTime : row.JobId.CompareTo(other.JobId);
    }

    /// <summary>
    /// What each referral rule of the jobs declares (rule set <c>explain</c>) combined with the stored derived facts: per job and
    /// rule id, the observed value and the limit; a rule without a declared limit gets none and RULE_DECLARES_NO_LIMIT.
    /// </summary>
    private async Task<Dictionary<Guid, Dictionary<string, RuleReading>>> ExplainsAsync(Guid legalEntity, List<Guid> jobIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, Dictionary<string, RuleReading>>();
        if (jobIds.Count == 0)
        {
            return result;
        }

        var facts = await reads.LatestFactsAsync(legalEntity, jobIds, cancellationToken).ConfigureAwait(false);
        foreach (var set in await reads.RuleSetsOfJobsAsync(legalEntity, jobIds, cancellationToken).ConfigureAwait(false))
        {
            var readings = result.TryGetValue(set.JobId, out var existing) ? existing : result[set.JobId] = new Dictionary<string, RuleReading>(StringComparer.Ordinal);
            var record = facts.TryGetValue(set.JobId, out var f) ? JsonSerializer.Deserialize<UwFactsRecord>(f.Facts, RuleSetJson.Options) : null;
            foreach (var rule in RuleSetJson.Deserialize(set.Definition).Rules)
            {
                var explain = rule.Explain ?? BuiltInRuleSets.BuiltInExplain(set.Code, rule);
                if (explain is not null)
                {
                    readings[rule.Id] = new RuleReading(Observed(explain.Fact, record), explain.Limit);
                }
            }
        }

        return result;
    }

    private static string? Observed(string fact, UwFactsRecord? record) => record is null ? null : fact switch
    {
        BuiltInRuleSets.FactDriverAge => record.YoungestDriverAgeBand,
        BuiltInRuleSets.FactVehicleAge => record.VehicleAgeYears.ToString(CultureInfo.InvariantCulture),
        BuiltInRuleSets.FactVehicleValue => record.VehicleValue,
        _ => null,
    };

    private static ReferralListItem Summary(
        Guid jobId, IReadOnlyList<IssueRecord> issues, bool workedOn, JobView? job, ReferralCustomer? customer, Dictionary<string, RuleReading>? readings)
    {
        var active = issues.Where(i => ActiveStatuses.Contains(i.Status, StringComparer.Ordinal)).ToList();
        var waiting = active.Where(i => i.Status is IssueStatus.Open or IssueStatus.Rejected).ToList();
        var lastDecided = issues.Where(i => i.DecidedAt is not null).MaxBy(i => i.DecidedAt);
        var version = job?.Versions.FirstOrDefault(v => v.VersionNo == job.CurrentVersionNo);
        return new ReferralListItem
        {
            JobRef = new JobId(jobId),
            JobNumber = job?.JobNumber.ToString(),
            JobState = job is null ? null : JsonSerializer.Serialize(job.State).Trim('"'),
            ProductCode = job?.ProductCode,
            Customer = customer,
            PremiumTotal = version?.Total,
            EffectiveDate = job is null ? null : job.EffectiveAt.ToBusinessDate(BusinessZone()),
            ReferralStatus = active.Any(i => i.Status == IssueStatus.Open) ? ReferralStatusCode.Open
                : active.Any(i => i.Status == IssueStatus.Rejected) ? ReferralStatusCode.Rejected
                : ReferralStatusCode.Approved,
            Reasons = [.. active.Select(i =>
            {
                var reading = readings is not null && readings.TryGetValue(i.RuleId, out var found) ? found : null;
                return new ReferralReason
                {
                    IssueId = new UwIssueId(i.IssueId),
                    IssueType = i.IssueType,
                    RuleId = i.RuleId,
                    Status = Enum.Parse<IssueStatusCode>(i.Status),
                    Observed = reading?.Observed,
                    Limit = reading?.Limit,
                    LimitUnavailableReason = reading is null ? "RULE_DECLARES_NO_LIMIT" : null,
                };
            })],
            RaisedAt = Time((waiting.Count > 0 ? waiting : issues).Min(i => i.CreatedAt)),
            LastDecidedAt = lastDecided?.DecidedAt is { } at ? Time(at) : null,
            LastDecidedBy = lastDecided?.DecidedBy,
            CallerWorkedOnJob = workedOn,
        };
    }

    private static ReferralRiskFacts Facts(EvaluationFactsRecord record, JobView? job, QuoteVersionView? version)
    {
        var facts = JsonSerializer.Deserialize<UwFactsRecord>(record.Facts, RuleSetJson.Options)!;
        string dataStatus = "ILLUSTRATIVE_TEST_DATA";
        using (var trace = JsonDocument.Parse(record.Trace))
        {
            if (trace.RootElement.TryGetProperty("ruleSet", out var ruleSet) && ruleSet.TryGetProperty("dataStatus", out var status) && status.ValueKind == JsonValueKind.String)
            {
                dataStatus = status.GetString()!;
            }
        }

        var vehicle = version?.RiskTree.Vehicles.Count > 0 ? version.RiskTree.Vehicles[0] : null;
        return new ReferralRiskFacts
        {
            EvaluatedAt = Time(record.CreatedAt),
            EffectiveDate = new BusinessDate(DateOnly.ParseExact(facts.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            RuleSetCode = record.RuleSetCode,
            RuleSetVersion = record.RuleSetVersion,
            DataStatus = dataStatus,
            Vehicle = new ReferralVehicleFacts
            {
                Make = vehicle?.Make,
                Model = vehicle?.Model,
                FirstRegistrationYear = facts.FirstRegistrationYear,
                AgeYears = facts.VehicleAgeYears,
                Value = facts.VehicleValue is { } value && job is not null
                    ? new Money(decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), job.Currency)
                    : null,
                EngineCapacityCc = facts.EngineCc,
                Usage = facts.Usage,
            },
            Driver = new ReferralDriverFacts
            {
                YoungestAgeBand = facts.YoungestDriverAgeBand is { } band
                    ? JsonSerializer.Deserialize<ReferralDriverAgeBand>($"\"{band}\"")
                    : null,
                ClaimsLast5Years = facts.ClaimsLast5Years,
            },
        };
    }

    /// <summary>The job through POL's in-process contract, or null when POL does not return it.</summary>
    private async Task<JobView?> JobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            return (await jobs.GetAsync(jobId.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Job;
        }
        catch (DomainException ex) when (ex.Error.Code.Name == "NOT-FOUND")
        {
            return null;
        }
    }

    /// <summary>The policyholder from PTY's masked view (P1 name and number only; no reveal, no P2).</summary>
    private async Task<ReferralCustomer?> CustomerAsync(PartyId partyId, CancellationToken cancellationToken)
    {
        try
        {
            var party = (await parties.GetAsync(partyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Party;
            var name = party.Names.FirstOrDefault(n => n.Form == PartyNameView.FormValue.Native) ?? (party.Names.Count > 0 ? party.Names[0] : null);
            var display = name is null ? null
                : name.OrganisationName ?? string.Join(' ', new[] { name.FamilyName, name.GivenNames }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return new ReferralCustomer
            {
                PartyId = party.PartyId,
                PartyNumber = party.PartyNumber.ToString(),
                DisplayName = string.IsNullOrWhiteSpace(display) ? null : display,
            };
        }
        catch (DomainException ex) when (ex.Error.Code.Name == "NOT-FOUND")
        {
            return null;
        }
    }

    private Guid LegalEntity() =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    /// <summary>The legal entity's business day (Europe/Athens) as a UTC window.</summary>
    private (DateTime Start, DateTime End) Today()
    {
        var zone = BusinessZone();
        var today = clock.Now.ToBusinessDate(zone).Value;
        var start = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        return (start, end);
    }

    internal static TimeZoneInfo BusinessZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static Instant Time(DateTime value) => Instant.FromUtcDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DomainError NotFound() => DomainError.Of(ModuleCode.UW, "NOT-FOUND", "No referral exists for this job.");

    private static string Encode(ReferralJobRecord last) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{DateTime.SpecifyKind(last.SortAt, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture)}_{last.JobId:N}"));

    private static bool TryDecode(string? cursor, out (DateTime SortAt, Guid JobId)? after)
    {
        after = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('_');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks is > 0 && ticks <= DateTime.MaxValue.Ticks && Guid.TryParseExact(parts[1], "N", out var id))
            {
                after = (new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
