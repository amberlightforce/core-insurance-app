using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.Modules.Market.Domain;

/// <summary>Time basis of a key (REQ-MKT-043).</summary>
internal static class TimeBases
{
    /// <summary>Date the value is effective on (default).</summary>
    public const string EffectiveDate = "EFFECTIVE_DATE";

    /// <summary>Tax follows the tax point, not the processing date (REQ-MKT-043 example on <c>tax.ipt.rate</c>).</summary>
    public const string TaxPointDate = "TAX_POINT_DATE";
}

/// <summary>
/// A registered configuration key (REQ-MKT-033/034/035): code-declared, with its value type and time basis. Only registered
/// keys can be resolved; a registered key with no value is an empty key that fails closed (D-REG-01).
/// </summary>
/// <param name="Key">Key, or key prefix when <paramref name="IsPrefix"/>.</param>
/// <param name="Type">Value type.</param>
/// <param name="TimeBasis">Time basis.</param>
/// <param name="Description">What the key holds.</param>
/// <param name="IsPrefix">The descriptor covers every key that starts with <paramref name="Key"/> (for example one rounding rule per tax class).</param>
internal sealed record ConfigKeyDescriptor(string Key, ConfigValueType Type, string TimeBasis, string Description, bool IsPrefix = false);

/// <summary>The key registry of the slice (REQ-MKT-033): the <c>tax.*</c> and <c>cur.*</c> keys MKT owns.</summary>
internal static class ConfigKeys
{
    public const string IptRateGeneral = "tax.ipt.rate.general";
    public const string IptRateFire = "tax.ipt.rate.fire";
    public const string IptMotorClass = "tax.ipt.motor_class";
    public const string IptLiabilityPoint = "tax.ipt.liability_point";
    public const string AuxFundCeilingRate = "tax.levy.auxfund.ceiling_rate";
    public const string AuxFundSplitBasis = "tax.levy.auxfund.split_basis";
    public const string AuxFundInsurerShare = "tax.levy.auxfund.split.insurer_share";
    public const string AuxFundPolicyholderShare = "tax.levy.auxfund.split.policyholder_share";
    public const string AuxFundBase = "tax.levy.auxfund.base";
    public const string CurTransaction = "cur.transaction";
    public const string CurFunctional = "cur.functional";
    public const string RoundingDefault = "cur.rounding.default";
    public const string RoundingPrefix = "cur.rounding.";
    public const string RoundingTaxPrefix = "cur.rounding.tax.";
    public const string OrderOfOperations = "cur.order_of_operations";

    public static IReadOnlyList<ConfigKeyDescriptor> All { get; } =
    [
        new(IptRateGeneral, ConfigValueType.ExactDecimal, TimeBases.TaxPointDate, "Premium tax (IPT) rate, general class (GR-01)"),
        new(IptRateFire, ConfigValueType.ExactDecimal, TimeBases.TaxPointDate, "Premium tax (IPT) rate, fire class (GR-01)"),
        new(IptMotorClass, ConfigValueType.Text, TimeBases.TaxPointDate, "IPT class applied to motor liability premium"),
        new(IptLiabilityPoint, ConfigValueType.Text, TimeBases.EffectiveDate, "IPT liability point, WRITTEN or DUE (REQ-MKT-328)"),
        new(AuxFundCeilingRate, ConfigValueType.ExactDecimal, TimeBases.EffectiveDate, "Auxiliary Fund contribution ceiling on MTPL premium (REQ-MKT-322)"),
        new(AuxFundSplitBasis, ConfigValueType.Text, TimeBases.EffectiveDate, "Amount the insurer/policyholder split applies to (REQ-MKT-322 reading)"),
        new(AuxFundInsurerShare, ConfigValueType.ExactDecimal, TimeBases.EffectiveDate, "Insurer share of the Auxiliary Fund contribution; empty (D-REG-06a)"),
        new(AuxFundPolicyholderShare, ConfigValueType.ExactDecimal, TimeBases.EffectiveDate, "Policyholder share of the Auxiliary Fund contribution; empty (D-REG-06a)"),
        new("tax.levy.auxfund.components", ConfigValueType.Json, TimeBases.EffectiveDate, "Component rates and the establishment condition of each (REQ-MKT-322); empty until the opinion"),
        new("tax.levy.auxfund.establishment_component", ConfigValueType.Text, TimeBases.EffectiveDate, "Component levied only on insurers established in Greece (REQ-MKT-322); empty until the opinion"),
        new("tax.levy.auxfund.ph_stamp_duty_rate", ConfigValueType.ExactDecimal, TimeBases.EffectiveDate, "Stamp duty on the policyholder share (REQ-MKT-322); the PRDs state no rate"),
        new(AuxFundBase, ConfigValueType.Text, TimeBases.EffectiveDate, "Base of the contribution (REQ-MKT-322)"),
        new("tax.levy.auxfund.midterm_base", ConfigValueType.Text, TimeBases.EffectiveDate, "Mid-term base (REQ-MKT-322); empty until the opinion"),
        new("tax.levy.auxfund.cancellation_treatment", ConfigValueType.Text, TimeBases.EffectiveDate, "Cancellation treatment (REQ-MKT-322); empty until the opinion"),
        new(CurTransaction, ConfigValueType.Text, TimeBases.EffectiveDate, "Transaction currency default (REQ-MKT-190)"),
        new(CurFunctional, ConfigValueType.Text, TimeBases.EffectiveDate, "Functional currency (REQ-MKT-190)"),
        new(RoundingDefault, ConfigValueType.Json, TimeBases.EffectiveDate, "Currency default rounding rule (REQ-MKT-191, BR-MKT-027)"),
        new(RoundingPrefix, ConfigValueType.Json, TimeBases.EffectiveDate, "Rounding rule per purpose, cur.rounding.<purpose> (REQ-MKT-192), and per tax class, cur.rounding.tax.<class> (REQ-MKT-193)", IsPrefix: true),
        new(OrderOfOperations, ConfigValueType.Json, TimeBases.EffectiveDate, "Order of operations for money calculations (REQ-MKT-194)"),
    ];

    /// <summary>The descriptor for <paramref name="key"/>, or null when it is not registered.</summary>
    public static ConfigKeyDescriptor? Find(string key) =>
        All.FirstOrDefault(d => d.Key == key) ?? All.FirstOrDefault(d => d.IsPrefix && key.StartsWith(d.Key, StringComparison.Ordinal) && key.Length > d.Key.Length);
}
