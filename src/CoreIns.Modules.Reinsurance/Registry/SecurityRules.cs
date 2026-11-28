using System.Text.Json;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>The rating scale of the security rules (S&amp;P-style long-term scale, best first). legalStatus Unverified.</summary>
internal static class RatingScale
{
    private static readonly string[] Order =
        ["AAA", "AA+", "AA", "AA-", "A+", "A", "A-", "BBB+", "BBB", "BBB-", "BB+", "BB", "BB-", "B+", "B", "B-", "CCC+", "CCC", "CCC-", "CC", "C", "D"];

    /// <summary>The rank of a rating (0 is the best); null when it is not on the scale.</summary>
    public static int? Rank(string? rating)
    {
        var index = Array.IndexOf(Order, rating?.Trim().ToUpperInvariant());
        return index < 0 ? null : index;
    }

    /// <summary>The rating in its canonical form.</summary>
    public static string Normalize(string rating) => rating.Trim().ToUpperInvariant();
}

/// <summary>A reinsurer's entry on the security list.</summary>
internal sealed record SecurityRating(Guid ReinsurerPartyId, string Rating, decimal? MaxSharePct);

/// <summary>A security rule set as stored (BO-RI-05).</summary>
internal sealed record SecurityRuleSet(Guid RuleId, int VersionNo, BusinessDate EffectiveFrom, string? RatingFloor, decimal? MaxSharePct, IReadOnlyList<SecurityRating> Ratings, Instant CreatedAt, string CreatedBy)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SecurityRuleSet From(SecurityRuleRow row) =>
        new(row.RuleId, row.VersionNo, row.EffectiveFrom, row.RatingFloor, row.MaxSharePct, JsonSerializer.Deserialize<SecurityRating[]>(row.Ratings, Json) ?? [], row.CreatedAt, row.CreatedBy);

    public static string Serialize(IEnumerable<SecurityRating> ratings) => JsonSerializer.Serialize(ratings.ToArray(), Json);

    public string? RatingOf(Guid reinsurer) => Ratings.FirstOrDefault(r => r.ReinsurerPartyId == reinsurer)?.Rating;

    /// <summary>The cap on the signed line of a reinsurer: its own cap, else the general one.</summary>
    public decimal? CapOf(Guid reinsurer) => Ratings.FirstOrDefault(r => r.ReinsurerPartyId == reinsurer)?.MaxSharePct ?? MaxSharePct;

    /// <summary>The rules that a line of <paramref name="reinsurer"/> breaches (empty when it is acceptable). A reinsurer without a rating fails a rating floor (fail closed).</summary>
    public IReadOnlyList<string> Breaches(Guid reinsurer, decimal signedLinePct)
    {
        var breaches = new List<string>();
        if (RatingFloor is not null && !(RatingScale.Rank(RatingOf(reinsurer)) is { } rank && rank <= RatingScale.Rank(RatingFloor)))
        {
            breaches.Add("RATING_FLOOR");
        }

        if (CapOf(reinsurer) is { } cap && signedLinePct > cap)
        {
            breaches.Add("MAX_SHARE");
        }

        return breaches;
    }

    public RiSecurityRuleSet ToView() =>
        new()
        {
            RuleSetId = RuleId, VersionNo = VersionNo, EffectiveFrom = EffectiveFrom, RatingFloor = RatingFloor, MaxSharePct = MaxSharePct is { } m ? ContractSupport.Percent(m) : null,
            Ratings = [.. Ratings.OrderBy(r => r.ReinsurerPartyId).Select(r => new RiSecurityRating { ReinsurerPartyId = new PartyId(r.ReinsurerPartyId), Rating = r.Rating, MaxSharePct = r.MaxSharePct })],
            CreatedAt = CreatedAt, CreatedBy = CreatedBy,
        };
}

