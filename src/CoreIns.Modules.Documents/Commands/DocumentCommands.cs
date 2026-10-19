using System.Text;
using System.Text.Json;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Contracts.Events;
using CoreIns.Modules.Documents.Domain;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Documents.Commands;

/// <summary>Documents settings: the legal entity's zone, which turns an instant into the business date of a proof.</summary>
public sealed class DocumentsOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Documents";

    /// <summary>IANA zone id of the legal entity (a stamp setting, not a regulatory value).</summary>
    public string TimeZone { get; set; } = "Europe/Athens";

    internal TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

/// <summary><c>doc.Document.request</c>: render a letter from a template and hand it to the delivery channel.</summary>
internal sealed record RequestDocument(DocumentRequestSpec Spec) : ICommand<DocumentRequestOutcome>;

internal sealed class RequestDocumentValidator : AbstractValidator<RequestDocument>
{
    public RequestDocumentValidator()
    {
        RuleFor(c => c.Spec.TemplateKey).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Spec.RequestNo).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Spec.Language).NotEmpty().MaximumLength(2);
    }
}

/// <summary>
/// Renders and "sends" one letter. Idempotent on (template key, request number): a repeat returns the same document and
/// sends nothing again (REQ-DOC RT-11). The medium follows the <see cref="StatutoryDeliveryRule"/>; rendering is never
/// proof of delivery, so the delivery stays <c>Sent</c> until a provider callback (<see cref="ReceiveProviderEventHandler"/>).
/// </summary>
internal sealed class RequestDocumentHandler(
    DocumentsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    INumberingService numbering,
    IOptions<DocumentsOptions> options) : ICommandHandler<RequestDocument, DocumentRequestOutcome>
{
    public async Task<Result<DocumentRequestOutcome>> HandleAsync(RequestDocument command, CancellationToken cancellationToken)
    {
        var spec = command.Spec;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

        var existing = await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.LegalEntityId == legalEntity && d.TemplateKey == spec.TemplateKey && d.RequestNo == spec.RequestNo, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new DocumentRequestOutcome(existing.DocumentId, DocumentNumber.Parse(existing.DocumentNumber), existing.Status, existing.Medium, IsDuplicate: true);
        }

        var template = Templates.Find(spec.TemplateKey);
        if (template is null)
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", $"Unknown template '{spec.TemplateKey}'.");
        }

        if (!Templates.IsLanguage(spec.Language))
        {
            return DomainError.Of(ModuleCode.DOC, "LANGUAGE-NOT-APPROVED", $"Language '{spec.Language}' is not approved for this template (el, en).");
        }

        if (spec.Medium is not null && !Media.IsKnown(spec.Medium))
        {
            return DomainError.Of(ModuleCode.DOC, "CHANNEL-NOT-PERMITTED", $"Medium '{spec.Medium}' is not available.");
        }

        var missing = Templates.Missing(template, spec.Placeholders);
        if (missing.Count > 0)
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", $"Missing placeholders: {string.Join(", ", missing)}.");
        }

        var now = clock.Now;
        var today = now.ToBusinessDate(options.Value.Zone);
        var medium = StatutoryDeliveryRule.MediumFor(template, spec.Medium);
        var documentId = DocumentId.New();
        var keys = BusinessKeys.Empty.With("documentId", documentId.Value.ToString("D"));
        var documentType = template.Key;

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DocumentRequestedV1.Descriptor), "Document", documentId.Value.ToString("D"),
            new DocumentRequestedV1 { RequestId = Guid.CreateVersion7(), DocumentType = documentType, SubjectRef = spec.Subject, RequesterType = "MODULE" },
            keys.With("requestId", spec.RequestNo)) { OccurredAt = now });

        var html = Templates.Render(template, spec.Language, spec.Placeholders);
        var bytes = Encoding.UTF8.GetBytes(html);
        var hash = Sha256Hash.Compute(bytes);
        var number = await numbering.NextAsync(new NumberRequest("DOCUMENT", today), cancellationToken).ConfigureAwait(false);

        var archive = new ArchiveRow
        {
            ArchiveId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            ContentType = "text/html; charset=utf-8",
            Content = bytes,
            ContentHash = hash.Value,
            Metadata = JsonSerializer.Serialize(new { documentId = documentId.Value, template = template.Key, language = spec.Language }),
            CreatedAt = now,
        };
        var document = new DocumentRow
        {
            DocumentId = documentId,
            LegalEntityId = legalEntity,
            DocumentNumber = number.Value,
            TemplateKey = template.Key,
            RequestNo = spec.RequestNo,
            SubjectModule = spec.Subject.Module.ToString(),
            SubjectType = spec.Subject.Type,
            SubjectId = spec.Subject.Id,
            RecipientPartyId = spec.RecipientPartyId,
            Language = spec.Language,
            Status = nameof(DocumentState.Rendered),
            Medium = medium,
            ArchiveId = archive.ArchiveId,
            ContentHash = hash.Value,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            RenderedAt = now,
        };
        db.Archives.Add(archive);
        db.Documents.Add(document);
        db.Deliveries.Add(NewDelivery(document, medium, now, isReprint: false));

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DocumentRenderedV1.Descriptor), "Document", documentId.Value.ToString("D"),
            new DocumentRenderedV1
            {
                DocumentNumber = DocumentNumber.Parse(number.Value),
                DocumentType = documentType,
                SubjectRef = spec.Subject,
                Language = spec.Language,
                Binding = template.Key == Templates.NonPayNotice,
                ContentHash = hash,
                ArchiveId = archive.ArchiveId.ToString("D"),
                Recipients = [new Recipient { Role = "ADDRESSEE", PartyId = spec.RecipientPartyId }],
            },
            keys) { OccurredAt = now });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentRequestOutcome(documentId, DocumentNumber.Parse(number.Value), document.Status, medium, IsDuplicate: false);
    }

    internal static DeliveryRow NewDelivery(DocumentRow document, string medium, Instant now, bool isReprint) => new()
    {
        DeliveryId = DeliveryId.New(),
        DocumentId = document.DocumentId,
        RecipientPartyId = document.RecipientPartyId,
        Channel = Media.ChannelFor(medium),
        Medium = medium,
        Status = nameof(DeliveryState.Sent),
        IsReprint = isReprint,
        CreatedAt = now,
        UpdatedAt = now,
    };
}

