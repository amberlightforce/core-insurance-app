using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CoreIns.Rules.Runtime;
using CoreIns.Rules.Syntax;

namespace CoreIns.Rules.DecisionTables;

/// <summary>
/// A compiled, immutable, thread-safe decision-table version evaluated with the rule-expression engine
/// (REQ-PLT-174 … 177). Create with <see cref="Compile"/>.
/// </summary>
public sealed class CompiledDecisionTable
{
    private readonly RuleEnvironment _baseEnvironment;
    private readonly CompiledExpression[] _variables;
    private readonly CompiledExpression[] _inputs;
    private readonly CompiledExpression?[][] _conditions;
    private readonly CompiledExpression[][] _outputs;
    private readonly int _slotCount;
    private readonly int _baseSlots;

    private CompiledDecisionTable(
        DecisionTableDefinition definition,
        RuleEnvironment baseEnvironment,
        CompiledExpression[] variables,
        CompiledExpression[] inputs,
        CompiledExpression?[][] conditions,
        CompiledExpression[][] outputs,
        string canonicalText)
    {
        Definition = definition;
        _baseEnvironment = baseEnvironment;
        _variables = variables;
        _inputs = inputs;
        _conditions = conditions;
        _outputs = outputs;
        _baseSlots = baseEnvironment.Schema.Variables.Count;
        _slotCount = variables.Concat(inputs).Concat(conditions.SelectMany(c => c).OfType<CompiledExpression>()).Concat(outputs.SelectMany(o => o))
            .Select(e => e.SlotCount)
            .DefaultIfEmpty(_baseSlots + variables.Length + inputs.Length)
            .Max();
        _slotCount = Math.Max(_slotCount, _baseSlots + variables.Length + inputs.Length);
        CanonicalText = canonicalText;
        ContentHash = CanonicalPrinter.Hash(canonicalText);
    }

    /// <summary>The definition.</summary>
    public DecisionTableDefinition Definition { get; }

    /// <summary>Versioning metadata.</summary>
    public DecisionTableMetadata Metadata => Definition.Metadata;

    /// <summary>The input schema callers build inputs from.</summary>
    public InputSchema InputSchema => _baseEnvironment.Schema;

    /// <summary>Canonical content text (excludes metadata).</summary>
    public string CanonicalText { get; }

    /// <summary>Lower-case hex SHA-256 of <see cref="CanonicalText"/>: the version content hash (REQ-PLT-175).</summary>
    public string ContentHash { get; }

