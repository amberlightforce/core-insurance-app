using System.Collections.Concurrent;
using System.Reflection;

namespace CoreIns.Platform.DataProtection;

/// <summary>
/// Personal-data classification (contract §3.2.1 rule 7; REQ-PTY-168; R-66 for event fields).
/// </summary>
public enum DataClass
{
    /// <summary>Not personal data.</summary>
    P0 = 0,

    /// <summary>Personal data.</summary>
    P1 = 1,

    /// <summary>Personal-sensitive: financial identifiers, national IDs, precise location. Field-encrypted when an identifier or IBAN (§3.9.12).</summary>
    P2 = 2,

    /// <summary>Special category: health, criminal offences (incl. fraud scores and SIU data, D-CON-27).</summary>
    P3 = 3,
}

/// <summary>
/// Declares the personal-data class of a property, field or parameter (contract §3.2.1 rule 7). The class drives
/// masking, exports, events, logs and AI minimisation (REQ-PTY-168, NFR-PTY-011): no P2/P3 values in events or logs.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = true)]
public sealed class DataClassAttribute(DataClass classification) : Attribute
{
    public DataClass Classification { get; } = classification;

    /// <summary>
    /// True when the value must be field-encrypted at rest (P2/P3 identifiers and IBANs, contract §3.9.12, D-ARC-14).
    /// Set by the owner for identifier-like values; free-text P3 notes are protected by access control instead
    /// (D-ARC-14: no full-text over encrypted health notes).
    /// </summary>
    public bool Encrypted { get; init; }
}

/// <summary>Reads the <see cref="DataClassAttribute"/> catalogue of a type (REQ-PTY-168 classification catalogue).</summary>
public static class DataClassCatalogue
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, DataClassAttribute>> Cache = new();

    /// <summary>Classification per public instance property of <paramref name="type"/> that declares one.</summary>
    public static IReadOnlyDictionary<string, DataClassAttribute> Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property.Name, Attribute: property.GetCustomAttribute<DataClassAttribute>(inherit: true)))
            .Where(entry => entry.Attribute is not null)
            .ToDictionary(entry => entry.Name, entry => entry.Attribute!, StringComparer.Ordinal));
    }

    /// <summary>Properties of <paramref name="type"/> whose class is at least <paramref name="minimum"/>.</summary>
    public static IReadOnlyList<string> PropertiesAtOrAbove(Type type, DataClass minimum) =>
        Of(type).Where(entry => entry.Value.Classification >= minimum).Select(entry => entry.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Undeclared public properties of <paramref name="type"/>. A classification gate (tests, schema registry) fails
    /// when this is not empty, so every attribute is classified.
    /// </summary>
    public static IReadOnlyList<string> Unclassified(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var declared = Of(type);
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => !declared.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
