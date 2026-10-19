using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Claims.Commands.FriendlySettlement;

/// <summary>
/// The Friendly Settlement gate (REQ-CLM-156, D-SL4-03): the MKT capability <c>cap.clm.friendly_settlement</c> must resolve on and the
/// clearing SPI must be bound. Off or unbound (always so in Production) gives <c>CLM-ERR-FS-DISABLED</c>; the SPI is then never called.
/// </summary>
internal sealed class FsGate(IServiceProvider services, IHostEnvironment environment)
{
    public static DomainError Disabled(string detail) => DomainError.Of(ModuleCode.CLM, "FS-DISABLED", detail);

    public async Task<Result<IFriendlySettlementClearing>> OpenAsync(CancellationToken cancellationToken)
    {
        var capabilities = services.GetService<IMarketCapabilityService>();
        if (capabilities is null)
        {
            return Disabled("The capability service is not available.");
        }

        var response = await capabilities.GetAsync(@switch: IFriendlySettlementClearing.CapabilityKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        var enabled = response.State is { ValueKind: JsonValueKind.Object } state
            && state.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (!enabled)
        {
            return Disabled("Friendly Settlement is switched off for the jurisdiction.");
        }

        if (services.GetService<IFriendlySettlementClearing>() is not { } spi)
        {
            return Disabled("No Friendly Settlement clearing adapter is bound.");
        }

        return Result.Success(spi);
    }

    /// <summary>PITFALLS 36: outside Settled (or provisional) the value is refused in Production.</summary>
    public bool Refuses(FsEligibilityDecision decision) =>
        environment.IsProduction() && (decision.LegalStatus != LegalStatus.Settled || decision.Provisional);
}

/// <summary>Mapping and loading shared by the FS case commands and queries.</summary>
internal static class FsCases
{
    public const string OwnRole = "OWN_INSURER";
    public const string AtFaultRole = "AT_FAULT_INSURER";

    public static string Code(PartyId party) => party.Value.ToString("D", CultureInfo.InvariantCulture);

    public static bool TryParty(string? code, out PartyId party)
    {
        var ok = Guid.TryParse(code, out var id) && id != Guid.Empty;
        party = ok ? new PartyId(id) : default;
        return ok;
    }

    public static string StateCode(FsCaseState state) => state switch
    {
        FsCaseState.EligibilityPending => "ELIGIBILITY_PENDING",
        FsCaseState.NotEligible => "NOT_ELIGIBLE",
        _ => state.ToString().ToUpperInvariant(),
    };

    public static FsEligibility EligibilityOf(FsEligibilityDecision d) => new()
    {
        Result = d.Result switch
        {
            FsEligibilityResult.Eligible => FsEligibility.ResultValue.Eligible,
            FsEligibilityResult.NotEligible => FsEligibility.ResultValue.NotEligible,
            _ => FsEligibility.ResultValue.NotApplicable,
        },
        Reasons = [.. d.Reasons],
        RuleId = d.RuleId,
        RuleVersion = d.RuleVersion,
        LegalStatus = d.LegalStatus.ToString().ToUpperInvariant(),
        Provisional = d.Provisional,
        ClearingValue = d.ClearingValue is { } v ? Money.Of(v.Amount, v.Currency) : null,
        ClearingValueBasis = d.ClearingValueBasis is null ? null
            : d.ClearingValueBasis == ClearingValueBasis.Actual ? FsEligibility.ClearingValueBasisValue.Actual : FsEligibility.ClearingValueBasisValue.Average,
    };

    public static DomainError FromSpi(SpiException ex) => ex.Error.Category == SpiErrorCategory.Validation
        ? DomainError.Of(ModuleCode.CLM, "VALIDATION", $"The clearing message was refused: {ex.Error.Code}{(ex.Error.Field is null ? string.Empty : " (" + ex.Error.Field + ")")}.")
        : DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", $"The clearing service answered {ex.Error.Code}.");

    public static async Task<FsCaseRow?> LockAsync(ClaimsDbContext db, LegalEntityId entity, FsCaseId id, CancellationToken cancellationToken)
    {
        var rows = await db.FsCases
            .FromSql($"SELECT * FROM clm.fs_case WHERE fs_case_id = {id.Value} AND legal_entity_id = {entity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The case view; expected gain/loss = clearing value minus paid on the own-settlement exposure (null until a value exists).</summary>
    public static async Task<FsCaseView> ViewAsync(ClaimsDbContext db, FinancialsReader financials, FsCaseRow row, CancellationToken cancellationToken)
    {
        FsEligibility? eligibility = row.EligibilityResult?.Deserialize<FsEligibility>(SharedKernelJson.Options);
        Money? gainLoss = null;
        if (row.Role == OwnRole && row.ExposureId is { } own && row.ClearingValue is { } value)
        {
            var lines = await financials.LinesAsync(row.ClaimId, cancellationToken).ConfigureAwait(false);
            var approved = await financials.ApprovedAsync(row.ClaimId, null, cancellationToken).ConfigureAwait(false);
            var paid = lines.Where(l => l.ExposureId == own).Sum(l => approved.TryGetValue(l.ReserveLineId, out var a) ? a.Paid : 0m);
            gainLoss = new Money(value - paid, Currency.EUR);
        }

        Guid? paymentId = null;
        if (row.Role == AtFaultRole && row.ExposureId is { } exposure)
        {
            paymentId = await db.ClaimPayments.AsNoTracking().Where(p => p.ClaimId == row.ClaimId && p.ExposureId == exposure && p.Method == "CLEARING")
                .OrderBy(p => p.CreatedAt).Select(p => (Guid?)p.ClaimPaymentId.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        Guid? statementId = null;
        string? lineRef = null;
        if (row.StatementLineId is { } lineId)
        {
            var line = await db.FsStatementLines.AsNoTracking().Where(l => l.FsStatementLineId == lineId)
                .Select(l => new { l.FsStatementId, l.ExternalReference }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            statementId = line?.FsStatementId;
            lineRef = line?.ExternalReference;
        }

        return new FsCaseView
        {
            FsCaseId = new FsCaseId(row.FsCaseId),
            ClaimId = row.ClaimId,
            Role = row.Role == OwnRole ? FsCaseRole.OwnInsurer : FsCaseRole.AtFaultInsurer,
            State = Enum.GetValues<FsCaseState>().Single(s => StateCode(s) == row.Status),
            CounterpartyCode = Code(row.CounterpartyInsurerPartyId),
            CounterpartyPartyId = row.CounterpartyInsurerPartyId,
            Eligibility = eligibility,
            ClearingValue = row.ClearingValue is { } cv ? new Money(cv, Currency.EUR) : null,
            ClearingReference = row.ClearingReference,
            OwnSettlementExposureId = row.Role == OwnRole ? row.ExposureId?.Value : null,
            RecoveryId = row.RecoveryId,
            AtFaultPaymentId = paymentId,
            FsStatementId = statementId,
            StatementLineRef = lineRef,
            ExpectedGainLoss = gainLoss,
            CreatedAt = row.CreatedAt,
            RecordVersion = row.RecordVersion,
        };
    }
}
