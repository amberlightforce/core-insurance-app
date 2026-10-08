using System.Collections.Concurrent;
using System.Globalization;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace CoreIns.Platform.Authority;

/// <summary>Outcome of an authority check (REQ-PLT-003/103: one enum ALLOW | REFER | DENY).</summary>
public enum AuthorityDecision
{
    /// <summary>The actor holds enough authority.</summary>
    Allow,

    /// <summary>The actor has authority of this type but not enough; refer to the referral targets.</summary>
    Refer,

    /// <summary>No authority of this type.</summary>
    Deny,
}

/// <summary>Kind of an authority dimension (PRD-14 AuthorityType dimensions).</summary>
public enum DimensionKind
{
    /// <summary>Amount and currency (amount, sum insured).</summary>
    Money,

    /// <summary>A decimal number (deviation %).</summary>
    Number,

    /// <summary>A code (product, LoB, territory, transaction type).</summary>
    Code,

    /// <summary>A yes/no flag.</summary>
    Flag,
}

/// <summary>How a limit compares (PRD-14 AuthorityLimit comparison).</summary>
public enum LimitComparison
{
    /// <summary>Value ≤ limit.</summary>
    AtMost,

    /// <summary>Value ≥ limit.</summary>
    AtLeast,

    /// <summary>Value is one of the listed codes.</summary>
    InSet,

    /// <summary>The flag must be granted (value true).</summary>
    Flag,

    /// <summary>Any value.</summary>
    Any,
}

/// <summary>A dimension value given to a check or held by a limit.</summary>
public sealed record DimensionValue
{
    private DimensionValue(DimensionKind kind)
    {
        Kind = kind;
    }

    /// <summary>Kind.</summary>
    public DimensionKind Kind { get; }

    /// <summary>Money value.</summary>
    public Money? Money { get; private init; }

    /// <summary>Number value.</summary>
    public decimal? Number { get; private init; }

    /// <summary>Code values (one for a check, several for an IN_SET limit).</summary>
    public IReadOnlyList<string> Codes { get; private init; } = [];

    /// <summary>Flag value.</summary>
    public bool? Flag { get; private init; }

    /// <summary>A money value.</summary>
    public static DimensionValue Of(Money money) => new(DimensionKind.Money) { Money = money };

    /// <summary>A decimal value.</summary>
    public static DimensionValue Of(decimal value) => new(DimensionKind.Number) { Number = value };

    /// <summary>A code value (or a set of codes for a limit).</summary>
    public static DimensionValue OfCodes(params string[] codes) => new(DimensionKind.Code) { Codes = codes };

    /// <summary>A flag value.</summary>
    public static DimensionValue Of(bool flag) => new(DimensionKind.Flag) { Flag = flag };

    /// <summary>Invariant text.</summary>
    public override string ToString() => Kind switch
    {
        DimensionKind.Money => Money?.ToString() ?? string.Empty,
        DimensionKind.Number => Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        DimensionKind.Code => string.Join('|', Codes),
        _ => Flag?.ToString() ?? string.Empty,
    };
}

/// <summary>A dimension declared by an authority type.</summary>
/// <param name="Name">Dimension name (lower camel case, e.g. <c>amount</c>).</param>
/// <param name="Kind">Value kind.</param>
public sealed record AuthorityDimensionDefinition(string Name, DimensionKind Kind);

/// <summary>An authority type registered by its owning module (CD-05, <c>plt.AuthorityType.register</c>).</summary>
/// <param name="Code">Type code.</param>
/// <param name="OwningModule">Module that registers and checks it.</param>
/// <param name="Name">Name in Greek and English.</param>
/// <param name="Dimensions">Dimensions a check may carry.</param>
public sealed record AuthorityTypeDefinition(
    AuthorityTypeCode Code, ModuleCode OwningModule, LocalizedText Name, IReadOnlyList<AuthorityDimensionDefinition> Dimensions);

/// <summary>One limit of a grant.</summary>
/// <param name="Dimension">Dimension name.</param>
/// <param name="Comparison">Comparison.</param>
/// <param name="Value">Limit value (null for <see cref="LimitComparison.Any"/>).</param>
public sealed record AuthorityLimit(string Dimension, LimitComparison Comparison, DimensionValue? Value);

/// <summary>Who a referral can go to.</summary>
/// <param name="Kind"><c>ROLE</c> or <c>USER</c>.</param>
/// <param name="Id">Role code or user id.</param>
public sealed record ReferralTarget(string Kind, string Id);

