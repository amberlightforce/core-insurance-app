using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>
/// An ISO 4217 currency: the alphabetic code and its number of minor units (EUR = 2, JPY = 0, KWD = 3).
/// Only codes of the built-in ISO 4217 table are accepted; the minor units come from that table and drive
/// <see cref="Money.RoundToMinorUnits"/>. Rounding mode and precision per tax line come from configuration (ADR §2 rule 2).
/// JSON: the alphabetic code as a string (contracts/events common <c>CurrencyCode</c>).
/// </summary>
[JsonConverter(typeof(CurrencyJsonConverter))]
public readonly record struct Currency : IComparable<Currency>
{
    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    /// <summary>ISO 4217 alphabetic code, upper case (e.g. <c>EUR</c>).</summary>
    public string Code { get; }

    /// <summary>Number of decimal places of the minor unit (ISO 4217 "minor unit").</summary>
    public int MinorUnits { get; }

    /// <summary>Euro, the functional currency of the Greek market.</summary>
    public static Currency EUR { get; } = FromCode("EUR");

    /// <summary>US dollar, the group reporting currency (contract §3.2.1 three-currency rule).</summary>
    public static Currency USD { get; } = FromCode("USD");

    /// <summary>True for <c>default(Currency)</c>, which is not a currency.</summary>
    public bool IsDefault => Code is null;

    /// <summary>All currencies of the built-in table, ordered by code.</summary>
    public static IReadOnlyList<Currency> All { get; } =
        [.. Iso4217.MinorUnits.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new Currency(pair.Key, pair.Value))];

    /// <summary>Returns the currency for an ISO 4217 alphabetic code; throws <see cref="FormatException"/> when unknown.</summary>
    public static Currency FromCode(string code) =>
        TryFromCode(code, out var currency)
            ? currency
            : throw new FormatException($"'{code}' is not an ISO 4217 currency code known to this system.");

    /// <summary>Looks up an ISO 4217 alphabetic code (exact upper case).</summary>
    public static bool TryFromCode([NotNullWhen(true)] string? code, out Currency currency)
    {
        if (code is not null && Iso4217.MinorUnits.TryGetValue(code, out var minor))
        {
            currency = new Currency(code, minor);
            return true;
        }

        currency = default;
        return false;
    }

    /// <inheritdoc />
    public int CompareTo(Currency other) => string.CompareOrdinal(Code, other.Code);

    /// <summary>The alphabetic code.</summary>
    public override string ToString() => Code ?? string.Empty;

    /// <summary>Orders by code.</summary>
    public static bool operator <(Currency left, Currency right) => left.CompareTo(right) < 0;

    /// <summary>Orders by code.</summary>
    public static bool operator >(Currency left, Currency right) => left.CompareTo(right) > 0;

    /// <summary>Orders by code.</summary>
    public static bool operator <=(Currency left, Currency right) => left.CompareTo(right) <= 0;

    /// <summary>Orders by code.</summary>
    public static bool operator >=(Currency left, Currency right) => left.CompareTo(right) >= 0;
}

/// <summary>ISO 4217 active alphabetic codes with their minor units (funds and precious metals without minor units are excluded).</summary>
internal static class Iso4217
{
    private const string ZeroDecimals = "BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX UYI VND VUV XAF XOF XPF";
    private const string ThreeDecimals = "BHD IQD JOD KWD LYD OMR TND";
    private const string FourDecimals = "CLF UYW";

    private const string TwoDecimals =
        "AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BMD BND BOB BOV BRL BSD BTN BWP BYN BZD CAD CDF CHE CHF "
        + "CHW CNY COP COU CRC CUP CVE CZK DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GTQ GYD HKD HNL HTG HUF "
        + "IDR ILS INR IRR JMD KES KGS KHR KPW KYD KZT LAK LBP LKR LRD LSL MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN "
        + "MXV MYR MZN NAD NGN NIO NOK NPR NZD PAB PEN PGK PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK SGD SHP SLE SOS "
        + "SRD SSP STN SVC SYP SZL THB TJS TMT TOP TRY TTD TWD TZS UAH USD USN UYU UZS VED VES WST XCD XCG YER ZAR ZMW ZWG";

    public static FrozenDictionary<string, int> MinorUnits { get; } = Build();

    private static FrozenDictionary<string, int> Build()
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        Add(table, ZeroDecimals, 0);
        Add(table, TwoDecimals, 2);
        Add(table, ThreeDecimals, 3);
        Add(table, FourDecimals, 4);
        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Add(Dictionary<string, int> table, string codes, int minorUnits)
    {
        foreach (var code in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            table.Add(code, minorUnits);
        }
    }
}
