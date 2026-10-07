using System.Text.Json;
using CoreIns.Modules.Compliance.Commands;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Compliance.Services;

/// <summary>
/// The in-process contract <see cref="IComplianceFiscalDocumentService"/> (D-ARC-16): <c>request</c> and <c>get</c>
/// (SL-BIL, W5-CMP-01 subset); the other members are CMP-ERR-NOT-AVAILABLE until W5-CMP-01.
/// </summary>
internal sealed class ComplianceFiscalDocumentService(
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    ICommandHandler<RequestFiscalDocument, FiscalDocumentRequestResponse> requestHandler,
    FiscalDocumentReader reader) : IComplianceFiscalDocumentService
{
    public async Task<FiscalDocumentRequestResponse> RequestAsync(FiscalDocumentRequestRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await requestHandler.HandleAsync(new RequestFiscalDocument(request), cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<FiscalDocumentGetResponse> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var found = Guid.TryParse(id, out var guid)
            ? await reader.GetAsync(legalEntity, new FiscalDocumentId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return found ?? throw new DomainException(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The fiscal document does not exist in your legal entity."));
    }

    public Task<JsonElement> ImportAsync(FiscalDocumentImportRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw NotAvailable("cmp.FiscalDocument.import");

    public Task<FiscalDocumentListBySourcePage> ListBySourceAsync(
        string? cursor = null, int? limit = null, string? sourceType = null, Guid? sourceId = null, CancellationToken cancellationToken = default) =>
        throw NotAvailable("cmp.FiscalDocument.listBySource");

    public Task<FiscalDocumentQrPayloadResponse> QrPayloadAsync(CancellationToken cancellationToken = default) =>
        throw NotAvailable("cmp.FiscalDocument.qrPayload");

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error!);

    private static DomainException NotAvailable(string operation) =>
        new(DomainError.Of(ModuleCode.CMP, "NOT-AVAILABLE", $"{operation} is not built yet (SL-BIL builds the fiscal-document request and get on a stub channel)."));
}
