using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>Upper-snake wire codes of the module's enums (stored in the database and published in events).</summary>
internal static class Codes
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static TEnum Parse<TEnum>(string code)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().First(value => string.Equals(Of(value), code, StringComparison.Ordinal));

    /// <summary>Check-constraint SQL listing every code of <typeparamref name="TEnum"/>.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => $"'{Of(v)}'"))})";

    /// <summary>The contract enum of a job state (same member names).</summary>
    public static JobStateCode Api(JobState state) => Enum.Parse<JobStateCode>(state.ToString());

    /// <summary>The contract enum of a term state (same member names).</summary>
    public static TermStateCode Api(PolicyTermState state) => Enum.Parse<TermStateCode>(state.ToString());
}

/// <summary>Job types of PRD-05 §7.3.2 (SL-POL builds Submission only).</summary>
internal enum JobType
{
    /// <summary>New business.</summary>
    Submission,
}

/// <summary>Charge categories returned by RAT (PRD-03: taxes and levies are separate charge types of category tax or levy).</summary>
internal static class ChargeCategories
{
    public const string Premium = "PREMIUM";
}

/// <summary>Delta kinds of a charge delta (PRD-05 §7.1). NET is the P1 mode (REQ-POL-121).</summary>
internal static class DeltaKinds
{
    public const string Net = "NET";
}
