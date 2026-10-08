using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Claims.Domain;

/// <summary>
/// The claim lifecycle of PRD-07 §7.3.1 / REQ-CLM-071 as one flat state: <c>Draft</c>, the Open sub-states
/// (<c>New</c>, <c>InProgress</c>, <c>UnderInvestigation</c>, <c>Settled</c>) and <c>Closed</c>. Stored as the pair
/// (status, sub_status) the contract shows (<see cref="ClaimStates"/>).
/// </summary>
internal enum ClaimState
{
    Draft,
    New,
    InProgress,
    UnderInvestigation,
    Settled,
    Closed,
}

/// <summary>Triggers of <see cref="ClaimState"/>.</summary>
internal enum ClaimTrigger
{
    /// <summary>FNOL submitted/confirmed: mandatory fields present, claim number issued (Draft → Open(New)).</summary>
    Submit,

    /// <summary>The handler's first action or contact (New → InProgress); later actions keep the sub-state.</summary>
    HandlerAction,

    /// <summary>Coverage in question, SIU, liability disputed (Open(any) → UnderInvestigation). Later work package.</summary>
    Investigate,

    /// <summary>All exposures settled but follow-up open (Open(any) → Settled). Later work package.</summary>
    Settle,

    /// <summary>Close with an outcome under the guards of REQ-CLM-072/073.</summary>
    Close,

    /// <summary>Draft discarded after confirmation declined (Draft → Closed(Withdrawn), no number). Later work package.</summary>
    DiscardDraft,

    /// <summary>Reopen with a coded reason (REQ-CLM-074). Later work package.</summary>
    Reopen,
}

/// <summary>The claim state machine (PRD-07 §7.3.1). The table is complete; SL2-CLM-CORE fires Submit, HandlerAction and Close.</summary>
internal static class ClaimStateModel
{
    private static readonly ClaimState[] Open = [ClaimState.New, ClaimState.InProgress, ClaimState.UnderInvestigation, ClaimState.Settled];

    public static StateMachine<ClaimState, ClaimTrigger> Machine { get; } =
        StateMachine.Define<ClaimState, ClaimTrigger>(ModuleCode.CLM, "Claim")
            .Initial(ClaimState.Draft)
            .Permit(ClaimState.Draft, ClaimTrigger.Submit, ClaimState.New)
            .Permit(ClaimState.Draft, ClaimTrigger.DiscardDraft, ClaimState.Closed)
            .Permit(ClaimState.New, ClaimTrigger.HandlerAction, ClaimState.InProgress)
            .Permit([ClaimState.InProgress, ClaimState.UnderInvestigation, ClaimState.Settled], ClaimTrigger.HandlerAction, ClaimState.InProgress)
            .Permit(Open, ClaimTrigger.Investigate, ClaimState.UnderInvestigation)
            .Permit(Open, ClaimTrigger.Settle, ClaimState.Settled)
            .Permit(Open, ClaimTrigger.Close, ClaimState.Closed)
            .Permit(ClaimState.Closed, ClaimTrigger.Reopen, ClaimState.InProgress)
            .Build();

    /// <summary>Fires a transition; an undeclared one is CLM-ERR-ILLEGAL-TRANSITION (REQ-CLM-071).</summary>
    public static Result<ClaimState> Fire(ClaimState from, ClaimTrigger trigger)
    {
        var to = Machine.Fire(from, trigger);
        return to.IsSuccess
            ? to
            : DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", $"A claim in state {ClaimStates.Describe(from)} does not allow {trigger}.");
    }
}

/// <summary>Exposure status (REQ-CLM-071: exposures follow the claim model; the slice uses Open(New) and Closed).</summary>
internal enum ExposureState
{
    New,
    InProgress,
    Closed,
}

/// <summary>Triggers of <see cref="ExposureState"/>.</summary>
internal enum ExposureTrigger
{
    HandlerAction,
    Close,
    Reopen,
}

