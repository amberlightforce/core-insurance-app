using System.Text.Json;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Commands.Change;

/// <summary>
/// Configuration section <c>Policy:Change</c> of SL3-POL-CHANGE. The effective-date limits are the PRD-05 §10.2 defaults
/// (change 0 days back for a CSR, 30 days for <c>Staff.Underwriter</c>): <b>illustrative</b> commercial values (D-SL3-08), kept in
/// configuration, never in code paths. The editable vehicle fields stand in for PFC's <c>changePermissions</c> (REQ-PFC-066) until
/// SL3-PFC-MOTOR11 is on main; reconcile then.
/// </summary>
internal sealed class ChangeOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Policy:Change";

    /// <summary>Days back a change may be effective for a role with no entry below (the CSR default: 0).</summary>
    public int BackdateDaysDefault { get; set; }

    /// <summary>Days back a change may be effective, per role; the most permissive role of the caller applies.</summary>
    public Dictionary<string, int> BackdateDaysByRole { get; } = new(StringComparer.Ordinal) { ["Staff.Underwriter"] = 30 };

    /// <summary>Vehicle fields a mid-term change may edit (rating fields only; plate, VIN and make are identification).</summary>
    public string[] EditableVehicleFields { get; set; } = ["firstRegistrationYear", "engineCapacityCc", "value", "use"];
}

/// <summary>Names the change commands share.</summary>
internal static class ChangeNames
{
    /// <summary>The permission that guards quoting, binding and previewing a change job (in addition to the job permissions).</summary>
    public const string Permission = "pol.change";

    /// <summary>Intent version stored with a Change transaction (the replay reads it).</summary>
    public const int IntentVersion = 1;

    /// <summary>The element type reported for vehicle changes (<c>PolicyChanged.changedElements</c>).</summary>
    public const string VehicleElementType = "VEHICLE";

    /// <summary>The cause code of a charge line written by a change.</summary>
    public const string JobType = "POLICY_CHANGE";
}

/// <summary>A new annual rate of the post-change risk, as stored on the Change transaction's intent (the replay of the term history).</summary>
internal sealed record IntentRate(string Element, string Coverage, string ChargeType, string Category, decimal AnnualRate);

/// <summary>The intent stored on a Change transaction: effective time and the full set of premium rates after the change.</summary>
internal sealed record ChangeIntentRecord(int Version, Instant EffectiveAt, IReadOnlyList<IntentRate> NewRates, RiskTree RiskTree);

/// <summary>One line of the before/after diff of a change (REQ-POL-193): element, field, before, after, grouped by section.</summary>
internal sealed record DiffEntry(string Section, string Element, string Field, string? Before, string? After);

/// <summary>The outcome of comparing the base risk tree with the edited one.</summary>
/// <param name="Entries">The diff.</param>
/// <param name="Violations">Edits a mid-term change does not allow (field code, message).</param>
/// <param name="AddedVehicles">Vehicle locators added by the edit.</param>
/// <param name="RemovedVehicles">Vehicle locators removed by the edit.</param>
/// <param name="ChangedVehicles">Vehicle locators whose rating fields changed.</param>
internal sealed record RiskDiff(
    IReadOnlyList<DiffEntry> Entries,
    IReadOnlyList<(string Field, string Message)> Violations,
    IReadOnlyList<string> AddedVehicles,
    IReadOnlyList<string> RemovedVehicles,
    IReadOnlyList<string> ChangedVehicles);

/// <summary>A tax or levy line on a premium delta, as the tax port returns it (RAT servicing tax lines + MKT treatment).</summary>
/// <param name="SourceKey">The premium delta the line derives from.</param>
/// <param name="Rate">The tax rate as a decimal fraction (0.15 = 15 %).</param>
/// <param name="Action">The MKT treatment action: <c>Apply</c> on a debit, <c>KeepNotReduced</c> on a credit.</param>
internal sealed record PricedTaxLine(
    ChargeKey SourceKey,
    string CoverageCode,
    string ChargeType,
    string ChargeCategory,
    decimal Rate,
    decimal Amount,
    TreatmentActionCode Action,
    string RuleId,
    string RuleVersion,
    string LegalStatus,
    bool Provisional);

/// <summary>What the tax port needs: the premium deltas of one transaction and where and when they are taxed.</summary>
internal sealed record ServicingTaxRequest(
    string LegalEntity,
    string Jurisdiction,
    BusinessDate TaxPointDate,
    Currency Currency,
    IReadOnlyList<ServicingDelta> PremiumDeltas);

