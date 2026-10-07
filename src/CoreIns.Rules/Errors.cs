using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CoreIns.Rules;

/// <summary>Typed error codes for compilation and evaluation. <see cref="RuleErrorCodeExtensions.ToCode"/> gives the stable text code.</summary>
public enum RuleErrorCode
{
    // ---- compile time ----
    Syntax,
    ExpressionTooLong,
    DepthExceeded,
    ReservedWord,
    InvalidLiteral,
    UnsupportedFeature,
    UnknownIdentifier,
    UnknownField,
    UnknownFunction,
    NonDeterministic,
    NoMatchingOverload,
    TypeMismatch,
    ResultTypeMismatch,
    InvalidRegex,
    InvalidArgument,
    InvalidDefinition,

    // ---- evaluation time ----
    DivisionByZero,
    Overflow,
    NullValue,
    IndexOutOfRange,
    NoSuchKey,
    DuplicateKey,
    CostExceeded,
    RegexTimeout,
    LimitExceeded,
    InvalidValue,
    HostFunctionFailed,
    InputInvalid,

    // ---- decision tables ----
    TableNotActive,
    HitPolicyViolation,
}

/// <summary>Maps <see cref="RuleErrorCode"/> to the stable code strings used in Problem Details and audit.</summary>
public static class RuleErrorCodeExtensions
{
    /// <summary>Returns the stable text code, for example <c>RULE-UNKNOWN-FIELD</c>.</summary>
    public static string ToCode(this RuleErrorCode code) => code switch
    {
        RuleErrorCode.Syntax => "RULE-SYNTAX",
        RuleErrorCode.ExpressionTooLong => "RULE-EXPRESSION-TOO-LONG",
        RuleErrorCode.DepthExceeded => "RULE-DEPTH-EXCEEDED",
        RuleErrorCode.ReservedWord => "RULE-RESERVED-WORD",
        RuleErrorCode.InvalidLiteral => "RULE-INVALID-LITERAL",
        RuleErrorCode.UnsupportedFeature => "RULE-UNSUPPORTED",
        RuleErrorCode.UnknownIdentifier => "RULE-UNKNOWN-IDENTIFIER",
        RuleErrorCode.UnknownField => "RULE-UNKNOWN-FIELD",
        RuleErrorCode.UnknownFunction => "RULE-UNKNOWN-FUNCTION",
        RuleErrorCode.NonDeterministic => "RULE-NONDETERMINISTIC",
        RuleErrorCode.NoMatchingOverload => "RULE-NO-MATCHING-OVERLOAD",
        RuleErrorCode.TypeMismatch => "RULE-TYPE-MISMATCH",
        RuleErrorCode.ResultTypeMismatch => "RULE-RESULT-TYPE-MISMATCH",
        RuleErrorCode.InvalidRegex => "RULE-INVALID-REGEX",
        RuleErrorCode.InvalidArgument => "RULE-INVALID-ARGUMENT",
        RuleErrorCode.InvalidDefinition => "RULE-INVALID-DEFINITION",
        RuleErrorCode.DivisionByZero => "RULE-DIVISION-BY-ZERO",
        RuleErrorCode.Overflow => "RULE-OVERFLOW",
        RuleErrorCode.NullValue => "RULE-NULL-VALUE",
        RuleErrorCode.IndexOutOfRange => "RULE-INDEX-OUT-OF-RANGE",
        RuleErrorCode.NoSuchKey => "RULE-NO-SUCH-KEY",
        RuleErrorCode.DuplicateKey => "RULE-DUPLICATE-KEY",
        RuleErrorCode.CostExceeded => "RULE-COST-EXCEEDED",
        RuleErrorCode.RegexTimeout => "RULE-REGEX-TIMEOUT",
        RuleErrorCode.LimitExceeded => "RULE-LIMIT-EXCEEDED",
        RuleErrorCode.InvalidValue => "RULE-INVALID-VALUE",
        RuleErrorCode.HostFunctionFailed => "RULE-HOST-FUNCTION-FAILED",
        RuleErrorCode.InputInvalid => "RULE-INPUT-INVALID",
        RuleErrorCode.TableNotActive => "PLT-ERR-TABLE-NOT-ACTIVE",
        RuleErrorCode.HitPolicyViolation => "PLT-ERR-HIT-POLICY-VIOLATION",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "unknown rule error code"),
    };
}

