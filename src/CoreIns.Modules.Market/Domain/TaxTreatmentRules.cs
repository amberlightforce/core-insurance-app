using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.Modules.Market.Domain;

/// <summary>
/// Treatment rule rows (D2, REQ-MKT-330/331) held as configuration entries under <see cref="KeyPrefix"/>:
/// <c>tax.treatment.rule.&lt;category&gt;.&lt;kind&gt;.&lt;source|ANY&gt;</c>, value JSON <c>{"action","ruleId","ruleVersion"}</c>.
/// The three detail fields are derived from the action by the PRD-17 section 9.4.4 table, so a result is consistent by
/// definition. A row is data of a pack; the core holds none (no core default, fail closed).
/// </summary>
internal static class TaxTreatmentRules
{
    public const string KeyPrefix = "tax.treatment.rule.";

    /// <summary>Withdrawal/void routing selector (REQ-MKT-331): registered, not used by the slice.</summary>
    public const string WithdrawalVoidRouting = "tax.treatment.withdrawal_void_routing";

    public const string AnySource = "ANY";

    public const string CancellationSourceCodeListKey = "code.cancellation_source";

    public const string DistanceWithdrawal = "DistanceWithdrawal";

    /// <summary>The cancellation sources of the shared code list (REQ-POL-205, R-84).</summary>
    public static IReadOnlyList<string> CancellationSources { get; } =
        ["Policyholder", "Insurer", "NonPayment", "DistanceWithdrawal", "LongTermWithdrawal", "Objection", "Statutory"];

    public static string Key(TaxCategory category, TaxTransactionKind kind, string? source) =>
        $"{KeyPrefix}{CategoryCode(category)}.{KindCode(kind)}.{(string.IsNullOrEmpty(source) ? AnySource : source)}";

    public static string CategoryCode(TaxCategory category) => category.ToString().ToUpperInvariant();

    public static string KindCode(TaxTransactionKind kind) => kind switch
    {
        TaxTransactionKind.NewBusiness => "NEW_BUSINESS",
        TaxTransactionKind.EndorsementDebit => "ENDORSEMENT_DEBIT",
        TaxTransactionKind.EndorsementCredit => "ENDORSEMENT_CREDIT",
        TaxTransactionKind.Cancellation => "CANCELLATION",
        TaxTransactionKind.DistanceWithdrawalVoid => "DISTANCE_WITHDRAWAL_VOID",
        TaxTransactionKind.Void => "VOID",
        TaxTransactionKind.ReturnPremium => "RETURN_PREMIUM",
        TaxTransactionKind.Reinstatement => "REINSTATEMENT",
        TaxTransactionKind.Fee => "FEE",
        TaxTransactionKind.Refund => "REFUND",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The detail fields of an action (PRD-17 section 9.4.4 table). APPLY has none: neutral values.</summary>
    public static (CustomerCredit Credit, AuthorityLiability Liability, FiscalDocumentTreatment Document) Details(TreatmentAction action) => action switch
    {
        TreatmentAction.Apply => (CustomerCredit.None, AuthorityLiability.NotReduce, FiscalDocumentTreatment.None),
        TreatmentAction.ReduceProRata => (CustomerCredit.ProRata, AuthorityLiability.Reduce, FiscalDocumentTreatment.None),
        TreatmentAction.ReverseAsVoid => (CustomerCredit.Full, AuthorityLiability.Reduce, FiscalDocumentTreatment.CreditNote),
        TreatmentAction.KeepNotReduced => (CustomerCredit.None, AuthorityLiability.NotReduce, FiscalDocumentTreatment.None),
        TreatmentAction.InsurerBears => (CustomerCredit.Full, AuthorityLiability.NotReduce, FiscalDocumentTreatment.None),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string ActionCode(TreatmentAction action) => action switch
    {
        TreatmentAction.Apply => "APPLY",
        TreatmentAction.ReduceProRata => "REDUCE_PRO_RATA",
        TreatmentAction.ReverseAsVoid => "REVERSE_AS_VOID",
        TreatmentAction.KeepNotReduced => "KEEP_NOT_REDUCED",
        TreatmentAction.InsurerBears => "INSURER_BEARS",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The JSON value of a row.</summary>
    public static string Value(TreatmentAction action, string ruleId, string ruleVersion) =>
        JsonSerializer.Serialize(new { action = ActionCode(action), ruleId, ruleVersion });

    public sealed record Row(TreatmentAction Action, string RuleId, string RuleVersion);

    public static Row Parse(string key, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Read(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!
            : throw new InvalidOperationException($"Treatment rule '{key}' has no '{name}'.");
        var actionCode = Read("action");
        var action = Enum.GetValues<TreatmentAction>().Cast<TreatmentAction?>().FirstOrDefault(a => ActionCode(a!.Value) == actionCode)
            ?? throw new InvalidOperationException($"Treatment rule '{key}' has unknown action '{actionCode}'.");
        return new Row(action, Read("ruleId"), Read("ruleVersion"));
    }

    /// <summary>
    /// Pack-load validation (TCK-TAX-NET-REFUND, PRD-17 section 9.4.4): a row is well formed, its category and kind are known,
    /// its source is in the code list, and it never gives the customer no credit on a distance withdrawal (a refund net of tax).
    /// </summary>
    public static void ValidateRow(string key, string json)
    {
        var parts = key[KeyPrefix.Length..].Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException($"Treatment rule key '{key}' must be {KeyPrefix}<category>.<kind>.<source|ANY>.");
        }

        if (!Enum.GetValues<TaxCategory>().Any(c => CategoryCode(c) == parts[0]))
        {
            throw new InvalidOperationException($"Treatment rule '{key}' has unknown category '{parts[0]}'.");
        }

        var kind = Enum.GetValues<TaxTransactionKind>().Cast<TaxTransactionKind?>().FirstOrDefault(k => KindCode(k!.Value) == parts[1])
            ?? throw new InvalidOperationException($"Treatment rule '{key}' has unknown transaction kind '{parts[1]}'.");
        var source = parts[2];
        if (source != AnySource && !CancellationSources.Contains(source))
        {
            throw new InvalidOperationException($"Treatment rule '{key}' has a source outside the cancellation-source code list.");
        }

        var row = Parse(key, json);
        var isWithdrawal = kind == TaxTransactionKind.DistanceWithdrawalVoid || source == DistanceWithdrawal;
        if (isWithdrawal && Details(row.Action).Credit == CustomerCredit.None)
        {
            throw new InvalidOperationException(
                $"Treatment rule '{key}' gives customerCredit NONE on a distance withdrawal: a refund net of tax is forbidden in every pack (TCK-TAX-NET-REFUND).");
        }
    }
}
