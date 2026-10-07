using System;
using System.Collections.Generic;

namespace CoreIns.Rules.DecisionTables;

/// <summary>Hit policies (REQ-PLT-174).</summary>
public enum HitPolicy
{
    /// <summary>The first matching rule in row order wins; later rules are not evaluated.</summary>
    First,

    /// <summary>At most one rule may match; more than one is an error (PLT-ERR-HIT-POLICY-VIOLATION).</summary>
    Unique,

    /// <summary>The matching rule with the highest <see cref="DecisionRule.Priority"/> wins; ties go to the earlier row.</summary>
    Priority,

    /// <summary>All matching rules, in row order.</summary>
    Collect,
}

/// <summary>Version lifecycle of a decision table (owned and persisted by PLT; the runtime only checks it).</summary>
public enum DecisionTableStatus
{
    Draft,
    Submitted,
    Approved,
    Active,
    Superseded,
    Retired,
}

/// <summary>Versioning metadata. Not part of the content hash.</summary>
/// <param name="TableId">Stable table identifier, for example <c>UW-MOTOR-GR-AGE</c>.</param>
/// <param name="Version">Version label, for example <c>2.3</c>.</param>
/// <param name="Status">Lifecycle status; only <see cref="DecisionTableStatus.Active"/> versions evaluate in production.</param>
/// <param name="EffectiveFrom">First effective date (inclusive).</param>
/// <param name="EffectiveTo">End of effectivity (exclusive), or null for open-ended (half-open [from, to), D5).</param>
public sealed record DecisionTableMetadata(string TableId, string Version, DecisionTableStatus Status, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null)
{
    /// <summary>Optional description.</summary>
    public string? Description { get; init; }
}

/// <summary>A variable computed before the table (REQ-UW-033), usable in input columns, conditions and outputs.</summary>
/// <param name="Name">Identifier.</param>
/// <param name="Type">Declared type.</param>
/// <param name="Expression">Expression over the inputs and earlier variables.</param>
public sealed record TableVariable(string Name, RuleType Type, string Expression);

/// <summary>A typed input column whose value is an expression over the inputs and variables.</summary>
/// <param name="Name">Identifier (also usable in conditions and outputs).</param>
/// <param name="Type">Declared type.</param>
/// <param name="Expression">Expression computing the column value.</param>
public sealed record InputColumn(string Name, RuleType Type, string Expression);

/// <summary>A typed output column.</summary>
/// <param name="Name">Identifier.</param>
/// <param name="Type">Declared type.</param>
public sealed record OutputColumn(string Name, RuleType Type);

/// <summary>
/// A table row. <see cref="Conditions"/> has one cell per input column in the condition-cell syntax
/// (see <see cref="ConditionCell"/>); <see cref="Outputs"/> has one expression per output column.
/// </summary>
public sealed record DecisionRule(string RuleId, IReadOnlyList<string> Conditions, IReadOnlyList<string> Outputs)
{
    /// <summary>Priority for the PRIORITY hit policy (higher wins).</summary>
    public int Priority { get; init; }

    /// <summary>Optional description / explanation key.</summary>
    public string? Description { get; init; }
}

/// <summary>A complete decision-table version.</summary>
public sealed record DecisionTableDefinition(
    DecisionTableMetadata Metadata,
    HitPolicy HitPolicy,
    IReadOnlyList<InputColumn> Inputs,
    IReadOnlyList<OutputColumn> Outputs,
    IReadOnlyList<DecisionRule> Rules)
{
    /// <summary>Variables computed before the input columns, in order.</summary>
    public IReadOnlyList<TableVariable> Variables { get; init; } = Array.Empty<TableVariable>();
}
