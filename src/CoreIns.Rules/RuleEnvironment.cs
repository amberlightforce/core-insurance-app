using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CoreIns.Rules.Checking;
using CoreIns.Rules.Runtime;
using CoreIns.Rules.Syntax;

namespace CoreIns.Rules;

/// <summary>
/// A compilation environment: the declared input schema, the host functions and the limits. Immutable and
/// thread-safe; holds a cache of compiled expressions.
/// </summary>
public sealed class RuleEnvironment
{
    private readonly Dictionary<string, HostFunction> _functions;
    private readonly ConcurrentDictionary<CacheKey, Lazy<CompileResult>> _cache = new();

    private RuleEnvironment(InputSchema schema, RuleLimits limits, Dictionary<string, HostFunction> functions)
    {
        Schema = schema;
        Limits = limits;
        _functions = functions;
    }

    /// <summary>The declared inputs.</summary>
    public InputSchema Schema { get; }

    /// <summary>The limits applied to every expression.</summary>
    public RuleLimits Limits { get; }

    /// <summary>Host functions available to expressions.</summary>
    public IReadOnlyCollection<HostFunction> Functions => _functions.Values;

    /// <summary>Number of cached compilations.</summary>
    public int CachedCount => _cache.Count;

    /// <summary>Creates an environment.</summary>
    public static RuleEnvironment Create(InputSchema schema, RuleLimits? limits = null, IEnumerable<HostFunction>? functions = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        limits ??= RuleLimits.Default;
        limits.Validate();
        var map = new Dictionary<string, HostFunction>(StringComparer.Ordinal);
        foreach (var f in functions ?? Enumerable.Empty<HostFunction>())
        {
            if (!map.TryAdd(f.Name, f))
            {
                throw new ArgumentException($"duplicate host function '{f.Name}'", nameof(functions));
            }
        }

        return new RuleEnvironment(schema, limits, map);
    }

    /// <summary>Compiles without throwing. <paramref name="expectedType"/>, when given, is the required result type.</summary>
    public CompileResult TryCompile(string source, RuleType? expectedType = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var ast = Parser.Parse(source, Limits);
            var binder = new Binder(Schema, _functions, Limits);
            var root = binder.Bind(ast);
            var resultType = root.Type;
            if (expectedType is not null)
            {
                bool nullLiteralIntoNonNull = root.Type.Kind == RuleTypeKind.Null && !expectedType.IsNullable && expectedType.Kind != RuleTypeKind.Null;
                if (!Binder.IsAssignable(root.Type, expectedType) || nullLiteralIntoNonNull)
                {
                    throw new CompileFailure(RuleErrorCode.ResultTypeMismatch, ast.Start, $"expression has type {root.Type} but {expectedType} is required");
                }

                root = Binder.Coerce(root, expectedType);
                if (!expectedType.IsNullable && expectedType.Kind is not (RuleTypeKind.Null or RuleTypeKind.Dyn))
                {
                    root = new NonNullNode(root);
                }

                resultType = expectedType;
            }

            var canonical = CanonicalPrinter.Print(ast);
            string fingerprint = EnvironmentFingerprint(binder.UsedHostFunctions);
            return CompileResult.Ok(new CompiledExpression(this, source, resultType, root, binder.SlotCount, canonical, fingerprint));
        }
        catch (CompileFailure f)
        {
            return CompileResult.Failed(f.ToError(source));
        }
    }

    /// <summary>Compiles, throwing <see cref="RuleCompileException"/> on error.</summary>
    public CompiledExpression Compile(string source, RuleType? expectedType = null) => TryCompile(source, expectedType).GetOrThrow();

    /// <summary>Compiles through the environment cache (same source and expected type give the same instance).</summary>
    public CompiledExpression GetOrCompile(string source, RuleType? expectedType = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lazy = _cache.GetOrAdd(new CacheKey(source, expectedType, expectedType?.IsNullable == true), k => new Lazy<CompileResult>(() => TryCompile(k.Source, k.Expected)));
        return lazy.Value.GetOrThrow();
    }

    /// <summary>A derived environment with the same functions and limits over an extended schema.</summary>
    internal RuleEnvironment WithSchema(InputSchema schema) => new(schema, Limits, _functions);

    /// <summary>
    /// Canonical text of what an expression's meaning depends on besides its syntax: the input schema (types,
    /// nullability, object shapes) and the signatures of the host functions it calls.
    /// </summary>
    internal string EnvironmentFingerprint(IEnumerable<HostFunction> usedFunctions) =>
        CanonicalPrinter.LengthPrefixed(new[] { Schema.Fingerprint() }.Concat(usedFunctions.Select(f => f.Signature).Order(StringComparer.Ordinal)));

    private readonly record struct CacheKey(string Source, RuleType? Expected, bool ExpectedNullable);
}