/// <summary>Body of a provider callback.</summary>
public sealed record ProviderEventBody
{
    /// <summary>The provider's own event id (idempotency key of the callback).</summary>
    public string? ProviderEventId { get; init; }

    /// <summary>The delivery the callback is about.</summary>
    public Guid? DeliveryId { get; init; }

    /// <summary><c>SENT</c>, <c>DELIVERED</c>, <c>RETURNED</c> or <c>BOUNCED</c>.</summary>
    public string? EventType { get; init; }

    /// <summary>The date the provider proves delivery (registered-post proof of delivery).</summary>
    public DateOnly? ProofDate { get; init; }

    /// <summary>The provider's proof reference.</summary>
    public string? ProofReference { get; init; }
}

/// <summary>Result of a provider callback.</summary>
/// <param name="Outcome"><c>APPLIED</c>, <c>DUPLICATE</c>, <c>IGNORED_TERMINAL</c> or <c>REJECTED_NO_PROOF</c>.</param>
/// <param name="DeliveryStatus">Delivery status after the callback.</param>
public sealed record ProviderEventResult(string Outcome, string DeliveryStatus);

/// <summary><c>doc.ProviderEvent.receive</c>: dev and stub provider callbacks (D-SL6-11); never served in Production.</summary>
internal sealed record ReceiveProviderEvent(string Provider, ProviderEventBody Body) : ICommand<ProviderEventResult>;

internal sealed class ReceiveProviderEventValidator : AbstractValidator<ReceiveProviderEvent>
{
    public ReceiveProviderEventValidator()
    {
        RuleFor(c => c.Provider).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Body.ProviderEventId).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Body.DeliveryId).NotNull();
        RuleFor(c => c.Body.EventType).Must(t => t is "SENT" or "DELIVERED" or "RETURNED" or "BOUNCED").WithMessage("eventType must be SENT, DELIVERED, RETURNED or BOUNCED.");
    }
}

