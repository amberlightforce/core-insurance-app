using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a Book; owner FIN.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<BookId>))]
public readonly record struct BookId(Guid Value) : IEntityId<BookId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static BookId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static BookId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Journal; owner FIN.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<JournalId>))]
public readonly record struct JournalId(Guid Value) : IEntityId<JournalId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static JournalId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static JournalId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a JournalLine; owner FIN.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<JournalLineId>))]
public readonly record struct JournalLineId(Guid Value) : IEntityId<JournalLineId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static JournalLineId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static JournalLineId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a general-ledger account (not a PTY Account); owner FIN.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<GlAccountId>))]
public readonly record struct GlAccountId(Guid Value) : IEntityId<GlAccountId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static GlAccountId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static GlAccountId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a accounting Period; owner FIN.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<FinancialPeriodId>))]
public readonly record struct FinancialPeriodId(Guid Value) : IEntityId<FinancialPeriodId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static FinancialPeriodId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static FinancialPeriodId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a IFRS 17 group; owner FIN.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<Ifrs17GroupId>))]
public readonly record struct Ifrs17GroupId(Guid Value) : IEntityId<Ifrs17GroupId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static Ifrs17GroupId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static Ifrs17GroupId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