/// <summary>A position in expression source text (offset is 0-based; line and column are 1-based).</summary>
public readonly record struct SourcePosition(int Offset, int Line, int Column)
{
    /// <summary>Computes the line and column of <paramref name="offset"/> in <paramref name="source"/>.</summary>
    public static SourcePosition FromOffset(string source, int offset)
    {
        ArgumentNullException.ThrowIfNull(source);
        offset = Math.Clamp(offset, 0, source.Length);
        int line = 1, column = 1;
        for (int i = 0; i < offset; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return new SourcePosition(offset, line, column);
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Line}:{Column}");
}

/// <summary>A compile-time (authoring or activation) error.</summary>
/// <param name="Code">Typed error code.</param>
/// <param name="Message">Human-readable message naming the offending input, field or function.</param>
/// <param name="Position">Position in the expression source, when the error is tied to one.</param>
/// <param name="Context">Where the expression lives (for example a decision-table cell), when known.</param>
public sealed record RuleCompileError(RuleErrorCode Code, string Message, SourcePosition? Position, string? Context = null)
{
    /// <summary>Stable text code, for example <c>RULE-UNKNOWN-IDENTIFIER</c>.</summary>
    public string CodeText => Code.ToCode();

    /// <inheritdoc />
    public override string ToString()
    {
        string where = Position is { } p ? " at " + p.ToString() : string.Empty;
        string ctx = Context is null ? string.Empty : " [" + Context + "]";
        return CodeText + where + ctx + ": " + Message;
    }
}

/// <summary>A typed evaluation error. Rules fail closed: callers must treat any error as "no decision".</summary>
public sealed record RuleEvaluationError(RuleErrorCode Code, string Message, SourcePosition? Position, string? Context = null)
{
    /// <summary>Stable text code, for example <c>RULE-DIVISION-BY-ZERO</c>.</summary>
    public string CodeText => Code.ToCode();

    /// <inheritdoc />
    public override string ToString()
    {
        string where = Position is { } p ? " at " + p.ToString() : string.Empty;
        string ctx = Context is null ? string.Empty : " [" + Context + "]";
        return CodeText + where + ctx + ": " + Message;
    }
}

/// <summary>Thrown when an expression or decision table does not compile.</summary>
public sealed class RuleCompileException : Exception
{
    /// <summary>Creates the exception from one or more errors.</summary>
    public RuleCompileException(IReadOnlyList<RuleCompileError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>The compile errors (at least one).</summary>
    public IReadOnlyList<RuleCompileError> Errors { get; }

    private static string BuildMessage(IReadOnlyList<RuleCompileError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count == 0
            ? "rule compilation failed"
            : string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
    }
}

/// <summary>Thrown by the <c>...OrThrow</c> evaluation helpers.</summary>
public sealed class RuleEvaluationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RuleEvaluationException(RuleEvaluationError error)
        : base(error?.ToString())
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>The evaluation error.</summary>
    public RuleEvaluationError Error { get; }
}

/// <summary>Thrown when an input value does not conform to the declared schema.</summary>
public sealed class RuleInputException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    public RuleInputException(string path, string message)
        : base(path + ": " + message)
    {
        Path = path;
    }

    /// <summary>Always <see cref="RuleErrorCode.InputInvalid"/>.</summary>
    public RuleErrorCode Code => RuleErrorCode.InputInvalid;

    /// <summary>Path of the offending input, for example <c>vehicle.value</c>.</summary>
    public string Path { get; }
}

/// <summary>Internal compile failure carrying an offset; converted to <see cref="RuleCompileError"/> at the API boundary.</summary>
internal sealed class CompileFailure : Exception
{
    public CompileFailure(RuleErrorCode code, int offset, string message)
        : base(message)
    {
        Code = code;
        Offset = offset;
    }

    public RuleErrorCode Code { get; }

    public int Offset { get; }

    public RuleCompileError ToError(string source, string? context = null) =>
        new(Code, Message, SourcePosition.FromOffset(source, Offset), context);
}
