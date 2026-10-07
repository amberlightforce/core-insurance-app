using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a Template; owner DOC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<TemplateId>))]
public readonly record struct TemplateId(Guid Value) : IEntityId<TemplateId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static TemplateId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static TemplateId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Clause; owner DOC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ClauseId>))]
public readonly record struct ClauseId(Guid Value) : IEntityId<ClauseId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ClauseId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ClauseId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a DocumentType; owner DOC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<DocumentTypeId>))]
public readonly record struct DocumentTypeId(Guid Value) : IEntityId<DocumentTypeId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static DocumentTypeId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static DocumentTypeId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a outbound Document; owner DOC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<DocumentId>))]
public readonly record struct DocumentId(Guid Value) : IEntityId<DocumentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static DocumentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static DocumentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Delivery; owner DOC.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<DeliveryId>))]
public readonly record struct DeliveryId(Guid Value) : IEntityId<DeliveryId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static DeliveryId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static DeliveryId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
