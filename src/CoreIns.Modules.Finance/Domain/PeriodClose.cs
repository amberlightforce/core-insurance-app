using System.Text.RegularExpressions;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>The close checklist of an accounting period and its state machine (BO-FIN-04, REQ-FIN-007, -227..-241).</summary>
internal static class PeriodClose
{
    public const string IntakeExceptions = "INTAKE_EXCEPTIONS";
    public const string Reconciliations = "RECONCILIATIONS";
    public const string EarningRun = "EARNING_RUN";
    public const string ManualJournals = "MANUAL_JOURNALS";

    public const string Open = "OPEN";
    public const string SoftClosed = "SOFT_CLOSED";
    public const string Locked = "LOCKED";

    public const string TaskOpen = "OPEN";
    public const string TaskSignedOff = "SIGNED_OFF";

    /// <summary>The checklist, in display order, with the role that signs each task off.</summary>
    public static readonly IReadOnlyList<(string Task, string Role)> Tasks =
    [
        (IntakeExceptions, "Staff.Finance"),
        (Reconciliations, "Staff.Finance"),
        (EarningRun, "Staff.Finance"),
        (ManualJournals, "Staff.Finance"),
    ];

    public static bool IsTask(string? code) => Tasks.Any(t => t.Task == code);

    public static string RoleOf(string task) => Tasks.First(t => t.Task == task).Role;

    public static bool IsPeriodCode(string? period) =>
        period is not null && Regex.IsMatch(period, "^[0-9]{4}-(0[1-9]|1[0-2])$", RegexOptions.None, TimeSpan.FromSeconds(1));
}

/// <summary>
/// A pre-condition of one close task: returns why the task cannot be signed off, or null when nothing blocks it. Other work packages
/// (reconciliation, earning, manual journals) register an implementation for their task; a task without one is a manual confirmation only.
/// </summary>
internal interface ICloseTaskGate
{
    string Task { get; }

    Task<string?> BlockerAsync(Guid legalEntity, string period, CancellationToken cancellationToken);
}
