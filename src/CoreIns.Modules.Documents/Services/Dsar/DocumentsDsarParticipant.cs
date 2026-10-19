using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Dsar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Documents.Services.Dsar;

/// <summary>
/// DOC's part in a data-subject request (CHN-304, DOC-314). Documents, delivery records and delivery proof are policy and
/// statutory-notice records: restrict-only with retention end UNSET_F404 (D-SL6-07). Nothing in DOC is erasable.
/// </summary>
internal sealed class DocumentsDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    internal const string Delivery = "DELIVERY";
    internal const string DeliveryProof = "DELIVERY_PROOF";
    private const string Basis = "Retention of policy documents and statutory notices (GDPR Art. 17(3)(b); retention period unset, F-404)";

    public string Module => "DOC";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var data = await CollectAsync(db, subject, ct).ConfigureAwait(false);
        var outcomes = data.Select(d => d.Count == 0
            ? DsarRecordOutcome.NotHeld(d.Class)
            : DsarRecordOutcome.Restricted(d.Class, Basis, d.Count) with { Detail = "exported" }).ToList();
        return new DsarModuleResult(Module, outcomes, JsonSerializer.Serialize(data.ToDictionary(d => d.Class, d => d.Rows)));
    }

    public Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default) =>
        ApplyAsync(subject, reason, legalBasis, retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);

    // Every DOC class is restrict-only, so erase restricts them all (idempotent).
    public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default) =>
        ApplyAsync(subject, "DSAR erase request", Basis, null, ct);

    private async Task<DsarModuleResult> ApplyAsync(SubjectRef subject, string reason, string legalBasis, string? retentionEnd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var outcomes = new List<DsarRecordOutcome>();
        foreach (var d in await CollectAsync(db, subject, ct).ConfigureAwait(false))
        {
            outcomes.Add(await DsarRestrictions.RestrictAsync(db, scope.ServiceProvider.GetRequiredService<IClock>(), subject.PartyId, d.Class, d.Count, reason, legalBasis, retentionEnd, ct).ConfigureAwait(false));
        }

        return new DsarModuleResult(Module, outcomes);
    }

    private static async Task<List<(string Class, int Count, object Rows)>> CollectAsync(DocumentsDbContext db, SubjectRef subject, CancellationToken ct)
    {
        var party = subject.PartyId;
        var documents = await db.Documents.AsNoTracking().Where(d => d.RecipientPartyId == party).OrderBy(d => d.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => d.RecipientPartyId == party).OrderBy(d => d.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var deliveryIds = deliveries.Select(d => d.DeliveryId).ToList();
        var evidence = await db.Evidence.AsNoTracking().Where(e => deliveryIds.Contains(e.DeliveryId)).ToListAsync(ct).ConfigureAwait(false);
        return
        [
            (DsarRetentionPolicy.PolicyDocument, documents.Count, documents.Select(d => new
            {
                id = d.DocumentId.Value,
                number = d.DocumentNumber,
                template = d.TemplateKey,
                requestNo = d.RequestNo,
                status = d.Status,
                medium = d.Medium,
                archiveId = d.ArchiveId,
                contentHash = d.ContentHash,
                createdAt = d.CreatedAt.ToString(),
            }).ToList()),
            (Delivery, deliveries.Count, deliveries.Select(d => new
            {
                id = d.DeliveryId.Value,
                documentId = d.DocumentId.Value,
                channel = d.Channel,
                status = d.Status,
                proofLevel = d.ProofLevel,
                notificationDate = d.NotificationDate?.ToString(),
            }).ToList()),
            (DeliveryProof, evidence.Count, evidence.Select(e => new
            {
                id = e.EvidenceId,
                deliveryId = e.DeliveryId.Value,
                provider = e.Provider,
                eventType = e.EventType,
                proofDate = e.ProofDate?.ToString(),
                outcome = e.Outcome,
            }).ToList()),
        ];
    }
}