/// <summary>
/// Applies a provider callback to a delivery. Delivery proof is a DELIVERED callback with a proof date; for registered
/// post (the non-payment notice) a DELIVERED without a proof date is recorded as evidence but proves nothing, so no
/// <c>DocumentDelivered</c> and no notification date follow (D-SL6-03). RETURNED publishes <c>DeliveryFailed</c>.
/// </summary>
internal sealed class ReceiveProviderEventHandler(
    DocumentsDbContext db,
    IClock clock,
    IEventPublisher events,
    IHostEnvironment environment,
    IOptions<DocumentsOptions> options) : ICommandHandler<ReceiveProviderEvent, ProviderEventResult>
{
    public async Task<Result<ProviderEventResult>> HandleAsync(ReceiveProviderEvent command, CancellationToken cancellationToken)
    {
        var body = command.Body;
        if (environment.IsProduction() || !Media.IsStubProvider(command.Provider))
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", $"Provider '{command.Provider}' is not available.");
        }

        var eventId = body.ProviderEventId!;
        var seen = await db.Evidence.AsNoTracking()
            .AnyAsync(e => e.Provider == command.Provider && e.ProviderEventId == eventId, cancellationToken).ConfigureAwait(false);
        var deliveryId = new DeliveryId(body.DeliveryId!.Value);
        var delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.DeliveryId == deliveryId, cancellationToken).ConfigureAwait(false);
        if (delivery is null)
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The delivery does not exist.");
        }

        if (seen)
        {
            return new ProviderEventResult("DUPLICATE", delivery.Status);
        }

        if (Media.ChannelFor(delivery.Medium) != command.Provider)
        {
            return DomainError.Of(ModuleCode.DOC, "CHANNEL-NOT-PERMITTED", $"Provider '{command.Provider}' does not carry this delivery.");
        }

        var now = clock.Now;
        var today = now.ToBusinessDate(options.Value.Zone);
        var proofDate = body.ProofDate is { } p ? new BusinessDate(p) : (BusinessDate?)null;
        var outcome = "APPLIED";
        var keys = BusinessKeys.Empty
            .With("documentId", delivery.DocumentId.Value.ToString("D"))
            .With("deliveryId", delivery.DeliveryId.Value.ToString("D"));

        var terminal = delivery.Status is nameof(DeliveryState.Delivered) or nameof(DeliveryState.Failed) or nameof(DeliveryState.Bounced);
        if (terminal)
        {
            // TODO(hardening): a RETURNED after DELIVERED (or the reverse) needs a human decision; today it is kept as evidence only.
            outcome = "IGNORED_TERMINAL";
        }
        else if (body.EventType == "DELIVERED")
        {
            // Fail closed: registered post needs a proof date, and a proof cannot lie in the future.
            if ((delivery.Medium == Media.RegisteredPost && proofDate is null) || (proofDate is { } d && d.Value > today.Value))
            {
                outcome = "REJECTED_NO_PROOF";
            }
            else
            {
                var notification = proofDate ?? today;
                delivery.Status = nameof(DeliveryState.Delivered);
                delivery.NotificationDate = notification;
                delivery.ProofLevel = StatutoryDeliveryRule.ProofLevel(delivery.Medium);
                delivery.UpdatedAt = now;
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(DocumentDeliveredV1.Descriptor), "Document", delivery.DocumentId.Value.ToString("D"),
                    new DocumentDeliveredV1
                    {
                        DeliveryId = delivery.DeliveryId,
                        RecipientRole = delivery.RecipientRole,
                        RecipientPartyId = delivery.RecipientPartyId,
                        Channel = delivery.Channel,
                        Medium = delivery.Medium,
                        ProofLevel = delivery.ProofLevel,
                        NotificationDate = notification,
                        EvidencePackId = delivery.DocumentId.Value,
                    },
                    keys) { OccurredAt = now });
            }
        }
        else if (body.EventType is "RETURNED" or "BOUNCED")
        {
            var returned = body.EventType == "RETURNED";
            delivery.Status = returned ? nameof(DeliveryState.Failed) : nameof(DeliveryState.Bounced);
            delivery.FailureReason = returned ? "ReturnedMail" : "HardBounce";
            delivery.UpdatedAt = now;
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(DeliveryFailedV1.Descriptor), "Document", delivery.DocumentId.Value.ToString("D"),
                new DeliveryFailedV1
                {
                    DeliveryId = delivery.DeliveryId,
                    RecipientRole = delivery.RecipientRole,
                    RecipientPartyId = delivery.RecipientPartyId,
                    Channel = delivery.Channel,
                    Reason = returned ? DeliveryFailedV1.ReasonValue.ReturnedMail : DeliveryFailedV1.ReasonValue.HardBounce,
                },
                keys) { OccurredAt = now });
        }

        db.Evidence.Add(new EvidenceRow
        {
            EvidenceId = Guid.CreateVersion7(),
            DeliveryId = delivery.DeliveryId,
            DocumentId = delivery.DocumentId,
            Provider = command.Provider,
            ProviderEventId = eventId,
            EventType = body.EventType!,
            ProofDate = proofDate,
            ProofReference = body.ProofReference,
            Outcome = outcome,
            ReceivedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderEventResult(outcome, delivery.Status);
    }
}

