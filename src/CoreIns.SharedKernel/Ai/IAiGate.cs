namespace CoreIns.SharedKernel.Ai;

/// <summary>Why the gate refused an AI use (D-SL6-08). Neutral codes; the manual path is always available.</summary>
public static class AiGateReasons
{
    /// <summary>The global kill switch is active.</summary>
    public const string KillSwitch = "KILL_SWITCH";

    /// <summary>The feature's own kill switch is active.</summary>
    public const string FeatureKillSwitch = "FEATURE_KILL_SWITCH";

    /// <summary>The feature has no toggle row or the toggle is off.</summary>
    public const string ToggleOff = "TOGGLE_OFF";

    /// <summary>The feature is unknown to the AI feature registry.</summary>
    public const string UnknownFeature = "UNKNOWN_FEATURE";

    /// <summary>The gate state could not be read (fail closed).</summary>
    public const string StateUnreadable = "STATE_UNREADABLE";
}

/// <summary>Result of <see cref="IAiGate.Check"/>: allowed, or disabled with a reason code.</summary>
public readonly record struct AiGateDecision
{
    private AiGateDecision(bool allowed, string? reason)
    {
        IsAllowed = allowed;
        Reason = reason;
    }

    /// <summary>True when the feature may be used now.</summary>
    public bool IsAllowed { get; }

    /// <summary>Reason code when disabled (see <see cref="AiGateReasons"/>), otherwise null.</summary>
    public string? Reason { get; }

    /// <summary>The feature may be used.</summary>
    public static AiGateDecision Allowed { get; } = new(true, null);

    /// <summary>The feature may not be used; callers fall back to the deterministic (manual) path.</summary>
    public static AiGateDecision Disabled(string reason) => new(false, reason);
}

/// <summary>
/// The deterministic AI gate (PLT-220, D-SL6-08). Every module calls it before any AI use and falls back to its
/// deterministic path when the answer is <see cref="AiGateDecision.Disabled"/>. The answer reflects database state
/// cached for at most 5 seconds; unreadable state means Disabled.
/// </summary>
public interface IAiGate
{
    /// <summary>Checks a feature (<c>AI-&lt;MOD&gt;-&lt;NN&gt;</c>) synchronously.</summary>
    AiGateDecision Check(string featureId);

    /// <summary>Checks a feature without blocking a request thread.</summary>
    Task<AiGateDecision> CheckAsync(string featureId, CancellationToken cancellationToken = default);
}