    /// <summary>
    /// Compiles and type-checks every expression of the table against <paramref name="environment"/>'s input schema.
    /// Reports all errors at once (each with its cell context) via <see cref="RuleCompileException"/>.
    /// </summary>
    public static CompiledDecisionTable Compile(DecisionTableDefinition definition, RuleEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(environment);
        var errors = new List<RuleCompileError>();
        ValidateStructure(definition, environment.Schema, errors);
        if (errors.Count > 0)
        {
            throw new RuleCompileException(errors);
        }

        var extended = environment.WithSchema(environment.Schema.Extend(
            definition.Variables.Select(v => (v.Name, v.Type)).Concat(definition.Inputs.Select(i => (i.Name, i.Type)))));

        // Variables may only reference earlier variables: compile each against a schema prefix.
        var variables = new CompiledExpression[definition.Variables.Count];
        for (int i = 0; i < variables.Length; i++)
        {
            var v = definition.Variables[i];
            var prefixEnv = environment.WithSchema(environment.Schema.Extend(definition.Variables.Take(i).Select(x => (x.Name, x.Type))));
            variables[i] = CompileOne(prefixEnv, v.Expression, v.Type, $"variable '{v.Name}'", errors)!;
        }

        var varsEnv = environment.WithSchema(environment.Schema.Extend(definition.Variables.Select(x => (x.Name, x.Type))));
        var inputs = definition.Inputs
            .Select(c => CompileOne(varsEnv, c.Expression, c.Type, $"input column '{c.Name}'", errors)!)
            .ToArray();

        var conditions = new CompiledExpression?[definition.Rules.Count][];
        var outputs = new CompiledExpression[definition.Rules.Count][];
        var canonical = new StringBuilder();
        canonical.Append("decision-table/").Append(RuleLanguage.Version).Append(";hit=").Append(definition.HitPolicy.ToString().ToUpperInvariant()).Append('\n');
        for (int i = 0; i < definition.Variables.Count; i++)
        {
            var v = definition.Variables[i];
            canonical.Append("var ").Append(v.Name).Append(':').Append(v.Type).Append('=').Append(variables[i]?.CanonicalText).Append('\n');
        }

        for (int i = 0; i < definition.Inputs.Count; i++)
        {
            var c = definition.Inputs[i];
            canonical.Append("in ").Append(c.Name).Append(':').Append(c.Type).Append('=').Append(inputs[i]?.CanonicalText).Append('\n');
        }

        foreach (var o in definition.Outputs)
        {
            canonical.Append("out ").Append(o.Name).Append(':').Append(o.Type).Append('\n');
        }

        for (int r = 0; r < definition.Rules.Count; r++)
        {
            var rule = definition.Rules[r];
            conditions[r] = new CompiledExpression?[definition.Inputs.Count];
            outputs[r] = new CompiledExpression[definition.Outputs.Count];
            canonical.Append("rule ").Append(rule.RuleId).Append(" prio=").Append(rule.Priority.ToString(CultureInfo.InvariantCulture));
            for (int c = 0; c < definition.Inputs.Count; c++)
            {
                var column = definition.Inputs[c];
                string? text = ConditionCell.Translate(rule.Conditions[c], column.Name);
                conditions[r][c] = text is null
                    ? null
                    : CompileOne(extended, text, RuleType.Bool, $"rule '{rule.RuleId}' condition '{column.Name}' ({rule.Conditions[c].Trim()})", errors);
                canonical.Append(" | ").Append(conditions[r][c]?.CanonicalText ?? "-");
            }

            canonical.Append(" =>");
            for (int o = 0; o < definition.Outputs.Count; o++)
            {
                var column = definition.Outputs[o];
                outputs[r][o] = CompileOne(extended, rule.Outputs[o], column.Type, $"rule '{rule.RuleId}' output '{column.Name}'", errors)!;
                canonical.Append(" | ").Append(outputs[r][o]?.CanonicalText);
            }

            canonical.Append('\n');
        }

        if (errors.Count > 0)
        {
            throw new RuleCompileException(errors);
        }

        return new CompiledDecisionTable(definition, environment, variables, inputs, conditions, outputs, canonical.ToString());
    }

    private static CompiledExpression? CompileOne(RuleEnvironment env, string source, RuleType type, string context, List<RuleCompileError> errors)
    {
        var result = env.TryCompile(source, type);
        if (result.IsSuccess)
        {
            return result.Expression;
        }

        errors.AddRange(result.Errors.Select(e => e with { Context = context }));
        return null;
    }

