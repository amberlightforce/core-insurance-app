using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.SharedKernel.Json;

/// <summary>
/// Decimal numbers as JSON strings (contracts/events common <c>Decimal</c>: <c>^-?(0|[1-9]\d*)(\.\d+)?$</c>). Amounts
/// and rates are never JSON numbers, so they never pass through binary floating point.
/// </summary>
public static partial class DecimalText
{
    /// <summary>Invariant text keeping the value's scale (e.g. <c>12.50</c>); negative zero prints as zero.</summary>
    public static string Format(decimal value) =>
        (value == 0m ? Math.Abs(value) : value).ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses the contract decimal-string format exactly.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out decimal value)
    {
        value = 0m;
        return text is { Length: <= 64 }
               && Pattern().IsMatch(text)
               && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Parses the contract decimal-string format or throws <see cref="FormatException"/>.</summary>
    public static decimal Parse(string text) =>
        TryParse(text, out var value) ? value : throw new FormatException($"'{text}' is not a decimal string (e.g. 12.50).");

    [GeneratedRegex("^-?(0|[1-9][0-9]*)(\\.[0-9]+)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Reads a string token or fails with a <see cref="JsonException"/>.</summary>
internal static class JsonReading
{
    public static string ReadString(ref Utf8JsonReader reader, string what) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString()!
            : throw new JsonException($"{what} must be a JSON string, not {reader.TokenType}.");

    public static T Convert<T>(string text, Func<string, T> parse, string what)
    {
        try
        {
            return parse(text);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new JsonException($"Invalid {what}: {ex.Message}", ex);
        }
    }

    public static void ExpectStartObject(ref Utf8JsonReader reader, string what)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"{what} must be a JSON object.");
        }
    }
}

/// <summary><see cref="Currency"/> as its ISO code.</summary>
public sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    /// <inheritdoc />
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(JsonReading.ReadString(ref reader, "currency"), Currency.FromCode, "currency");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Code);
    }
}

/// <summary><see cref="Money"/> as <c>{"amount": "12.50", "currency": "EUR"}</c>.</summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    /// <inheritdoc />
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonReading.ExpectStartObject(ref reader, "Money");
        string? amount = null;
        string? currency = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "amount":
                    amount = JsonReading.ReadString(ref reader, "Money.amount");
                    break;
                case "currency":
                    currency = JsonReading.ReadString(ref reader, "Money.currency");
                    break;
                default:
                    throw new JsonException($"Money has no member '{name}'.");
            }
        }

        if (amount is null || currency is null)
        {
            throw new JsonException("Money needs both amount and currency.");
        }

        var value = JsonReading.Convert(amount, DecimalText.Parse, "Money.amount");
        return new Money(value, JsonReading.Convert(currency, Currency.FromCode, "Money.currency"));
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteString("amount", DecimalText.Format(value.Amount));
        writer.WriteString("currency", value.Currency.Code);
        writer.WriteEndObject();
    }
}

/// <summary><see cref="Rate"/> as a decimal string.</summary>
public sealed class RateJsonConverter : JsonConverter<Rate>
{
    /// <inheritdoc />
    public override Rate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonReading.Convert(JsonReading.ReadString(ref reader, "rate"), DecimalText.Parse, "rate"));

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Rate value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(DecimalText.Format(value.Value));
    }
}

/// <summary><see cref="Percentage"/> as a decimal string.</summary>
public sealed class PercentageJsonConverter : JsonConverter<Percentage>
{
    /// <inheritdoc />
    public override Percentage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonReading.Convert(JsonReading.ReadString(ref reader, "percentage"), DecimalText.Parse, "percentage"));

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Percentage value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(DecimalText.Format(value.Value));
    }
}

/// <summary><see cref="Instant"/> as RFC 3339 UTC.</summary>
public sealed class InstantJsonConverter : JsonConverter<Instant>
{
    /// <inheritdoc />
    public override Instant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(JsonReading.ReadString(ref reader, "instant"), Instant.Parse, "instant");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Instant value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary><see cref="BusinessDate"/> as <c>yyyy-MM-dd</c>.</summary>
public sealed class BusinessDateJsonConverter : JsonConverter<BusinessDate>
{
    /// <inheritdoc />
    public override BusinessDate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(JsonReading.ReadString(ref reader, "date"), BusinessDate.Parse, "date");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BusinessDate value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary><see cref="DateRange"/> as <c>{"from": date, "to": date|null}</c>.</summary>
public sealed class DateRangeJsonConverter : JsonConverter<DateRange>
{
    /// <inheritdoc />
    public override DateRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var (from, to) = RangeReading.Read(ref reader, "DatePeriod");
        return JsonReading.Convert(from, f => new DateRange(BusinessDate.Parse(f), to is null ? null : BusinessDate.Parse(to)), "DatePeriod");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateRange value, JsonSerializerOptions options) =>
        RangeReading.Write(writer, value.Start.ToString(), value.End?.ToString());
}

/// <summary><see cref="InstantRange"/> as <c>{"from": instant, "to": instant|null}</c>.</summary>
public sealed class InstantRangeJsonConverter : JsonConverter<InstantRange>
{
    /// <inheritdoc />
    public override InstantRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var (from, to) = RangeReading.Read(ref reader, "TimeWindow");
        return JsonReading.Convert(from, f => new InstantRange(Instant.Parse(f), to is null ? null : Instant.Parse(to)), "TimeWindow");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, InstantRange value, JsonSerializerOptions options) =>
        RangeReading.Write(writer, value.Start.ToString(), value.End?.ToString());
}

