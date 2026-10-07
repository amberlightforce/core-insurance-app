using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.SharedKernel.StateMachines;

/// <summary>One declared transition: in state <see cref="From"/>, trigger <see cref="Trigger"/> leads to <see cref="To"/>.</summary>
/// <typeparam name="TState">State enum.</typeparam>
/// <typeparam name="TTrigger">Trigger enum.</typeparam>
/// <param name="From">Source state.</param>
/// <param name="Trigger">Trigger.</param>
/// <param name="To">Target state.</param>
public sealed record StateTransition<TState, TTrigger>(TState From, TTrigger Trigger, TState To)
    where TState : struct, Enum
    where TTrigger : struct, Enum;

/// <summary>
/// A declarative lifecycle (ADR §2 rule 5): the full set of states, the states a new object may start in, and the only
/// allowed transitions. Anything not declared is rejected with the typed error
/// <c>&lt;MOD&gt;-ERR-INVALID-STATE-TRANSITION</c>. The machine has no behaviour beyond the table: guards, side effects
/// and events belong to the owning module. The table is exposed for tests, documentation and database check constraints.
/// </summary>
/// <typeparam name="TState">State enum; member names are the canonical state names (contract §3.2.4, PRD-18 §9).</typeparam>
/// <typeparam name="TTrigger">Trigger enum.</typeparam>
public sealed class StateMachine<TState, TTrigger>
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    private readonly FrozenDictionary<(TState From, TTrigger Trigger), TState> _table;

    internal StateMachine(
        ModuleCode owner,
        string name,
        IReadOnlyList<StateTransition<TState, TTrigger>> transitions,
        IReadOnlySet<TState> initialStates,
        IReadOnlySet<TState> terminalStates,
        IReadOnlySet<TState> notUsed)
    {
        Owner = owner;
        Name = name;
        Transitions = transitions;
        InitialStates = initialStates;
        TerminalStates = terminalStates;
        States = [.. Enum.GetValues<TState>().Where(s => !notUsed.Contains(s))];
        _table = transitions.ToFrozenDictionary(t => (t.From, t.Trigger), t => t.To);
    }

    /// <summary>Owning module (prefix of the rejection error code).</summary>
    public ModuleCode Owner { get; }

    /// <summary>Entity name, e.g. <c>PolicyTerm</c>.</summary>
    public string Name { get; }

    /// <summary>The states of this machine (the enum's members minus any declared not used), in declaration order.</summary>
    public IReadOnlyList<TState> States { get; }

    /// <summary>States a new object may be created in.</summary>
    public IReadOnlySet<TState> InitialStates { get; }

    /// <summary>States with no way out.</summary>
    public IReadOnlySet<TState> TerminalStates { get; }

    /// <summary>The transition table, in declaration order.</summary>
    public IReadOnlyList<StateTransition<TState, TTrigger>> Transitions { get; }

    /// <summary>The error code used for rejected transitions and starts.</summary>
    public ErrorCode RejectionCode => ErrorCode.For(Owner, "INVALID-STATE-TRANSITION");

    /// <summary>True when <paramref name="trigger"/> is declared for <paramref name="from"/>.</summary>
    public bool CanFire(TState from, TTrigger trigger) => _table.ContainsKey((from, trigger));

    /// <summary>The triggers declared for a state.</summary>
    public IReadOnlyList<TTrigger> PermittedTriggers(TState from) =>
        [.. Transitions.Where(t => EqualityComparer<TState>.Default.Equals(t.From, from)).Select(t => t.Trigger)];

    /// <summary>The target of a declared transition, or a typed failure for an undeclared one.</summary>
    public Result<TState> Fire(TState from, TTrigger trigger) =>
        _table.TryGetValue((from, trigger), out var to)
            ? Result.Success(to)
            : Result.Failure<TState>(Rejection(
                $"{Name}: trigger {trigger} is not allowed in state {from}.",
                ("machine", Name), ("from", from.ToString()), ("trigger", trigger.ToString())));

    /// <summary>Like <see cref="Fire"/> but throws <see cref="InvalidStateTransitionException"/> for an undeclared transition.</summary>
    public TState FireOrThrow(TState from, TTrigger trigger)
    {
        var result = Fire(from, trigger);
        return result.IsSuccess ? result.Value : throw new InvalidStateTransitionException(result.Error);
    }

    /// <summary>Accepts <paramref name="state"/> as the first state of a new object, or fails when it is not an initial state.</summary>
    public Result<TState> Start(TState state) =>
        InitialStates.Contains(state)
            ? Result.Success(state)
            : Result.Failure<TState>(Rejection(
                $"{Name}: a new object cannot start in state {state}.", ("machine", Name), ("to", state.ToString())));

    /// <summary>True when <paramref name="state"/> is terminal.</summary>
    public bool IsTerminal(TState state) => TerminalStates.Contains(state);

    /// <summary>The canonical names of the states (enum member names), for database check constraints and API enums.</summary>
    public IReadOnlyList<string> StateNames => [.. States.Select(s => s.ToString())];

    /// <summary>
    /// A PostgreSQL check constraint expression restricting <paramref name="column"/> to the declared state names, e.g.
    /// <c>status IN ('Draft', 'Quoted')</c>. The column name must be a plain lower-case identifier.
    /// </summary>
    public string CheckConstraintSql(string column)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        if (!column.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') || !char.IsAsciiLetterLower(column[0]))
        {
            throw new ArgumentException("Column must be a lower-case identifier.", nameof(column));
        }

        var builder = new StringBuilder(column).Append(" IN (");
        builder.AppendJoin(", ", StateNames.Select(name => "'" + name + "'"));
        return builder.Append(')').ToString();
    }

    /// <summary>The distinct (from, to) state pairs, for a database transition guard.</summary>
    public IReadOnlyList<(string From, string To)> StatePairs =>
        [.. Transitions.Select(t => (t.From.ToString(), t.To.ToString())).Distinct()];

    /// <summary>Mermaid <c>stateDiagram-v2</c> text of the table (documentation).</summary>
    public string ToMermaid()
    {
        var builder = new StringBuilder("stateDiagram-v2\n");
        foreach (var initial in InitialStates)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  [*] --> {initial}\n");
        }

        foreach (var transition in Transitions)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  {transition.From} --> {transition.To} : {transition.Trigger}\n");
        }

        foreach (var terminal in TerminalStates)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  {terminal} --> [*]\n");
        }

        return builder.ToString();
    }

    private DomainError Rejection(string detail, params (string Key, string Value)[] metadata) =>
        new(RejectionCode, detail) { Metadata = metadata.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal) };
}

