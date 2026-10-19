using CoreIns.Modules.Documents.Commands;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;

namespace CoreIns.Modules.Documents.Services;

/// <summary>The in-process <see cref="IDocumentRequestService"/>: runs the request command in the caller's unit of work.</summary>
internal sealed class DocumentRequestService(ICommandHandler<RequestDocument, DocumentRequestOutcome> handler) : IDocumentRequestService
{
    public async Task<DocumentRequestOutcome> RequestAsync(DocumentRequestSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var result = await handler.HandleAsync(new RequestDocument(spec), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
    }
}
