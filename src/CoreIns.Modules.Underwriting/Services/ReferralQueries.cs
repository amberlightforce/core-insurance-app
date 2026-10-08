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
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary>
/// <c>uw.Referral.list</c> and <c>uw.Referral.get</c> (D-USR-13): the referral workbench reads. UW owns the queues (issues and
/// decisions); the quote header comes from POL (<see cref="IPolicyJobService"/>) and the policyholder's display name from PTY's
/// masked view (<see cref="IPartyPartyService"/>, never a reveal), both in-process and in the caller's legal entity. The risk
/// facts are the derived facts UW recorded with POL's latest evaluation (no birth date: the youngest driver's age band).
/// A page joins at most <c>limit</c> jobs one by one (in-process calls; a batched read comes with pol.Job.list).
/// </summary>
internal sealed class ReferralQueries(
    ReferralReads reads,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IPolicyJobService jobs,
    IPartyPartyService parties)
{
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
        var which = queue switch
        {
            ReferralQueueCode.ApprovedToday => ReferralQueue.ApprovedToday,
            ReferralQueueCode.Rejected => ReferralQueue.Rejected,
            ReferralQueueCode.DecidedByMeToday => ReferralQueue.DecidedByMeToday,
            _ => ReferralQueue.Open,
        };
        var rows = await reads.JobsAsync(legalEntity, which, me, dayStart, dayEnd, after, size + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        var jobIds = page.Select(r => r.JobId).ToList();
        var issues = jobIds.Count == 0 ? [] : await reads.IssuesOfJobsAsync(legalEntity, jobIds, cancellationToken).ConfigureAwait(false);
        var workedOn = jobIds.Count == 0 ? new HashSet<Guid>() : await reads.WorkedOnAsync(legalEntity, jobIds, me, cancellationToken).ConfigureAwait(false);
        var counts = await reads.CountsAsync(legalEntity, me, dayStart, dayEnd, cancellationToken).ConfigureAwait(false);

        var items = new List<ReferralListItem>(page.Count);
        foreach (var jobId in jobIds)
        {
            var job = await JobAsync(jobId, cancellationToken).ConfigureAwait(false);
            var customer = job is null ? null : await CustomerAsync(job.PolicyholderPartyId, cancellationToken).ConfigureAwait(false);
            items.Add(Summary(jobId, [.. issues.Where(i => i.JobId == jobId).Select(i => i.Issue)], workedOn.Contains(jobId), job, customer));
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

        var me = context.Actor.ToString();
        var workedOn = await reads.WorkedOnAsync(legalEntity, [jobId], me, cancellationToken).ConfigureAwait(false);
        var job = await JobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var customer = job is null ? null : await CustomerAsync(job.PolicyholderPartyId, cancellationToken).ConfigureAwait(false);
        var version = job?.Versions.FirstOrDefault(v => v.VersionNo == job.CurrentVersionNo);
        var facts = await reads.LatestFactsAsync(legalEntity, jobId, cancellationToken).ConfigureAwait(false);
        var zone = BusinessZone();

        return new ReferralGetResponse
        {
            Referral = new ReferralView
            {
                Summary = Summary(jobId, issues, workedOn.Contains(jobId), job, customer),
                ProductVersion = job?.ProductVersion.ToString(),
                ExpirationDate = job is null ? null : job.ExpirationAt.ToBusinessDate(zone),
                ProducerCode = job?.ProducerCode,
                QuoteVersionNo = version?.VersionNo,
                Premium = version?.Premium,
                Taxes = version?.Taxes,
                Facts = facts is null ? null : Facts(facts, job, version),
                Issues = [.. issues.Select(UnderwritingIssueQueries.Item)],
            },
        };
    }

    private static ReferralListItem Summary(Guid jobId, IReadOnlyList<IssueRecord> issues, bool workedOn, JobView? job, ReferralCustomer? customer)
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
            Reasons = [.. active.Select(i => new ReferralReason
            {
                IssueId = new UwIssueId(i.IssueId),
                IssueType = i.IssueType,
                RuleId = i.RuleId,
                Status = Enum.Parse<IssueStatusCode>(i.Status),
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