internal static class RangeReading
{
    public static (string From, string? To) Read(ref Utf8JsonReader reader, string what)
    {
        JsonReading.ExpectStartObject(ref reader, what);
        string? from = null;
        string? to = null;
        var sawTo = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "from":
                    from = JsonReading.ReadString(ref reader, what + ".from");
                    break;
                case "to":
                    sawTo = true;
                    to = reader.TokenType == JsonTokenType.Null ? null : JsonReading.ReadString(ref reader, what + ".to");
                    break;
                default:
                    throw new JsonException($"{what} has no member '{name}'.");
            }
        }

        return from is not null && sawTo ? (from, to) : throw new JsonException($"{what} needs 'from' and 'to' (null when open-ended).");
    }

    public static void Write(Utf8JsonWriter writer, string from, string? to)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteString("from", from);
        if (to is null)
        {
            writer.WriteNull("to");
        }
        else
        {
            writer.WriteString("to", to);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Any <see cref="IEntityId{TSelf}"/> as its lower-case UUID text (also as a dictionary key).</summary>
/// <typeparam name="TId">Id type.</typeparam>
public sealed class EntityIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IEntityId<TId>
{
    /// <inheritdoc />
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(JsonReading.ReadString(ref reader, typeof(TId).Name), EntityIds.Parse<TId>, typeof(TId).Name);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value.ToString("D"));
    }

    /// <inheritdoc />
    public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(reader.GetString()!, EntityIds.Parse<TId>, typeof(TId).Name);

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Value.ToString("D"));
    }
}

/// <summary>Any <see cref="IStringValue{TSelf}"/> as its text (also as a dictionary key).</summary>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class StringValueJsonConverter<TValue> : JsonConverter<TValue>
    where TValue : struct, IStringValue<TValue>
{
    /// <inheritdoc />
    public override TValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(JsonReading.ReadString(ref reader, typeof(TValue).Name), TValue.Parse, typeof(TValue).Name);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }

    /// <inheritdoc />
    public override TValue ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(reader.GetString()!, TValue.Parse, typeof(TValue).Name);

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Value);
    }
}

/// <summary>Base for value objects written as a single string.</summary>
/// <typeparam name="T">Value type.</typeparam>
public abstract class TextJsonConverter<T> : JsonConverter<T>
{
    /// <summary>Name used in error messages.</summary>
    protected abstract string What { get; }

    /// <summary>Parses the text.</summary>
    protected abstract T Parse(string text);

    /// <summary>Formats the value.</summary>
    protected abstract string Format(T value);

    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonReading.Convert(JsonReading.ReadString(ref reader, What), Parse, What);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }
}

/// <summary><see cref="ProductVersionNumber"/> as <c>major.minor</c>.</summary>
public sealed class ProductVersionNumberJsonConverter : TextJsonConverter<ProductVersionNumber>
{
    /// <inheritdoc />
    protected override string What => "product version";

    /// <inheritdoc />
    protected override ProductVersionNumber Parse(string text) => ProductVersionNumber.Parse(text);

    /// <inheritdoc />
    protected override string Format(ProductVersionNumber value) => value.ToString();
}

/// <summary><see cref="IdempotencyKey"/> as a UUID string.</summary>
public sealed class IdempotencyKeyJsonConverter : TextJsonConverter<IdempotencyKey>
{
    /// <inheritdoc />
    protected override string What => "idempotency key";

    /// <inheritdoc />
    protected override IdempotencyKey Parse(string text) => IdempotencyKey.Parse(text);

    /// <inheritdoc />
    protected override string Format(IdempotencyKey value) => value.ToString();
}

/// <summary><see cref="CorrelationId"/> as 32 hex digits.</summary>
public sealed class CorrelationIdJsonConverter : TextJsonConverter<CorrelationId>
{
    /// <inheritdoc />
    protected override string What => "correlation id";

    /// <inheritdoc />
    protected override CorrelationId Parse(string text) => CorrelationId.Parse(text);

    /// <inheritdoc />
    protected override string Format(CorrelationId value) => value.Value;
}