/// <summary>Thrown by <see cref="StateMachine{TState,TTrigger}.FireOrThrow"/> for an undeclared transition.</summary>
public sealed class InvalidStateTransitionException : InvalidOperationException
{
    /// <summary>Creates the exception from the rejection error.</summary>
    public InvalidStateTransitionException(DomainError error)
        : base(error?.ToString())
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>Creates the exception.</summary>
    public InvalidStateTransitionException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public InvalidStateTransitionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public InvalidStateTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The rejection.</summary>
    public DomainError? Error { get; }
}

/// <summary>Entry point for declaring a state machine.</summary>
public static class StateMachine
{
    /// <summary>Starts the declaration of a machine owned by <paramref name="owner"/>.</summary>
    public static StateMachineBuilder<TState, TTrigger> Define<TState, TTrigger>(ModuleCode owner, string name)
        where TState : struct, Enum
        where TTrigger : struct, Enum => new(owner, name);
}

/// <summary>
/// Declares a <see cref="StateMachine{TState,TTrigger}"/>. <see cref="Build"/> refuses an inconsistent table: a
/// (state, trigger) pair declared twice, a state of the enum that is never used, a state unreachable from the initial
/// states, a declared terminal state with outgoing transitions, or a non-terminal state with no way out.
/// </summary>
/// <typeparam name="TState">State enum.</typeparam>
/// <typeparam name="TTrigger">Trigger enum.</typeparam>
public sealed class StateMachineBuilder<TState, TTrigger>
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    private readonly ModuleCode _owner;
    private readonly string _name;
    private readonly List<StateTransition<TState, TTrigger>> _transitions = [];
    private readonly HashSet<TState> _initial = [];
    private readonly HashSet<TState> _terminal = [];
    private readonly HashSet<TState> _notUsed = [];

    internal StateMachineBuilder(ModuleCode owner, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _owner = owner;
        _name = name;
    }

    /// <summary>Declares the states a new object may start in.</summary>
    public StateMachineBuilder<TState, TTrigger> Initial(params TState[] states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _initial.UnionWith(states);
        return this;
    }

    /// <summary>Declares states with no way out.</summary>
    public StateMachineBuilder<TState, TTrigger> Terminal(params TState[] states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _terminal.UnionWith(states);
        return this;
    }

    /// <summary>
    /// Declares enum members this machine never uses (when one state enum serves several variants, e.g. the clock kinds).
    /// They may not appear in any transition.
    /// </summary>
    public StateMachineBuilder<TState, TTrigger> NotUsed(params TState[] states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _notUsed.UnionWith(states);
        return this;
    }

    /// <summary>Declares <paramref name="from"/> --<paramref name="trigger"/>--&gt; <paramref name="to"/>.</summary>
    public StateMachineBuilder<TState, TTrigger> Permit(TState from, TTrigger trigger, TState to)
    {
        _transitions.Add(new StateTransition<TState, TTrigger>(from, trigger, to));
        return this;
    }

    /// <summary>Declares the same trigger and target from several states.</summary>
    public StateMachineBuilder<TState, TTrigger> Permit(IEnumerable<TState> from, TTrigger trigger, TState to)
    {
        ArgumentNullException.ThrowIfNull(from);
        foreach (var state in from)
        {
            Permit(state, trigger, to);
        }

        return this;
    }

    /// <summary>Validates the declaration and returns the immutable machine.</summary>
    public StateMachine<TState, TTrigger> Build()
    {
        var comparer = EqualityComparer<TState>.Default;
        var problems = new List<string>();

        if (_initial.Count == 0)
        {
            problems.Add("no initial state");
        }

        foreach (var duplicate in _transitions.GroupBy(t => (t.From, t.Trigger)).Where(g => g.Count() > 1))
        {
            problems.Add($"({duplicate.Key.From}, {duplicate.Key.Trigger}) is declared {duplicate.Count()} times");
        }

        var used = new HashSet<TState>(_initial.Concat(_terminal).Concat(_transitions.SelectMany(t => new[] { t.From, t.To })));
        problems.AddRange(used.Where(_notUsed.Contains).Select(s => $"state {s} is declared not used but appears in the table"));
        problems.AddRange(Enum.GetValues<TState>().Where(s => !used.Contains(s) && !_notUsed.Contains(s)).Select(s => $"state {s} is never used"));

        var reachable = new HashSet<TState>(_initial);
        var frontier = new Queue<TState>(_initial);
        while (frontier.TryDequeue(out var state))
        {
            foreach (var next in _transitions.Where(t => comparer.Equals(t.From, state)).Select(t => t.To))
            {
                if (reachable.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        problems.AddRange(used.Where(s => !reachable.Contains(s)).Select(s => $"state {s} is unreachable from the initial states"));
        problems.AddRange(_terminal.Where(s => _transitions.Any(t => comparer.Equals(t.From, s)))
            .Select(s => $"terminal state {s} has outgoing transitions"));
        problems.AddRange(used.Where(s => !_terminal.Contains(s) && !_transitions.Any(t => comparer.Equals(t.From, s)))
            .Select(s => $"state {s} has no way out but is not declared terminal"));

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"State machine {_owner}.{_name} is inconsistent: {string.Join("; ", problems)}.");
        }

        return new StateMachine<TState, TTrigger>(
            _owner, _name, _transitions.ToArray(), _initial.ToFrozenSet(), _terminal.ToFrozenSet(), _notUsed.ToFrozenSet());
    }
}
