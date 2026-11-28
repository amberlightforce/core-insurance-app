using CoreIns.Modules.Policy.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>The two lifecycle actions on a term's suspension flag.</summary>
internal enum SuspensionAction
{
    /// <summary>Stop the cover and the earning of premium (no refund).</summary>
    Suspend,

    /// <summary>Resume the cover and the earning of premium.</summary>
    Reactivate,
}

/// <summary>
/// The transition table of suspension (REQ-POL-236, BO-POL-08). Suspension is a transaction kind and a flag on the term
/// (<c>suspended_from</c>), never a term state (D-CON-02): the term stays InForce while suspended. Pure: no database, no clock.
/// </summary>
internal static class SuspensionRules
{
    /// <summary>A term whose cover is suspended refuses changes and their binds until it is reactivated.</summary>
    public static bool IsSuspended(Instant? suspendedFrom) => suspendedFrom is not null;

    /// <summary>
    /// Decides whether <paramref name="action"/> may be applied to a term, <c>null</c> when it may. A cancelled term refuses everything
    /// (<c>AFTER-CANCELLATION</c>); only a Scheduled or InForce term can be suspended; a suspended term can only be reactivated and an
    /// active one only suspended; the effective time lies inside the term, is not before the term's latest bound transaction
    /// (<c>OUT-OF-SEQUENCE</c>) and is not backdated beyond <paramref name="skew"/>.
    /// TODO(hardening): backdate limit by role, as for changes (ChangeLimits).
    /// </summary>
    public static DomainError? Check(
        SuspensionAction action, PolicyTermState storedState, Instant? cancelledAt, Instant? suspendedFrom, Instant termFrom, Instant termTo,
        Instant latestBoundEffective, Instant effectiveAt, Instant recordTime, TimeSpan skew)
    {
        if (storedState == PolicyTermState.Cancelled || cancelledAt is not null)
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.AfterCancellation, "The cover of this term has ended; suspension and reactivation are refused after a cancellation.");
        }

        if (storedState is not (PolicyTermState.Scheduled or PolicyTermState.InForce))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {storedState} term cannot be {Verb(action)}.");
        }

        if (recordTime >= termTo)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The policy term has already ended.");
        }

        var suspended = IsSuspended(suspendedFrom);
        if (action == SuspensionAction.Suspend && suspended)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term's cover is already suspended.");
        }

        if (action == SuspensionAction.Reactivate && !suspended)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The term's cover is not suspended.");
        }

        if (effectiveAt < termFrom || effectiveAt >= termTo)
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "The effective time is outside the term.");
        }

        if (effectiveAt < recordTime - skew)
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, "Suspension and reactivation cannot be backdated.");
        }

        if (effectiveAt < latestBoundEffective || (action == SuspensionAction.Reactivate && effectiveAt <= suspendedFrom))
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.OutOfSequence, "The effective time is not after the term's latest bound transaction (D-SL3-02).");
        }

        return null;
    }

    private static string Verb(SuspensionAction action) => action == SuspensionAction.Suspend ? "suspended" : "reactivated";
}
