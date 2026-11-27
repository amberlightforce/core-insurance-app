using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Queries;

/// <summary>Read side of commission agreements (BO-PTY-10): the agreement view and the comparison of two versions.</summary>
internal sealed class CommissionAgreementQueries(PartyDbContext db)
{
    public async Task<CommissionAgreementView?> AgreementAsync(LegalEntityId legalEntity, Guid agreementId, CancellationToken cancellationToken)
    {
        var agreement = await db.CommissionAgreements.AsNoTracking()
            .SingleOrDefaultAsync(a => a.AgreementId == agreementId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (agreement is null)
        {
            return null;
        }

        var versions = await db.CommissionVersions.AsNoTracking().Where(v => v.AgreementId == agreementId).OrderBy(v => v.VersionNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = versions.Select(v => v.VersionId).ToList();
        var lines = await db.CommissionRateLines.AsNoTracking().Where(l => ids.Contains(l.VersionId)).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var views = versions.Select(v => ToView(v, versions, lines.Where(l => l.VersionId == v.VersionId))).ToList();
        return new CommissionAgreementView
        {
            AgreementId = agreement.AgreementId,
            IntermediaryId = agreement.IntermediaryId,
            ProductCode = agreement.ProductCode,
            LineOfBusiness = agreement.LineOfBusiness,
            Versions = views,
        };
    }

    public async Task<CommissionAgreementVersionView> VersionViewAsync(CommissionAgreementVersionRow version, CancellationToken cancellationToken)
    {
        var siblings = await db.CommissionVersions.AsNoTracking().Where(v => v.AgreementId == version.AgreementId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = await db.CommissionRateLines.AsNoTracking().Where(l => l.VersionId == version.VersionId).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToView(version, siblings, lines);
    }

    /// <summary>The end of a version is the day before the next ACTIVE version starts; null for the latest.</summary>
    private static CommissionAgreementVersionView ToView(
        CommissionAgreementVersionRow row, IReadOnlyList<CommissionAgreementVersionRow> siblings, IEnumerable<CommissionRateLineRow> lines)
    {
        BusinessDate? effectiveTo = null;
        if (row.Status == CommissionStatus.Active)
        {
            var next = siblings.Where(v => v.Status == CommissionStatus.Active && v.EffectiveFrom > row.EffectiveFrom).OrderBy(v => v.EffectiveFrom).FirstOrDefault();
            effectiveTo = next?.EffectiveFrom.AddDays(-1);
        }

        return new CommissionAgreementVersionView
        {
            VersionId = row.VersionId,
            AgreementId = row.AgreementId,
            VersionNo = row.VersionNo,
            Status = Enum.Parse<CommissionAgreementVersionView.StatusValue>(row.Status.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
            EffectiveFrom = row.EffectiveFrom,
            EffectiveTo = effectiveTo,
            ExpectedAnnualVolume = new Money(row.ExpectedVolume, Currency.FromCode(row.Currency)),
            MaxRatePct = row.MaxRatePct,
            RateLines = [.. lines.Select(ToLine)],
            ContentHash = Sha256Hash.Parse(row.ContentHash),
            ApprovalRequestId = row.ApprovalRequestId is { } request ? new ApprovalRequestId(request) : null,
            Notes = row.Notes,
            CreatedBy = row.CreatedBy,
            CreatedAt = row.CreatedAt,
            SubmittedBy = row.SubmittedBy,
            DecidedBy = row.DecidedBy,
            DecidedAt = row.DecidedAt,
            DecisionComment = row.DecisionComment,
        };
    }

    public static CommissionRateLine ToLine(CommissionRateLineRow row) => new()
    {
        BusinessType = Enum.Parse<CommissionRateLine.BusinessTypeValue>(row.BusinessType.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
        LineOfBusiness = row.LineOfBusiness,
        Basis = Enum.Parse<CommissionRateLine.BasisValue>(row.Basis.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
        RatePct = row.RatePct,
    };

    /// <summary>
    /// What differs between two versions of the same agreement: the scalar fields and the rate lines, matched on business type, line of
    /// business and basis. Null when a version is missing or they belong to different agreements.
    /// </summary>
    public async Task<CommissionAgreementCompareResponse?> CompareAsync(LegalEntityId legalEntity, Guid fromVersionId, Guid toVersionId, CancellationToken cancellationToken)
    {
        var versions = await db.CommissionVersions.AsNoTracking()
            .Where(v => (v.VersionId == fromVersionId || v.VersionId == toVersionId) && v.LegalEntityId == legalEntity).ToListAsync(cancellationToken).ConfigureAwait(false);
        var from = versions.SingleOrDefault(v => v.VersionId == fromVersionId);
        var to = versions.SingleOrDefault(v => v.VersionId == toVersionId);
        if (from is null || to is null || from.AgreementId != to.AgreementId)
        {
            return null;
        }

        var lines = await db.CommissionRateLines.AsNoTracking().Where(l => l.VersionId == fromVersionId || l.VersionId == toVersionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var fromLines = lines.Where(l => l.VersionId == fromVersionId).ToDictionary(l => CommissionRules.LineKey(l.BusinessType, l.LineOfBusiness, l.Basis), StringComparer.Ordinal);
        var toLines = lines.Where(l => l.VersionId == toVersionId).ToDictionary(l => CommissionRules.LineKey(l.BusinessType, l.LineOfBusiness, l.Basis), StringComparer.Ordinal);

        var fields = new List<CommissionFieldChange>();
        Add(fields, "effectiveFrom", from.EffectiveFrom.ToString(), to.EffectiveFrom.ToString());
        Add(fields, "expectedAnnualVolume", from.ExpectedVolume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " " + from.Currency, to.ExpectedVolume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " " + to.Currency);
        Add(fields, "notes", from.Notes, to.Notes);

        var changes = new List<CommissionRateLineChange>();
        foreach (var (key, line) in toLines.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!fromLines.TryGetValue(key, out var before))
            {
                changes.Add(Change(CommissionRateLineChange.KindValue.Added, line, null, line.RatePct));
            }
            else if (before.RatePct != line.RatePct)
            {
                changes.Add(Change(CommissionRateLineChange.KindValue.Changed, line, before.RatePct, line.RatePct));
            }
        }

        foreach (var (key, line) in fromLines.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => !toLines.ContainsKey(p.Key)))
        {
            changes.Add(Change(CommissionRateLineChange.KindValue.Removed, line, line.RatePct, null));
        }

        return new CommissionAgreementCompareResponse
        {
            AgreementId = from.AgreementId,
            FromVersionId = from.VersionId,
            ToVersionId = to.VersionId,
            Identical = fields.Count == 0 && changes.Count == 0,
            FieldChanges = fields,
            RateLineChanges = changes,
        };
    }

    private static void Add(List<CommissionFieldChange> fields, string field, string? from, string? to)
    {
        if (!string.Equals(from, to, StringComparison.Ordinal))
        {
            fields.Add(new CommissionFieldChange { Field = field, From = from, To = to });
        }
    }

    private static CommissionRateLineChange Change(CommissionRateLineChange.KindValue kind, CommissionRateLineRow line, decimal? from, decimal? to) => new()
    {
        Kind = kind,
        BusinessType = line.BusinessType,
        LineOfBusiness = line.LineOfBusiness,
        Basis = line.Basis,
        FromRatePct = from,
        ToRatePct = to,
    };
}
