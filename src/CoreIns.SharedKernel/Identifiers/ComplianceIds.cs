using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a StatutoryClock instance; owner CMP.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<StatutoryClockId>))]
public readonly record struct StatutoryClockId(Guid Value) : IEntityId<StatutoryClockId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static StatutoryClockId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static StatutoryClockId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a FiscalDocument; owner CMP.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<FiscalDocumentId>))]
public readonly record struct FiscalDocumentId(Guid Value) : IEntityId<FiscalDocumentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static FiscalDocumentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static FiscalDocumentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Complaint; owner CMP.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ComplaintId>))]
public readonly record struct ComplaintId(Guid Value) : IEntityId<ComplaintId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ComplaintId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ComplaintId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a DSAR request; owner CMP.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<DsarId>))]
public readonly record struct DsarId(Guid Value) : IEntityId<DsarId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static DsarId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static DsarId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Obligation; owner CMP.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ObligationId>))]
public readonly record struct ObligationId(Guid Value) : IEntityId<ObligationId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ObligationId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ObligationId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a AiSystem register entry; owner CMP.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AiSystemId>))]
public readonly record struct AiSystemId(Guid Value) : IEntityId<AiSystemId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static AiSystemId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static AiSystemId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
