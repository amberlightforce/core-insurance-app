using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Product version <c>major.minor</c> (contract §3.2.3); ordered numerically (1.10 &gt; 1.9).</summary>
[JsonConverter(typeof(ProductVersionNumberJsonConverter))]
public readonly record struct ProductVersionNumber : IComparable<ProductVersionNumber>
{
    /// <summary>Creates <c>major.minor</c>; both non-negative.</summary>
    public ProductVersionNumber(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    /// <summary>Major part.</summary>
    public int Major { get; }

    /// <summary>Minor part.</summary>
    public int Minor { get; }

    /// <summary>Parses <c>major.minor</c> (digits only, no leading zeros except a single 0).</summary>
    public static ProductVersionNumber Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a product version (major.minor).");

    /// <summary>Tries to parse <c>major.minor</c>.</summary>
    public static bool TryParse(string? text, out ProductVersionNumber version)
    {
        version = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 2 } || !IsNumber(parts[0]) || !IsNumber(parts[1]))
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new ProductVersionNumber(major, minor);
        return true;

        static bool IsNumber(string part) =>
            part.Length is >= 1 and <= 9 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');
    }

    /// <inheritdoc />
    public int CompareTo(ProductVersionNumber other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    /// <summary><c>major.minor</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    /// <summary>Numeric order.</summary>
    public static bool operator <(ProductVersionNumber left, ProductVersionNumber right) => left.CompareTo(right) < 0;

    /// <summary>Numeric order.</summary>
    public static bool operator >(ProductVersionNumber left, ProductVersionNumber right) => left.CompareTo(right) > 0;

    /// <summary>Numeric order.</summary>
    public static bool operator <=(ProductVersionNumber left, ProductVersionNumber right) => left.CompareTo(right) <= 0;

    /// <summary>Numeric order.</summary>
    public static bool operator >=(ProductVersionNumber left, ProductVersionNumber right) => left.CompareTo(right) >= 0;
}

/// <summary>Policy term number: an integer, 1-based per policy (contract §3.2.3).</summary>
[JsonConverter(typeof(TermNumberJsonConverter))]
public readonly record struct TermNumber : IComparable<TermNumber>
{
    /// <summary>Creates a term number (≥ 1).</summary>
    public TermNumber(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        Value = value;
    }

    /// <summary>The number.</summary>
    public int Value { get; }

    /// <summary>The first term of a policy.</summary>
    public static TermNumber First { get; } = new(1);

    /// <summary>The next term (renewal).</summary>
    public TermNumber Next() => new(checked(Value + 1));

    /// <inheritdoc />
    public int CompareTo(TermNumber other) => Value.CompareTo(other.Value);

    /// <summary>The number as invariant text.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Numeric order.</summary>
    public static bool operator <(TermNumber left, TermNumber right) => left.CompareTo(right) < 0;

    /// <summary>Numeric order.</summary>
    public static bool operator >(TermNumber left, TermNumber right) => left.CompareTo(right) > 0;

    /// <summary>Numeric order.</summary>
    public static bool operator <=(TermNumber left, TermNumber right) => left.CompareTo(right) <= 0;

    /// <summary>Numeric order.</summary>
    public static bool operator >=(TermNumber left, TermNumber right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// The <c>Idempotency-Key</c> of a command (contract §3.5.3): a UUID chosen by the caller. A retry with the same key
/// and the same request returns the original result; with a different request it fails with
/// <c>&lt;MOD&gt;-ERR-IDEMPOTENCY-MISMATCH</c>. Text form: lower-case UUID.
/// </summary>
[JsonConverter(typeof(IdempotencyKeyJsonConverter))]
public readonly record struct IdempotencyKey
{
    private IdempotencyKey(Guid value) => Value = value;

    /// <summary>The key.</summary>
    public Guid Value { get; }

    /// <summary>A new random key (for internal callers and tests).</summary>
    public static IdempotencyKey New() => new(Guid.NewGuid());

    /// <summary>Wraps a UUID; the empty GUID is rejected.</summary>
    public static IdempotencyKey From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>Parses a header value: a UUID in <c>8-4-4-4-12</c> form (any case).</summary>
    public static bool TryParse(string? text, out IdempotencyKey key)
    {
        key = default;
        if (text is not { Length: 36 } || !Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty)
        {
            return false;
        }

        key = new IdempotencyKey(value);
        return true;
    }

    /// <summary>Parses a header value or throws <see cref="FormatException"/>.</summary>
    public static IdempotencyKey Parse(string text) =>
        TryParse(text, out var key) ? key : throw new FormatException("An Idempotency-Key must be a UUID (8-4-4-4-12).");

    /// <summary>Lower-case UUID.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// The W3C trace id of a request (32 lower-case hex digits, not all zero). It is <b>technical tracing only</b>: journey
/// lineage uses <see cref="BusinessKeys"/>, never this id (D5, D-CON-01). Problem Details call it <c>traceId</c> (D-API-05).
/// </summary>
[JsonConverter(typeof(CorrelationIdJsonConverter))]
public readonly record struct CorrelationId
{
    private CorrelationId(string value) => Value = value;

    /// <summary>The trace id.</summary>
    public string Value { get; }

    /// <summary>A new random trace id (when no incoming <c>traceparent</c> exists).</summary>
    public static CorrelationId New()
    {
        Span<byte> bytes = stackalloc byte[16];
        do
        {
            RandomNumberGenerator.Fill(bytes);
        }
        while (bytes.IndexOfAnyExcept((byte)0) < 0);

        return new CorrelationId(Convert.ToHexStringLower(bytes));
    }

    /// <summary>Accepts a W3C trace id (32 hex digits, not all zero); upper case is normalised to lower case.</summary>
    public static bool TryParse(string? text, out CorrelationId id)
    {
        id = default;
        if (text is not { Length: 32 } || !text.All(char.IsAsciiHexDigit) || text.All(c => c == '0'))
        {
            return false;
        }

        id = new CorrelationId(text.ToLowerInvariant());
        return true;
    }

    /// <summary>Parses a trace id or throws <see cref="FormatException"/>.</summary>
    public static CorrelationId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not a W3C trace id.");

    /// <summary>The trace id.</summary>
    public override string ToString() => Value ?? string.Empty;
}
