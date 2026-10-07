namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 3 <c>AddressFormatter</c> (REQ-MKT-092; PRD-17 §9.4.3; spi.md §3): parse, validate and format addresses per
/// country and output purpose. Binding axis RISK_LOCATION / address country. Mode S, pure (formatting, REQ-MKT-117),
/// 50 ms; fallback free-form. Core default: free-form lines, no postcode check.
/// Errors: VALIDATION (<c>POSTCODE_FORMAT</c>, <c>MISSING_LOCALITY</c>).
/// </summary>
public interface IAddressFormatter
{
    /// <summary>ISO 3166-1 alpha-2 countries this implementation formats.</summary>
    IReadOnlyCollection<string> Countries { get; }

    /// <summary><c>parse(lines, country)</c>: structured address from free lines (best effort, never throws on content).</summary>
    ValueTask<PostalAddress> ParseAsync(
        IReadOnlyList<string> lines, string country, CancellationToken cancellationToken = default);

    /// <summary><c>validate(address) → {status, errors[]}</c>.</summary>
    ValueTask<AddressValidationResult> ValidateAsync(
        PostalAddress address, CancellationToken cancellationToken = default);

    /// <summary><c>format(address, purpose, script)</c>: output lines for the purpose, in the requested script.</summary>
    /// <param name="address">Address in native script.</param>
    /// <param name="purpose">Output purpose.</param>
    /// <param name="script">ISO 15924 script of the output (<see cref="ScriptCodes"/>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<FormattedAddress> FormatAsync(
        PostalAddress address, AddressPurpose purpose, string script, CancellationToken cancellationToken = default);
}

/// <summary>
/// Structured address (PRD-01 §7 Address: structured fields plus up to three free lines). Fields are native script.
/// </summary>
public sealed record PostalAddress
{
    /// <summary>ISO 3166-1 alpha-2 country.</summary>
    public required string Country { get; init; }

    public string? Street { get; init; }

    public string? Number { get; init; }

    public string? Building { get; init; }

    public string? Floor { get; init; }

    public string? Unit { get; init; }

    public string? Postcode { get; init; }

    public string? Locality { get; init; }

    public string? Municipality { get; init; }

    public string? RegionalUnit { get; init; }

    public string? Region { get; init; }

    /// <summary>Free-form lines (at most three) kept when the address could not be structured.</summary>
    public IReadOnlyList<string> FreeLines { get; init; } = [];
}

/// <summary>Validation state (PRD-01 Address <c>validation_state</c>).</summary>
public enum AddressValidationStatus
{
    Valid,
    Invalid,

    /// <summary>Not checked (core default, or no rule for the country).</summary>
    Unvalidated,
}

/// <summary>Result of <c>validate</c>.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Errors">Errors (<see cref="AddressErrorCodes"/>).</param>
/// <param name="Normalised">The address with normalised fields (for example the postcode without spaces).</param>
public sealed record AddressValidationResult(
    AddressValidationStatus Status, IReadOnlyList<SpiError> Errors, PostalAddress Normalised);

/// <summary>Output purpose of <c>format</c> (PRD-17 §9.4.3).</summary>
public enum AddressPurpose
{
    Postal,
    Document,
    SingleLine,
}

/// <summary>Result of <c>format</c>.</summary>
/// <param name="Lines">Output lines (one line for <see cref="AddressPurpose.SingleLine"/>).</param>
/// <param name="Script">ISO 15924 script of the lines.</param>
/// <param name="RuleSetId">Formatting rule set (and transliteration rule set when Latin output was generated).</param>
public sealed record FormattedAddress(IReadOnlyList<string> Lines, string Script, string RuleSetId);

/// <summary>Error codes of <c>AddressFormatter</c> (PRD-17 §9.4.3).</summary>
public static class AddressErrorCodes
{
    public const string PostcodeFormat = "POSTCODE_FORMAT";
    public const string MissingLocality = "MISSING_LOCALITY";
}