/// <summary>Reads the rule sets and builds the exposure report (BO-RI-05, REQ-RI-050, -053).</summary>
internal sealed class SecurityReader(
    ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options)
{
    private LegalEntityId Entity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>The rule set in force on <paramref name="date"/>: the latest version among those effective by then; null when none is.</summary>
    public async Task<SecurityRuleSet?> InForceAsync(BusinessDate date, CancellationToken cancellationToken)
    {
        var entity = Entity;
        var rows = await db.SecurityRules.AsNoTracking().Where(r => r.LegalEntityId == entity).ToListAsync(cancellationToken).ConfigureAwait(false);
        var row = rows.Where(r => r.EffectiveFrom <= date).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.VersionNo).FirstOrDefault();
        return row is null ? null : SecurityRuleSet.From(row);
    }

    /// <summary>One rule set by id in the caller's legal entity; null when it does not exist.</summary>
    public async Task<SecurityRuleSet?> FindAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var entity = Entity;
        var row = await db.SecurityRules.AsNoTracking().SingleOrDefaultAsync(r => r.RuleId == ruleId && r.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : SecurityRuleSet.From(row);
    }

    /// <summary>One rule set by id (<c>current</c> is the one in force today).</summary>
    public async Task<Result<SecurityRulesGetResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        SecurityRuleSet? set;
        if (string.Equals(id, "current", StringComparison.OrdinalIgnoreCase))
        {
            set = await InForceAsync(clock.Now.ToBusinessDate(options.Value.Zone), cancellationToken).ConfigureAwait(false);
        }
        else if (Guid.TryParse(id, out var ruleId))
        {
            set = await FindAsync(ruleId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return RiErrors.Validation("id", "ID_INVALID", "The id is a rule set id or 'current'.");
        }

        return set is null ? DomainError.Of(ModuleCode.RI, "NOT-FOUND", "The security rule set does not exist.") : new SecurityRulesGetResponse { RuleSet = set.ToView() };
    }

    /// <summary>Exposure per reinsurer over the Active contracts in force at <paramref name="validAt"/> (limit times signed line per layer), with the breaches of the rule set.</summary>
    public async Task<SecurityReportResponse> ReportAsync(BusinessDate validAt, SecurityRuleSet? rules, CancellationToken cancellationToken)
    {
        var entity = Entity;
        var now = clock.Now;
        var contracts = await db.Contracts.AsNoTracking().Where(c => c.LegalEntityId == entity && c.Status == "ACTIVE").ToListAsync(cancellationToken).ConfigureAwait(false);
        var exposure = new Dictionary<Guid, (decimal Limit, int Count, decimal Line)>();
        // TODO(hardening): one query per contract; batch the in-force versions when the book grows.
        foreach (var contract in contracts)
        {
            var version = await ContractVersions.InForceAsync(db, contract.ContractId, validAt, now, approvedOnly: true, cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                continue;
            }

            var totalLimit = await db.Layers.AsNoTracking().Where(l => l.VersionId == version.VersionId && l.Rev == version.ContentRev).SumAsync(l => l.LimitAmount, cancellationToken).ConfigureAwait(false);
            var lines = await db.Participations.AsNoTracking().Where(p => p.VersionId == version.VersionId && p.Rev == version.ContentRev).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var current = exposure.GetValueOrDefault(line.ReinsurerPartyId.Value);
                exposure[line.ReinsurerPartyId.Value] = (current.Limit + (totalLimit * line.SignedLinePct / 100m), current.Count + 1, Math.Max(current.Line, line.SignedLinePct));
            }
        }

        var total = exposure.Values.Sum(e => e.Limit);
        var rows = new List<RiSecurityExposureRow>();
        foreach (var (reinsurer, value) in exposure.OrderByDescending(e => e.Value.Limit).ThenBy(e => e.Key))
        {
            var share = total == 0m ? 0m : decimal.Round(value.Limit / total * 100m, ContractContent.PercentScale, MidpointRounding.ToZero);
            var breaches = new List<string>();
            if (rules is not null)
            {
                // The report judges the reinsurer's largest line and its rating; the share cap is on the line per contract.
                breaches.AddRange(rules.Breaches(reinsurer, value.Line));
            }

            rows.Add(new RiSecurityExposureRow
            {
                ReinsurerPartyId = new PartyId(reinsurer), Rating = rules?.RatingOf(reinsurer), ContractCount = value.Count,
                LimitExposure = new Money(ContractSupport.Amount(DecimalRounding.RoundTo(value.Limit, 2, DecimalRounding.DefaultMoneyMode)), Currency.FromCode(ContractRules.Eur)),
                ExposureSharePct = ContractSupport.Percent(share), Breaches = breaches,
            });
        }

        return new SecurityReportResponse { ValidAt = validAt, RuleSetId = rules?.RuleId, Rows = rows, BreachCount = rows.Count(r => r.Breaches.Count > 0) };
    }
}

/// <summary><c>ri.SecurityRules.set</c> as a command of the platform pipeline.</summary>
internal sealed record SetSecurityRules(SecurityRulesSetRequest Request) : ICommand<SecurityRulesSetResponse>;

