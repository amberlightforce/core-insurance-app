using System;

namespace CoreIns.Rules;

/// <summary>Resource limits that bound every rule (no runaway rules). All values must be positive.</summary>
public sealed record RuleLimits
{
    /// <summary>Default limits.</summary>
    public static RuleLimits Default { get; } = new();

    /// <summary>Maximum expression source length in characters.</summary>
    public int MaxExpressionLength { get; init; } = 8_192;

    /// <summary>Maximum syntax-tree depth (and parser nesting).</summary>
    public int MaxAstDepth { get; init; } = 200;

    /// <summary>Cost budget: maximum evaluation steps (one per node evaluation plus one per comprehension iteration).</summary>
    public long MaxEvaluationSteps { get; init; } = 100_000;

    /// <summary>Maximum size of a list or map produced during evaluation.</summary>
    public int MaxCollectionSize { get; init; } = 10_000;

    /// <summary>Maximum length of a string produced during evaluation.</summary>
    public int MaxStringLength { get; init; } = 65_536;

    /// <summary>Maximum length of a regular-expression pattern.</summary>
    public int MaxRegexPatternLength { get; init; } = 512;

    /// <summary>Timeout for a single regular-expression match (the engine is also linear-time: RegexOptions.NonBacktracking).</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromTicks(50 * TimeSpan.TicksPerMillisecond);

    /// <summary>Maximum number of trace entries recorded per evaluation (further entries are dropped and the trace is marked truncated).</summary>
    public int MaxTraceEntries { get; init; } = 10_000;

    internal void Validate()
    {
        if (MaxExpressionLength <= 0 || MaxAstDepth <= 0 || MaxEvaluationSteps <= 0 || MaxCollectionSize <= 0
            || MaxStringLength <= 0 || MaxRegexPatternLength <= 0 || RegexTimeout <= TimeSpan.Zero || MaxTraceEntries < 0)
        {
            throw new ArgumentException("rule limits must be positive");
        }
    }
}
