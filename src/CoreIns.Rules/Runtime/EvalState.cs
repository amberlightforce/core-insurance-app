using System.Diagnostics;
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
    /// CEL error absorption: <c>false &amp;&amp; error</c> and <c>error &amp;&amp; false</c> are false. Resource
    /// failures (budget, allocation, time, cancellation, regex timeout) are never absorbed, so a runaway rule always fails.
    /// </summary>
    public bool Absorbable => Code is not (RuleErrorCode.CostExceeded or RuleErrorCode.LimitExceeded or RuleErrorCode.RegexTimeout
        or RuleErrorCode.Timeout or RuleErrorCode.Cancelled);
}

/// <summary>
/// Per-evaluation mutable state (never shared between threads). Enforces the three resource bounds of a rule:
/// the cost budget (steps charged in proportion to the work done, not just per node), the allocation budget
/// (elements created), and the wall-clock deadline / cancellation.
/// </summary>
internal sealed class EvalState
{
    private const long DeadlineCheckInterval = 256;

    private readonly long _maxSteps;
    private readonly long _deadline;
    private readonly CancellationToken _cancellation;
    private long _sinceDeadlineCheck;

    public EvalState(int slotCount, RuleLimits limits, long maxSteps, bool trace, CancellationToken cancellation = default)
    {
        Slots = new RuleValue[slotCount];
        Limits = limits;
        _maxSteps = maxSteps;
        _cancellation = cancellation;
        long ticks = limits.MaxEvaluationTime.Ticks;
        long now = Stopwatch.GetTimestamp();
        long budget = ticks >= long.MaxValue / Stopwatch.Frequency ? long.MaxValue : ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        _deadline = budget > long.MaxValue - now ? long.MaxValue : now + budget;
        Trace = trace ? new List<(BoundNode, RuleValue)>() : null;
    }

    public RuleValue[] Slots { get; }

    public RuleLimits Limits { get; }

    /// <summary>Steps charged so far, reported at most one past the budget (M3: never a saturated sentinel).</summary>
    public long Steps => _maxSteps == long.MaxValue ? AttemptedSteps : Math.Min(AttemptedSteps, _maxSteps + 1);

    /// <summary>All work charged, including the charge that broke the budget (may be very large, saturating).</summary>
    public long AttemptedSteps { get; private set; }

    public long Allocated { get; private set; }

    public List<(BoundNode Node, RuleValue Value)>? Trace { get; set; }

    public int TraceCount { get; private set; }

    public bool TraceTruncated { get; private set; }

    /// <summary>One step for evaluating a node (or one comprehension iteration).</summary>
    public void Tick(BoundNode node)
    {
        try
        {
            Charge(1);
        }
        catch (EvalFailure f)
        {
            f.Node ??= node;
            throw;
        }
    }

    /// <summary>Charges <paramref name="units"/> steps of work (for example the size of the values an operation traverses).</summary>
    public void Charge(long units)
    {
        AttemptedSteps = units >= long.MaxValue - AttemptedSteps ? long.MaxValue : AttemptedSteps + units;
        if (AttemptedSteps > _maxSteps)
        {
            throw new EvalFailure(
                RuleErrorCode.CostExceeded,
                string.Create(CultureInfo.InvariantCulture, $"evaluation exceeded the cost budget of {_maxSteps} steps"));
        }

        _sinceDeadlineCheck += units;
        if (_sinceDeadlineCheck >= DeadlineCheckInterval)
        {
            _sinceDeadlineCheck = 0;
            CheckDeadline();
        }
    }

    /// <summary>Charges a comparison or traversal of two values: the smaller value's weight bounds the work.</summary>
    public void ChargeCompare(RuleValue a, RuleValue b) => Charge(Math.Min(a.Weight, b.Weight));

    /// <summary>Records <paramref name="elements"/> newly allocated elements (also charged as steps).</summary>
    public void Allocate(long elements)
    {
        Allocated = elements >= long.MaxValue - Allocated ? long.MaxValue : Allocated + elements;
        if (Allocated > Limits.MaxAllocatedElements)
        {
            throw new EvalFailure(
                RuleErrorCode.LimitExceeded,
                string.Create(CultureInfo.InvariantCulture, $"evaluation allocated more than {Limits.MaxAllocatedElements} elements"));
        }

        Charge(elements);
    }

    /// <summary>Charges the work of scanning a string (one step per 16 characters).</summary>
    public void ChargeText(string s) => Charge(1 + (s.Length / 16));

    public void CheckDeadline()
    {
        if (_cancellation.IsCancellationRequested)
        {
            throw new EvalFailure(RuleErrorCode.Cancelled, "evaluation was cancelled");
        }

        if (Stopwatch.GetTimestamp() > _deadline)
        {
            throw new EvalFailure(
                RuleErrorCode.Timeout,
                string.Create(CultureInfo.InvariantCulture, $"evaluation exceeded the deadline of {Limits.MaxEvaluationTime.Ticks / TimeSpan.TicksPerMillisecond} ms"));
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