/// <summary>A check request (REQ-PLT-003; D-API-08 names the instant <c>validAt</c>).</summary>
/// <param name="Actor">Who wants to act.</param>
/// <param name="Roles">The actor's roles.</param>
/// <param name="Type">Authority type.</param>
/// <param name="Dimensions">Values of the action by dimension name.</param>
/// <param name="ObjectRef">The object acted on.</param>
/// <param name="ValidAt">The business instant the authority must be valid at.</param>
public sealed record AuthorityCheckRequest(
    ActorRef Actor,
    IReadOnlyCollection<string> Roles,
    AuthorityTypeCode Type,
    IReadOnlyDictionary<string, DimensionValue> Dimensions,
    ObjectRef? ObjectRef,
    Instant ValidAt);

/// <summary>The result of <c>plt.Authority.check</c>: decision, reason code, applicable limits and their source grant, referral targets and check id.</summary>
/// <param name="CheckId">Check id (recorded on the audit record of the action).</param>
/// <param name="Type">Authority type.</param>
/// <param name="Decision">Allow, refer or deny.</param>
/// <param name="ReasonCode">Reason, e.g. <c>AMOUNT_ABOVE_LIMIT</c>, <c>NO_GRANT</c>, <c>WITHIN_LIMIT</c>.</param>
/// <param name="ApplicableLimits">Limits of the grant that decided.</param>
/// <param name="SourceGrant">Grant that decided (allow) or that was exceeded (refer).</param>
/// <param name="ReferralTargets">Holders of enough authority (refer).</param>
/// <param name="ValidAt">Instant checked.</param>
/// <param name="CheckedAt">When the check ran.</param>
public sealed record AuthorityCheckResult(
    AuthorityCheckId CheckId,
    AuthorityTypeCode Type,
    AuthorityDecision Decision,
    string ReasonCode,
    IReadOnlyList<AuthorityLimit> ApplicableLimits,
    string? SourceGrant,
    IReadOnlyList<ReferralTarget> ReferralTargets,
    Instant ValidAt,
    Instant CheckedAt);

/// <summary>The authority framework (<c>plt.Authority.check</c>, CD-05).</summary>
public interface IAuthorityService
{
    /// <summary>Checks whether the actor may act; throws <see cref="DomainException"/> <c>PLT-ERR-UNKNOWN-TYPE</c> for an unregistered type.</summary>
    Task<AuthorityCheckResult> CheckAsync(AuthorityCheckRequest request, CancellationToken cancellationToken);
}

/// <summary>Registered authority types (<c>plt.AuthorityType.register</c>).</summary>
public interface IAuthorityTypeRegistry
{
    /// <summary>Registers a type; registering the same definition again is a no-op, a different one fails with <c>PLT-ERR-TYPE-EXISTS</c>.</summary>
    void Register(AuthorityTypeDefinition definition);

    /// <summary>Looks up a type.</summary>
    bool TryGet(AuthorityTypeCode code, out AuthorityTypeDefinition definition);

    /// <summary>All registered types.</summary>
    IReadOnlyCollection<AuthorityTypeDefinition> All { get; }
}

/// <summary>In-memory registry fed by module registrations at start-up.</summary>
internal sealed class AuthorityTypeRegistry : IAuthorityTypeRegistry
{
    private readonly ConcurrentDictionary<AuthorityTypeCode, AuthorityTypeDefinition> _types = new();

    public AuthorityTypeRegistry(IEnumerable<AuthorityTypeDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            Register(definition);
        }
    }

    public IReadOnlyCollection<AuthorityTypeDefinition> All => [.. _types.Values];

    public void Register(AuthorityTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var stored = _types.GetOrAdd(definition.Code, definition);
        if (!ReferenceEquals(stored, definition) && !SameDefinition(stored, definition))
        {
            throw new DomainException(new DomainError(
                ErrorCode.For(ModuleCode.PLT, PlatformErrors.AuthorityTypeExists),
                $"Authority type {definition.Code} is already registered by {stored.OwningModule}."));
        }
    }

    public bool TryGet(AuthorityTypeCode code, out AuthorityTypeDefinition definition) => _types.TryGetValue(code, out definition!);

    private static bool SameDefinition(AuthorityTypeDefinition a, AuthorityTypeDefinition b) =>
        a.Code == b.Code && a.OwningModule == b.OwningModule && a.Name == b.Name && a.Dimensions.SequenceEqual(b.Dimensions);
}