/// <summary>Outcome of <see cref="RuleEnvironment.TryCompile"/>.</summary>
public sealed class CompileResult
{
    private CompileResult(CompiledExpression? expression, IReadOnlyList<RuleCompileError> errors)
    {
        Expression = expression;
        Errors = errors;
    }

    /// <summary>The compiled expression when successful.</summary>
    public CompiledExpression? Expression { get; }

    /// <summary>Compile errors (empty when successful).</summary>
    public IReadOnlyList<RuleCompileError> Errors { get; }

    /// <summary>Whether compilation succeeded.</summary>
    public bool IsSuccess => Expression is not null;

    /// <summary>Returns the expression or throws <see cref="RuleCompileException"/>.</summary>
    public CompiledExpression GetOrThrow() => Expression ?? throw new RuleCompileException(Errors);

    internal static CompileResult Ok(CompiledExpression e) => new(e, Array.Empty<RuleCompileError>());

    internal static CompileResult Failed(RuleCompileError e) => new(null, new[] { e });
}

/// <summary>Per-call evaluation options.</summary>
public sealed record EvaluationOptions
{
    /// <summary>No trace, environment cost budget.</summary>
    public static EvaluationOptions Default { get; } = new();

    /// <summary>Record an explanation trace of every sub-expression and its value.</summary>
    public bool Trace { get; init; }

    /// <summary>Optional lower cost budget for this call (cannot raise the environment budget).</summary>
    public long? MaxSteps { get; init; }

    /// <summary>Cancels the evaluation (typed RULE-CANCELLED); the wall-clock deadline applies in addition.</summary>
    public CancellationToken Cancellation { get; init; }
}

/// <summary>One trace entry: a sub-expression (by source span) and the value it produced, in evaluation order.</summary>
/// <param name="NodeId">Syntax node id (stable for a given source).</param>
/// <param name="Position">Start of the sub-expression in the source.</param>
/// <param name="Expression">The sub-expression's source text.</param>
/// <param name="Value">The value it evaluated to.</param>
public sealed record TraceEntry(int NodeId, SourcePosition Position, string Expression, RuleValue Value)
{
    /// <inheritdoc />
    public override string ToString() => Expression + " = " + Value;
}

/// <summary>Result of evaluating a compiled expression. Rules fail closed: check <see cref="IsSuccess"/>.</summary>
public sealed class EvaluationResult
{
    internal EvaluationResult(RuleValue? value, RuleEvaluationError? error, IReadOnlyList<TraceEntry> trace, bool traceTruncated, long steps)
    {
        Value = value;
        Error = error;
        Trace = trace;
        TraceTruncated = traceTruncated;
        StepsUsed = steps;
    }

    /// <summary>Whether evaluation succeeded.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>The value when successful.</summary>
    public RuleValue? Value { get; }

    /// <summary>The typed error when evaluation failed.</summary>
    public RuleEvaluationError? Error { get; }

    /// <summary>Explanation trace (empty unless requested).</summary>
    public IReadOnlyList<TraceEntry> Trace { get; }

    /// <summary>Whether the trace hit <see cref="RuleLimits.MaxTraceEntries"/>.</summary>
    public bool TraceTruncated { get; }

    /// <summary>Evaluation steps consumed (cost).</summary>
    public long StepsUsed { get; }

