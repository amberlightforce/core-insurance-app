using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>
/// Business lineage keys (D5, D-CON-01, D-CON-28): named business ids such as <c>quoteId</c>, <c>jobId</c>,
/// <c>policyId</c>, <c>transactionId</c>, <c>chargeId</c>, <c>invoiceId</c>, <c>claimId</c>, <c>journalId</c>. They
/// travel in every event envelope (<c>businessKeys</c>) and every audit record, and they are the only way journeys are
/// joined (never the trace id). Immutable; keys are ordered by name (ordinal). Names are lower camel case
/// (<c>^[a-z][A-Za-z0-9]*$</c>), values 1–200 characters.
/// </summary>
[JsonConverter(typeof(BusinessKeysJsonConverter))]
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix",
    Justification = "BusinessKeys is the contract name of the envelope field (D-CON-01); 'BusinessKeysDictionary' would obscure it.")]
public sealed class BusinessKeys : IReadOnlyDictionary<string, string>, IEquatable<BusinessKeys>
{
    private readonly ImmutableSortedDictionary<string, string> _keys;

    private BusinessKeys(ImmutableSortedDictionary<string, string> keys) => _keys = keys;

    /// <summary>No keys.</summary>
    public static BusinessKeys Empty { get; } = new(ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal));

    /// <summary>Creates keys from pairs; every name and value is validated, duplicate names are rejected.</summary>
    public static BusinessKeys From(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var keys = Empty;
        foreach (var (name, value) in pairs)
        {
            if (keys.ContainsKey(name))
            {
                throw new ArgumentException($"Business key '{name}' appears twice.", nameof(pairs));
            }

            keys = keys.With(name, value);
        }

        return keys;
    }

    /// <summary>A copy with <paramref name="name"/> set to <paramref name="value"/> (replacing an existing value).</summary>
    public BusinessKeys With(string name, string value)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a business key name (lower camel case, e.g. policyId).", nameof(name));
        }

        if (value is not { Length: >= 1 and <= 200 })
        {
            throw new ArgumentException($"Business key '{name}' needs a value of 1-200 characters.", nameof(value));
        }

        return new BusinessKeys(_keys.SetItem(name, value));
    }

    /// <summary>A copy with an entity id as the value.</summary>
    public BusinessKeys With<TId>(string name, TId id)
        where TId : struct, IEntityId<TId> => With(name, id.Value.ToString("D"));

    /// <summary>A copy with all keys of <paramref name="other"/> added; equal values are fine, conflicting values are rejected.</summary>
    public BusinessKeys Merge(BusinessKeys other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var merged = this;
        foreach (var (name, value) in other)
        {
            if (merged.TryGetValue(name, out var existing) && !string.Equals(existing, value, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Business key '{name}' has two values: '{existing}' and '{value}'.", nameof(other));
            }

            merged = merged.With(name, value);
        }

        return merged;
    }

    /// <summary>True when the name is a valid business key name.</summary>
    public static bool IsValidName([NotNullWhen(true)] string? name) =>
        name is { Length: >= 1 and <= 64 } && char.IsAsciiLetterLower(name[0]) && name.All(char.IsAsciiLetterOrDigit);

    /// <summary>Names in ordinal order.</summary>
    public IEnumerable<string> Keys => _keys.Keys;

    /// <summary>Values in key order.</summary>
    public IEnumerable<string> Values => _keys.Values;

    /// <summary>Number of keys.</summary>
    public int Count => _keys.Count;

    /// <summary>True when there are no keys.</summary>
    public bool IsEmpty => _keys.IsEmpty;

    /// <summary>The value of a key.</summary>
    public string this[string key] => _keys[key];

    /// <inheritdoc />
    public bool ContainsKey(string key) => _keys.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _keys.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(BusinessKeys? other) =>
        other is not null && other.Count == Count && this.All(pair => other.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BusinessKeys other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var (key, value) in _keys)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary><c>name=value</c> pairs.</summary>
    public override string ToString() => string.Join(", ", _keys.Select(pair => $"{pair.Key}={pair.Value}"));
}
