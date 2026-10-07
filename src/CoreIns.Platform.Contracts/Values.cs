using System.Text.Json;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Platform.Contracts;

/// <summary>
/// Options of an in-process command call (contract §3.5.3, D-ARC-16): the same idempotency and dry-run rules as the HTTP
/// operation. A replay with the same key returns the original result; a different payload fails with
/// <c>&lt;MOD&gt;-ERR-IDEMPOTENCY-MISMATCH</c>.
/// </summary>
/// <param name="IdempotencyKey">Caller-chosen key (kept at least 7 days by the owner).</param>
public sealed record CommandOptions(IdempotencyKey IdempotencyKey)
{
    /// <summary>Run every check and return the full result without side effects (only where the operation offers dry-run).</summary>
    public bool DryRun { get; init; }

    /// <summary>Options with a new random key.</summary>
    public static CommandOptions New() => new(IdempotencyKey.New());
}

/// <summary>
/// The valid-time input <c>validAt</c> (D-API-02, D-API-08): a business date or an RFC 3339 instant (common.yaml
/// <c>ValidAt</c> = LocalDate | Instant). Exactly one of the two is set.
/// </summary>
[JsonConverter(typeof(ValidAtJsonConverter))]
public readonly record struct ValidAt
{
    private ValidAt(BusinessDate? date, Instant? instant)
    {
        Date = date;
        Instant = instant;
    }

    /// <summary>The date form, when given as a date.</summary>
    public BusinessDate? Date { get; }

    /// <summary>The instant form, when given as an instant.</summary>
    public Instant? Instant { get; }

    /// <summary>A date.</summary>
    public static ValidAt From(BusinessDate date) => new(date, null);

    /// <summary>An instant.</summary>
    public static ValidAt From(Instant instant) => new(null, instant);

    /// <summary>The wire form.</summary>
    public override string ToString() => Date?.ToString() ?? Instant?.ToString() ?? string.Empty;
}

/// <summary><see cref="ValidAt"/> as a date or instant string.</summary>
public sealed class ValidAtJsonConverter : JsonConverter<ValidAt>
{
    /// <inheritdoc />
    public override ValidAt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException("validAt must be a string.");
        if (Instant.TryParse(text, out var instant))
        {
            return ValidAt.From(instant);
        }

        try
        {
            return ValidAt.From(BusinessDate.Parse(text));
        }
        catch (FormatException ex)
        {
            throw new JsonException($"'{text}' is neither a date nor an RFC 3339 UTC instant.", ex);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ValidAt value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// A named term that is either an amount or a decimal (events common <c>NamedAmounts</c> values; plt
/// <c>AuthorityCheckResponse.limit</c>): <c>Money</c> or a decimal string, never a bare JSON number.
/// </summary>
[JsonConverter(typeof(MoneyOrDecimalJsonConverter))]
public readonly record struct MoneyOrDecimal
{
    private MoneyOrDecimal(Money? money, decimal? number)
    {
        Money = money;
        Number = number;
    }

    /// <summary>The amount, when the term is an amount.</summary>
    public Money? Money { get; }

    /// <summary>The decimal (rate, share), when the term is not an amount.</summary>
    public decimal? Number { get; }

    /// <summary>An amount.</summary>
    public static MoneyOrDecimal From(Money money) => new(money, null);

    /// <summary>A decimal.</summary>
    public static MoneyOrDecimal From(decimal value) => new(null, value);
}

/// <summary><see cref="MoneyOrDecimal"/> as a Money object or a decimal string.</summary>
public sealed class MoneyOrDecimalJsonConverter : JsonConverter<MoneyOrDecimal>
{
    private static readonly MoneyJsonConverter MoneyConverter = new();

    /// <inheritdoc />
    public override MoneyOrDecimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.StartObject => MoneyOrDecimal.From(MoneyConverter.Read(ref reader, typeof(Money), options)),
            JsonTokenType.String => MoneyOrDecimal.From(DecimalStringJsonConverter.ParseText(reader.GetString())),
            _ => throw new JsonException("A named amount is a Money object or a decimal string."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, MoneyOrDecimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.Money is { } money)
        {
            MoneyConverter.Write(writer, money, options);
        }
        else
        {
            writer.WriteStringValue(DecimalText.Format(value.Number ?? 0m));
        }
    }
}

/// <summary>
/// <c>decimal</c> (and <c>decimal?</c>) as a contract decimal string (events common <c>Decimal</c>): amounts and rates
/// never travel as JSON numbers.
/// </summary>
public sealed class DecimalStringJsonConverter : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(decimal) || typeToConvert == typeof(decimal?);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        typeToConvert == typeof(decimal) ? new Plain() : new NullableDecimal();

    /// <summary>Parses the contract decimal-string format.</summary>
    internal static decimal ParseText(string? text) =>
        DecimalText.TryParse(text, out var value) ? value : throw new JsonException($"'{text}' is not a decimal string (e.g. 12.50).");

    private static decimal ReadValue(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String
            ? ParseText(reader.GetString())
            : throw new JsonException("A decimal must be a JSON string, never a JSON number.");

    private sealed class Plain : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadValue(ref reader);

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DecimalText.Format(value));
    }

    private sealed class NullableDecimal : JsonConverter<decimal?>
    {
        public override bool HandleNull => true;

        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : ReadValue(ref reader);

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
        {
            if (value is { } number)
            {
                writer.WriteStringValue(DecimalText.Format(number));
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
