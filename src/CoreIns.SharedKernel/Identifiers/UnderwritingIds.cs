using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a UwIssue; owner UW.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<UwIssueId>))]
public readonly record struct UwIssueId(Guid Value) : IEntityId<UwIssueId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static UwIssueId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static UwIssueId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Referral; owner UW.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ReferralId>))]
public readonly record struct ReferralId(Guid Value) : IEntityId<ReferralId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ReferralId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ReferralId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a PolicyHold; owner UW.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PolicyHoldId>))]
public readonly record struct PolicyHoldId(Guid Value) : IEntityId<PolicyHoldId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PolicyHoldId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PolicyHoldId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