/// <summary>JSON helpers for the stored intent.</summary>
internal static class ChangeJson
{
    /// <summary>Reads the premium rates of a stored Change intent; fails closed on anything else.</summary>
    public static IReadOnlyList<ChargeRate>? Rates(string intentJson, out Instant effectiveAt)
    {
        effectiveAt = default;
        using var document = JsonDocument.Parse(intentJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var version) || version.GetInt32() != ChangeNames.IntentVersion
            || !root.TryGetProperty("effectiveAt", out var at) || !Instant.TryParse(at.GetString(), out effectiveAt)
            || !root.TryGetProperty("newRates", out var rates) || rates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<ChargeRate>();
        foreach (var rate in rates.EnumerateArray())
        {
            result.Add(new ChargeRate(
                rate.GetProperty("element").GetString()!, rate.GetProperty("coverage").GetString()!, rate.GetProperty("chargeType").GetString()!,
                rate.GetProperty("category").GetString()!, rate.GetProperty("annualRate").GetDecimal()));
        }

        return result;
    }

    /// <summary>The stored form of a change intent.</summary>
    public static string Intent(Instant effectiveAt, IEnumerable<ChargeRate> rates, RiskTree tree) =>
        JobSupport.Json(new ChangeIntentRecord(
            ChangeNames.IntentVersion, effectiveAt,
            [.. rates.OrderBy(r => r.Key).Select(r => new IntentRate(r.ElementLocator, r.CoverageCode, r.ChargeType, r.ChargeCategory, r.AnnualRate))], tree));

    /// <summary>The transaction kind of a charge line (the MKT code) for the engine's kind.</summary>
    public static TaxTransactionKind Kind(Domain.Servicing.TransactionKind kind) => kind switch
    {
        Domain.Servicing.TransactionKind.NewBusiness => TaxTransactionKind.NewBusiness,
        Domain.Servicing.TransactionKind.EndorsementDebit => TaxTransactionKind.EndorsementDebit,
        Domain.Servicing.TransactionKind.EndorsementCredit => TaxTransactionKind.EndorsementCredit,
        Domain.Servicing.TransactionKind.Cancellation => TaxTransactionKind.Cancellation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transaction kind."),
    };
}

/// <summary>Error definitions of the change commands (RFC 9457, D-API-15): the codes POL-CHANGE raises beyond the module's list.</summary>
internal static class ChangeErrors
{
    public static CoreIns.Platform.Errors.ErrorDefinition[] Definitions { get; } =
    [
        CoreIns.Platform.Errors.ErrorDefinition.For(
            CoreIns.SharedKernel.Identifiers.ModuleCode.POL, PolicyErrorNames.Preempted, 409, "Άλλη συναλλαγή δεσμεύτηκε στον όρο στο μεταξύ", "Another transaction was bound on the term meanwhile")
            .Describe("Η αλλαγή ξεκίνησε πάνω σε παλαιότερη κατάσταση του όρου. Ξεκινήστε την αλλαγή ξανά.", "The change started on an older state of the term. Start the change again."),
        CoreIns.Platform.Errors.ErrorDefinition.For(
            CoreIns.SharedKernel.Identifiers.ModuleCode.POL, PolicyErrorNames.AfterCancellation, 422, "Η κάλυψη έχει λήξει με ακύρωση", "The cover has ended by cancellation")
            .Describe("Μετά από δεσμευμένη ακύρωση δεν επιτρέπεται καμία αλλαγή στον όρο.", "Nothing can be changed on the term at or after a bound cancellation."),
        CoreIns.Platform.Errors.ErrorDefinition.For(
            CoreIns.SharedKernel.Identifiers.ModuleCode.POL, "JOB-CONFLICT", 409, "Υπάρχει ήδη ανοιχτή εργασία του ίδιου τύπου", "An open job of the same type already exists")
            .Describe("Ο όρος έχει ήδη ανοιχτή αλλαγή. Ολοκληρώστε την ή αποσύρετέ την.", "The term already has an open change. Finish or withdraw it."),
    ];
}

/// <summary>The wire codes of the MKT treatment actions (as stored on the charge line and carried on the event).</summary>
internal static class TreatmentActions
{
    public static string Code(TreatmentActionCode action) => action switch
    {
        TreatmentActionCode.Apply => "APPLY",
        TreatmentActionCode.ReduceProRata => "REDUCE_PRO_RATA",
        TreatmentActionCode.ReverseAsVoid => "REVERSE_AS_VOID",
        TreatmentActionCode.KeepNotReduced => "KEEP_NOT_REDUCED",
        TreatmentActionCode.InsurerBears => "INSURER_BEARS",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown treatment action."),
    };
}
