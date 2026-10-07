using System.Diagnostics.CodeAnalysis;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>
/// A strongly typed internal id: one <c>readonly record struct</c> per entity wrapping a UUIDv7 (contract §3.2.1 rule 4,
/// D-ARC-05: generated in .NET with <see cref="Guid.CreateVersion7()"/>). Ids of different entities never mix.
/// </summary>
/// <typeparam name="TSelf">The id type.</typeparam>
public interface IEntityId<TSelf>
    where TSelf : struct, IEntityId<TSelf>
{
    /// <summary>The underlying UUID.</summary>
    Guid Value { get; }

    /// <summary>Wraps an existing UUID (the empty GUID is rejected).</summary>
    static abstract TSelf From(Guid value);
}

/// <summary>
/// A validated text value object: business numbers issued by PLT numbering (format is pack data, never personal data)
/// and codes whose format the contract fixes (clock codes, operation names, error codes, …).
/// </summary>
/// <typeparam name="TSelf">The value type.</typeparam>
public interface IStringValue<TSelf>
    where TSelf : struct, IStringValue<TSelf>
{
    /// <summary>The value as text.</summary>
    string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    static abstract TSelf Parse(string value);

    /// <summary>Validates without throwing.</summary>
    static abstract bool TryParse([NotNullWhen(true)] string? value, out TSelf result);
}

/// <summary>Helpers for <see cref="IEntityId{TSelf}"/> implementations.</summary>
public static class EntityIds
{
    /// <summary>A new UUIDv7 (time-ordered, D-ARC-05).</summary>
    public static Guid NewGuid() => Guid.CreateVersion7();

    /// <summary>Returns <paramref name="value"/> unless it is <see cref="Guid.Empty"/>.</summary>
    public static Guid RequireNotEmpty(Guid value) =>
        value != Guid.Empty ? value : throw new ArgumentException("An entity id cannot be the empty GUID.", nameof(value));

    /// <summary>Parses an id of any entity type from its text form.</summary>
    public static TId Parse<TId>(string text)
        where TId : struct, IEntityId<TId> =>
        Guid.TryParse(text, out var value) && value != Guid.Empty
            ? TId.From(value)
            : throw new FormatException($"'{text}' is not a valid {typeof(TId).Name}.");
}
