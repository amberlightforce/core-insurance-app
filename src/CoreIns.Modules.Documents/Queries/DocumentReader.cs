using System.Text.Json;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Paging;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Documents.Queries;

internal sealed record DeliveryView(
    Guid DeliveryId, string Channel, string Medium, string Status, string? ProofLevel, DateOnly? NotificationDate, string? FailureReason, bool IsReprint);

internal sealed record DocumentView(
    Guid DocumentId, string DocumentNumber, string DocumentType, string RequestNo, string Subject, Guid RecipientPartyId,
    string Language, string Status, string Medium, Guid ArchiveId, string ContentHash, DateTimeOffset CreatedAt, IReadOnlyList<DeliveryView> Deliveries,
    bool CustomerVisible = false, bool Hidden = false, Guid? SupersededByDocumentId = null, string? WithdrawnReason = null);

internal sealed record DocumentSearchFilter(
    string? TemplateKey, string? Status, string? Medium, string? SubjectModule, string? SubjectType, string? SubjectId, Guid? RecipientPartyId,
    DateOnly? CreatedFrom, DateOnly? CreatedTo, string? Text, bool IncludeHidden = false);

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

    /// <summary>
    /// Library search: newest first by (created, document number); the cursor is an opaque keyset of both (NX-HD-12). Scoped to the legal entity;
    /// rows are the same views <see cref="GetAsync"/> serves, so nothing beyond <c>get</c> is exposed. Returns null when the cursor is malformed.
    /// </summary>
    public async Task<(IReadOnlyList<DocumentView> Items, string? NextCursor, int Limit)?> SearchAsync(
        DocumentSearchFilter f, string? cursor, int take, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryDecode(cursor, 2, out var after) || (after is not null && !KeysetPaging.TryLong(after[0], out _)))
        {
            return null;
        }

        var entity = LegalEntity;
        var query = db.Documents.AsNoTracking().Where(d => d.LegalEntityId == entity);
        if (!f.IncludeHidden)
        {
            query = query.Where(d => !d.Hidden);
        }

        if (f.TemplateKey is { Length: > 0 } key)
        {
            query = query.Where(d => d.TemplateKey == key);
        }

        if (f.Status is { Length: > 0 } status)
        {
            query = query.Where(d => d.Status == status);
        }

        if (f.Medium is { Length: > 0 } medium)
        {
            query = query.Where(d => d.Medium == medium);
        }

        if (f.SubjectModule is { Length: > 0 } sm)
        {
            query = query.Where(d => d.SubjectModule == sm);
        }

        if (f.SubjectType is { Length: > 0 } st)
        {
            query = query.Where(d => d.SubjectType == st);
        }

        if (f.SubjectId is { Length: > 0 } sid)
        {
            query = query.Where(d => d.SubjectId == sid);
        }

        if (f.RecipientPartyId is { } party)
        {
            var recipient = new PartyId(party);
            query = query.Where(d => d.RecipientPartyId == recipient);
        }

        if (f.CreatedFrom is { } from)
        {
            var start = Instant.FromUtcDateTime(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(d => d.CreatedAt >= start);
        }

        if (f.CreatedTo is { } to)
        {
            var end = Instant.FromUtcDateTime(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(d => d.CreatedAt < end);
        }

        if (f.Text is { Length: > 0 } text)
        {
            var like = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(d => EF.Functions.ILike(d.DocumentNumber, like) || EF.Functions.ILike(d.RequestNo, like));
        }

        if (after is not null)
        {
            var at = Instant.FromDateTimeOffset(new DateTimeOffset(long.Parse(after[0], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero));
            var number = after[1];
            query = query.Where(d => d.CreatedAt < at || (d.CreatedAt == at && d.DocumentNumber.CompareTo(number) < 0));
        }

        var rows = await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.DocumentNumber).Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.CreatedAt.ToDateTimeOffset().UtcTicks), r.DocumentNumber));
        return (await ViewsAsync(page, cancellationToken).ConfigureAwait(false), next, take);
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

    /// <summary>Archive entries (letters and their renditions) of the documents about one object; no bytes are read.</summary>
    public async Task<IReadOnlyList<ArchiveListForObjectItem>> ListArchivesForObjectAsync(ObjectRef subject, int limit, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var module = subject.Module.ToString();
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.LegalEntityId == entity && d.SubjectModule == module && d.SubjectType == subject.Type && d.SubjectId == subject.Id)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.DocumentId).Take(limit).Select(d => new { d.DocumentId, d.ArchiveId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = documents.Select(d => (Guid?)d.ArchiveId).ToList();
        var archives = await db.Archives.AsNoTracking()
            .Where(a => a.LegalEntityId == entity && (ids.Contains(a.ArchiveId) || ids.Contains(a.ParentArchiveId)))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.ArchiveId)
            .Select(a => new { a.ArchiveId, a.ContentHash, a.ContentType, a.ParentArchiveId, a.RenditionKind, a.RetentionClass, a.CreatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. archives.Select(a => new ArchiveListForObjectItem
            {
                ArchiveId = a.ArchiveId,
                Hash = Sha256Hash.Parse(a.ContentHash),
                DocumentId = documents.First(d => d.ArchiveId == (a.ParentArchiveId ?? a.ArchiveId)).DocumentId,
                ContentType = a.ContentType,
                ParentArchiveId = a.ParentArchiveId,
                RenditionKind = a.RenditionKind,
                RetentionClass = a.RetentionClass,
                CreatedAt = a.CreatedAt.ToDateTimeOffset().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }),
        ];
    }

    /// <summary>The stored bytes of an archive entry with its media type and stored hash (BO-DOC-04 download).</summary>
    public async Task<(byte[] Content, string ContentType, string Hash)?> DownloadAsync(Guid archiveId, CancellationToken cancellationToken)
    {
        var entity = LegalEntity;
        var row = await db.Archives.AsNoTracking().SingleOrDefaultAsync(a => a.ArchiveId == archiveId && a.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (row.Content, row.ContentType, row.ContentHash);
    }

    public static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, SharedKernelJson.Options);

    private async Task<IReadOnlyList<DocumentView>> ViewsAsync(IReadOnlyList<DocumentRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.DocumentId).ToList();
        var keys = rows.Select(r => r.TemplateKey).Distinct().ToList();
        var visible = (await db.DocumentTypes.AsNoTracking().Where(t => keys.Contains(t.DocumentType) && t.CustomerVisible).Select(t => t.DocumentType)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => ids.Contains(d.DocumentId)).OrderBy(d => d.CreatedAt).ThenBy(d => d.DeliveryId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.Select(r => new DocumentView(
                r.DocumentId.Value, r.DocumentNumber, r.TemplateKey, r.RequestNo, $"{r.SubjectModule}/{r.SubjectType}/{r.SubjectId}", r.RecipientPartyId.Value,
                r.Language, r.Status, r.Medium, r.ArchiveId, r.ContentHash, r.CreatedAt.ToDateTimeOffset(),
                [.. deliveries.Where(d => d.DocumentId == r.DocumentId).Select(d => new DeliveryView(
                    d.DeliveryId.Value, d.Channel, d.Medium, d.Status, d.ProofLevel, d.NotificationDate?.Value, d.FailureReason, d.IsReprint))],
                visible.Contains(r.TemplateKey), r.Hidden, r.SupersededBy?.Value, r.WithdrawnReason)),
        ];
    }
}
