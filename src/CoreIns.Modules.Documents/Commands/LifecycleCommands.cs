using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Events;
using CoreIns.Modules.Documents.Domain;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Modules.Documents.Services.Dsar;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Documents.Commands;

/// <summary>
/// Shared rules of the lifecycle commands (BO-DOC-02). A document is never edited or deleted: these commands add a delivery,
/// flip a list flag or move the document to <c>Superseded</c>; the archive and every delivery and evidence row stay as they are.
/// </summary>
internal static class LifecycleRules
{
    public const int MaxReason = 500;
    public const string AdminRole = "Platform.Admin";

    public static bool IsStatutory(DocumentRow document) => document.TemplateKey == Templates.NonPayNotice;

    public static DomainError? ReasonProblem(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReason
            ? DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", $"A reason (at most {MaxReason} characters) is required.")
            : null;

    public static DomainError NotFound() => DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The document does not exist in your legal entity.");

    public static LegalEntityId Entity(RequestContext context, ILegalEntityDirectory legalEntities) =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>A statutory notice can only be withdrawn or replaced by the admin role (the reason is required for every document).</summary>
    public static DomainError? StatutoryRoleProblem(DocumentRow document, RequestContext context) =>
        IsStatutory(document) && !context.Roles.Contains(AdminRole)
            ? DomainError.Of(ModuleCode.DOC, "ROLE-REQUIRED", "Withdrawing or replacing a statutory notice needs the Platform.Admin role.")
            : null;
}

/// <summary><c>doc.Document.resend</c>: a new delivery of the same archived document (same content, same hash).</summary>
internal sealed record ResendDocument(DocumentId DocumentId, PartyId? Recipient, string? Channel, string? Reason) : ICommand<ResendResult>;

internal sealed record ResendResult(DocumentId DocumentId, DeliveryId DeliveryId, string Medium);

/// <summary>
/// Hands the archived letter to the delivery channel again. The medium rules are those of the request: a statutory notice always
/// goes by registered post; any other known medium may be chosen. A recipient under a DSAR delivery restriction gets nothing.
/// The earlier deliveries and their evidence are untouched; the new delivery starts at <c>Sent</c> and only a provider callback proves it.
/// </summary>
internal sealed class ResendDocumentHandler(
    DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock) : ICommandHandler<ResendDocument, ResendResult>
{
    public async Task<Result<ResendResult>> HandleAsync(ResendDocument command, CancellationToken cancellationToken)
    {
        if (LifecycleRules.ReasonProblem(command.Reason) is { } problem)
        {
            return problem;
        }

        var entity = LifecycleRules.Entity(context, legalEntities);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.DocumentId == command.DocumentId && d.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return LifecycleRules.NotFound();
        }

        if (document.Status != nameof(DocumentState.Rendered))
        {
            return DomainError.Of(ModuleCode.DOC, "INVALID-STATE-TRANSITION", $"A {document.Status} document cannot be resent.");
        }

        var medium = command.Channel is { Length: > 0 } requested ? requested : document.Medium;
        if (!Media.IsKnown(medium) || (LifecycleRules.IsStatutory(document) && medium != Media.RegisteredPost))
        {
            return DomainError.Of(ModuleCode.DOC, "CHANNEL-NOT-PERMITTED", $"Medium '{medium}' is not available for this document.");
        }

        var recipient = command.Recipient ?? document.RecipientPartyId;
        if (await DsarRestrictions.IsRestrictedAsync(db, recipient, DocumentsDsarParticipant.Delivery, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.DOC, "DSAR-RESTRICTED", "The recipient's data is restricted (DSAR); the document is not sent again.");
        }

        var delivery = RequestDocumentHandler.NewDelivery(document, medium, clock.Now, isReprint: false);
        delivery.RecipientPartyId = recipient;
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ResendResult(document.DocumentId, delivery.DeliveryId, medium);
    }
}

/// <summary><c>doc.Document.hide</c> / <c>doc.Document.unhide</c>: list hygiene. Repeating the same call changes nothing.</summary>
internal sealed record SetDocumentHidden(DocumentId DocumentId, bool Hidden, string? Reason) : ICommand<HiddenResult>;

