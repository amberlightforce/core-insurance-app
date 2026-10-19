using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Documents.Contracts;

/// <summary>
/// Synchronous in-process SPI to request a letter from a template (SL6-DOC-CORE, D-SL6-12). The call renders the
/// document in the caller's transaction, records the delivery and publishes <c>DocumentRendered</c>. Delivery proof
/// arrives later as <c>DocumentDelivered</c> (with <c>notificationDate</c>) or <c>DeliveryFailed</c>; rendering is never proof.
/// </summary>
public interface IDocumentRequestService
{
    /// <summary>
    /// Requests a document. Idempotent on (<see cref="DocumentRequestSpec.TemplateKey"/>, <see cref="DocumentRequestSpec.RequestNo"/>):
    /// a repeat returns the existing document with <see cref="DocumentRequestOutcome.IsDuplicate"/> set and sends nothing again.
    /// </summary>
    Task<DocumentRequestOutcome> RequestAsync(DocumentRequestSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>What to render and to whom.</summary>
/// <param name="TemplateKey">Template, e.g. <c>DT-NONPAY-NOTICE</c>, <c>DT-WITHDRAWAL-ACK</c>, <c>DT-CANCELLATION-NOTICE</c>, <c>DT-REFUND-ADVICE</c>.</param>
/// <param name="RequestNo">Caller's request number; with the template key it is the de-duplication key.</param>
/// <param name="Subject">The business object the letter is about.</param>
/// <param name="RecipientPartyId">The addressee.</param>
/// <param name="Language"><c>el</c> or <c>en</c>.</param>
/// <param name="Placeholders">Values for the template placeholders (the only data a template can use).</param>
/// <param name="Medium">Requested delivery medium (<c>EMAIL</c>, <c>REGISTERED_POST</c>, ...); null takes the template default. A statutory rule can override it.</param>
public sealed record DocumentRequestSpec(
    string TemplateKey,
    string RequestNo,
    ObjectRef Subject,
    PartyId RecipientPartyId,
    string Language,
    IReadOnlyDictionary<string, string> Placeholders,
    string? Medium = null);

/// <summary>Result of <see cref="IDocumentRequestService.RequestAsync"/>.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="DocumentNumber">Its number.</param>
/// <param name="Status">Document state (<c>Rendered</c> or <c>Failed</c>).</param>
/// <param name="Medium">The medium actually used after statutory rules.</param>
/// <param name="IsDuplicate">True when the key was already requested.</param>
public sealed record DocumentRequestOutcome(DocumentId DocumentId, DocumentNumber DocumentNumber, string Status, string Medium, bool IsDuplicate);
