using System.Globalization;
using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Channels.Services;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Queries;

/// <summary>The outcome of a portal download: the bytes, or a refusal by the transaction-permission matrix.</summary>
internal sealed record PortalDownload(DocumentContent? Content, bool Denied);

/// <summary>
/// The customer's document vault (BO-CHN-04). Documents are read from DOC in-process by subject (one of the customer's policies) and kept only
/// when they are rendered and addressed to a party in the customer's scope. A customer never sees a document addressed to someone else, nor a
/// staff-internal type. Downloading is checked against the permission matrix (<c>document.download</c>) and recorded as "opened by portal".
/// </summary>
internal sealed class PortalDocumentReader(
    PortalScope scope,
    ChannelsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    PermissionMatrixService matrix,
    IServiceProvider services,
    IClock clock)
{
    private const string PortalChannel = "WEB_DIRECT";
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;
    private const int MaxPolicies = 50;
    private const int PerPolicy = 200;

    public async Task<PortalDocumentListPage?> ListAsync(string? policyId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return null;
        }

        var policies = await scope.MyPoliciesAsync(cancellationToken).ConfigureAwait(false);
        if (policyId is not null)
        {
            // A policy filter outside the customer's scope answers 404, like a policy detail would.
            if (!Guid.TryParse(policyId, out var wanted) || policies.All(p => p.PolicyId.Value != wanted))
            {
                return null;
            }

            policies = [.. policies.Where(p => p.PolicyId.Value == wanted)];
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var found = new List<(DocumentSearchItem Item, PolicyId Policy)>();
        if (services.GetService<IDocumentsDocumentService>() is { } documents)
        {
            foreach (var policy in policies.Take(MaxPolicies))
            {
                try
                {
                    var page = await documents.ListForObjectAsync(limit: PerPolicy, objectRef: $"POL/Policy/{policy.PolicyId.Value:D}", cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    found.AddRange(page.Items.Where(d => Visible(d, caller)).Select(d => (d, policy.PolicyId)));
                }
                catch (DomainException)
                {
                    // One policy's documents being unreadable must not blank the vault.
                }
            }
        }

        var ordered = found.DistinctBy(f => f.Item.DocumentId).OrderByDescending(f => f.Item.CreatedAt, StringComparer.Ordinal).ThenBy(f => f.Item.DocumentNumber, StringComparer.Ordinal).ToList();
        var pageItems = ordered.Skip(offset).Take(size).ToList();
        var ids = pageItems.Select(p => p.Item.DocumentId.Value).ToList();
        var legalEntity = legalEntities.Resolve(context.LegalEntity!.Value);
        var opens = await db.DocumentOpens.AsNoTracking()
            .Where(d => d.LegalEntityId == legalEntity && d.PartyId == caller.PartyId && ids.Contains(d.DocumentId))
            .ToDictionaryAsync(d => d.DocumentId, cancellationToken).ConfigureAwait(false);
        var items = pageItems.Select(p => new PortalDocument
        {
            DocumentId = p.Item.DocumentId,
            DocumentNumber = DocumentNumber.Parse(p.Item.DocumentNumber),
            DocumentType = p.Item.DocumentType,
            PolicyId = p.Policy,
            Language = p.Item.Language,
            CreatedAt = Instant.FromDateTimeOffset(DateTimeOffset.Parse(p.Item.CreatedAt, CultureInfo.InvariantCulture)),
            FirstOpenedAt = opens.TryGetValue(p.Item.DocumentId.Value, out var open) ? open.FirstOpenedAt : null,
            OpenCount = opens.TryGetValue(p.Item.DocumentId.Value, out var counted) ? counted.OpenCount : 0,
        }).ToList();
        var next = offset + size < ordered.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return new PortalDocumentListPage { Items = items, NextCursor = next, Limit = size };
    }

    /// <summary>
    /// The bytes of one document, or null (404) when the caller has no portal link, the id is malformed, the document does not exist or is not
    /// addressed to a party in the caller's scope. A matrix DENY or REFER answers <see cref="PortalDownload.Denied"/>.
    /// </summary>
    public async Task<PortalDownload?> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid)
            || await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller
            || services.GetService<IDocumentContentService>() is not { } content)
        {
            return null;
        }

        var found = await content.ReadAsync(new DocumentId(guid), cancellationToken).ConfigureAwait(false);
        if (found is null || !caller.PartyIds.Contains(found.RecipientPartyId))
        {
            return null;
        }

        var answer = await matrix.EvaluateAsync(new PermissionCheck("document.download", PortalChannel, null, null, null, null, false), cancellationToken).ConfigureAwait(false);
        if (!answer.IsSuccess || answer.Value.Decision.Outcome != MatrixOutcomes.Allow)
        {
            return new PortalDownload(null, true);
        }

        await RecordOpenAsync(caller.PartyId, found.DocumentId.Value, cancellationToken).ConfigureAwait(false);
        return new PortalDownload(found, false);
    }

    private async Task RecordOpenAsync(PartyId party, Guid documentId, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity!.Value);
        var now = clock.Now;
        var row = await db.DocumentOpens.SingleOrDefaultAsync(d => d.LegalEntityId == legalEntity && d.PartyId == party && d.DocumentId == documentId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.DocumentOpens.Add(new DocumentOpenRow
            {
                DocumentOpenId = Guid.CreateVersion7(), LegalEntityId = legalEntity, DocumentId = documentId, PartyId = party, FirstOpenedAt = now, LastOpenedAt = now, OpenCount = 1,
            });
        }
        else
        {
            row.LastOpenedAt = now;
            row.OpenCount++;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // TODO(hardening): two simultaneous first opens race on the unique index; the record of one is lost, the download is not.
            db.ChangeTracker.Clear();
        }
    }

    private static bool Visible(DocumentSearchItem d, PortalCaller caller) =>
        string.Equals(d.Status, "Rendered", StringComparison.Ordinal)
        && caller.PartyIds.Contains(d.RecipientPartyId)
        // TODO(hardening): a customer-visibility flag on the DOC document type (BO-DOC-05 catalogue) instead of this naming convention; DSAR-restricted recipients (DOC's own restriction table).
        && !d.DocumentType.Contains("INTERNAL", StringComparison.OrdinalIgnoreCase);
}