/// <summary><c>doc.Document.reprint</c>: a new delivery of the same archived document (same content, same hash).</summary>
internal sealed record ReprintDocument(DocumentId DocumentId, string? Reason) : ICommand<ReprintResult>;

internal sealed record ReprintResult(DocumentId DocumentId, DeliveryId DeliveryId, string Medium);

internal sealed class ReprintDocumentHandler(DocumentsDbContext db, IClock clock) : ICommandHandler<ReprintDocument, ReprintResult>
{
    public async Task<Result<ReprintResult>> HandleAsync(ReprintDocument command, CancellationToken cancellationToken)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.DocumentId == command.DocumentId, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The document does not exist.");
        }

        // TODO(hardening): reprint of a statutory notice needs an approval and a reason code; here it only adds a delivery.
        var delivery = RequestDocumentHandler.NewDelivery(document, document.Medium, clock.Now, isReprint: true);
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ReprintResult(document.DocumentId, delivery.DeliveryId, document.Medium);
    }
}

/// <summary><c>doc.Archive.store</c>: stores bytes with a declared hash.</summary>
internal sealed record StoreArchive(ArchiveStoreRequest Request) : ICommand<ArchiveStoreResponse>;

internal sealed class StoreArchiveHandler(DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<StoreArchive, ArchiveStoreResponse>
{
    private const int MaxBytes = 5 * 1024 * 1024;

    public async Task<Result<ArchiveStoreResponse>> HandleAsync(StoreArchive command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.BinaryStream is not { ValueKind: JsonValueKind.String } stream || !TryDecode(stream.GetString(), out var bytes))
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "binaryStream must be a base64 string of at most 5 MB.");
        }

        var hash = Sha256Hash.Compute(bytes);
        if (request.DeclaredHash is { } declared && declared.Value != hash.Value)
        {
            return DomainError.Of(ModuleCode.DOC, "HASH-MISMATCH", "The declared hash does not match the content.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var archive = new ArchiveRow
        {
            ArchiveId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            ContentType = "application/octet-stream",
            Content = bytes,
            ContentHash = hash.Value,
            Metadata = request.Metadata is { ValueKind: JsonValueKind.Object } m ? m.GetRawText() : "{}",
            CreatedAt = clock.Now,
        };
        db.Archives.Add(archive);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ArchiveStoreResponse { ArchiveId = archive.ArchiveId, Hash = hash };
    }

    private static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text) || text.Length > MaxBytes * 4 / 3 + 4)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(text);
            return bytes.Length <= MaxBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
