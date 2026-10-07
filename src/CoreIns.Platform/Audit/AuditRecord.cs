using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Platform.Audit;

/// <summary>Outcome of an audited action.</summary>
public enum AuditOutcome
{
    /// <summary>The action took effect.</summary>
    Succeeded,

    /// <summary>The action was refused (validation, authority, state, business rule); nothing changed.</summary>
    Rejected,

    /// <summary>The action failed unexpectedly; nothing changed.</summary>
    Failed,
}

/// <summary>
/// One field-level change (contract §3.9.1 "before/after at field level"). P2/P3 values are not stored in clear: until
/// per-subject field encryption exists (D-ARC-14, W1-PLT), they are recorded as redacted.
/// </summary>
/// <param name="Field">JSON path of the field.</param>
/// <param name="Before">Value before (JSON), or null.</param>
/// <param name="After">Value after (JSON), or null.</param>
/// <param name="Classification">Personal-data class of the field.</param>
public sealed record AuditChange(string Field, JsonNode? Before, JsonNode? After, DataClassification Classification = DataClassification.P0);

/// <summary>
/// An audit record (PRD-14 AuditEvent; ADR §2 rule 9; D-ARC-15): who (actor, on-behalf-of, roles), with which
/// authority, did what (operation) to which object, before/after values, reason, outcome, trace and lineage keys,
/// origin, legal entity and time. Appended to the insert-only, hash-chained <c>plt.audit_event</c>.
/// </summary>
public sealed record AuditRecord
{
    /// <summary>UUIDv7 id.</summary>
    public AuditEventId Id { get; init; } = AuditEventId.New();

    /// <summary>Who acted.</summary>
    public required ActorRef Actor { get; init; }

    /// <summary>For whom (AI agent / service acting for a person).</summary>
    public ActorRef? OnBehalfOf { get; init; }

    /// <summary>Roles of the actor.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The authority check the action relied on.</summary>
    public AuthorityCheckId? AuthorityCheckId { get; init; }

    /// <summary>The authority type and grant used (e.g. <c>CLM_RESERVE:grant-…</c>).</summary>
    public string? AuthorityUsed { get; init; }

    /// <summary>Operation name.</summary>
    public required OperationName Operation { get; init; }

    /// <summary>Outcome.</summary>
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Succeeded;

    /// <summary>Error code when rejected or failed.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The object acted on.</summary>
    public ObjectRef? ObjectRef { get; init; }

    /// <summary>The object's business number, if any.</summary>
    public string? ObjectNumber { get; init; }

    /// <summary>Field-level changes.</summary>
    public IReadOnlyList<AuditChange> Changes { get; init; } = [];

    /// <summary>Reason given by the actor.</summary>
    public string? Reason { get; init; }

    /// <summary>Channel code.</summary>
    public string? Channel { get; init; }

    /// <summary>W3C trace id.</summary>
    public required CorrelationId CorrelationId { get; init; }

    /// <summary>Causing event or command.</summary>
    public Guid? CausationId { get; init; }

    /// <summary>AI interaction behind the action, if any.</summary>
    public AiInteractionId? AiInteractionId { get; init; }

    /// <summary>Lineage keys of the affected object (REQ-PLT-363).</summary>
    public BusinessKeys BusinessKeys { get; init; } = BusinessKeys.Empty;

    /// <summary>LIVE, MIGRATION or REPLAY (REQ-PLT-345).</summary>
    public EventOrigin Origin { get; init; } = EventOrigin.Live;

    /// <summary>Legal entity.</summary>
    public required LegalEntityCode LegalEntity { get; init; }

    /// <summary>Jurisdiction.</summary>
    public required Jurisdiction Jurisdiction { get; init; }

    /// <summary>Business time of the action.</summary>
    public required Instant OccurredAt { get; init; }

    /// <summary>Record time (set when appended).</summary>
    public Instant RecordedAt { get; init; }

    /// <summary>The changes as stored: a JSON array of <c>{field, before, after, pdClass}</c>, P2/P3 values redacted.</summary>
    public string ChangesJson()
    {
        var array = new JsonArray();
        foreach (var change in Changes)
        {
            var redact = change.Classification >= DataClassification.P2;
            var item = new JsonObject
            {
                ["field"] = change.Field,
                ["before"] = redact ? Redacted() : change.Before?.DeepClone(),
                ["after"] = redact ? Redacted() : change.After?.DeepClone(),
                ["pdClass"] = change.Classification.ToString(),
            };
            array.Add(item);
        }

        return array.ToJsonString();

        static JsonObject Redacted() => new() { ["redacted"] = true };
    }

    /// <summary>The lineage keys as stored (JSON object).</summary>
    public string BusinessKeysJson() => JsonSerializer.Serialize(BusinessKeys, SharedKernelJson.Options);
}

/// <summary>Builds field-level before/after changes by comparing two JSON-serialisable snapshots.</summary>
public static class AuditDiff
{
    /// <summary>
    /// The changed leaf fields between <paramref name="before"/> and <paramref name="after"/> (either may be null for
    /// create/delete), as JSON paths. <paramref name="classify"/> gives each field's personal-data class (default P0).
    /// </summary>
    public static IReadOnlyList<AuditChange> Compute(object? before, object? after, Func<string, DataClassification>? classify = null)
    {
        var left = before is null ? null : JsonSerializer.SerializeToNode(before, before.GetType(), SharedKernelJson.Options);
        var right = after is null ? null : JsonSerializer.SerializeToNode(after, after.GetType(), SharedKernelJson.Options);
        var changes = new List<AuditChange>();
        Walk("$", left, right, changes, classify ?? (_ => DataClassification.P0));
        return changes;
    }

    private static void Walk(string path, JsonNode? left, JsonNode? right, List<AuditChange> changes, Func<string, DataClassification> classify)
    {
        if (left is JsonObject l && right is JsonObject r)
        {
            foreach (var name in l.Select(p => p.Key).Union(r.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                Walk($"{path}.{name}", l[name], r[name], changes, classify);
            }

            return;
        }

        if (!JsonNode.DeepEquals(left, right))
        {
            changes.Add(new AuditChange(path, left?.DeepClone(), right?.DeepClone(), classify(path)));
        }
    }
}
