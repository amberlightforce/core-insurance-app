using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreIns.Rules.DecisionTables;

/// <summary>A named value (column, variable or output).</summary>
public sealed record NamedValue(string Name, RuleValue Value)
{
    /// <inheritdoc />
    public override string ToString() => Name + " = " + Value;
}

/// <summary>A matched rule with its outputs.</summary>
public sealed class DecisionMatch
{
    internal DecisionMatch(string ruleId, int rowIndex, IReadOnlyList<NamedValue> outputs)
    {
        RuleId = ruleId;
        RowIndex = rowIndex;
        Outputs = outputs;
    }

    /// <summary>Rule id.</summary>
    public string RuleId { get; }

    /// <summary>Zero-based row index.</summary>
    public int RowIndex { get; }

    /// <summary>Output values in column order.</summary>
    public IReadOnlyList<NamedValue> Outputs { get; }

    /// <summary>Gets an output by column name.</summary>
    public RuleValue Output(string name) =>
        Outputs.FirstOrDefault(o => o.Name == name)?.Value ?? throw new ArgumentException($"no output column '{name}'", nameof(name));
}

/// <summary>Result of one cell during evaluation.</summary>
public enum CellOutcome
{
    /// <summary>The cell matched.</summary>
    Matched,

    /// <summary>The cell did not match (later cells of the row are not evaluated).</summary>
    NotMatched,

    /// <summary>The cell is "any" (<c>-</c>).</summary>
    Any,

    /// <summary>Not evaluated (short-circuited).</summary>
    NotEvaluated,
}

/// <summary>Trace of one rule row.</summary>
public sealed record RuleTrace(string RuleId, int RowIndex, IReadOnlyList<CellOutcome> Cells, bool Matched);

/// <summary>Detailed trace of one expression (variable, column, cell or output).</summary>
public sealed record ExpressionTrace(string Label, string Source, IReadOnlyList<TraceEntry> Entries);

/// <summary>Evaluation trace for explanation and audit (REQ-PLT-177, REQ-UW-061).</summary>
public sealed class DecisionTrace
{
    internal DecisionTrace(
        DecisionTableMetadata metadata,
        string contentHash,
        HitPolicy hitPolicy,
        IReadOnlyList<NamedValue> variables,
        IReadOnlyList<NamedValue> inputs,
        IReadOnlyList<RuleTrace> rules,
        IReadOnlyList<DecisionMatch> matches,
        IReadOnlyList<ExpressionTrace> expressions,
        long stepsUsed)
    {
        TableId = metadata.TableId;
        Version = metadata.Version;
        ContentHash = contentHash;
        HitPolicy = hitPolicy;
        Variables = variables;
        Inputs = inputs;
        Rules = rules;
        Matches = matches;
        Expressions = expressions;
        StepsUsed = stepsUsed;
    }

    /// <summary>Table id.</summary>
    public string TableId { get; }

    /// <summary>Table version.</summary>
    public string Version { get; }

    /// <summary>Version content hash.</summary>
    public string ContentHash { get; }

    /// <summary>Hit policy applied.</summary>
    public HitPolicy HitPolicy { get; }

    /// <summary>Computed variable values.</summary>
    public IReadOnlyList<NamedValue> Variables { get; }

    /// <summary>Input column values.</summary>
    public IReadOnlyList<NamedValue> Inputs { get; }

    /// <summary>Per-row cell outcomes, for the rows that were evaluated.</summary>
    public IReadOnlyList<RuleTrace> Rules { get; }

    /// <summary>Selected matches with outputs.</summary>
    public IReadOnlyList<DecisionMatch> Matches { get; }

    /// <summary>Matched rule ids (selected by the hit policy).</summary>
    public IReadOnlyList<string> MatchedRuleIds => Matches.Select(m => m.RuleId).ToArray();

    /// <summary>Sub-expression traces (only with <see cref="DecisionEvaluationOptions.DetailedTrace"/>).</summary>
    public IReadOnlyList<ExpressionTrace> Expressions { get; }

    /// <summary>Evaluation steps consumed.</summary>
    public long StepsUsed { get; }
}

/// <summary>Options for evaluating a decision table.</summary>
public sealed record DecisionEvaluationOptions
{
    /// <summary>Default options.</summary>
    public static DecisionEvaluationOptions Default { get; } = new();

    /// <summary>Also record the sub-expression trace of every evaluated expression.</summary>
    public bool DetailedTrace { get; init; }

    /// <summary>Cancels the evaluation (typed RULE-CANCELLED); the wall-clock deadline applies in addition.</summary>
    public CancellationToken Cancellation { get; init; }
}

/// <summary>Result of evaluating a decision table. Fails closed: check <see cref="IsSuccess"/>.</summary>
public sealed class DecisionResult
{
    internal DecisionResult(RuleEvaluationError? error, IReadOnlyList<DecisionMatch> matches, DecisionTrace trace)
    {
        Error = error;
        Matches = matches;
        Trace = trace;
    }

    /// <summary>Whether evaluation succeeded.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>The typed error, if any.</summary>
    public RuleEvaluationError? Error { get; }

    /// <summary>Selected matches (empty when no rule matched or on error).</summary>
    public IReadOnlyList<DecisionMatch> Matches { get; }

    /// <summary>Matched rule ids.</summary>
    public IReadOnlyList<string> MatchedRuleIds => Matches.Select(m => m.RuleId).ToArray();

    /// <summary>The single match for FIRST/UNIQUE/PRIORITY, or null when none matched.</summary>
    public DecisionMatch? Match => Matches.Count > 0 ? Matches[0] : null;

    /// <summary>The explanation trace.</summary>
    public DecisionTrace Trace { get; }
}

/// <summary>A test case shipped with a table version (REQ-PLT-176).</summary>
/// <param name="Name">Case name.</param>
/// <param name="Inputs">Inputs (built from the table's input schema).</param>
/// <param name="ExpectedRuleIds">Expected matched rule ids, in order.</param>
public sealed record DecisionTableTestCase(string Name, RuleInputs Inputs, IReadOnlyList<string> ExpectedRuleIds)
{
    /// <summary>Optional expected outputs per match (only the listed columns are compared).</summary>
    public IReadOnlyList<IReadOnlyList<NamedValue>>? ExpectedOutputs { get; init; }

    /// <summary>When set, the case expects evaluation to fail with this code.</summary>
    public RuleErrorCode? ExpectedError { get; init; }
}

/// <summary>Outcome of one test case.</summary>
public sealed record DecisionTableTestOutcome(string Name, bool Passed, string? Failure, DecisionResult Result);

/// <summary>Whether a version may be activated: every test passes (and, optionally, every rule is covered).</summary>
public sealed record ActivationReadiness(bool CanActivate, string? RefusalReason, IReadOnlyList<DecisionTableTestOutcome> Outcomes, IReadOnlyList<string> UncoveredRuleIds)
{
    /// <summary>The failing cases.</summary>
    public IReadOnlyList<DecisionTableTestOutcome> FailingCases => Outcomes.Where(o => !o.Passed).ToArray();
}