    private static void ValidateStructure(DecisionTableDefinition d, InputSchema schema, List<RuleCompileError> errors)
    {
        void Error(string message) => errors.Add(new RuleCompileError(RuleErrorCode.InvalidDefinition, message, null, d.Metadata?.TableId));

        if (d.Metadata is null || string.IsNullOrWhiteSpace(d.Metadata.TableId) || string.IsNullOrWhiteSpace(d.Metadata.Version))
        {
            Error("table id and version are required");
        }

        if (d.Metadata is { EffectiveTo: { } to } && to <= d.Metadata.EffectiveFrom)
        {
            Error("effective-to must be after effective-from (half-open range)");
        }

        if (d.Inputs is null || d.Outputs is null || d.Rules is null || d.Variables is null)
        {
            Error("inputs, outputs, rules and variables must not be null");
            return;
        }

        if (d.Outputs.Count == 0)
        {
            Error("a decision table needs at least one output column");
        }

        var names = new HashSet<string>(schema.Variables.Select(v => v.Name), StringComparer.Ordinal);
        foreach (var name in d.Variables.Select(v => v.Name).Concat(d.Inputs.Select(i => i.Name)))
        {
            if (!Identifiers.IsValid(name))
            {
                Error($"'{name}' is not a valid column or variable name");
            }
            else if (!names.Add(name))
            {
                Error($"name '{name}' is declared more than once (inputs, variables and columns share one namespace)");
            }
        }

        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in d.Outputs)
        {
            if (!Identifiers.IsValid(o.Name) || !outputNames.Add(o.Name))
            {
                Error($"output column '{o.Name}' is invalid or duplicated");
            }
        }

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in d.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.RuleId) || !ruleIds.Add(r.RuleId))
            {
                Error($"rule id '{r.RuleId}' is empty or duplicated");
            }

            if (r.Conditions is null || r.Conditions.Count != d.Inputs.Count)
            {
                Error(string.Create(CultureInfo.InvariantCulture, $"rule '{r.RuleId}' must have exactly {d.Inputs.Count} condition cell(s)"));
            }

            if (r.Outputs is null || r.Outputs.Count != d.Outputs.Count)
            {
                Error(string.Create(CultureInfo.InvariantCulture, $"rule '{r.RuleId}' must have exactly {d.Outputs.Count} output expression(s)"));
            }
        }
    }

    /// <summary>
    /// Evaluates the table for production use: refuses (PLT-ERR-TABLE-NOT-ACTIVE) unless the version is Active and
    /// effective on <paramref name="asOf"/>. Deterministic: the same version and inputs always give the same outputs,
    /// matched rules and trace.
    /// </summary>
    public DecisionResult Evaluate(RuleInputs inputs, DateOnly asOf, DecisionEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var m = Metadata;
        string? refusal = null;
        if (m.Status != DecisionTableStatus.Active)
        {
            refusal = $"decision table '{m.TableId}' version {m.Version} is {m.Status}, not Active";
        }
        else if (asOf < m.EffectiveFrom || (m.EffectiveTo is { } to && asOf >= to))
        {
            refusal = $"decision table '{m.TableId}' version {m.Version} is not effective on {DateValue.Format(asOf)}";
        }

        if (refusal is not null)
        {
            var error = new RuleEvaluationError(RuleErrorCode.TableNotActive, refusal, null, m.TableId);
            return new DecisionResult(error, Array.Empty<DecisionMatch>(), EmptyTrace(0));
        }

        return EvaluateCore(inputs, options ?? DecisionEvaluationOptions.Default);
    }

    /// <summary>Evaluates regardless of status and effectivity (authoring, test cases before activation).</summary>
    public DecisionResult EvaluateForTesting(RuleInputs inputs, DecisionEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return EvaluateCore(inputs, options ?? DecisionEvaluationOptions.Default);
    }

    private DecisionTrace EmptyTrace(long steps) => new(
        Metadata, ContentHash, Definition.HitPolicy, Array.Empty<NamedValue>(), Array.Empty<NamedValue>(),
        Array.Empty<RuleTrace>(), Array.Empty<DecisionMatch>(), Array.Empty<ExpressionTrace>(), steps);

    private DecisionResult EvaluateCore(RuleInputs inputs, DecisionEvaluationOptions options)
    {
        if (!ReferenceEquals(inputs.Schema, _baseEnvironment.Schema))
        {
            throw new ArgumentException("the inputs were built for a different input schema", nameof(inputs));
        }

        var limits = _baseEnvironment.Limits;
        var state = new EvalState(_slotCount, limits, limits.MaxEvaluationSteps, options.DetailedTrace);
        inputs.CopyTo(state.Slots);
        var variableValues = new List<NamedValue>();
        var inputValues = new List<NamedValue>();
        var ruleTraces = new List<RuleTrace>();
        var expressionTraces = new List<ExpressionTrace>();
        var matchedRows = new List<int>();
        var d = Definition;

        DecisionResult Failed(RuleEvaluationError error) => new(
            error,
            Array.Empty<DecisionMatch>(),
            new DecisionTrace(d.Metadata, ContentHash, d.HitPolicy, variableValues, inputValues, ruleTraces, Array.Empty<DecisionMatch>(), expressionTraces, state.Steps));

        RuleValue? Run(CompiledExpression expression, string label, out RuleEvaluationError? error)
        {
            var result = expression.Run(state, label);
            if (options.DetailedTrace)
            {
                expressionTraces.Add(new ExpressionTrace(label, expression.Source, result.Trace));
            }

            error = result.Error;
            return result.Value;
        }

        int slot = _baseSlots;
        for (int i = 0; i < _variables.Length; i++, slot++)
        {
            var value = Run(_variables[i], $"variable '{d.Variables[i].Name}'", out var error);
            if (error is not null)
            {
                return Failed(error);
            }

            state.Slots[slot] = value!;
            variableValues.Add(new NamedValue(d.Variables[i].Name, value!));
        }

        for (int i = 0; i < _inputs.Length; i++, slot++)
        {
            var value = Run(_inputs[i], $"input column '{d.Inputs[i].Name}'", out var error);
            if (error is not null)
            {
                return Failed(error);
            }

            state.Slots[slot] = value!;
            inputValues.Add(new NamedValue(d.Inputs[i].Name, value!));
        }

        for (int r = 0; r < d.Rules.Count; r++)
        {
            var rule = d.Rules[r];
            var cells = new CellOutcome[_inputs.Length];
            Array.Fill(cells, CellOutcome.NotEvaluated);
            bool matched = true;
            for (int c = 0; c < cells.Length; c++)
            {
                var cell = _conditions[r][c];
                if (cell is null)
                {
                    cells[c] = CellOutcome.Any;
                    continue;
                }

                string label = $"rule '{rule.RuleId}' condition '{d.Inputs[c].Name}'";
                var value = Run(cell, label, out var error);
                if (error is null && value is not BoolValue)
                {
                    error = new RuleEvaluationError(RuleErrorCode.NullValue, "condition evaluated to null", null, label);
                }

                if (error is not null)
                {
                    ruleTraces.Add(new RuleTrace(rule.RuleId, r, cells, false));
                    return Failed(error);
                }

                bool ok = ((BoolValue)value!).Value;
                cells[c] = ok ? CellOutcome.Matched : CellOutcome.NotMatched;
                if (!ok)
                {
                    matched = false;
                    break;
                }
            }

            ruleTraces.Add(new RuleTrace(rule.RuleId, r, cells, matched));
            if (matched)
            {
                matchedRows.Add(r);
                if (d.HitPolicy == HitPolicy.First)
                {
                    break;
                }
            }
        }

        IEnumerable<int> selected = matchedRows;
        switch (d.HitPolicy)
        {
            case HitPolicy.Unique when matchedRows.Count > 1:
                return Failed(new RuleEvaluationError(
                    RuleErrorCode.HitPolicyViolation,
                    "hit policy UNIQUE violated: rules " + string.Join(", ", matchedRows.Select(i => d.Rules[i].RuleId)) + " all match",
                    null,
                    d.Metadata.TableId));
            case HitPolicy.Priority when matchedRows.Count > 0:
            {
                int best = matchedRows[0];
                foreach (int i in matchedRows)
                {
                    if (d.Rules[i].Priority > d.Rules[best].Priority)
                    {
                        best = i;
                    }
                }

                selected = new[] { best };
                break;
            }
        }

        var matches = new List<DecisionMatch>();
        foreach (int r in selected)
        {
            var rule = d.Rules[r];
            var values = new NamedValue[_outputs[r].Length];
            for (int o = 0; o < values.Length; o++)
            {
                var value = Run(_outputs[r][o], $"rule '{rule.RuleId}' output '{d.Outputs[o].Name}'", out var error);
                if (error is not null)
                {
                    return Failed(error);
                }

                values[o] = new NamedValue(d.Outputs[o].Name, value!);
            }

            matches.Add(new DecisionMatch(rule.RuleId, r, values));
        }

        var trace = new DecisionTrace(d.Metadata, ContentHash, d.HitPolicy, variableValues, inputValues, ruleTraces, matches, expressionTraces, state.Steps);
        return new DecisionResult(null, matches, trace);
    }

    /// <summary>Runs test cases against this version (status is ignored).</summary>
    public IReadOnlyList<DecisionTableTestOutcome> RunTests(IEnumerable<DecisionTableTestCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        return cases.Select(RunTest).ToArray();
    }

    /// <summary>
    /// Activation gate (REQ-PLT-176, REQ-UW-038): refuses when there are no test cases, when any case fails, or — when
    /// <paramref name="requireEveryRuleCovered"/> — when some rule is not expected to match in any case.
    /// </summary>
    public ActivationReadiness CheckActivationReadiness(IEnumerable<DecisionTableTestCase> cases, bool requireEveryRuleCovered = false)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var list = cases.ToArray();
        var outcomes = RunTests(list);
        var covered = new HashSet<string>(list.SelectMany(c => c.ExpectedRuleIds), StringComparer.Ordinal);
        var uncovered = Definition.Rules.Select(r => r.RuleId).Where(id => !covered.Contains(id)).ToArray();
        string? refusal = null;
        if (list.Length == 0)
        {
            refusal = "a table version must ship with test cases";
        }
        else if (outcomes.Any(o => !o.Passed))
        {
            refusal = "failing test case(s): " + string.Join(", ", outcomes.Where(o => !o.Passed).Select(o => o.Name));
        }
        else if (requireEveryRuleCovered && uncovered.Length > 0)
        {
            refusal = "rule(s) without a test case: " + string.Join(", ", uncovered);
        }

        return new ActivationReadiness(refusal is null, refusal, outcomes, uncovered);
    }

    private DecisionTableTestOutcome RunTest(DecisionTableTestCase testCase)
    {
        var result = EvaluateForTesting(testCase.Inputs);
        string? failure = null;
        if (testCase.ExpectedError is { } expectedError)
        {
            if (result.Error?.Code != expectedError)
            {
                failure = $"expected error {expectedError.ToCode()} but got {(result.Error is null ? "success" : result.Error.CodeText)}";
            }
        }
        else if (result.Error is not null)
        {
            failure = "evaluation failed: " + result.Error;
        }
        else if (!result.MatchedRuleIds.SequenceEqual(testCase.ExpectedRuleIds))
        {
            failure = $"expected rules [{string.Join(", ", testCase.ExpectedRuleIds)}] but matched [{string.Join(", ", result.MatchedRuleIds)}]";
        }
        else if (testCase.ExpectedOutputs is { } expected)
        {
            if (expected.Count != result.Matches.Count)
            {
                failure = "expected outputs for a different number of matches";
            }
            else
            {
                for (int i = 0; i < expected.Count && failure is null; i++)
                {
                    foreach (var e in expected[i])
                    {
                        var actual = result.Matches[i].Outputs.FirstOrDefault(o => o.Name == e.Name);
                        if (actual is null || !actual.Value.Equals(e.Value))
                        {
                            failure = $"match {result.Matches[i].RuleId}: expected {e.Name} = {e.Value} but got {actual?.Value.ToString() ?? "(no such output)"}";
                            break;
                        }
                    }
                }
            }
        }

        return new DecisionTableTestOutcome(testCase.Name, failure is null, failure, result);
    }
}
