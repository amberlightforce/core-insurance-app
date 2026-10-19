using System.Globalization;
using System.Text.Json;
using CoreIns.SharedKernel.Dsar;

namespace CoreIns.Platform.Dsar;

/// <summary>
/// PLT's part in a data-subject request (D-SL6-07). The platform keeps append-only audit entries, the event outbox and
/// approvals: none of them is erasable, and none is keyed by data subject. They are reported RESTRICTED with retention end
/// UNSET_F404 (fail closed) so PLT never shows as NOT_BUILT. This is the one participant allowed outside a module project.
/// </summary>
public sealed class PlatformDsarParticipant : IDsarParticipant
{
    internal static readonly string[] Classes = ["AUDIT_LOG", "EVENT_OUTBOX", "APPROVALS"];

    private const string Basis = "Retention of audit, outbox and approval records (GDPR Art. 17(3)(b); retention period unset, F-404)";

    /// <inheritdoc />
    public string Module => "PLT";

    /// <inheritdoc />
    public Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        // TODO(hardening): export the audit entries that name the subject once they are keyed by party.
        var payload = JsonSerializer.Serialize(new { module = Module, note = "Platform records are not keyed by data subject." });
        return Task.FromResult(new DsarModuleResult(Module, Restricted(null, true), payload));
    }

    /// <inheritdoc />
    public Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult(new DsarModuleResult(Module, Restricted(retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false)));
    }

    /// <inheritdoc />
    public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult(new DsarModuleResult(Module, Restricted(null, false)));
    }

    private static List<DsarRecordOutcome> Restricted(string? retentionEnd, bool exported) =>
        [.. Classes.Select(c => DsarRecordOutcome.Restricted(c, Basis, 0, retentionEnd) with { Detail = exported ? "exported" : null })];
}
