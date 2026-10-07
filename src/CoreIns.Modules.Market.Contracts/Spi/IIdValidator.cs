namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 1 <c>IdValidator</c> (REQ-MKT-090; PRD-17 §9.4.1; spi.md §1): validate and normalise identifiers per scheme,
/// with optional external verification. Scheme <c>VEHICLE_PLATE</c> adds <c>normalise</c> and <c>searchKey</c>
/// (REQ-MKT-339). Binding axis SCHEME (qualifier = scheme code). Mode S (verify: A). Idempotency P / K.
/// Timeout 50 ms validate; verify 3 s → <see cref="IdVerificationStatus.NotAvailable"/>.
/// Core default: optional schemes accepted as <see cref="IdValidationStatus.Unverified"/>; mandatory schemes fail closed;
/// plates trimmed and upper-cased, <see cref="IdValidationStatus.Unverified"/>.
/// Errors: VALIDATION (<c>CHECK_DIGIT</c>, <c>FORMAT</c>, <c>LENGTH</c>), NOT_APPLICABLE (scheme not bound), UNAVAILABLE.
/// </summary>
public interface IIdValidator
{
    /// <summary>Schemes this implementation validates (the pack's binding qualifiers).</summary>
    IReadOnlyCollection<IdScheme> Schemes { get; }

    /// <summary>
    /// <c>validate(scheme, value, context) → {status, normalised, errors[]}</c>. Pure. Validation problems are returned
    /// in <see cref="IdValidationResult.Errors"/>; an unbound scheme throws <see cref="SpiException"/> NOT_APPLICABLE.
    /// </summary>
    ValueTask<IdValidationResult> ValidateAsync(
        IdScheme scheme, string value, IdValidationContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>verify(scheme, value, evidenceContext) → {status, source, checkedAt}</c>; asynchronous when external,
    /// idempotent on the input. Returns <see cref="IdVerificationStatus.NotAvailable"/> when no source is reachable.
    /// </summary>
    Task<IdVerificationResult> VerifyAsync(
        IdScheme scheme, string value, IdEvidenceContext evidenceContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scheme <c>VEHICLE_PLATE</c>: <c>normalise(value, context) → {normalised, findings[]}</c> (REQ-MKT-339). Pure,
    /// 20 ms. Look-alike letters mapped to the pack's canonical series, separators removed, case folded.
    /// <c>LegacyDataProfile.profilePlate</c> delegates here so migrated and new plates normalise identically.
    /// </summary>
    ValueTask<PlateNormalisationResult> NormaliseAsync(
        string value, IdValidationContext context, CancellationToken cancellationToken = default);

    /// <summary>Scheme <c>VEHICLE_PLATE</c>: <c>searchKey(value) → key</c> (REQ-MKT-339). Pure, 20 ms.</summary>
    ValueTask<string> SearchKeyAsync(string value, CancellationToken cancellationToken = default);
}

/// <summary>
/// An identifier scheme code (for example the pack-defined schemes of the identifier catalogue, REQ-PTY-047).
/// Scheme codes are pack data; the core knows only <see cref="VehiclePlate"/>, which the SPI itself defines.
/// </summary>
/// <param name="Code">Upper-snake scheme code.</param>
public readonly record struct IdScheme(string Code)
{
    /// <summary>Scheme defined by the SPI (REQ-MKT-339).</summary>
    public static IdScheme VehiclePlate { get; } = new("VEHICLE_PLATE");

    public override string ToString() => Code;
}

/// <summary>Validation outcome (PRD-17 §9.4.1).</summary>
public enum IdValidationStatus
{
    /// <summary>Format and check digit verified.</summary>
    Valid,

    /// <summary>Format verified; no check digit exists or is known (for example the Cyprus stub TIC, REQ-MKT-264).</summary>
    ValidFormat,

    /// <summary>The value fails a stated rule; see <see cref="IdValidationResult.Errors"/>.</summary>
    Invalid,

    /// <summary>Accepted without validation (no format is stated for the scheme, or core default).</summary>
    Unverified,
}

/// <summary>Context of a validation call.</summary>
/// <param name="IssuingCountry">ISO 3166-1 alpha-2 issuing country, when known.</param>
/// <param name="PartyType">Person or Organisation, when known (heuristics only).</param>
public sealed record IdValidationContext(string? IssuingCountry = null, string? PartyType = null)
{
    public static IdValidationContext None { get; } = new();
}

/// <summary>Result of <c>validate</c>.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Normalised">Normalised value to store (null when the value cannot be normalised).</param>
/// <param name="Errors">Validation errors (category VALIDATION; codes <c>CHECK_DIGIT</c>, <c>FORMAT</c>, <c>LENGTH</c>).</param>
/// <param name="Findings">Informational findings (for example a corrected prefix); never errors.</param>
/// <param name="ValidatorVersion">Rule-set / validator version, stored with the identifier (REQ-PTY-048).</param>
public sealed record IdValidationResult(
    IdValidationStatus Status,
    string? Normalised,
    IReadOnlyList<SpiError> Errors,
    IReadOnlyList<string> Findings,
    string ValidatorVersion)
{
    public bool IsAccepted => Status != IdValidationStatus.Invalid;
}

/// <summary>Evidence context of a <c>verify</c> call (registry, document).</summary>
/// <param name="Purpose">Why the verification runs (audit).</param>
/// <param name="Attributes">Additional evidence attributes (for example a name to compare).</param>
public sealed record IdEvidenceContext(string? Purpose = null, IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>Verification outcome (PRD-17 §9.4.1).</summary>
public enum IdVerificationStatus
{
    Verified,
    NotFound,
    Mismatch,
    NotAvailable,
}

/// <summary>Result of <c>verify</c>.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Source">Verification source (for example a registry name), null when not available.</param>
/// <param name="CheckedAt">When the check ran.</param>
public sealed record IdVerificationResult(IdVerificationStatus Status, string? Source, DateTimeOffset CheckedAt);

/// <summary>Result of <c>VEHICLE_PLATE</c> <c>normalise</c> (REQ-MKT-339).</summary>
/// <param name="Normalised">Canonical plate.</param>
/// <param name="Findings">Finding codes, see <see cref="PlateFindings"/>.</param>
/// <param name="RuleSetId">Rule-set id of the normalisation.</param>
public sealed record PlateNormalisationResult(string Normalised, IReadOnlyList<string> Findings, string RuleSetId);

/// <summary>Plate finding codes (PRD-17 §9.4.41 <c>profilePlate</c>).</summary>
public static class PlateFindings
{
    /// <summary>Look-alike characters of another script were mapped to the canonical series.</summary>
    public const string LookalikeNormalised = "LOOKALIKE_NORMALISED";

    /// <summary>A letter outside the pack's plate series.</summary>
    public const string InvalidSeries = "INVALID_SERIES";

    /// <summary>The value does not match the pack's plate format.</summary>
    public const string Format = "FORMAT";
}

/// <summary>Error codes of <c>IdValidator</c> (PRD-17 §9.4.1).</summary>
public static class IdValidationErrorCodes
{
    public const string CheckDigit = "CHECK_DIGIT";
    public const string Format = "FORMAT";
    public const string Length = "LENGTH";
}
