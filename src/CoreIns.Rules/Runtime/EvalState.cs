using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreIns.Rules.Runtime;

/// <summary>Internal evaluation failure; converted to <see cref="RuleEvaluationError"/> at the API boundary.</summary>
internal sealed class EvalFailure : Exception
{
    public EvalFailure(RuleErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public RuleErrorCode Code { get; }

    /// <summary>The node that raised the failure (set by the first enclosing node).</summary>
    public BoundNode? Node { get; set; }

    /// <summary>
    /// CEL error absorption: <c>false &amp;&amp; error</c> and <c>error &amp;&amp; false</c> are false. Budget/limit
    /// failures are never absorbed, so a runaway rule always fails.
    /// </summary>
    public bool Absorbable => Code is not (RuleErrorCode.CostExceeded or RuleErrorCode.LimitExceeded or RuleErrorCode.RegexTimeout);
}

/// <summary>Per-evaluation mutable state (never shared between threads).</summary>
internal sealed class EvalState
{
    private readonly long _maxSteps;

    public EvalState(int slotCount, RuleLimits limits, long maxSteps, bool trace)
    {
        Slots = new RuleValue[slotCount];
        Limits = limits;
        _maxSteps = maxSteps;
        Trace = trace ? new List<(BoundNode, RuleValue)>() : null;
    }

    public RuleValue[] Slots { get; }

    public RuleLimits Limits { get; }

    public long Steps { get; private set; }

    public List<(BoundNode Node, RuleValue Value)>? Trace { get; set; }

    public int TraceCount { get; private set; }

    public bool TraceTruncated { get; private set; }

    public void Tick(BoundNode node)
    {
        if (++Steps > _maxSteps)
        {
            throw new EvalFailure(
                RuleErrorCode.CostExceeded,
                string.Create(CultureInfo.InvariantCulture, $"evaluation exceeded the cost budget of {_maxSteps} steps"))
            {
                Node = node,
            };
        }
    }

    public void Record(BoundNode node, RuleValue value)
    {
        if (Trace is null || !node.Traceable)
        {
            return;
        }

        if (TraceCount >= Limits.MaxTraceEntries)
        {
            TraceTruncated = true;
            return;
        }

        TraceCount++;
        Trace.Add((node, value));
    }
}
