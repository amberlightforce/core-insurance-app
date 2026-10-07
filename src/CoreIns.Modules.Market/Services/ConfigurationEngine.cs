using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Queries;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// The configuration resolver (REQ-MKT-001, REQ-MKT-044, REQ-MKT-045) and rounding (REQ-MKT-195) over the catalogue.
/// Deterministic: the same catalogue hash, context and dates give the same values. The Production gate (D-REG-02,
/// REQ-MKT-343) refuses to serve any value that is not Settled (or not regulatory) when the host environment is Production;
/// everywhere else such values are served with <c>provisional = true</c>.
/// </summary>
internal sealed class ConfigurationEngine(
    ConfigurationCatalogue catalogue, LegalEntityRegistry registry, IHostEnvironment environment, IClock clock)
{
    /// <summary>True when non-Settled values must be refused (the environment is Production).</summary>
    public bool EnforceSettled => environment.IsProduction();

    public ConfigurationCatalogue Catalogue => catalogue;

    public ConfigurationResolveResponse Resolve(ConfigurationResolveRequest request, ValidAt? validAt, Instant? knownAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = EntityOf(request.LegalEntity);
        if ((request.Keys is null) == (request.Namespace is null))
        {
            throw Error("CFG-VALIDATION", "Give exactly one of keys or namespace (REQ-MKT-044).");
        }

        if (request.ConfigurationHash is { } hash && hash != catalogue.Hash)
        {
            throw Error("CFG-HASH-UNKNOWN", $"Configuration state {hash} is not known; the stamp holds {catalogue.Hash} (history arrives with W1-MKT-01).");
        }

        if (knownAt is { } known && known < catalogue.ActivatedAt)
        {
            throw Error("NOT-AVAILABLE", "knownAt earlier than the current state needs the configuration history (W1-MKT-01).");
        }

        var date = validAt is { Date: { } day } ? day
            : validAt is { Instant: { } at } ? at.ToBusinessDate(entity.Zone)
            : clock.Now.ToBusinessDate(entity.Zone);
        var keys = request.Keys is { } requested
            ? requested.Distinct(StringComparer.Ordinal).ToList()
            : KeysOfNamespace(request.Namespace!);
        var items = new List<ConfigurationResolveResponse.ValueItem>();
        var missing = new List<string>();
        var refused = new List<string>();
        foreach (var key in keys)
        {
            var descriptor = ConfigKeys.Find(key) ?? throw Error("CFG-UNKNOWN-KEY", $"'{key}' is not a registered configuration key (REQ-MKT-033).");
            var basisDate = BasisDate(descriptor, request, date);
            var entry = catalogue.Find(key, request.Jurisdiction, basisDate);
            if (entry is null)
            {
                missing.Add(key);
                continue;
            }

            if (EnforceSettled && !entry.IsSettled)
            {
                refused.Add($"{key} ({entry.LegalStatus})");
                continue;
            }

            items.Add(ToItem(entry, descriptor));
        }

        if (refused.Count > 0)
        {
            throw Error("CFG-NOT-SETTLED", $"Production refuses values that are not Settled (D-REG-02, REQ-MKT-343): {string.Join(", ", refused)}.");
        }

        return new ConfigurationResolveResponse
        {
            ConfigurationHash = catalogue.Hash,
            Values = items,
            MissingKeys = missing,
            HasProvisionalValues = items.Any(i => i.Provisional),
        };
    }

    public ConfigurationCurrentHashResponse CurrentHash() =>
        new() { Hash = catalogue.Hash.Hash, ActivatedAt = catalogue.ActivatedAt };

    public RoundingApplyResponse ApplyRounding(RoundingApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount is not { } amount || request.Context is not { } context)
        {
            throw Error("CFG-VALIDATION", "amount and context.legalEntity are required (REQ-MKT-195).");
        }

        var currency = request.Currency ?? amount.Currency;
        if (currency != amount.Currency)
        {
            throw Error("CFG-VALIDATION", "currency must match the currency of amount.");
        }

        var entity = EntityOf(context.LegalEntity);
        var date = context.ValidAt ?? clock.Now.ToBusinessDate(entity.Zone);

        // Precedence (BR-MKT-027): tax-class rule, then purpose rule, then the currency default.
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(context.TaxClass))
        {
            candidates.Add(ConfigKeys.RoundingTaxPrefix + context.TaxClass);
        }

        if (!string.IsNullOrEmpty(request.Purpose))
        {
            candidates.Add(ConfigKeys.RoundingPrefix + request.Purpose);
        }

        candidates.Add(ConfigKeys.RoundingDefault);
        ConfigEntry? entry = null;
        foreach (var key in candidates)
        {
            if (ConfigKeys.Find(key) is not null && catalogue.Find(key, entity.HomeJurisdiction, date) is { } found)
            {
                entry = found;
                break;
            }
        }

        if (entry is null)
        {
            throw Error("CFG-RULE-MISSING", "No rounding rule is configured, not even the currency default.");
        }

        if (EnforceSettled && !entry.IsSettled)
        {
            throw Error("CFG-NOT-SETTLED", $"Production refuses values that are not Settled (D-REG-02, REQ-MKT-343): {entry.Key} ({entry.LegalStatus}).");
        }

        var rule = RoundingRule.Parse(entry.ToJsonElement());
        var scale = rule.Scale ?? currency.MinorUnits;
        var rounded = rule.Apply(amount.Amount, scale);
        return new RoundingApplyResponse
        {
            AmountAfterRounding = new Money(rounded, currency),
            Residual = new Money(amount.Amount - rounded, currency),
            RuleId = rule.IdFor(entry.Key),
            RuleKey = entry.Key,
            Mode = Enum.Parse<RoundingApplyResponse.ModeValue>(rule.Mode.ToString()),
            Scale = scale,
            LegalStatus = entry.LegalStatus.ToString(),
            Provisional = !entry.IsSettled,
        };
    }

    private LegalEntityInfo EntityOf(string code) =>
        LegalEntityCode.TryParse(code, out var parsed) && registry.Find(parsed) is { } entity
            ? entity
            : throw Error("CFG-LEGAL-ENTITY-UNKNOWN", $"Legal entity '{code}' is not in the registry (REQ-MKT-151).");

    private static BusinessDate BasisDate(ConfigKeyDescriptor descriptor, ConfigurationResolveRequest request, BusinessDate validAt)
    {
        if (request.TimeBasisDates is { } dates && dates.TryGetValue(descriptor.TimeBasis, out var date))
        {
            return date;
        }

        // REQ-MKT-043: tax follows the tax point; a tax key resolved without a tax-point date is an error, never a guess.
        if (descriptor.TimeBasis == TimeBases.TaxPointDate)
        {
            throw Error("CFG-TIMEBASIS-MISSING", $"'{descriptor.Key}' resolves on {TimeBases.TaxPointDate}; give it in timeBasisDates (REQ-MKT-043).");
        }

        return validAt;
    }

    private List<string> KeysOfNamespace(string prefix)
    {
        var dotted = prefix.EndsWith('.') ? prefix : prefix + ".";
        var keys = catalogue.Entries.Select(e => e.Key)
            .Concat(ConfigKeys.All.Where(d => !d.IsPrefix).Select(d => d.Key))
            .Where(k => k.StartsWith(dotted, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return keys.Count > 0 ? keys : throw Error("CFG-UNKNOWN-KEY", $"No registered key lives in namespace '{prefix}'.");
    }

    private static ConfigurationResolveResponse.ValueItem ToItem(ConfigEntry entry, ConfigKeyDescriptor descriptor) =>
        new()
        {
            Key = entry.Key,
            Value = entry.ToJsonElement(),
            SourceLayer = entry.Node,
            ValueVersionId = entry.VersionId,
            Validity = entry.Validity,
            Final = false,
            LegalStatus = Enum.Parse<ConfigurationResolveResponse.ValueItem.LegalStatusValue>(entry.LegalStatus.ToString()),
            LegalSourceRef = entry.SourceRef,
            MotorPath = entry.MotorPath,
            Provisional = !entry.IsSettled,
            PackId = entry.PackId,
            TimeBasis = descriptor.TimeBasis,
        };

    private static DomainException Error(string name, string detail) =>
        new(DomainError.Of(ModuleCode.MKT, name, detail));
}
