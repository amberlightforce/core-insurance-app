using System.Text.Json;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance.Services.Dsar;

/// <summary>
/// Finance participant in data-subject requests (SL6-DSAR-PART-B, D-SL6-07). Every record class this module holds is
/// regulated (LEDGER): export returns the subject's records as JSON; erase and restrict never destroy them, they
/// write one <c>dsar_restriction</c> row per (subject, class) with the legal basis and retentionEnd UNSET_F404 (F-404).
/// A class with no data for the subject is reported NOT_HELD; no class is skipped silently. Idempotent: a second call
/// finds the existing restriction row and reports the same outcome.
/// </summary>
internal sealed class FinanceDsarParticipant(IServiceScopeFactory scopes, IClock clock) : IDsarParticipant
{
    private const string RetentionBasis = "GDPR Art 17(3)(b): legal obligation to retain; retention period pending F-404";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <inheritdoc />
    public string Module => "FIN";

    /// <inheritdoc />
    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var held = await CollectAsync(db, subject.PartyId, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(
            new { module = Module, partyId = subject.PartyId, records = held.ToDictionary(h => h.RecordClass, h => h.Rows) },
            Json);
        var outcomes = held
            .Select(h => h.Rows.Count == 0
                ? DsarRecordOutcome.NotHeld(h.RecordClass)
                : DsarRecordOutcome.Restricted(h.RecordClass, h.LegalBasis, h.Rows.Count) with { Detail = "exported" })
            .ToList();
        return new DsarModuleResult(Module, outcomes, payload);
    }

    /// <inheritdoc />
    public Task<DsarModuleResult> RestrictAsync(
        SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return ApplyAsync(subject, reason, string.IsNullOrWhiteSpace(legalBasis) ? null : legalBasis, retentionEnd, "dsar", ct);
    }

    /// <inheritdoc />
    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(context);

        // Every class here is regulated and held under retention (ledger, invoice, claim file, policy/fiscal document): nothing is
        // erased. Each held class is restricted and reported NOT_ERASED with the reason, so the DPO sees a refusal, not a silent keep.
        // TODO(hardening): erase non-regulated classes (none exist in this module today) once F-404 names retention classes.
        var result = await ApplyAsync(subject, "ERASURE_REQUEST", null, null, context.ApprovedBy ?? context.RequestedBy, ct).ConfigureAwait(false);
        return result with
        {
            Outcomes = [.. result.Outcomes.Select(o => o.Kind == DsarOutcomeKind.Restricted
                ? o with { Detail = DsarRetentionPolicy.NotErasedDetail(o.LegalBasis) }
                : o)],
        };
    }

    private async Task<DsarModuleResult> ApplyAsync(
        SubjectRef subject, string reason, string? legalBasis, DateOnly? retentionEnd, string actor, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var held = await CollectAsync(db, subject.PartyId, ct).ConfigureAwait(false);
        var existing = await db.Set<DsarRestrictionRow>().Where(r => r.SubjectPartyId == subject.PartyId).ToListAsync(ct).ConfigureAwait(false);
        var requestedEnd = retentionEnd?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? DsarRetention.UnsetF404;
        var outcomes = new List<DsarRecordOutcome>();
        foreach (var h in held)
        {
            if (h.Rows.Count == 0)
            {
                outcomes.Add(DsarRecordOutcome.NotHeld(h.RecordClass));
                continue;
            }

            var row = existing.FirstOrDefault(r => string.Equals(r.RecordClass, h.RecordClass, StringComparison.Ordinal));
            if (row is null)
            {
                row = new DsarRestrictionRow
                {
                    RestrictionId = Guid.NewGuid(),
                    SubjectPartyId = subject.PartyId,
                    RecordClass = h.RecordClass,
                    LegalBasis = legalBasis ?? h.LegalBasis,
                    RetentionEnd = requestedEnd,
                    Reason = reason,
                    RecordCount = h.Rows.Count,
                    RestrictedBy = actor,
                    RestrictedAt = clock.Now,
                };
                db.Set<DsarRestrictionRow>().Add(row);
            }
            else
            {
                // Idempotent: keep the original basis and end; only the record count follows new data of the same class.
                row.RecordCount = h.Rows.Count;
            }

            outcomes.Add(DsarRecordOutcome.Restricted(h.RecordClass, row.LegalBasis, h.Rows.Count, row.RetentionEnd));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, outcomes);
    }

    private sealed record Held(string RecordClass, string LegalBasis, IReadOnlyList<object> Rows);

    private static async Task<List<Held>> CollectAsync(FinanceDbContext db, PartyId p, CancellationToken ct)
    {
        Guid party = p.Value;
        var lines = await db.JournalLines.AsNoTracking().Where(l => l.CounterpartyPartyId == party).ToListAsync(ct).ConfigureAwait(false);
        var journalIds = lines.Select(l => l.JournalId).Distinct().ToList();
        var journals = await db.Journals.AsNoTracking().Where(j => journalIds.Contains(j.JournalId)).ToListAsync(ct).ConfigureAwait(false);
        var byId = journals.ToDictionary(j => j.JournalId);

        return
        [
            new Held(
                DsarRetentionPolicy.Ledger,
                RetentionBasis,
                lines.Select(l => (object)new
                {
                    lineId = l.LineId,
                    journalId = l.JournalId,
                    journalNumber = byId.TryGetValue(l.JournalId, out var j) ? j.JournalNumber : null,
                    accountingDate = byId.TryGetValue(l.JournalId, out var j2) ? (object?)j2.AccountingDate : null,
                    l.AccountCode,
                    l.Side,
                    l.Amount,
                    l.Currency,
                }).ToList()),
        ];
    }
}
