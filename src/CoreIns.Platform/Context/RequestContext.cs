using System.Diagnostics;
using System.Text.Json.Serialization;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Platform.Context;

/// <summary>Where a fact came from (contract §3.4.1 <c>origin</c>, D-CON-09); carried on events and audit records.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventOrigin>))]
public enum EventOrigin
{
    /// <summary>Normal business.</summary>
    [JsonStringEnumMemberName("LIVE")]
    Live,

    /// <summary>Converted business (migration import).</summary>
    [JsonStringEnumMemberName("MIGRATION")]
    Migration,

    /// <summary>Re-emitted by the replay command.</summary>
    [JsonStringEnumMemberName("REPLAY")]
    Replay,
}

/// <summary>Wire codes of <see cref="EventOrigin"/>.</summary>
public static class EventOrigins
{
    /// <summary>LIVE, MIGRATION or REPLAY.</summary>
    public static string ToCode(this EventOrigin origin) => origin switch
    {
        EventOrigin.Live => "LIVE",
        EventOrigin.Migration => "MIGRATION",
        EventOrigin.Replay => "REPLAY",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };

    /// <summary>Parses LIVE, MIGRATION or REPLAY.</summary>
    public static EventOrigin Parse(string code) => code switch
    {
        "LIVE" => EventOrigin.Live,
        "MIGRATION" => EventOrigin.Migration,
        "REPLAY" => EventOrigin.Replay,
        _ => throw new FormatException($"'{code}' is not an origin (LIVE, MIGRATION, REPLAY)."),
    };
}

/// <summary>
/// Who is acting, for which legal entity, under which trace, with which idempotency key: the per-scope context that the
/// command pipeline, the event publisher and the audit writer read. One instance per DI scope (HTTP request, event
/// handler invocation, job). HTTP requests fill it in <c>RequestContextMiddleware</c>; the outbox dispatcher fills it
/// for handlers (actor = the handler service, causation = the event).
/// </summary>
public sealed class RequestContext
{
    /// <summary>The acting user, service or AI agent. Defaults to the platform service identity.</summary>
    public ActorRef Actor { get; set; } = ActorRef.Service("coreins");

    /// <summary>The person an AI agent or service acts for, if any.</summary>
    public ActorRef? OnBehalfOf { get; set; }

    /// <summary>Application roles of the actor (Entra app roles).</summary>
    public IReadOnlyCollection<string> Roles { get; set; } = [];

    /// <summary>Legal entity of the work (defaults to the stamp's legal entity).</summary>
    public LegalEntityCode? LegalEntity { get; set; }

    /// <summary>Jurisdiction whose rules apply (defaults to the stamp's country).</summary>
    public Jurisdiction? Jurisdiction { get; set; }

    /// <summary>W3C trace id (technical only, D-CON-01).</summary>
    public CorrelationId CorrelationId { get; set; } = CurrentTraceId();

    /// <summary>The event or command that caused this work, if any.</summary>
    public Guid? CausationId { get; set; }

    /// <summary>LIVE, MIGRATION or REPLAY.</summary>
    public EventOrigin Origin { get; set; } = EventOrigin.Live;

    /// <summary>The AI interaction whose accepted suggestion this work applies, if any.</summary>
    public AiInteractionId? AiInteractionId { get; set; }

    /// <summary>Channel code (CHN code list: STAFF, WEB_DIRECT, …).</summary>
    public string? Channel { get; set; }

    /// <summary>Language for messages (el default, en).</summary>
    public Language Language { get; set; } = Language.El;

    /// <summary>The caller's <c>Idempotency-Key</c>, required for state-changing commands.</summary>
    public IdempotencyKey? IdempotencyKey { get; set; }

    /// <summary>True for <c>?dryRun=true</c> / <c>X-Dry-Run: true</c>: compute the full result, then roll back.</summary>
    public bool DryRun { get; set; }

    /// <summary>The reason the actor gave, where one is required.</summary>
    public string? Reason { get; set; }

    /// <summary>The configuration hash pinned for this unit of work (REQ-MKT-051 hash pinning).</summary>
    public ConfigurationHash? ConfigurationHash { get; set; }

    /// <summary>Authority checks made during this unit of work (the audit records the one used).</summary>
    public IList<AuthorityCheckResult> AuthorityChecks { get; } = [];

    /// <summary>The trace id of the ambient <see cref="Activity"/>, or a new one.</summary>
    public static CorrelationId CurrentTraceId() =>
        Activity.Current is { } activity
        && activity.TraceId != default
        && CorrelationId.TryParse(activity.TraceId.ToHexString(), out var id)
            ? id
            : CorrelationId.New();
}
