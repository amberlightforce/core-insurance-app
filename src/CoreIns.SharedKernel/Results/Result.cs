using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Results;

/// <summary>
/// A stable error code <c>&lt;MOD&gt;-ERR-&lt;NNN or NAME&gt;</c> (contract §3.5.4, D-API-01), e.g.
/// <c>POL-ERR-IDEMPOTENCY-MISMATCH</c> or <c>PLT-ERR-UNKNOWN-TYPE</c>. It selects the Problem Details <c>type</c> URI
/// and localized title.
/// </summary>
[JsonConverter(typeof(ErrorCodeJsonConverter))]
public readonly partial record struct ErrorCode
{
    private ErrorCode(string value) => Value = value;

    /// <summary>The code.</summary>
    public string Value { get; }

    /// <summary>The owning module (the part before <c>-ERR-</c>).</summary>
    public ModuleCode Module => Enum.Parse<ModuleCode>(Value[..Value.IndexOf('-', StringComparison.Ordinal)]);

    /// <summary>The part after <c>-ERR-</c> (e.g. <c>IDEMPOTENCY-MISMATCH</c>).</summary>
    public string Name => Value[(Value.IndexOf("-ERR-", StringComparison.Ordinal) + 5)..];

    /// <summary>Builds <c>&lt;module&gt;-ERR-&lt;name&gt;</c>.</summary>
    public static ErrorCode For(ModuleCode module, string name) => Parse($"{module}-ERR-{name}");

    /// <summary>Parses a code; throws <see cref="FormatException"/> when malformed.</summary>
    public static ErrorCode Parse(string text) =>
        TryParse(text, out var code) ? code : throw new FormatException($"'{text}' is not an error code (<MOD>-ERR-<NNN|NAME>).");

    /// <summary>Validates a code.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out ErrorCode code)
    {
        code = default;
        if (text is not { Length: <= 100 } || !Pattern().IsMatch(text)
            || !ModuleCodes.TryParse(text[..text.IndexOf('-', StringComparison.Ordinal)], out _))
        {
            return false;
        }

        code = new ErrorCode(text);
        return true;
    }

    /// <summary>The code.</summary>
    public override string ToString() => Value ?? string.Empty;

    [GeneratedRegex("^[A-Z]{2,3}-ERR-[A-Z0-9]+(-[A-Z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>One field-level problem (Problem Details <c>errors[]</c>: field path, code, message key, localized message).</summary>
/// <param name="Field">JSON path of the field, e.g. <c>payment.amount</c>.</param>
/// <param name="Code">Validation code (e.g. <c>NotEmpty</c>, <c>CHECK_DIGIT</c>).</param>
/// <param name="MessageKey">Translation key of the message.</param>
/// <param name="Message">Message already localized by the producer, if any.</param>
public sealed record FieldError(string Field, string Code, string MessageKey, string? Message = null);

/// <summary>
/// An expected business failure, returned (not thrown) by command handlers and mapped to RFC 9457 Problem Details at the
/// API boundary. Business outcomes that are not errors (e.g. UW issues) are structured results, not errors (contract §3.5.4).
/// </summary>
/// <param name="Code">Stable error code.</param>
/// <param name="Detail">Human-readable detail (English, for logs and developers; never personal data).</param>
public sealed record DomainError(ErrorCode Code, string? Detail = null)
{
    /// <summary>Field-level problems (validation).</summary>
    public IReadOnlyList<FieldError> FieldErrors { get; init; } = [];

    /// <summary>Extra structured facts for the caller (e.g. referral target of an authority check). No personal data.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Error with a code and detail.</summary>
    public static DomainError Of(ModuleCode module, string name, string? detail = null) => new(ErrorCode.For(module, name), detail);

    /// <summary><c>code: detail</c>.</summary>
    public override string ToString() => Detail is null ? Code.ToString() : $"{Code}: {Detail}";
}

/// <summary>The "no value" result of a command that only changes state.</summary>
public readonly record struct Unit
{
    /// <summary>The only value.</summary>
    public static Unit Value { get; }
}

/// <summary>Success with a <typeparamref name="T"/> value, or failure with a <see cref="DomainError"/>.</summary>
/// <typeparam name="T">Value type.</typeparam>
public readonly record struct Result<T>
{
    private readonly T? _value;
    private readonly DomainError? _error;

    internal Result(T? value, DomainError? error)
    {
        _value = value;
        _error = error;
    }

    /// <summary>True on success.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => _error is null;

    /// <summary>True on failure.</summary>
    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => _error is not null;

    /// <summary>The value; throws on failure.</summary>
    public T Value => _error is null ? _value! : throw new InvalidOperationException($"No value: the result failed with {_error}.");

    /// <summary>The error on failure; null on success.</summary>
    public DomainError? Error => _error;

    /// <summary>Maps the value of a success.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return _error is null ? new Result<TOut>(map(_value!), null) : new Result<TOut>(default, _error);
    }

    /// <summary>Chains another fallible step.</summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return _error is null ? next(_value!) : new Result<TOut>(default, _error);
    }

    /// <summary>Folds both cases.</summary>
    public TOut Match<TOut>(Func<T, TOut> success, Func<DomainError, TOut> failure)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(failure);
        return _error is null ? success(_value!) : failure(_error);
    }

    /// <summary>A success.</summary>
    public static implicit operator Result<T>(T value) => new(value, null);

    /// <summary>A failure.</summary>
    public static implicit operator Result<T>(DomainError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary><c>Success(value)</c> or <c>Failure(error)</c>.</summary>
    public override string ToString() => _error is null ? $"Success({_value})" : $"Failure({_error})";
}

/// <summary>Factories for <see cref="Result{T}"/>.</summary>
public static class Result
{
    /// <summary>A success.</summary>
    public static Result<T> Success<T>(T value) => new(value, null);

    /// <summary>A success without a value.</summary>
    public static Result<Unit> Success() => new(Unit.Value, null);

    /// <summary>A failure.</summary>
    public static Result<T> Failure<T>(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }
}
