using System.Text.RegularExpressions;
using CoreIns.CountryPacks.GR.Transliteration;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Addresses;

/// <summary>
/// Greece pack <c>AddressFormatter</c> (REQ-MKT-092, REQ-PTY-076, REQ-PTY-012; PRD-17 §9.4.3): five-digit postcodes
/// (<c>10557</c> valid, <c>1055</c> → <c>POSTCODE_FORMAT</c>), a locality is required (<c>MISSING_LOCALITY</c>), street +
/// number order, postcode before locality, Greek and Latin (ELOT 743) forms. No County/State in Greece (PRD-01).
/// "Λεωφ. Κηφισίας 124, 11526 Αθήνα" → Latin "Leof. Kifisias 124, 11526 Athina".
/// </summary>
/// <remarks>
/// Postcode–locality consistency and locality autofill (REQ-PTY-076) need the pack's postcode list, which is pack data
/// not present in the PRDs; until it is loaded the consistency check is not performed (open item, see the F-1e report).
/// A postcode keyed with the customary space ("115 26") is accepted and stored without it.
/// </remarks>
public sealed partial class GreekAddressFormatter : IAddressFormatter
{
    public IReadOnlyCollection<string> Countries { get; } = [GrPack.Country];

    public ValueTask<PostalAddress> ParseAsync(
        IReadOnlyList<string> lines, string country, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(country);

        var parts = lines
            .SelectMany(line => (line ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        string? postcode = null;
        string? locality = null;
        var postcodeIndex = parts.FindLastIndex(part => PostcodeAndLocality().IsMatch(part));
        if (postcodeIndex >= 0)
        {
            var match = PostcodeAndLocality().Match(parts[postcodeIndex]);
            postcode = match.Groups["postcode"].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            locality = NullIfEmpty(match.Groups["locality"].Value);
            parts.RemoveAt(postcodeIndex);
        }

        string? street = null;
        string? number = null;
        if (parts.Count > 0)
        {
            var streetMatch = StreetAndNumber().Match(parts[0]);
            if (streetMatch.Success)
            {
                street = streetMatch.Groups["street"].Value.Trim();
                number = streetMatch.Groups["number"].Value;
            }
            else
            {
                street = parts[0];
            }

            parts.RemoveAt(0);
        }

        var address = new PostalAddress
        {
            Country = country,
            Street = street,
            Number = number,
            Postcode = postcode,
            Locality = locality,
            FreeLines = parts.Take(3).ToList(),
        };
        return ValueTask.FromResult(address);
    }

    public ValueTask<AddressValidationResult> ValidateAsync(PostalAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        var errors = new List<SpiError>();
        var postcode = address.Postcode?.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (postcode is null || !FiveDigits().IsMatch(postcode))
        {
            errors.Add(new SpiError(SpiErrorCategory.Validation, AddressErrorCodes.PostcodeFormat, nameof(PostalAddress.Postcode)));
        }

        if (string.IsNullOrWhiteSpace(address.Locality))
        {
            errors.Add(new SpiError(SpiErrorCategory.Validation, AddressErrorCodes.MissingLocality, nameof(PostalAddress.Locality)));
        }

        var normalised = address with { Postcode = postcode, Locality = address.Locality?.Trim() };
        var status = errors.Count == 0 ? AddressValidationStatus.Valid : AddressValidationStatus.Invalid;
        return ValueTask.FromResult(new AddressValidationResult(status, errors, normalised));
    }

    public ValueTask<FormattedAddress> FormatAsync(
        PostalAddress address, AddressPurpose purpose, string script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(script);

        var latin = script switch
        {
            ScriptCodes.Latin => true,
            ScriptCodes.Greek => false,
            _ => throw new SpiException(
                new SpiError(SpiErrorCategory.Validation, "UNSUPPORTED_SCRIPT", nameof(script)),
                $"Script '{script}' is not supported for Greek addresses."),
        };

        string? Render(string? value) => value is null ? null : latin ? ElotTransliterator.Transliterate(value, out _) : value;

        var streetLine = Join(" ", Render(address.Street), address.Number);
        var localityLine = Join(" ", address.Postcode?.Replace(" ", string.Empty, StringComparison.Ordinal), Render(address.Locality));
        var lines = new List<string>(4);
        if (streetLine is not null)
        {
            lines.Add(streetLine);
        }

        lines.AddRange(address.FreeLines.Select(line => Render(line)!));
        if (localityLine is not null)
        {
            lines.Add(localityLine);
        }

        IReadOnlyList<string> output = purpose == AddressPurpose.SingleLine ? [string.Join(", ", lines)] : lines;
        var ruleSet = latin ? $"{GrPack.AddressRuleSetId}+{GrPack.ElotRuleSetId}" : GrPack.AddressRuleSetId;
        return ValueTask.FromResult(new FormattedAddress(output, script, ruleSet));
    }

    private static string? Join(string separator, params string?[] values)
    {
        var present = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^(?<postcode>[0-9]{3} ?[0-9]{2})(?:\s+(?<locality>.+))?$")]
    private static partial Regex PostcodeAndLocality();

    [GeneratedRegex(@"^(?<street>.*[^0-9])\s+(?<number>[0-9]+\p{L}?(?:-[0-9]+\p{L}?)?)$")]
    private static partial Regex StreetAndNumber();

    [GeneratedRegex(@"^[0-9]{5}$")]
    private static partial Regex FiveDigits();
}
