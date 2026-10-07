using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a User profile (on top of Entra ID); owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<UserId>))]
public readonly record struct UserId(Guid Value) : IEntityId<UserId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static UserId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static UserId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Role; owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RoleId>))]
public readonly record struct RoleId(Guid Value) : IEntityId<RoleId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RoleId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RoleId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a AuthorityGrant; owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AuthorityGrantId>))]
public readonly record struct AuthorityGrantId(Guid Value) : IEntityId<AuthorityGrantId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static AuthorityGrantId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static AuthorityGrantId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a AuthorityCheckResult; owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AuthorityCheckId>))]
public readonly record struct AuthorityCheckId(Guid Value) : IEntityId<AuthorityCheckId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static AuthorityCheckId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static AuthorityCheckId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ApprovalRequest (maker-checker); owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ApprovalRequestId>))]
public readonly record struct ApprovalRequestId(Guid Value) : IEntityId<ApprovalRequestId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ApprovalRequestId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ApprovalRequestId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a AuditEvent; owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AuditEventId>))]
public readonly record struct AuditEventId(Guid Value) : IEntityId<AuditEventId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static AuditEventId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static AuditEventId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a event envelope eventId; consumers are idempotent on it; owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<EventId>))]
public readonly record struct EventId(Guid Value) : IEntityId<EventId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static EventId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static EventId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a AiInteractionRecord; owner PLT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AiInteractionId>))]
public readonly record struct AiInteractionId(Guid Value) : IEntityId<AiInteractionId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static AiInteractionId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static AiInteractionId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
