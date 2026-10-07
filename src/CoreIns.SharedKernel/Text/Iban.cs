using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>
/// An International Bank Account Number (ISO 13616). Accepts the print format (groups of four, any case) and stores
/// the electronic format (upper case, no spaces). Valid when the country is in the IBAN registry, the length matches
/// the country and the ISO 7064 mod-97-10 check gives 1. IBANs are personal-sensitive (P2): <see cref="ToString"/>
/// masks them; <see cref="Value"/> is the full number for the few places that must use it (the BIL payment instrument
/// record, encrypted at rest per D-ARC-14).
/// </summary>
[JsonConverter(typeof(IbanJsonConverter))]
public readonly record struct Iban
{
    private Iban(string value) => Value = value;

    /// <summary>Electronic format (e.g. <c>GR1601101250000000012300695</c>).</summary>
    public string Value { get; }

    /// <summary>ISO 3166-1 country of the account.</summary>
    public string CountryCode => Value[..2];

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static Iban Parse(string text) =>
        TryParse(text, out var iban) ? iban : throw new FormatException("Not a valid IBAN (country, length or check digits).");

    /// <summary>Parses and validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out Iban iban)
    {
        iban = default;
        if (text is null)
        {
            return false;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == ' ')
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        var value = builder.ToString();
        if (value.Length < 5
            || !char.IsAsciiLetterUpper(value[0]) || !char.IsAsciiLetterUpper(value[1])
            || !char.IsAsciiDigit(value[2]) || !char.IsAsciiDigit(value[3])
            || !IbanRegistry.Lengths.TryGetValue(value[..2], out var length)
            || value.Length != length
            || Mod97(value) != 1)
        {
            return false;
        }

        iban = new Iban(value);
        return true;
    }

    /// <summary>Print format in groups of four (e.g. <c>GR16 0110 1250 0000 0001 2300 695</c>).</summary>
    public string ToPrintFormat()
    {
        var value = Value;
        return string.Join(' ', Enumerable.Range(0, (value.Length + 3) / 4).Select(i => value.Substring(i * 4, Math.Min(4, value.Length - (i * 4)))));
    }

    /// <summary>Masked form for logs and screens without permission: country, check digits and the last four characters.</summary>
    public override string ToString() => Value is null ? string.Empty : $"{Value[..4]}…{Value[^4..]}";

    /// <summary>ISO 7064 MOD 97-10 over the rearranged IBAN (letters as 10…35).</summary>
    private static int Mod97(string value)
    {
        var remainder = 0;
        foreach (var c in value[4..] + value[..4])
        {
            var digit = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = digit >= 10 ? ((remainder * 100) + digit) % 97 : ((remainder * 10) + digit) % 97;
        }

        return remainder;
    }
}

/// <summary>Country lengths from the SWIFT IBAN registry.</summary>
internal static class IbanRegistry
{
    private const string Table =
        "AD24 AE23 AL28 AT20 AZ28 BA20 BE16 BG22 BH22 BI27 BR29 BY28 CH21 CR22 CY28 CZ24 DE22 DJ27 DK18 DO28 EE20 EG29 "
        + "ES24 FI18 FK18 FO18 FR27 GB22 GE22 GI23 GL18 GR27 GT28 HR21 HU28 IE22 IL23 IQ23 IS26 IT27 JO30 KW30 KZ20 LB28 "
        + "LC32 LI21 LT20 LU20 LV21 LY25 MC27 MD24 ME22 MK19 MN20 MR27 MT31 MU30 NI28 NL18 NO15 OM23 PK24 PL28 PS29 PT25 "
        + "QA29 RO24 RS22 RU33 SA24 SC31 SD18 SE24 SI19 SK24 SM27 SO23 ST25 SV28 TL23 TN24 TR26 UA29 VA22 VG24 XK20 YE30";

    public static FrozenDictionary<string, int> Lengths { get; } = Table
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .ToFrozenDictionary(entry => entry[..2], entry => int.Parse(entry[2..], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
}

/// <summary>
/// A tax identifier as issued by a country (e.g. a Greek AFM): the issuing country and the value as given. Opaque in
/// core: validation and normalisation belong to the country pack's <c>IdValidator</c> (contract §3.5.8; the AFM mod-11
/// rule is in the GR pack). Personal data: <see cref="ToString"/> masks the value.
/// </summary>
public sealed record TaxIdentifier
{
    /// <summary>Creates a tax identifier; the value is trimmed and must be 1–50 characters.</summary>
    public TaxIdentifier(Jurisdiction country, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Length is < 1 or > 50 || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("A tax identifier has 1-50 printable characters.", nameof(value));
        }

        Country = country;
        Value = trimmed;
    }

    /// <summary>Issuing country.</summary>
    public Jurisdiction Country { get; }

    /// <summary>The identifier as given (pack validators normalise it).</summary>
    public string Value { get; }

    /// <summary>Masked: country and the last two characters.</summary>
    public override string ToString() => $"{Country}:…{(Value.Length > 2 ? Value[^2..] : string.Empty)}";
}
