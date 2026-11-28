using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Domain;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Configuration;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// <c>mkt.StatutoryClockSet.list/get</c> and <c>mkt.RegimeCode.list/get/validate/crosswalk</c> (BO-MKT-06). Reads code data only; no
/// database. Values that are not Settled carry <c>failClosed: true</c> and <c>usableInProduction: false</c>.
/// </summary>
internal sealed class StatutoryClockAndCodeService(IConfiguration configuration)
{
    private string Country => configuration["Stamp:Country"] ?? "GR";

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.MKT, "CFG-VALIDATION", message);

    private static object ToView(StatutoryClockValue v) => new
    {
        code = v.Code, name = v.Name, kind = v.Kind, duration = v.Duration, jurisdiction = v.Jurisdiction, warnBefore = v.WarnBefore,
        extendable = v.Extendable, legalStatus = v.LegalStatus.ToString(), source = v.Source, failClosed = v.FailClosed, usableInProduction = !v.FailClosed,
    };

    /// <summary>The clock values of a jurisdiction (default: the stamp country), optionally one clock code.</summary>
    public StatutoryClockSetListResponse ListClocks(string? clockCode, string? jurisdiction)
    {
        var scope = string.IsNullOrWhiteSpace(jurisdiction) ? Country : jurisdiction.Trim().ToUpperInvariant();
        var values = StatutoryClockSets.Find(scope, string.IsNullOrWhiteSpace(clockCode) ? null : clockCode);
        return new StatutoryClockSetListResponse
        {
            Values = JsonSerializer.SerializeToElement(new
            {
                jurisdiction = scope, items = values.Select(ToView).ToList(), failClosed = values.Count == 0 || values.Any(v => v.FailClosed),
            }),
        };
    }

    /// <summary>One clock value by code, in the stamp country.</summary>
    public StatutoryClockSetGetResponse GetClock(string clockCode)
    {
        var found = StatutoryClockSets.Find(Country, clockCode);
        if (found.Count == 0)
        {
            throw new DomainException(DomainError.Of(ModuleCode.MKT, "CFG-RULE-MISSING", $"No statutory clock value '{clockCode}' for {Country}; the operation fails closed."));
        }

        return new StatutoryClockSetGetResponse { Values = JsonSerializer.SerializeToElement(ToView(found[0])) };
    }

    private static object ListView(RegimeCodeList l) => new
    {
        regime = l.Regime, version = l.Version, name = l.Name, legalStatus = l.LegalStatus.ToString(), source = l.Source,
        failClosed = l.FailClosed, codes = l.Codes.Select(c => new { code = c.Code, name = c.Name }).ToList(),
    };

    private static JsonElement Validity(RegimeCodeList l, bool? valid = null, string? reason = null) =>
        JsonSerializer.SerializeToElement(new { version = l.Version, legalStatus = l.LegalStatus.ToString(), failClosed = l.FailClosed, usableInProduction = !l.FailClosed, valid, reason });

    /// <summary>All regimes, or one when <paramref name="regime"/> is given.</summary>
    public static RegimeCodeListPage ListCodes(string? regime)
    {
        var lists = string.IsNullOrWhiteSpace(regime) ? RegimeCodeLists.All : [.. RegimeCodeLists.All.Where(l => string.Equals(l.Regime, regime, StringComparison.OrdinalIgnoreCase))];
        return new RegimeCodeListPage
        {
            Items = [.. lists.Select(l => new RegimeCodeListItem { Codes = JsonSerializer.SerializeToElement(ListView(l)), Validity = Validity(l) })],
            NextCursor = null,
            Limit = lists.Count,
        };
    }

    /// <summary>One regime's list by regime id.</summary>
    public static RegimeCodeGetResponse GetCodes(string regime)
    {
        var list = RegimeCodeLists.Find(regime) ?? throw new DomainException(Invalid($"Unknown regime '{regime}'."));
        return new RegimeCodeGetResponse { Codes = JsonSerializer.SerializeToElement(ListView(list)), Validity = Validity(list) };
    }

    /// <summary>Whether a code is in the regime's list. An unverified list answers with the fail-closed flag set.</summary>
    public static RegimeCodeValidateResponse Validate(RegimeCodeValidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var regime = request.Regime is { ValueKind: JsonValueKind.String } r ? r.GetString() : null;
        var code = request.Code is { ValueKind: JsonValueKind.String } c ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(regime) || string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException(Invalid("regime and code are required strings."));
        }

        var list = RegimeCodeLists.Find(regime) ?? throw new DomainException(Invalid($"Unknown regime '{regime}'."));
        var valid = RegimeCodeLists.IsValid(list, code);
        return new RegimeCodeValidateResponse
        {
            Codes = JsonSerializer.SerializeToElement(list.Codes.Where(e => e.Code == code).Select(e => new { code = e.Code, name = e.Name }).ToList()),
            Validity = Validity(list, valid, valid ? null : $"'{code}' is not in {list.Regime} {list.Version}."),
        };
    }

    /// <summary>Maps a code from one regime to another. The reverse call (swap from/to) returns the start again.</summary>
    public static RegimeCodeCrosswalkResponse Crosswalk(string? from, string? to, string? code)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException(Invalid("from, to and code are required."));
        }

        var source = RegimeCodeLists.Find(from) ?? throw new DomainException(Invalid($"Unknown regime '{from}'."));
        var target = RegimeCodeLists.Find(to) ?? throw new DomainException(Invalid($"Unknown regime '{to}'."));
        var mapped = RegimeCodeLists.Map(source.Regime, target.Regime, code);
        var failClosed = source.FailClosed || target.FailClosed;
        return new RegimeCodeCrosswalkResponse
        {
            Codes = JsonSerializer.SerializeToElement(new { from = source.Regime, to = target.Regime, code, mapped }),
            Validity = JsonSerializer.SerializeToElement(new { failClosed, usableInProduction = !failClosed, mappedAny = mapped.Count > 0 }),
        };
    }
}
