using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.CY;

/// <summary>
/// Cyprus stub treatment rows (D-REG-05): <b>synthetic</b>, CI-only, status Draft so Production refuses them. They exist to
/// prove that the treatment mechanism holds no Greek assumption (REQ-MKT-008): every credit is <c>REDUCE_PRO_RATA</c>
/// (the opposite of the Greek default), in every charge category and for every cancellation source. They are not Cyprus law.
/// </summary>
public sealed class CyTreatmentRules : IPackConfigurationSource
{
    public const string Version = "0.1.0";

    private const string Synthetic = "SYNTHETIC cy-stub treatment row (D-REG-05, PRD-17 REQ-MKT-263..269); not a Cyprus legal rule";

    private static readonly string[] Categories = ["TAX", "LEVY", "STAMP"];
    private static readonly string[] Sources =
        ["Policyholder", "Insurer", "NonPayment", "DistanceWithdrawal", "LongTermWithdrawal", "Objection", "Statutory"];
    private static readonly string[] AnySourceCredits = ["ENDORSEMENT_CREDIT", "RETURN_PREMIUM", "REFUND", "DISTANCE_WITHDRAWAL_VOID"];
    private static readonly string[] AnySourceDebits = ["NEW_BUSINESS", "ENDORSEMENT_DEBIT", "FEE"];

    public string PackId => CyPack.PackId;

    public string PackVersion => Version;

    public string Country => CyPack.Country;

    public IReadOnlyList<PackConfigValue> Values { get; } = Build();

    private static List<PackConfigValue> Build()
    {
        var values = new List<PackConfigValue>();
        foreach (var category in Categories)
        {
            foreach (var source in Sources)
            {
                values.Add(Row(category, "CANCELLATION", source, "REDUCE_PRO_RATA"));
                values.Add(Row(category, "VOID", source, "REDUCE_PRO_RATA"));
            }

            values.AddRange(AnySourceCredits.Select(kind => Row(category, kind, "ANY", "REDUCE_PRO_RATA")));
            values.AddRange(AnySourceDebits.Select(kind => Row(category, kind, "ANY", "APPLY")));
        }

        return values;
    }

    private static PackConfigValue Row(string category, string kind, string source, string action) =>
        new($"tax.treatment.rule.{category}.{kind}.{source}", ConfigValueType.Json,
            $$"""{"action":"{{action}}","ruleId":"CY-STUB-TRT-{{category}}-{{kind}}-{{source}}","ruleVersion":"{{Version}}"}""",
            LegalStatus.Draft, Synthetic, MotorPath: false);
}
