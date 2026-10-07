using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a ProductLine; owner PFC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ProductLineId>))]
public readonly record struct ProductLineId(Guid Value) : IEntityId<ProductLineId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ProductLineId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ProductLineId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Product; owner PFC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ProductId>))]
public readonly record struct ProductId(Guid Value) : IEntityId<ProductId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ProductId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ProductId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ProductVersion; owner PFC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ProductVersionId>))]
public readonly record struct ProductVersionId(Guid Value) : IEntityId<ProductVersionId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ProductVersionId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ProductVersionId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a CoverageDef; owner PFC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CoverageDefId>))]
public readonly record struct CoverageDefId(Guid Value) : IEntityId<CoverageDefId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static CoverageDefId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static CoverageDefId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ChargeType; owner PFC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ChargeTypeId>))]
public readonly record struct ChargeTypeId(Guid Value) : IEntityId<ChargeTypeId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ChargeTypeId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ChargeTypeId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a QuestionSet; owner PFC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<QuestionSetId>))]
public readonly record struct QuestionSetId(Guid Value) : IEntityId<QuestionSetId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static QuestionSetId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static QuestionSetId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
