using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Documents.Commands;

/// <summary><c>doc.Archive.verify</c>: recompute the SHA-256 of the stored bytes and compare it with the stored hash (BO-DOC-04).</summary>
internal sealed record VerifyArchive(Guid ArchiveId) : ICommand<ArchiveVerifyResponse>;

internal sealed class VerifyArchiveHandler(DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<VerifyArchive, ArchiveVerifyResponse>
{
    public async Task<Result<ArchiveVerifyResponse>> HandleAsync(VerifyArchive command, CancellationToken cancellationToken)
    {
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await db.Archives.AsNoTracking().SingleOrDefaultAsync(a => a.ArchiveId == command.ArchiveId && a.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The archive does not exist in your legal entity.");
        }

        var computed = Sha256Hash.Compute(row.Content);
        return new ArchiveVerifyResponse
        {
            ArchiveId = row.ArchiveId,
            Hash = Sha256Hash.Parse(row.ContentHash),
            ComputedHash = computed,
            Result = computed.Value == row.ContentHash ? ArchiveVerifyResponse.ResultValue.Ok : ArchiveVerifyResponse.ResultValue.Tampered,
        };
    }
}

/// <summary><c>doc.Archive.addRendition</c>: stores another artefact (for instance the PDF) next to an existing archive entry.</summary>
internal sealed record AddRendition(ArchiveAddRenditionRequest Request) : ICommand<ArchiveAddRenditionResponse>;

internal sealed class AddRenditionHandler(DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<AddRendition, ArchiveAddRenditionResponse>
{
    public async Task<Result<ArchiveAddRenditionResponse>> HandleAsync(AddRendition command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!StoreArchiveHandler.TryDecode(request.BinaryStream, out var bytes) || bytes.Length == 0)
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "binaryStream must be a non-empty base64 string of at most 5 MB.");
        }

        if (string.IsNullOrWhiteSpace(request.ContentType) || request.ContentType.Length > 100 || string.IsNullOrWhiteSpace(request.RenditionKind) || request.RenditionKind.Length > 32)
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "contentType (at most 100) and renditionKind (at most 32) are required.");
        }

        var hash = Sha256Hash.Compute(bytes);
        if (request.DeclaredHash is { } declared && declared.Value != hash.Value)
        {
            return DomainError.Of(ModuleCode.DOC, "HASH-MISMATCH", "The declared hash does not match the content.");
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var parent = await db.Archives.AsNoTracking().Where(a => a.ArchiveId == request.ParentArchiveId && a.LegalEntityId == entity)
            .Select(a => new { a.ArchiveId, a.ParentArchiveId, a.RetentionClass }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The parent archive does not exist in your legal entity.");
        }

        // A rendition belongs to the archive entry itself, never to another rendition.
        var root = parent.ParentArchiveId ?? parent.ArchiveId;
        var archive = new ArchiveRow
        {
            ArchiveId = Guid.CreateVersion7(),
            LegalEntityId = entity,
            ContentType = request.ContentType.Trim(),
            Content = bytes,
            ContentHash = hash.Value,
            Metadata = request.Metadata is { ValueKind: System.Text.Json.JsonValueKind.Object } m ? m.GetRawText() : "{}",
            CreatedAt = clock.Now,
            ParentArchiveId = root,
            RenditionKind = request.RenditionKind.Trim(),
            RetentionClass = parent.RetentionClass,
        };
        db.Archives.Add(archive);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ArchiveAddRenditionResponse { ArchiveId = archive.ArchiveId, Hash = hash };
    }
}

/// <summary>
/// <c>doc.Archive.setRetentionClass</c>: tags an archive entry and its renditions with a retention class. The accepted classes come
/// from the market pack (<see cref="DocumentsOptions.RetentionClasses"/>, empty until it is supplied); an unknown class is refused and no
/// retention duration is invented (D-REG-07). The change is audited by the command pipeline.
/// </summary>
internal sealed record SetRetentionClass(ArchiveSetRetentionClassRequest Request) : ICommand<ArchiveSetRetentionClassResponse>;

internal sealed class SetRetentionClassHandler(DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IOptions<DocumentsOptions> options)
    : ICommandHandler<SetRetentionClass, ArchiveSetRetentionClassResponse>
{
    public async Task<Result<ArchiveSetRetentionClassResponse>> HandleAsync(SetRetentionClass command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (string.IsNullOrWhiteSpace(request.RetentionClass) || !options.Value.RetentionClasses.Contains(request.RetentionClass, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.DOC, "RETENTION-CLASS-UNKNOWN", "The retention class is not in the market pack list.");
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var rows = await db.Archives.Where(a => a.LegalEntityId == entity && (a.ArchiveId == request.ArchiveId || a.ParentArchiveId == request.ArchiveId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.All(r => r.ArchiveId != request.ArchiveId))
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The archive does not exist in your legal entity.");
        }

        foreach (var row in rows)
        {
            row.RetentionClass = request.RetentionClass;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ArchiveSetRetentionClassResponse { ArchiveId = request.ArchiveId, RetentionClass = request.RetentionClass };
    }
}