/// <summary><see cref="Sha256Hash"/> as 64 hex digits.</summary>
public sealed class Sha256HashJsonConverter : TextJsonConverter<Sha256Hash>
{
    /// <inheritdoc />
    protected override string What => "SHA-256 hash";

    /// <inheritdoc />
    protected override Sha256Hash Parse(string text) => Sha256Hash.Parse(text);

    /// <inheritdoc />
    protected override string Format(Sha256Hash value) => value.Value;
}

/// <summary><see cref="ConfigurationHash"/> as 64 hex digits.</summary>
public sealed class ConfigurationHashJsonConverter : TextJsonConverter<ConfigurationHash>
{
    /// <inheritdoc />
    protected override string What => "configuration hash";

    /// <inheritdoc />
    protected override ConfigurationHash Parse(string text) => ConfigurationHash.Parse(text);

    /// <inheritdoc />
    protected override string Format(ConfigurationHash value) => value.ToString();
}

/// <summary><see cref="ResolutionHash"/> as 64 hex digits.</summary>
public sealed class ResolutionHashJsonConverter : TextJsonConverter<ResolutionHash>
{
    /// <inheritdoc />
    protected override string What => "resolution hash";

    /// <inheritdoc />
    protected override ResolutionHash Parse(string text) => ResolutionHash.Parse(text);

    /// <inheritdoc />
    protected override string Format(ResolutionHash value) => value.ToString();
}

/// <summary><see cref="Iban"/> as its electronic format (full value: use only where the contract allows a P2 field).</summary>
public sealed class IbanJsonConverter : TextJsonConverter<Iban>
{
    /// <inheritdoc />
    protected override string What => "IBAN";

    /// <inheritdoc />
    protected override Iban Parse(string text) => Iban.Parse(text);

    /// <inheritdoc />
    protected override string Format(Iban value) => value.Value;
}

/// <summary><see cref="ErrorCode"/> as text.</summary>
public sealed class ErrorCodeJsonConverter : TextJsonConverter<ErrorCode>
{
    /// <inheritdoc />
    protected override string What => "error code";

    /// <inheritdoc />
    protected override ErrorCode Parse(string text) => ErrorCode.Parse(text);

    /// <inheritdoc />
    protected override string Format(ErrorCode value) => value.Value;
}

/// <summary><see cref="TermNumber"/> as a JSON integer (a typed count, contracts/events <c>NonNegativeInt</c>).</summary>
public sealed class TermNumberJsonConverter : JsonConverter<TermNumber>
{
    /// <inheritdoc />
    public override TermNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value >= 1
            ? new TermNumber(value)
            : throw new JsonException("A term number is an integer >= 1.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TermNumber value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Value);
    }
}

/// <summary><see cref="BusinessKeys"/> as a JSON object of string values.</summary>
public sealed class BusinessKeysJsonConverter : JsonConverter<BusinessKeys>
{
    /// <inheritdoc />
    public override BusinessKeys Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonReading.ExpectStartObject(ref reader, "businessKeys");
        var pairs = new List<KeyValuePair<string, string>>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            pairs.Add(new KeyValuePair<string, string>(name, JsonReading.ReadString(ref reader, "businessKeys." + name)));
        }

        return JsonReading.Convert(string.Empty, _ => BusinessKeys.From(pairs), "businessKeys");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BusinessKeys value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (name, key) in value)
        {
            writer.WriteString(name, key);
        }

        writer.WriteEndObject();
    }
}

/// <summary><see cref="LocalizedText"/> as <c>{"el": "…", "en": "…"}</c>.</summary>
public sealed class LocalizedTextJsonConverter : JsonConverter<LocalizedText>
{
    /// <inheritdoc />
    public override LocalizedText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonReading.ExpectStartObject(ref reader, "LocalizedText");
        string? el = null;
        string? en = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "el":
                    el = JsonReading.ReadString(ref reader, "LocalizedText.el");
                    break;
                case "en":
                    en = JsonReading.ReadString(ref reader, "LocalizedText.en");
                    break;
                default:
                    throw new JsonException($"LocalizedText has no member '{name}'.");
            }
        }

        return JsonReading.Convert(string.Empty, _ => new LocalizedText(el!, en!), "LocalizedText");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteString("el", value.El);
        writer.WriteString("en", value.En);
        writer.WriteEndObject();
    }
}

/// <summary>The JSON settings for SharedKernel types: camelCase members, no indentation, strict number handling.</summary>
public static class SharedKernelJson
{
    /// <summary>Shared options (read-only). Value types carry their own converters, so any options work; these add camelCase.</summary>
    public static JsonSerializerOptions Options { get; } = CreateReadOnly();

    /// <summary>A fresh mutable copy of <see cref="Options"/> to extend.</summary>
    public static JsonSerializerOptions Create() => new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = false,
    };

    private static JsonSerializerOptions CreateReadOnly()
    {
        var options = Create();
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
