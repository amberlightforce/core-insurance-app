using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a RatingRequest; owner RAT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RatingRequestId>))]
public readonly record struct RatingRequestId(Guid Value) : IEntityId<RatingRequestId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RatingRequestId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RatingRequestId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a RatingResult; owner RAT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RatingResultId>))]
public readonly record struct RatingResultId(Guid Value) : IEntityId<RatingResultId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RatingResultId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RatingResultId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Worksheet; owner RAT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<WorksheetId>))]
public readonly record struct WorksheetId(Guid Value) : IEntityId<WorksheetId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static WorksheetId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static WorksheetId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a RateTable; owner RAT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RateTableId>))]
public readonly record struct RateTableId(Guid Value) : IEntityId<RateTableId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RateTableId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RateTableId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
