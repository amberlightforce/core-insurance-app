using CoreIns.CountryPacks.GR.Language;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Identifiers;

/// <summary>
/// Greece pack <c>IdValidator</c> (REQ-MKT-090, REQ-MKT-260, REQ-PTY-049, REQ-MKT-339; PRD-17 §9.4.1).
/// <list type="bullet">
/// <item><c>AFM</c>: nine digits, mod-11 check digit, never 000000000, no prefix (REQ-PTY-049) → Valid / Invalid.</item>
/// <item><c>VAT</c>: "EL" + AFM; a "GR" prefix is corrected to "EL" with finding <c>PREFIX_CORRECTED</c>
/// (REQ-PTY-050, BR-PTY-003) → Valid / Invalid.</item>
/// <item><c>GEMI</c>, <c>PASSPORT</c>, <c>NATIONAL_ID</c>, <c>RESIDENCE_PERMIT</c>, <c>DRIVING_LICENCE</c>: the PRDs name
/// the schemes (REQ-PTY-047; PRD-17 §9.4.1 "passport/ID card format checks", "GEMI number format") but state no
/// format, so the value is normalised (trimmed, upper-cased with the Greek rules, inner spaces removed) and returned
/// <c>Unverified</c> with finding <c>FORMAT_NOT_STATED</c> — the core default for optional schemes, never a guess.</item>
/// <item><c>VEHICLE_PLATE</c>: <see cref="GreekVehiclePlate"/> → Unverified with findings.</item>
/// </list>
/// <c>verify</c> returns <c>NotAvailable</c>: registry and VIES checks arrive with the <c>RegistryLookup</c> adapters.
/// </summary>
public sealed class GreekIdValidator(TimeProvider timeProvider) : IIdValidator
{
    /// <summary>Finding: a "GR" VAT prefix was corrected to the VIES prefix "EL" (REQ-PTY-050).</summary>
    public const string PrefixCorrected = "PREFIX_CORRECTED";

    /// <summary>Finding: the PRDs state no format for the scheme; the value was not validated.</summary>
    public const string FormatNotStated = "FORMAT_NOT_STATED";

    private const string VatPrefix = "EL";
    private const string WrongVatPrefix = "GR";

    private static readonly IdScheme[] Supported =
    [
        new(GrSchemes.Afm), new(GrSchemes.Vat), new(GrSchemes.Gemi), new(GrSchemes.Passport), new(GrSchemes.NationalId),
        new(GrSchemes.ResidencePermit), new(GrSchemes.DrivingLicence), IdScheme.VehiclePlate,
    ];

    public GreekIdValidator()
        : this(TimeProvider.System)
    {
    }

    public IReadOnlyCollection<IdScheme> Schemes => Supported;

    public ValueTask<IdValidationResult> ValidateAsync(
        IdScheme scheme, string value, IdValidationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ValueTask.FromResult(scheme.Code switch
        {
            GrSchemes.Afm => ValidateAfm(value),
            GrSchemes.Vat => ValidateVat(value),
            GrSchemes.Gemi or GrSchemes.Passport or GrSchemes.NationalId or GrSchemes.ResidencePermit
                or GrSchemes.DrivingLicence => NotValidated(value),
            "VEHICLE_PLATE" => ValidatePlate(value),
            _ => throw NotBound(scheme),
        });
    }

    public Task<IdVerificationResult> VerifyAsync(
        IdScheme scheme, string value, IdEvidenceContext evidenceContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Supported.Contains(scheme))
        {
            throw NotBound(scheme);
        }

        return Task.FromResult(new IdVerificationResult(IdVerificationStatus.NotAvailable, null, timeProvider.GetUtcNow()));
    }

    public ValueTask<PlateNormalisationResult> NormaliseAsync(
        string value, IdValidationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GreekVehiclePlate.Normalise(value));

    public ValueTask<string> SearchKeyAsync(string value, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GreekVehiclePlate.SearchKey(value));

    private static IdValidationResult ValidateAfm(string value)
    {
        var normalised = Afm.Normalise(value);
        var error = Afm.Check(normalised);
        return error is null
            ? Result(IdValidationStatus.Valid, normalised, null, [])
            : Result(IdValidationStatus.Invalid, normalised, error, []);
    }

    private static IdValidationResult ValidateVat(string value)
    {
        var normalised = Afm.Normalise(value).ToUpperInvariant();
        var findings = new List<string>();
        if (normalised.StartsWith(WrongVatPrefix, StringComparison.Ordinal))
        {
            normalised = VatPrefix + normalised[WrongVatPrefix.Length..];
            findings.Add(PrefixCorrected);
        }

        if (!normalised.StartsWith(VatPrefix, StringComparison.Ordinal))
        {
            return Result(IdValidationStatus.Invalid, normalised, IdValidationErrorCodes.Format, findings);
        }

        var error = Afm.Check(normalised[VatPrefix.Length..]);
        return Result(error is null ? IdValidationStatus.Valid : IdValidationStatus.Invalid, normalised, error, findings);
    }

    private static IdValidationResult NotValidated(string value)
    {
        var normalised = string.Concat(GreekCaseMapper.ToUpper(value.Trim()).Where(character => !char.IsWhiteSpace(character)));
        return Result(IdValidationStatus.Unverified, normalised, null, [FormatNotStated]);
    }

    private static IdValidationResult ValidatePlate(string value)
    {
        var plate = GreekVehiclePlate.Normalise(value);
        return new IdValidationResult(IdValidationStatus.Unverified, plate.Normalised, [], plate.Findings, plate.RuleSetId);
    }

    private static IdValidationResult Result(IdValidationStatus status, string normalised, string? error, IReadOnlyList<string> findings) =>
        new(
            status,
            normalised,
            error is null ? [] : [new SpiError(SpiErrorCategory.Validation, error)],
            findings,
            GrPack.IdValidatorVersion);

    private static SpiException NotBound(IdScheme scheme) =>
        new(new SpiError(SpiErrorCategory.NotApplicable, "SCHEME_NOT_BOUND"), $"Scheme '{scheme.Code}' is not bound in pack {GrPack.PackId}.");
}
