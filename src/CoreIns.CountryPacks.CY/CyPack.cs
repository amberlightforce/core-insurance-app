using System.Text.RegularExpressions;
using CoreIns.CountryPacks.GR.Transliteration;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.CY;

/// <summary>
/// Identity of the Cyprus stub pack (<c>cy-stub</c>, REQ-MKT-263…269, PRD-17 §10.4.7). Synthetic and CI-only
/// (D-REG-05): it exists to prove that the core holds no Greek assumptions (REQ-MKT-008).
/// </summary>
public static class CyPack
{
    public const string PackId = "cy-stub";

    public const string Country = "CY";

    /// <summary>Same ELOT 743 algorithm as Greece, different rule-set id (PRD-17 §9.4.2, research F14).</summary>
    public const string ElotRuleSetId = "CY-ELOT743-T2/1";

    public const string IdValidatorVersion = "CY-ID/1";

    public const string AddressRuleSetId = "CY-ADDR/1";

    /// <summary>Cyprus tax identification code scheme (REQ-MKT-264).</summary>
    public const string TicScheme = "TIC";

    /// <summary>
    /// Binding language of documents: English, with Greek informative (REQ-MKT-268, a stub design choice proving that
    /// the binding language is data, OI-MKT-09).
    /// </summary>
    public const string BindingDocumentLanguage = "en";

    public const string InformativeDocumentLanguage = "el";
}

/// <summary>Cyprus stub <c>NameTransliterator</c>: the Greece ELOT engine bound under <see cref="CyPack.ElotRuleSetId"/>.</summary>
public sealed class CyNameTransliterator : INameTransliterator
{
    private readonly GreekNameTransliterator _inner = new(CyPack.ElotRuleSetId);

    public string RuleSetId => _inner.RuleSetId;

    public ValueTask<TransliterationResult> TransliterateAsync(
        string text, string sourceScript, string? ruleSet = null, CancellationToken cancellationToken = default) =>
        _inner.TransliterateAsync(text, sourceScript, ruleSet, cancellationToken);

    public ValueTask<IReadOnlyList<string>> SearchVariantsAsync(string text, CancellationToken cancellationToken = default) =>
        _inner.SearchVariantsAsync(text, cancellationToken);
}

/// <summary>
/// Cyprus stub <c>IdValidator</c> (REQ-MKT-264, REQ-MKT-090 acceptance): scheme <c>TIC</c> is 8 digits followed by one
/// Latin letter, format only, <c>ValidFormat</c> (the check-letter algorithm is unverified, OI-MKT-07):
/// <c>60000001A</c> and <c>12345678L</c> are ValidFormat, <c>6000001A</c> is Invalid. Scheme <c>VEHICLE_PLATE</c> uses the
/// core default (trim and upper-case, <c>Unverified</c>, REQ-MKT-339) because the PRDs do not state the Cyprus plate
/// format.
/// </summary>
public sealed partial class CyIdValidator(TimeProvider timeProvider) : IIdValidator
{
    private const string PlateRuleSetId = "CY-PLATE/1";

    private static readonly IdScheme[] Supported = [new(CyPack.TicScheme), IdScheme.VehiclePlate];

    public CyIdValidator()
        : this(TimeProvider.System)
    {
    }

    public IReadOnlyCollection<IdScheme> Schemes => Supported;

    public ValueTask<IdValidationResult> ValidateAsync(
        IdScheme scheme, string value, IdValidationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (scheme == IdScheme.VehiclePlate)
        {
            var plate = PlateDefault(value);
            return ValueTask.FromResult(new IdValidationResult(IdValidationStatus.Unverified, plate, [], [], PlateRuleSetId));
        }

        if (scheme.Code != CyPack.TicScheme)
        {
            throw NotBound(scheme);
        }

        var normalised = string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
        string? error = null;
        if (normalised.Length != 9)
        {
            error = IdValidationErrorCodes.Length;
        }
        else if (!Tic().IsMatch(normalised))
        {
            error = IdValidationErrorCodes.Format;
        }

        var result = error is null
            ? new IdValidationResult(IdValidationStatus.ValidFormat, normalised, [], [], CyPack.IdValidatorVersion)
            : new IdValidationResult(
                IdValidationStatus.Invalid, normalised, [new SpiError(SpiErrorCategory.Validation, error)], [], CyPack.IdValidatorVersion);
        return ValueTask.FromResult(result);
    }

