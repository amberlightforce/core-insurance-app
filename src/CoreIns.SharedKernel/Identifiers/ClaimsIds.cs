using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a Claim; owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ClaimId>))]
public readonly record struct ClaimId(Guid Value) : IEntityId<ClaimId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ClaimId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ClaimId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Exposure; owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ExposureId>))]
public readonly record struct ExposureId(Guid Value) : IEntityId<ExposureId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ExposureId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ExposureId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Claimant; owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ClaimantId>))]
public readonly record struct ClaimantId(Guid Value) : IEntityId<ClaimantId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ClaimantId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ClaimantId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ReserveLine; owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ReserveLineId>))]
public readonly record struct ReserveLineId(Guid Value) : IEntityId<ReserveLineId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ReserveLineId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ReserveLineId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ClaimTransactionSet; owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ClaimTransactionSetId>))]
public readonly record struct ClaimTransactionSetId(Guid Value) : IEntityId<ClaimTransactionSetId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ClaimTransactionSetId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ClaimTransactionSetId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ClaimPayment; owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ClaimPaymentId>))]
public readonly record struct ClaimPaymentId(Guid Value) : IEntityId<ClaimPaymentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ClaimPaymentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ClaimPaymentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a claim Recovery (not an RI recovery); owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RecoveryId>))]
public readonly record struct RecoveryId(Guid Value) : IEntityId<RecoveryId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RecoveryId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RecoveryId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Friendly Settlement case; owner CLM.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<FsCaseId>))]
public readonly record struct FsCaseId(Guid Value) : IEntityId<FsCaseId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static FsCaseId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static FsCaseId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
