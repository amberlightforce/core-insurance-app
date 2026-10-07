using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a BillingAccount; owner BIL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<BillingAccountId>))]
public readonly record struct BillingAccountId(Guid Value) : IEntityId<BillingAccountId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static BillingAccountId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static BillingAccountId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a PaymentPlan; owner BIL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PaymentPlanId>))]
public readonly record struct PaymentPlanId(Guid Value) : IEntityId<PaymentPlanId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PaymentPlanId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PaymentPlanId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Invoice; owner BIL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<InvoiceId>))]
public readonly record struct InvoiceId(Guid Value) : IEntityId<InvoiceId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static InvoiceId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static InvoiceId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a incoming Payment (receipt); owner BIL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PaymentId>))]
public readonly record struct PaymentId(Guid Value) : IEntityId<PaymentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PaymentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PaymentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a PaymentInstrument, the only bank-account record (R-38); owner BIL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PaymentInstrumentId>))]
public readonly record struct PaymentInstrumentId(Guid Value) : IEntityId<PaymentInstrumentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PaymentInstrumentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PaymentInstrumentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Refund; owner BIL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RefundId>))]
public readonly record struct RefundId(Guid Value) : IEntityId<RefundId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static RefundId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static RefundId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Disbursement; owner BIL.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<DisbursementId>))]
public readonly record struct DisbursementId(Guid Value) : IEntityId<DisbursementId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static DisbursementId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static DisbursementId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
