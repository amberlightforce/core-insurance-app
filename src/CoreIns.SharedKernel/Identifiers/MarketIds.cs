using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a LegalEntity (MKT; its human code is LegalEntityCode); owner MKT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<LegalEntityId>))]
public readonly record struct LegalEntityId(Guid Value) : IEntityId<LegalEntityId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static LegalEntityId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static LegalEntityId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a configuration value binding; owner MKT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ConfigBindingId>))]
public readonly record struct ConfigBindingId(Guid Value) : IEntityId<ConfigBindingId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ConfigBindingId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ConfigBindingId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a PackVersion; owner MKT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PackVersionId>))]
public readonly record struct PackVersionId(Guid Value) : IEntityId<PackVersionId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PackVersionId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PackVersionId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a regime CodeList version; owner MKT.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CodeListId>))]
public readonly record struct CodeListId(Guid Value) : IEntityId<CodeListId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static CodeListId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static CodeListId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
