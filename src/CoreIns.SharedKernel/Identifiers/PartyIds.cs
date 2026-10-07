using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a Party (person or organisation); owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PartyId>))]
public readonly record struct PartyId(Guid Value) : IEntityId<PartyId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PartyId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PartyId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a PartyRole; owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<PartyRoleId>))]
public readonly record struct PartyRoleId(Guid Value) : IEntityId<PartyRoleId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static PartyRoleId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static PartyRoleId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Account (customer account, CD-04; not a GL account); owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AccountId>))]
public readonly record struct AccountId(Guid Value) : IEntityId<AccountId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static AccountId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static AccountId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Household; owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<HouseholdId>))]
public readonly record struct HouseholdId(Guid Value) : IEntityId<HouseholdId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static HouseholdId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static HouseholdId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Consent; owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ConsentId>))]
public readonly record struct ConsentId(Guid Value) : IEntityId<ConsentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ConsentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ConsentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Intermediary; owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<IntermediaryId>))]
public readonly record struct IntermediaryId(Guid Value) : IEntityId<IntermediaryId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static IntermediaryId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static IntermediaryId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a ProducerCode record; owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ProducerCodeId>))]
public readonly record struct ProducerCodeId(Guid Value) : IEntityId<ProducerCodeId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ProducerCodeId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ProducerCodeId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a CommissionAgreement; owner PTY.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CommissionAgreementId>))]
public readonly record struct CommissionAgreementId(Guid Value) : IEntityId<CommissionAgreementId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static CommissionAgreementId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static CommissionAgreementId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
