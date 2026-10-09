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
/// The configuration resolver (REQ-MKT-001, REQ-MKT-044, REQ-MKT-045) and rounding (REQ-MKT-195) over a configuration state.
/// Deterministic: the same state hash, context and dates give the same values. The state comes from the request's
/// <c>configurationHash</c> (any recorded state, REQ-MKT-048), else the state current at <c>knownAt</c>, else the hash pinned for
/// the unit of work (REQ-MKT-051), else the current state (cached at most 1 s, REQ-MKT-050). The Production gate (D-REG-02,
/// REQ-MKT-343) refuses to serve any value that is not Settled (or not regulatory) when the host environment is Production;
/// everywhere else such values are served with <c>provisional = true</c>.
/// </summary>
internal sealed class ConfigurationEngine(
    IConfigurationStates states, LegalEntityRegistry registry, IHostEnvironment environment, IClock clock)
{
    /// <summary>An engine over one fixed state (tests and tools): that state is both the current state and the only recorded one.</summary>
    public ConfigurationEngine(ConfigurationCatalogue catalogue, LegalEntityRegistry registry, IHostEnvironment environment, IClock clock)
        : this(new StaticConfigurationStates(catalogue), registry, environment, clock)
    {
    }

    /// <summary>True when non-Settled values must be refused (the environment is Production).</summary>
    public bool EnforceSettled => environment.IsProduction();

    /// <summary>The source of configuration states.</summary>
    public IConfigurationStates States => states;

    /// <summary>The fixed state of an engine built over one catalogue. A persisted engine has no such state: use <see cref="StateAsync"/>.</summary>
    public ConfigurationCatalogue Catalogue =>
        (states as StaticConfigurationStates)?.Catalogue
        ?? throw new InvalidOperationException("This engine serves persisted states: select one with StateAsync.");

    /// <summary>
    /// Selects the state: the request's hash if given (CFG-HASH-UNKNOWN when it was never recorded), else the state current at
    /// <paramref name="knownAt"/> (NOT-AVAILABLE before the first state), else the unit of work's <paramref name="pinned"/> hash,
    /// else the current state.
    /// </summary>
    public async Task<ConfigurationCatalogue> StateAsync(
        ConfigurationHash? hash, Instant? knownAt, ConfigurationHash? pinned, CancellationToken cancellationToken)
    {
        if (knownAt is { } observation && observation > clock.Now)
        {
            throw Error("CFG-VALIDATION", "knownAt cannot be in the future.");
        }

        if (hash is { } requested)
        {
            return await states.ByHashAsync(requested, cancellationToken).ConfigureAwait(false)
                ?? throw Error("CFG-HASH-UNKNOWN", $"Configuration state {requested} was never recorded by this stamp.");
        }

        if (knownAt is { } known)
        {
            return await states.AtAsync(known, cancellationToken).ConfigureAwait(false)
                ?? throw Error("NOT-AVAILABLE", "knownAt is earlier than the first recorded configuration state.");
        }

        // An event or command's pin is as binding as an explicit hash. Older unrecorded hashes fail closed (D-SL5-06).
        if (pinned is { } unitOfWork)
        {
            return await states.ByHashAsync(unitOfWork, cancellationToken).ConfigureAwait(false)
                ?? throw Error("CFG-HASH-UNKNOWN", $"Configuration state {unitOfWork} was never recorded by this stamp.");
        }

        return await states.CurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConfigurationResolveResponse> ResolveAsync(
        ConfigurationResolveRequest request, ValidAt? validAt, Instant? knownAt, ConfigurationHash? pinned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalogue = await StateAsync(request.ConfigurationHash, knownAt, pinned, cancellationToken).ConfigureAwait(false);
        return Resolve(catalogue, request, validAt);
    }

    /// <summary>Resolves against the fixed state of an engine built over one catalogue (tests); persisted engines use <see cref="ResolveAsync"/>.</summary>
    public ConfigurationResolveResponse Resolve(ConfigurationResolveRequest request, ValidAt? validAt, Instant? knownAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(FixedState(request.ConfigurationHash, knownAt), request, validAt);
    }

    internal ConfigurationCatalogue FixedState(ConfigurationHash? hash, Instant? knownAt)
    {
        if (states is not StaticConfigurationStates)
        {
            throw new InvalidOperationException("This engine serves persisted states: use the async members.");
        }

        return StateAsync(hash, knownAt, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal ConfigurationResolveResponse Resolve(ConfigurationCatalogue catalogue, ConfigurationResolveRequest request, ValidAt? validAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = EntityOf(request.LegalEntity);
        if ((request.Keys is null) == (request.Namespace is null))
        {
            throw Error("CFG-VALIDATION", "Give exactly one of keys or namespace (REQ-MKT-044).");
        }

        var date = validAt is { Date: { } day } ? day
            : validAt is { Instant: { } at } ? at.ToBusinessDate(entity.Zone)
            : clock.Now.ToBusinessDate(entity.Zone);
        var keys = request.Keys is { } requested
            ? requested.Distinct(StringComparer.Ordinal).ToList()
            : KeysOfNamespace(catalogue, request.Namespace!);
        if (keys.Any(k => k.StartsWith(TaxTreatmentRules.TreatmentNamespace, StringComparison.Ordinal)))
        {
            throw Error("CFG-VALIDATION", "tax.treatment.* keys are read only by the TaxCalculator (ARCH-11, REQ-MKT-332).");
        }

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

    /// <summary>The current state's hash and activation instant (cached at most 1 s).</summary>
    public async Task<ConfigurationCurrentHashResponse> CurrentHashAsync(CancellationToken cancellationToken)
    {
        var current = await states.CurrentAsync(cancellationToken).ConfigureAwait(false);
        return new ConfigurationCurrentHashResponse { Hash = current.Hash.Hash, ActivatedAt = current.ActivatedAt };
    }

    public ConfigurationCurrentHashResponse CurrentHash()
    {
        var current = FixedState(null, null);
        return new ConfigurationCurrentHashResponse { Hash = current.Hash.Hash, ActivatedAt = current.ActivatedAt };
    }

    public async Task<RoundingApplyResponse> ApplyRoundingAsync(RoundingApplyRequest request, ConfigurationHash? pinned, CancellationToken cancellationToken)
    {
        var catalogue = await StateAsync(null, null, pinned, cancellationToken).ConfigureAwait(false);
        return ApplyRounding(catalogue, request);
    }

    public RoundingApplyResponse ApplyRounding(RoundingApplyRequest request) => ApplyRounding(FixedState(null, null), request);

    internal RoundingApplyResponse ApplyRounding(ConfigurationCatalogue catalogue, RoundingApplyRequest request)
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

    private static List<string> KeysOfNamespace(ConfigurationCatalogue catalogue, string prefix)
    {
        var dotted = prefix.EndsWith('.') ? prefix : prefix + ".";
        var keys = catalogue.Entries.Select(e => e.Key)
            .Concat(ConfigKeys.All.Where(d => !d.IsPrefix).Select(d => d.Key))
            .Where(k => k.StartsWith(dotted, StringComparison.Ordinal) && !k.StartsWith(TaxTreatmentRules.TreatmentNamespace, StringComparison.Ordinal))
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