/// <summary>Shape rules: cap in (0, 100] with at most six decimals, ratings on the scale, one entry per reinsurer.</summary>
internal sealed class SetSecurityRulesValidator : AbstractValidator<SetSecurityRules>
{
    public SetSecurityRulesValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.RatingFloor).Must(r => RatingScale.Rank(r) is not null).WithErrorCode("RATING_UNKNOWN").When(c => c.Request?.RatingFloor is not null);
        RuleFor(c => c.Request.MaxSharePct).Must(Share).WithErrorCode("PERCENT_RANGE").When(c => c.Request?.MaxSharePct is not null);
        RuleFor(c => c.Request.Ratings).Must(r => r!.Select(x => x.ReinsurerPartyId).Distinct().Count() == r!.Count).WithErrorCode("REINSURER_REPEATED").When(c => c.Request?.Ratings is not null);
        RuleForEach(c => c.Request.Ratings).ChildRules(r =>
        {
            r.RuleFor(x => x.Rating).Must(v => RatingScale.Rank(v) is not null).WithErrorCode("RATING_UNKNOWN");
            r.RuleFor(x => x.MaxSharePct).Must(Share).WithErrorCode("PERCENT_RANGE").When(x => x.MaxSharePct is not null);
        }).When(c => c.Request?.Ratings is not null);
    }

    private static bool Share(decimal? value) =>
        value is > 0m and <= 100m && decimal.Round(value.Value, ContractContent.PercentScale, MidpointRounding.ToZero) == value.Value;
}

/// <summary>Records a new security rule set from a date (append-only), checking every listed reinsurer against PTY, and answers with the exposure report as of that date.</summary>
internal sealed class SetSecurityRulesHandler(
    ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, PartyDirectory parties, SecurityReader reader)
    : ICommandHandler<SetSecurityRules, SecurityRulesSetResponse>
{
    public async Task<Result<SecurityRulesSetResponse>> HandleAsync(SetSecurityRules command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var ratings = (request.Ratings ?? []).Select(r => new SecurityRating(r.ReinsurerPartyId.Value, RatingScale.Normalize(r.Rating), r.MaxSharePct)).ToList();
        if (await parties.CheckAsync([.. ratings.Select(r => new ContentLine(r.ReinsurerPartyId, null, 1m, false))], cancellationToken).ConfigureAwait(false) is { } partyError)
        {
            return partyError;
        }

        var versionNo = await db.SecurityRules.Where(r => r.LegalEntityId == entity).Select(r => (int?)r.VersionNo).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var row = new SecurityRuleRow
        {
            RuleId = Guid.CreateVersion7(), LegalEntityId = entity, VersionNo = versionNo + 1, EffectiveFrom = request.EffectiveFrom,
            RatingFloor = request.RatingFloor is null ? null : RatingScale.Normalize(request.RatingFloor), MaxSharePct = request.MaxSharePct,
            Ratings = SecurityRuleSet.Serialize(ratings.OrderBy(r => r.ReinsurerPartyId)), CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(),
        };
        db.SecurityRules.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return RiErrors.Stale("The security rules changed meanwhile.");
        }

        var set = SecurityRuleSet.From(row);
        var effective = await reader.InForceAsync(request.EffectiveFrom, cancellationToken).ConfigureAwait(false);
        return new SecurityRulesSetResponse { RuleSet = set.ToView(), Report = await reader.ReportAsync(request.EffectiveFrom, effective, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of <c>ri.SecurityRules.set</c>.</summary>
internal sealed class SetSecurityRulesAuditor : ICommandAuditor<SetSecurityRules, SecurityRulesSetResponse>
{
    public CommandAuditFacts Describe(SetSecurityRules command, Result<SecurityRulesSetResponse>? result)
    {
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = new ObjectRef(ModuleCode.RI, "SecurityRules", Guid.Empty.ToString("D")) };
        }

        var set = ok.Value.RuleSet;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.RI, "SecurityRules", set.RuleSetId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("versionNo", set.VersionNo.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Changes = AuditDiff.Compute(
                new { versionNo = set.VersionNo - 1 },
                new { versionNo = set.VersionNo, effectiveFrom = set.EffectiveFrom.ToString(), ratingFloor = set.RatingFloor, maxSharePct = set.MaxSharePct?.ToString(System.Globalization.CultureInfo.InvariantCulture), ratings = set.Ratings.Count }),
        };
    }
}
