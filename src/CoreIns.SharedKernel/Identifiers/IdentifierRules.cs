using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>
/// Validation rules of the text value objects. Formats fixed by the contract (§3.6.1) are enforced exactly; business
/// numbers only get structural checks because their format is pack data (NumberingScheme SPI).
/// </summary>
internal static partial class IdentifierRules
{
    public const string BusinessNumberDescription = "1-64 characters, no control characters, no surrounding spaces";
    public const string CodeDescription = "1-128 characters without spaces or control characters";
    public const string LegalEntityDescription = "upper-case letters, digits, '-' or '_' (1-64), e.g. GR-TEST";
    public const string JurisdictionDescription = "ISO 3166-1 alpha-2, upper case (e.g. GR)";
    public const string ClockCodeDescription = "<MOD>_<NAME>, e.g. BIL_NONPAY_NOTICE";
    public const string AiFeatureIdDescription = "AI-<MOD>-<NN>, e.g. AI-CLM-02";
    public const string RetentionClassDescription = "RC-..., e.g. RC-AUD-BUS";
    public const string ObligationDescription = "OBL-<CODE>, e.g. OBL-GDPR";
    public const string OperationNameDescription = "<mod>.<Resource>.<operation>, e.g. pol.Job.bind";
    public const string EventTypeNameDescription = "PascalCase, e.g. PolicyBound";
    public const string ConfigKeyDescription = "dotted key in the owning module's namespace, e.g. cfg.retro.max_days";
    public const string CapabilitySwitchDescription = "cap.<area>.<name>, e.g. cap.fin.posting_model";
    public const string PackCodeDescription = "lower-case pack id, e.g. gr or cy-stub";
    public const string AuthorityTypeDescription = "upper-case code, e.g. MKT_CONFIG_APPROVAL";

    public static string Invalid(string type, string? value, string rule) => $"'{value}' is not a valid {type} ({rule}).";

    public static bool IsBusinessNumber([NotNullWhen(true)] string? value) =>
        value is { Length: >= 1 and <= 64 }
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1])
        && !value.Any(char.IsControl);

    public static bool IsCode([NotNullWhen(true)] string? value) =>
        value is { Length: >= 1 and <= 128 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    public static bool IsLegalEntity([NotNullWhen(true)] string? value) => value is not null && LegalEntityPattern().IsMatch(value);

    public static bool IsJurisdiction([NotNullWhen(true)] string? value) => value is not null && JurisdictionPattern().IsMatch(value);

    public static bool IsClockCode([NotNullWhen(true)] string? value) => value is { Length: <= 100 } && ClockCodePattern().IsMatch(value);

    public static bool IsAiFeatureId([NotNullWhen(true)] string? value) => value is { Length: <= 32 } && AiFeaturePattern().IsMatch(value);

    public static bool IsRetentionClass([NotNullWhen(true)] string? value) => value is { Length: <= 64 } && RetentionPattern().IsMatch(value);

    public static bool IsObligation([NotNullWhen(true)] string? value) => value is { Length: <= 64 } && ObligationPattern().IsMatch(value);

    public static bool IsOperationName([NotNullWhen(true)] string? value) => value is { Length: <= 200 } && OperationPattern().IsMatch(value);

    public static bool IsEventTypeName([NotNullWhen(true)] string? value) => value is { Length: <= 100 } && EventTypePattern().IsMatch(value);

    public static bool IsConfigKey([NotNullWhen(true)] string? value) => value is { Length: <= 200 } && ConfigKeyPattern().IsMatch(value);

    public static bool IsCapabilitySwitch([NotNullWhen(true)] string? value) => value is { Length: <= 200 } && CapabilityPattern().IsMatch(value);

    public static bool IsPackCode([NotNullWhen(true)] string? value) => value is not null && PackCodePattern().IsMatch(value);

    public static bool IsAuthorityType([NotNullWhen(true)] string? value) => value is not null && AuthorityTypePattern().IsMatch(value);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LegalEntityPattern();

    [GeneratedRegex("^[A-Z]{2}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex JurisdictionPattern();

    [GeneratedRegex("^[A-Z]{2,3}_[A-Z0-9_]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClockCodePattern();

    [GeneratedRegex("^AI-[A-Z]{2,3}-[0-9]{2,}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AiFeaturePattern();

    [GeneratedRegex("^RC-[A-Z0-9-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RetentionPattern();

    [GeneratedRegex("^OBL-[A-Z0-9-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObligationPattern();

    [GeneratedRegex("^[a-z]{2,3}\\.[A-Z][A-Za-z0-9]*\\.[a-z][A-Za-z0-9]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OperationPattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypePattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+)+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigKeyPattern();

    [GeneratedRegex("^cap\\.[a-z0-9_]+\\.[a-z0-9_]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackCodePattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_.]{1,99}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorityTypePattern();
}
