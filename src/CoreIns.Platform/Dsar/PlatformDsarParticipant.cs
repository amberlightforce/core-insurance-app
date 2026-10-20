using System.Globalization;
using System.Text.Json;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Dsar;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Dsar;

/// <summary>
/// PLT's part in a data-subject request (D-SL6-07). The platform keeps append-only audit entries, the event outbox and
/// approvals: none of them is erasable, and none is keyed by data subject. They are reported RESTRICTED with retention end
/// UNSET_F404 (fail closed) so PLT never shows as NOT_BUILT. This is the one participant allowed outside a module project.
/// </summary>
public sealed class PlatformDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    internal static readonly string[] Classes = ["AUDIT_LOG", "EVENT_OUTBOX", "APPROVALS"];

    private const string Basis = "Retention of audit, outbox and approval records (GDPR Art. 17(3)(b); retention period unset, F-404)";

    /// <inheritdoc />
    public string Module => "PLT";

    /// <inheritdoc />
    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // Audit entries that name the subject through their business keys (partyId) or as the audited object. Metadata only: the
        // field-level changes are redacted for personal data and other actors' identities are left out.
        await using var scope = scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text, count(*) FROM (" +
            "SELECT audit_id, occurred_at, operation, outcome, object_module, object_type, object_id, object_number, reason " +
            "FROM plt.audit_event WHERE business_keys ->> 'partyId' = @p OR (object_type = 'Party' AND object_id = @p) " +
            "ORDER BY occurred_at, audit_id LIMIT 5000) t", connection, session.Transaction);
        command.Parameters.AddWithValue("p", NpgsqlDbType.Text, subject.PartyId.Value.ToString("D", CultureInfo.InvariantCulture));
        string audit;
        long count;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            audit = reader.GetString(0);
            count = reader.GetInt64(1);
        }

        // TODO(hardening): page the audit export beyond 5000 entries; outbox and approvals are not keyed by party and are not exported.
        using var auditJson = JsonDocument.Parse(audit);
        var payload = JsonSerializer.Serialize(new { module = Module, auditLog = auditJson.RootElement, note = "Outbox and approval records are not keyed by data subject." });
        var outcomes = Restricted(null, true)
            .Select(o => o.RecordClass == "AUDIT_LOG" ? o with { RecordCount = (int)count } : o)
            .ToList();
        return new DsarModuleResult(Module, outcomes, payload);
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
        return Task.FromResult(new DsarModuleResult(Module, [.. Restricted(null, false).Select(o => o with { Detail = DsarRetentionPolicy.NotErasedDetail(Basis) })]));
    }

    private static List<DsarRecordOutcome> Restricted(string? retentionEnd, bool exported) =>
        [.. Classes.Select(c => DsarRecordOutcome.Restricted(c, Basis, 0, retentionEnd) with { Detail = exported ? "exported" : null })];
}