/// <summary>Configuration of grants for the default authority service (<c>Platform:Authority</c>).</summary>
public sealed class AuthorityOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Platform:Authority";

    /// <summary>The grants.</summary>
    public IList<AuthorityGrantOptions> Grants { get; } = [];

    /// <summary>
    /// Illustrative grants are test data (e.g. the claims limits of D-SL2-03): they never apply in Production, where the
    /// affected checks fail closed (no grant → DENY) until approved limits are configured. Without a known host
    /// environment they are dropped as well (fail closed).
    /// </summary>
    public static void DropIllustrativeIn(AuthorityOptions options, Microsoft.Extensions.Hosting.IHostEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment is not null && !Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsProduction(environment))
        {
            return;
        }

        foreach (var grant in options.Grants.Where(g => g.Illustrative).ToList())
        {
            options.Grants.Remove(grant);
        }
    }
}

/// <summary>One configured grant: a role or user, an authority type, limits and a validity window.</summary>
public sealed class AuthorityGrantOptions
{
    /// <summary>Grant id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Grantee role (Entra app role), or null.</summary>
    public string? Role { get; set; }

    /// <summary>Grantee user id, or null.</summary>
    public string? User { get; set; }

    /// <summary>Authority type code.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Valid from (RFC 3339 UTC), inclusive; null = always.</summary>
    public string? ValidFrom { get; set; }

    /// <summary>Valid to (RFC 3339 UTC), exclusive; null = open.</summary>
    public string? ValidTo { get; set; }

    /// <summary>Limits; all must pass.</summary>
    public IList<AuthorityLimitOptions> Limits { get; } = [];

    /// <summary>True for illustrative test data (not an approved limit); ignored in Production.</summary>
    public bool Illustrative { get; set; }
}

/// <summary>One configured limit.</summary>
public sealed class AuthorityLimitOptions
{
    /// <summary>Dimension name.</summary>
    public string Dimension { get; set; } = string.Empty;

    /// <summary>Comparison.</summary>
    public LimitComparison Comparison { get; set; } = LimitComparison.Any;

    /// <summary>Amount (decimal string) for money and decimal limits.</summary>
    public string? Amount { get; set; }

    /// <summary>Currency for money limits.</summary>
    public string? Currency { get; set; }

    /// <summary>Codes for IN_SET limits.</summary>
    public IList<string> Codes { get; } = [];
}

/// <summary>
/// The default, deterministic, configuration-backed authority service: grants come from <see cref="AuthorityOptions"/>.
/// Persistent profiles, limits, delegations, FX conversion of monetary limits and stored check results are W1-PLT;
/// until then a monetary limit in another currency refers (<c>CURRENCY_NOT_CONVERTIBLE</c>). AI agents are never
/// granted authority (REQ-PLT-111).
/// </summary>
internal sealed class ConfiguredAuthorityService(IAuthorityTypeRegistry registry, IOptions<AuthorityOptions> options, IClock clock) : IAuthorityService
{
    public Task<AuthorityCheckResult> CheckAsync(AuthorityCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!registry.TryGet(request.Type, out _))
        {
            throw new DomainException(new DomainError(
                ErrorCode.For(ModuleCode.PLT, PlatformErrors.UnknownAuthorityType), $"Authority type {request.Type} is not registered."));
        }

        var now = clock.Now;
        AuthorityCheckResult Result(AuthorityDecision decision, string reason, Grant? grant, IReadOnlyList<ReferralTarget> referrals) =>
            new(AuthorityCheckId.New(), request.Type, decision, reason, grant?.Limits ?? [], grant?.Id, referrals, request.ValidAt, now);

        if (request.Actor.Kind == ActorKind.AiAgent)
        {
            return Task.FromResult(Result(AuthorityDecision.Deny, "AI_ACTOR_NOT_GRANTED", null, []));
        }

        var grants = Grants()
            .Where(g => g.Type == request.Type && g.IsValidAt(request.ValidAt))
            .OrderBy(g => g.Id, StringComparer.Ordinal)
            .ToList();
        var own = grants.Where(g => g.Covers(request.Actor, request.Roles)).ToList();
        if (own.Count == 0)
        {
            return Task.FromResult(Result(AuthorityDecision.Deny, "NO_GRANT", null, []));
        }

        string? firstFailure = null;
        Grant? exceeded = null;
        foreach (var grant in own)
        {
            var failure = grant.Evaluate(request.Dimensions);
            if (failure is null)
            {
                return Task.FromResult(Result(AuthorityDecision.Allow, "WITHIN_LIMIT", grant, []));
            }

            firstFailure ??= failure;
            exceeded ??= grant;
        }

