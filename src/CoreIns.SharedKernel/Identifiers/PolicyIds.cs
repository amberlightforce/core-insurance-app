using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a Policy; owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PolicyId>))]
public readonly record struct PolicyId(Guid Value) : IEntityId<PolicyId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PolicyId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PolicyId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a PolicyTerm; owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PolicyTermId>))]
public readonly record struct PolicyTermId(Guid Value) : IEntityId<PolicyTermId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PolicyTermId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PolicyTermId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Job; owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<JobId>))]
public readonly record struct JobId(Guid Value) : IEntityId<JobId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static JobId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static JobId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a PolicyTransaction; owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PolicyTransactionId>))]
public readonly record struct PolicyTransactionId(Guid Value) : IEntityId<PolicyTransactionId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PolicyTransactionId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PolicyTransactionId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Segment; owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<SegmentId>))]
public readonly record struct SegmentId(Guid Value) : IEntityId<SegmentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static SegmentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static SegmentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Quote (quote version); owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<QuoteId>))]
public readonly record struct QuoteId(Guid Value) : IEntityId<QuoteId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static QuoteId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static QuoteId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Coverage instance; owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CoverageId>))]
public readonly record struct CoverageId(Guid Value) : IEntityId<CoverageId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static CoverageId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static CoverageId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ChargeDelta charge id, carried into BIL invoice items, RI cessions and FIN business events (contract §3.2.3); owner POL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ChargeId>))]
public readonly record struct ChargeId(Guid Value) : IEntityId<ChargeId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ChargeId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ChargeId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
