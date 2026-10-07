using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>A SHA-256 digest as 64 lower-case hex digits (contracts/events common <c>Sha256</c>).</summary>
[JsonConverter(typeof(Sha256HashJsonConverter))]
public readonly record struct Sha256Hash
{
    private Sha256Hash(string value) => Value = value;

    /// <summary>64 lower-case hex digits.</summary>
    public string Value { get; }

    /// <summary>SHA-256 of bytes.</summary>
    public static Sha256Hash Compute(ReadOnlySpan<byte> data) => new(Convert.ToHexStringLower(SHA256.HashData(data)));

    /// <summary>SHA-256 of the UTF-8 encoding of a string.</summary>
    public static Sha256Hash ComputeUtf8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Compute(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Wraps a 32-byte digest.</summary>
    public static Sha256Hash FromBytes(ReadOnlySpan<byte> digest) =>
        digest.Length == SHA256.HashSizeInBytes
            ? new Sha256Hash(Convert.ToHexStringLower(digest))
            : throw new ArgumentException("A SHA-256 digest has 32 bytes.", nameof(digest));

    /// <summary>Accepts 64 hex digits (normalised to lower case).</summary>
    public static bool TryParse(string? text, out Sha256Hash hash)
    {
        hash = default;
        if (text is not { Length: 64 } || !text.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        hash = new Sha256Hash(text.ToLowerInvariant());
        return true;
    }

    /// <summary>Parses 64 hex digits or throws <see cref="FormatException"/>.</summary>
    public static Sha256Hash Parse(string text) =>
        TryParse(text, out var hash) ? hash : throw new FormatException($"'{text}' is not a SHA-256 hex digest.");

    /// <summary>The 32 digest bytes.</summary>
    public byte[] ToBytes() => Convert.FromHexString(Value);

    /// <summary>64 lower-case hex digits.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Content address of the whole configuration state in force (REQ-MKT-046, D-ARC-12): SHA-256 over the RFC 8785
/// canonical JSON of the state manifest. Stamped on quotes, transactions, invoices, journals and every event envelope.
/// </summary>
[JsonConverter(typeof(ConfigurationHashJsonConverter))]
public readonly record struct ConfigurationHash(Sha256Hash Hash)
{
    /// <summary>Parses 64 hex digits.</summary>
    public static ConfigurationHash Parse(string text) => new(Sha256Hash.Parse(text));

    /// <summary>Tries to parse 64 hex digits.</summary>
    public static bool TryParse(string? text, out ConfigurationHash hash)
    {
        var ok = Sha256Hash.TryParse(text, out var value);
        hash = new ConfigurationHash(value);
        return ok;
    }

    /// <summary>64 lower-case hex digits.</summary>
    public override string ToString() => Hash.ToString();
}

/// <summary>
/// Hash of the resolution manifest of a transaction (R-21): which product, rating and configuration versions a
/// resolution used. SHA-256 hex.
/// </summary>
[JsonConverter(typeof(ResolutionHashJsonConverter))]
public readonly record struct ResolutionHash(Sha256Hash Hash)
{
    /// <summary>Parses 64 hex digits.</summary>
    public static ResolutionHash Parse(string text) => new(Sha256Hash.Parse(text));

    /// <summary>Tries to parse 64 hex digits.</summary>
    public static bool TryParse(string? text, out ResolutionHash hash)
    {
        var ok = Sha256Hash.TryParse(text, out var value);
        hash = new ResolutionHash(value);
        return ok;
    }

    /// <summary>64 lower-case hex digits.</summary>
    public override string ToString() => Hash.ToString();
}