        var referrals = grants
            .Where(g => !g.Covers(request.Actor, request.Roles) && g.Evaluate(request.Dimensions) is null)
            .Select(g => g.Role is { } role ? new ReferralTarget("ROLE", role) : new ReferralTarget("USER", g.User!))
            .Distinct()
            .ToList();
        return Task.FromResult(referrals.Count > 0
            ? Result(AuthorityDecision.Refer, firstFailure!, exceeded, referrals)
            : Result(AuthorityDecision.Deny, firstFailure!, exceeded, []));
    }

    private IEnumerable<Grant> Grants() => options.Value.Grants.Select(Grant.Create);

    private sealed record Grant(string Id, string? Role, string? User, AuthorityTypeCode Type, Instant? From, Instant? To, IReadOnlyList<AuthorityLimit> Limits)
    {
        public static Grant Create(AuthorityGrantOptions o)
        {
            if (o.Role is null == o.User is null)
            {
                throw new InvalidOperationException($"Authority grant '{o.Id}' needs exactly one of Role or User.");
            }

            return new Grant(
                o.Id,
                o.Role,
                o.User,
                AuthorityTypeCode.Parse(o.Type),
                o.ValidFrom is null ? null : Instant.Parse(o.ValidFrom),
                o.ValidTo is null ? null : Instant.Parse(o.ValidTo),
                [.. o.Limits.Select(ToLimit)]);
        }

        public bool IsValidAt(Instant at) => (From is null || at >= From.Value) && (To is null || at < To.Value);

        public bool Covers(ActorRef actor, IReadOnlyCollection<string> roles) =>
            (User is not null && actor.Kind == ActorKind.User && string.Equals(User, actor.Id, StringComparison.Ordinal))
            || (Role is not null && roles.Contains(Role, StringComparer.Ordinal));

        /// <summary>Null when every limit passes, otherwise the reason code of the first failing limit.</summary>
        public string? Evaluate(IReadOnlyDictionary<string, DimensionValue> dimensions)
        {
            foreach (var limit in Limits.Where(l => l.Comparison != LimitComparison.Any))
            {
                var name = limit.Dimension.ToUpperInvariant();
                if (!dimensions.TryGetValue(limit.Dimension, out var value))
                {
                    return $"{name}_MISSING";
                }

                var failure = limit.Comparison switch
                {
                    LimitComparison.AtMost => Compare(value, limit.Value!) is { } c ? (c <= 0 ? null : $"{name}_ABOVE_LIMIT") : "CURRENCY_NOT_CONVERTIBLE",
                    LimitComparison.AtLeast => Compare(value, limit.Value!) is { } c ? (c >= 0 ? null : $"{name}_BELOW_LIMIT") : "CURRENCY_NOT_CONVERTIBLE",
                    LimitComparison.InSet => value.Codes.Count == 1 && limit.Value!.Codes.Contains(value.Codes[0], StringComparer.Ordinal) ? null : $"{name}_NOT_ALLOWED",
                    LimitComparison.Flag => value.Flag == true ? null : $"{name}_NOT_GRANTED",
                    _ => null,
                };
                if (failure is not null)
                {
                    return failure;
                }
            }

            return null;
        }

        private static int? Compare(DimensionValue value, DimensionValue limit) => (value.Kind, limit.Kind) switch
        {
            (DimensionKind.Money, DimensionKind.Money) when value.Money!.Value.Currency == limit.Money!.Value.Currency =>
                value.Money.Value.Amount.CompareTo(limit.Money.Value.Amount),
            (DimensionKind.Number, DimensionKind.Number) => value.Number!.Value.CompareTo(limit.Number!.Value),
            _ => null,
        };

        private static AuthorityLimit ToLimit(AuthorityLimitOptions o) => new(
            o.Dimension,
            o.Comparison,
            o.Comparison switch
            {
                LimitComparison.Any => null,
                LimitComparison.Flag => DimensionValue.Of(true),
                LimitComparison.InSet => DimensionValue.OfCodes([.. o.Codes]),
                _ when o.Currency is not null => DimensionValue.Of(new Money(DecimalText.Parse(o.Amount ?? string.Empty), SharedKernel.Currency.FromCode(o.Currency))),
                _ => DimensionValue.Of(DecimalText.Parse(o.Amount ?? string.Empty)),
            });
    }
}
