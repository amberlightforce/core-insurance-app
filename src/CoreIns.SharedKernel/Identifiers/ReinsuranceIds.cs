using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a RI Programme; owner RI.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RiProgrammeId>))]
public readonly record struct RiProgrammeId(Guid Value) : IEntityId<RiProgrammeId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RiProgrammeId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RiProgrammeId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a RI Contract; owner RI.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RiContractId>))]
public readonly record struct RiContractId(Guid Value) : IEntityId<RiContractId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RiContractId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RiContractId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a RI Layer; owner RI.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RiLayerId>))]
public readonly record struct RiLayerId(Guid Value) : IEntityId<RiLayerId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RiLayerId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RiLayerId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a RI Participation; owner RI.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RiParticipationId>))]
public readonly record struct RiParticipationId(Guid Value) : IEntityId<RiParticipationId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RiParticipationId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RiParticipationId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Cession; owner RI.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CessionId>))]
public readonly record struct CessionId(Guid Value) : IEntityId<CessionId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static CessionId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static CessionId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a RI recovery (not a claim recovery); owner RI.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RiRecoveryId>))]
public readonly record struct RiRecoveryId(Guid Value) : IEntityId<RiRecoveryId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RiRecoveryId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RiRecoveryId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