    public Task<IdVerificationResult> VerifyAsync(
        IdScheme scheme, string value, IdEvidenceContext evidenceContext, CancellationToken cancellationToken = default)
    {
        if (!Supported.Contains(scheme))
        {
            throw NotBound(scheme);
        }

        return Task.FromResult(new IdVerificationResult(IdVerificationStatus.NotAvailable, null, timeProvider.GetUtcNow()));
    }

    public ValueTask<PlateNormalisationResult> NormaliseAsync(
        string value, IdValidationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ValueTask.FromResult(new PlateNormalisationResult(PlateDefault(value), [], PlateRuleSetId));
    }

    public ValueTask<string> SearchKeyAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ValueTask.FromResult(PlateDefault(value));
    }

    private static string PlateDefault(string value) => value.Trim().ToUpperInvariant();

    private static SpiException NotBound(IdScheme scheme) =>
        new(new SpiError(SpiErrorCategory.NotApplicable, "SCHEME_NOT_BOUND"), $"Scheme '{scheme.Code}' is not bound in pack {CyPack.PackId}.");

    [GeneratedRegex("^[0-9]{8}[A-Z]$")]
    private static partial Regex Tic();
}

/// <summary>
/// Cyprus stub <c>AddressFormatter</c> (PRD-17 §9.4.3, §10.4.7): four-digit postcodes, English-first formatting —
/// Latin script is the default output and native-script output is not offered by the stub.
/// </summary>
public sealed partial class CyAddressFormatter : IAddressFormatter
{
    public IReadOnlyCollection<string> Countries { get; } = [CyPack.Country];

    public ValueTask<PostalAddress> ParseAsync(
        IReadOnlyList<string> lines, string country, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var parts = lines.SelectMany(line => (line ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
        var postcodeIndex = parts.FindLastIndex(part => PostcodeAndLocality().IsMatch(part));
        string? postcode = null;
        string? locality = null;
        if (postcodeIndex >= 0)
        {
            var match = PostcodeAndLocality().Match(parts[postcodeIndex]);
            postcode = match.Groups["postcode"].Value;
            locality = match.Groups["locality"].Success ? match.Groups["locality"].Value.Trim() : null;
            parts.RemoveAt(postcodeIndex);
        }

        return ValueTask.FromResult(new PostalAddress
        {
            Country = country,
            Street = parts.Count > 0 ? parts[0] : null,
            Postcode = postcode,
            Locality = locality,
            FreeLines = parts.Skip(1).Take(3).ToList(),
        });
    }

    public ValueTask<AddressValidationResult> ValidateAsync(PostalAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var errors = new List<SpiError>();
        if (address.Postcode is null || !FourDigits().IsMatch(address.Postcode))
        {
            errors.Add(new SpiError(SpiErrorCategory.Validation, AddressErrorCodes.PostcodeFormat, nameof(PostalAddress.Postcode)));
        }

        if (string.IsNullOrWhiteSpace(address.Locality))
        {
            errors.Add(new SpiError(SpiErrorCategory.Validation, AddressErrorCodes.MissingLocality, nameof(PostalAddress.Locality)));
        }

        var status = errors.Count == 0 ? AddressValidationStatus.Valid : AddressValidationStatus.Invalid;
        return ValueTask.FromResult(new AddressValidationResult(status, errors, address));
    }

    public ValueTask<FormattedAddress> FormatAsync(
        PostalAddress address, AddressPurpose purpose, string script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (script != ScriptCodes.Latin)
        {
            throw new SpiException(
                new SpiError(SpiErrorCategory.Validation, "UNSUPPORTED_SCRIPT", nameof(script)),
                "The Cyprus stub formats addresses in Latin script only (English-first).");
        }

        var lines = new List<string>();
        var street = string.Join(' ', new[] { address.Number, address.Street }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (street.Length > 0)
        {
            lines.Add(street);
        }

        lines.AddRange(address.FreeLines);
        var locality = string.Join(' ', new[] { address.Locality?.ToUpperInvariant(), address.Postcode }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (locality.Length > 0)
        {
            lines.Add(locality);
        }

        IReadOnlyList<string> output = purpose == AddressPurpose.SingleLine ? [string.Join(", ", lines)] : lines;
        return ValueTask.FromResult(new FormattedAddress(output, ScriptCodes.Latin, CyPack.AddressRuleSetId));
    }

    [GeneratedRegex(@"^(?<postcode>[0-9]{4})(?:\s+(?<locality>.+))?$")]
    private static partial Regex PostcodeAndLocality();

    [GeneratedRegex("^[0-9]{4}$")]
    private static partial Regex FourDigits();
}
