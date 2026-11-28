using System.Text.Json;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Domain;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Queries;

/// <summary><c>cmp.FiscalDocument.get</c> (REQ-CMP-054): the document and its identifiers, filtered by legal entity.</summary>
internal sealed class FiscalDocumentReader(ComplianceDbContext db)
{
    public async Task<FiscalDocumentGetResponse?> GetAsync(LegalEntityId legalEntity, FiscalDocumentId id, CancellationToken cancellationToken)
    {
        var row = await db.FiscalDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.FiscalDocumentId == id && d.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToResponse(row);
    }

    /// <summary><c>cmp.FiscalDocument.listBySource</c>: documents of this legal entity, newest first, optionally narrowed by source.</summary>
    public async Task<IReadOnlyList<FiscalDocumentGetResponse>> ListAsync(
        LegalEntityId legalEntity, string? sourceType, string? sourceId, int limit, CancellationToken cancellationToken)
    {
        var query = db.FiscalDocuments.AsNoTracking().Where(d => d.LegalEntityId == legalEntity);
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            query = query.Where(d => d.SourceType == sourceType);
        }

        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            query = query.Where(d => d.SourceId == sourceId);
        }

        var rows = await query.OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.FiscalDocumentId)
            .Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToResponse)];
    }

    internal static FiscalDocumentGetResponse ToResponse(FiscalDocumentRow row)
    {
        var currency = Currency.FromCode(row.Currency);
        var lines = JsonSerializer.Deserialize<List<FiscalDocumentRequestRequest.LineItem>>(row.Lines, SharedKernelJson.Options) ?? [];
        return new FiscalDocumentGetResponse
        {
            Document = new FiscalDocumentView
            {
                FiscalDocumentId = row.FiscalDocumentId,
                Status = Enum.Parse<FiscalDocumentView.StatusValue>(Pascal(row.Status)),
                SourceType = row.SourceType,
                SourceId = row.SourceId,
                Role = Enum.Parse<FiscalDocumentView.RoleValue>(Pascal(row.Role)),
                Revision = row.Revision,
                DocumentType = row.DocumentType,
                DocumentTypeIsPlaceholder = row.DocumentTypeIsPlaceholder,
                IssueDate = row.IssueDate,
                CounterpartyPartyId = row.CounterpartyPartyId,
                Total = new Money(row.Total, currency),
                Lines = [.. lines.Select(l => new FiscalDocumentView.LineItem { FiscalCategoryKey = l.FiscalCategoryKey, Amount = l.Amount, ChargeId = l.ChargeId })],
                RejectionCodes = row.RejectionCodes.Length == 0 ? null : row.RejectionCodes,
                CorrelatedDocumentId = row.CorrelatedDocumentId,
            },
            Identifiers = new FiscalDocumentIdentifiers
            {
                Series = row.Series,
                Number = row.Number,
                Mark = row.Mark,
                Uid = row.Uid,
                QrPayloadRef = row.QrPayloadRef,
                Channel = row.Channel,
                Stub = row.Stub,
            },
        };
    }

    private static string Pascal(string code) =>
        string.Concat(code.Split('_').Select(part => part[..1] + part[1..].ToLowerInvariant()));
}