    /// <summary>Returns the value or throws <see cref="RuleEvaluationException"/>.</summary>
    public RuleValue GetValueOrThrow() => Error is null ? Value! : throw new RuleEvaluationException(Error);
}

/// <summary>
/// A compiled, type-checked expression. Immutable and thread-safe: evaluate it concurrently from any number of
/// threads. <see cref="ContentHash"/> identifies its canonical content for the configuration hash.
/// </summary>
public sealed class CompiledExpression
{
    internal CompiledExpression(RuleEnvironment environment, string source, RuleType resultType, BoundNode root, int slotCount, string canonicalText, string environmentFingerprint)
    {
        EnvironmentFingerprint = environmentFingerprint;
        Environment = environment;
        Source = source;
        ResultType = resultType;
        Root = root;
        SlotCount = slotCount;
        CanonicalText = canonicalText;
        ContentHash = CanonicalPrinter.Hash(CanonicalPrinter.LengthPrefixed(new[] { canonicalText, resultType.ToString(), environmentFingerprint }));
    }

    /// <summary>The environment the expression was compiled in.</summary>
    public RuleEnvironment Environment { get; }

    /// <summary>Source text.</summary>
    public string Source { get; }

    /// <summary>Static result type.</summary>
    public RuleType ResultType { get; }

    /// <summary>Canonical text of the syntax tree (whitespace and comments removed, language version prefixed).</summary>
    public string CanonicalText { get; }

    /// <summary>Input schema and host-function signatures the expression was type-checked against.</summary>
    public string EnvironmentFingerprint { get; }

    /// <summary>
    /// Lower-case hex SHA-256 over the length-prefixed <see cref="CanonicalText"/>, result type and
    /// <see cref="EnvironmentFingerprint"/>: equal hashes mean equal behaviour.
    /// </summary>
    public string ContentHash { get; }

    internal BoundNode Root { get; }

    internal int SlotCount { get; }

    /// <summary>Evaluates against inputs built from this expression's schema.</summary>
    public EvaluationResult Evaluate(RuleInputs inputs, EvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!ReferenceEquals(inputs.Schema, Environment.Schema))
        {
            throw new ArgumentException("the inputs were built for a different input schema", nameof(inputs));
        }

        options ??= EvaluationOptions.Default;
        long budget = Math.Min(options.MaxSteps ?? Environment.Limits.MaxEvaluationSteps, Environment.Limits.MaxEvaluationSteps);
        var state = new EvalState(SlotCount, Environment.Limits, budget, options.Trace, options.Cancellation);
        inputs.CopyTo(state.Slots);
        return Run(state, context: null);
    }

    /// <summary>Evaluates and returns the value, throwing <see cref="RuleEvaluationException"/> on error.</summary>
    public RuleValue EvaluateOrThrow(RuleInputs inputs) => Evaluate(inputs).GetValueOrThrow();

    internal EvaluationResult Run(EvalState state, string? context)
    {
        var raw = state.Trace;
        if (raw is not null)
        {
            raw.Clear();
        }

        try
        {
            var value = Root.Eval(state);
            return new EvaluationResult(value, null, BuildTrace(raw), state.TraceTruncated, state.Steps);
        }
        catch (EvalFailure f)
        {
            var position = f.Node is null ? (SourcePosition?)null : SourcePosition.FromOffset(Source, f.Node.Syntax.Start);
            var error = new RuleEvaluationError(f.Code, f.Message, position, context);
            return new EvaluationResult(null, error, BuildTrace(raw), state.TraceTruncated, state.Steps);
        }
    }

    private TraceEntry[] BuildTrace(List<(BoundNode Node, RuleValue Value)>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<TraceEntry>();
        }

        var entries = new TraceEntry[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            var (node, value) = raw[i];
            var syntax = node.Syntax;
            entries[i] = new TraceEntry(syntax.Id, SourcePosition.FromOffset(Source, syntax.Start), Source[syntax.Start..syntax.End], value);
        }

        return entries;
    }

    /// <inheritdoc />
    public override string ToString() => Source;
}