/// <summary>The exposure state machine (PRD-07 §7.3.1, the claim model applied to exposures).</summary>
internal static class ExposureStateModel
{
    public static StateMachine<ExposureState, ExposureTrigger> Machine { get; } =
        StateMachine.Define<ExposureState, ExposureTrigger>(ModuleCode.CLM, "Exposure")
            .Initial(ExposureState.New)
            .Permit([ExposureState.New, ExposureState.InProgress], ExposureTrigger.HandlerAction, ExposureState.InProgress)
            .Permit([ExposureState.New, ExposureState.InProgress], ExposureTrigger.Close, ExposureState.Closed)
            .Permit(ExposureState.Closed, ExposureTrigger.Reopen, ExposureState.InProgress)
            .Build();
}

/// <summary>Mapping between the flat state and the stored (status, sub_status) pair.</summary>
internal static class ClaimStates
{
    public const string Draft = "DRAFT";
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";

    public static (string Status, string? SubStatus) ToColumns(ClaimState state) => state switch
    {
        ClaimState.Draft => (Draft, null),
        ClaimState.Closed => (Closed, null),
        _ => (Open, Codes.Of(state)),
    };

    public static ClaimState FromColumns(string status, string? subStatus) => status switch
    {
        Draft => ClaimState.Draft,
        Closed => ClaimState.Closed,
        _ => Codes.Parse<ClaimState>(subStatus ?? throw new InvalidOperationException("An open claim has no sub-status.")),
    };

    public static string Describe(ClaimState state) => state is ClaimState.Draft or ClaimState.Closed ? state.ToString() : $"Open({state})";
}

/// <summary>Exposure stored status (OPEN with sub-status, or CLOSED).</summary>
internal static class ExposureStates
{
    public static (string Status, string? SubStatus) ToColumns(ExposureState state) =>
        state == ExposureState.Closed ? (ClaimStates.Closed, null) : (ClaimStates.Open, Codes.Of(state));

    public static ExposureState FromColumns(string status, string? subStatus) =>
        status == ClaimStates.Closed ? ExposureState.Closed : Codes.Parse<ExposureState>(subStatus ?? "NEW");
}

/// <summary>Snapshot verification status (PRD-07 §7.1).</summary>
internal enum SnapshotStatus
{
    Pending,
    Verified,
    ReverificationRequired,
}

/// <summary>Closure outcomes (REQ-CLM-071).</summary>
internal enum ClaimOutcomeCode
{
    Completed,
    Denied,
    Withdrawn,
    Duplicate,
    NoPayment,
}

/// <summary>Claimant types (PRD-07 §7.1; the slice uses Insured and ThirdParty).</summary>
internal enum ClaimantType
{
    Insured,
    ThirdParty,
    GuaranteeFund,
    Bureau,
}

/// <summary>Coverage indication of an exposure (REQ-CLM-048): an indication until a coverage decision is recorded.</summary>
internal enum CoverageIndicationCode
{
    Covered,
    NotCovered,
    InQuestion,
}

/// <summary>Coverage decision of an exposure (PRD-07 §7.1). Decisions are a later work package; the slice stores Pending.</summary>
internal enum CoverageDecisionCode
{
    Pending,
    Covered,
    CoveredWithReservation,
    PartiallyCovered,
    NotCovered,
}

/// <summary>Upper-snake wire codes of the module's enums (stored in the database and published in events).</summary>
internal static class Codes
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static TEnum Parse<TEnum>(string code)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().First(value => string.Equals(Of(value), code, StringComparison.Ordinal));

    /// <summary>Check-constraint SQL listing every code of <typeparamref name="TEnum"/>.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => $"'{Of(v)}'"))})";

    /// <summary>Contract enums use the same member names as the module enums.</summary>
    public static TTarget Map<TSource, TTarget>(TSource value)
        where TSource : struct, Enum
        where TTarget : struct, Enum => Enum.Parse<TTarget>(value.ToString());
}
