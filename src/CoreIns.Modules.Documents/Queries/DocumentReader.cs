using System.Text.Json;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Documents.Queries;

internal sealed record DeliveryView(
    Guid DeliveryId, string Channel, string Medium, string Status, string? ProofLevel, DateOnly? NotificationDate, string? FailureReason, bool IsReprint);

internal sealed record DocumentView(
    Guid DocumentId, string DocumentNumber, string DocumentType, string RequestNo, string Subject, Guid RecipientPartyId,
    string Language, string Status, string Medium, Guid ArchiveId, string ContentHash, DateTimeOffset CreatedAt, IReadOnlyList<DeliveryView> Deliveries);

internal sealed record EvidenceView(
    Guid EvidenceId, Guid DeliveryId, string Provider, string ProviderEventId, string EventType, DateOnly? ProofDate, string? ProofReference, string Outcome, DateTimeOffset ReceivedAt);

/// <summary>Reads documents, deliveries, evidence and archives of the request's legal entity.</summary>
internal sealed class DocumentReader(DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private LegalEntityId LegalEntity =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public async Task<DocumentView?> GetAsync(DocumentId id, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var row = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.DocumentId == id && d.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await ViewsAsync([row], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<DocumentView>> ListForObjectAsync(ObjectRef subject, int limit, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var module = subject.Module.ToString();
        var rows = await db.Documents.AsNoTracking()
            .Where(d => d.LegalEntityId == entity && d.SubjectModule == module && d.SubjectType == subject.Type && d.SubjectId == subject.Id)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.DocumentId).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ViewsAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delivery statuses of one document, or of every document about a subject; complete when no delivery is still pending proof.</summary>
    public async Task<(IReadOnlyList<DeliveryView> Statuses, bool Complete)?> StatusAsync(DocumentId? documentId, ObjectRef? subject, CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentView> documents;
        if (documentId is { } id)
        {
            var one = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (one is null)
            {
                return null;
            }

            documents = [one];
        }
        else if (subject is not null)
        {
            documents = await ListForObjectAsync(subject, 200, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return null;
        }

        var statuses = documents.SelectMany(d => d.Deliveries).ToList();
        return (statuses, statuses.All(s => s.Status is "Delivered" or "Failed" or "Bounced"));
    }

    public async Task<IReadOnlyList<EvidenceView>> EvidenceAsync(DocumentId id, CancellationToken cancellationToken)
    {
        var rows = await db.Evidence.AsNoTracking().Where(e => e.DocumentId == id).OrderBy(e => e.ReceivedAt).ThenBy(e => e.EvidenceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.Select(e => new EvidenceView(
                e.EvidenceId, e.DeliveryId.Value, e.Provider, e.ProviderEventId, e.EventType, e.ProofDate?.Value, e.ProofReference, e.Outcome, e.ReceivedAt.ToDateTimeOffset())),
        ];
    }

    public async Task<ArchiveGetResponse?> GetArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var row = await db.Archives.AsNoTracking().SingleOrDefaultAsync(a => a.ArchiveId == id && a.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new ArchiveGetResponse { ArchiveId = row.ArchiveId, Hash = Sha256Hash.Parse(row.ContentHash) };
    }

    /// <summary>The archived bytes of a document (the HTML letter).</summary>
    public async Task<(byte[] Content, string ContentType)?> ContentAsync(Guid archiveId, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var row = await db.Archives.AsNoTracking().SingleOrDefaultAsync(a => a.ArchiveId == archiveId && a.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (row.Content, row.ContentType);
    }

    public static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, SharedKernelJson.Options);

    private async Task<IReadOnlyList<DocumentView>> ViewsAsync(IReadOnlyList<DocumentRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.DocumentId).ToList();
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => ids.Contains(d.DocumentId)).OrderBy(d => d.CreatedAt).ThenBy(d => d.DeliveryId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.Select(r => new DocumentView(
                r.DocumentId.Value, r.DocumentNumber, r.TemplateKey, r.RequestNo, $"{r.SubjectModule}/{r.SubjectType}/{r.SubjectId}", r.RecipientPartyId.Value,
                r.Language, r.Status, r.Medium, r.ArchiveId, r.ContentHash, r.CreatedAt.ToDateTimeOffset(),
                [.. deliveries.Where(d => d.DocumentId == r.DocumentId).Select(d => new DeliveryView(
                    d.DeliveryId.Value, d.Channel, d.Medium, d.Status, d.ProofLevel, d.NotificationDate?.Value, d.FailureReason, d.IsReprint))])),
        ];
    }
}