internal sealed record HiddenResult(string Status, bool Hidden);

internal sealed class SetDocumentHiddenHandler(DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<SetDocumentHidden, HiddenResult>
{
    public async Task<Result<HiddenResult>> HandleAsync(SetDocumentHidden command, CancellationToken cancellationToken)
    {
        if (LifecycleRules.ReasonProblem(command.Reason) is { } problem)
        {
            return problem;
        }

        var entity = LifecycleRules.Entity(context, legalEntities);
        var document = await db.Documents.SingleOrDefaultAsync(d => d.DocumentId == command.DocumentId && d.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return LifecycleRules.NotFound();
        }

        if (document.Hidden != command.Hidden)
        {
            document.Hidden = command.Hidden;
            document.HiddenReason = command.Hidden ? command.Reason!.Trim() : null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new HiddenResult(document.Status, document.Hidden);
    }
}

/// <summary>
/// <c>doc.Document.supersede</c> (replacement set) and <c>doc.Document.withdraw</c> (no replacement): the document becomes
/// <c>Superseded</c> and says why; <c>DocumentSuperseded</c> is published. The archive, deliveries and evidence are kept.
/// </summary>
internal sealed record SupersedeDocument(DocumentId DocumentId, DocumentId? Replacement, string? Reason) : ICommand<SupersedeResult>;

internal sealed record SupersedeResult(string Status, DocumentId? SupersededBy, string Reason);

internal sealed class SupersedeDocumentHandler(
    DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events)
    : ICommandHandler<SupersedeDocument, SupersedeResult>
{
    public async Task<Result<SupersedeResult>> HandleAsync(SupersedeDocument command, CancellationToken cancellationToken)
    {
        if (LifecycleRules.ReasonProblem(command.Reason) is { } problem)
        {
            return problem;
        }

        var reason = command.Reason!.Trim();
        var entity = LifecycleRules.Entity(context, legalEntities);
        var document = await db.Documents.SingleOrDefaultAsync(d => d.DocumentId == command.DocumentId && d.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return LifecycleRules.NotFound();
        }

        if (LifecycleRules.StatutoryRoleProblem(document, context) is { } role)
        {
            return role;
        }

        if (document.Status == nameof(DocumentState.Superseded))
        {
            // Repeating the same outcome is a no-op; a different outcome on a superseded document is refused.
            return document.SupersededBy == command.Replacement
                ? new SupersedeResult(document.Status, document.SupersededBy, document.WithdrawnReason ?? reason)
                : DomainError.Of(ModuleCode.DOC, "INVALID-STATE-TRANSITION", "The document is already superseded or withdrawn.");
        }

        if (command.Replacement is { } replacementId)
        {
            if (replacementId == document.DocumentId)
            {
                return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "A document cannot replace itself.");
            }

            var replacement = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.DocumentId == replacementId && d.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
            if (replacement is null)
            {
                return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The replacement document does not exist in your legal entity.");
            }

            if (replacement.Status != nameof(DocumentState.Rendered)
                || replacement.TemplateKey != document.TemplateKey
                || replacement.SubjectModule != document.SubjectModule || replacement.SubjectType != document.SubjectType || replacement.SubjectId != document.SubjectId)
            {
                return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "The replacement must be a rendered document of the same type about the same subject.");
            }
        }

        var next = DocumentStateModel.Machine.Fire(Enum.Parse<DocumentState>(document.Status), DocumentTrigger.Supersede);
        if (next.IsFailure)
        {
            return DomainError.Of(ModuleCode.DOC, "INVALID-STATE-TRANSITION", $"A {document.Status} document cannot be superseded or withdrawn.");
        }

        var now = clock.Now;
        document.Status = next.Value.ToString();
        document.SupersededBy = command.Replacement;
        document.WithdrawnReason = reason;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DocumentSupersededV1.Descriptor), "Document", document.DocumentId.Value.ToString("D"),
            new DocumentSupersededV1 { SupersededByDocumentId = command.Replacement, Reason = reason },
            BusinessKeys.Empty.With("documentId", document.DocumentId.Value.ToString("D"))) { OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SupersedeResult(document.Status, document.SupersededBy, reason);
    }
}
