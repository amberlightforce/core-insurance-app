using System.Text.Json;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Compliance.Domain;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance.Commands;

/// <summary><c>cmp.FiscalDocument.request</c>: a source module asks CMP for a fiscal document (REQ-CMP-001, REQ-CMP-030).</summary>
internal sealed record RequestFiscalDocument(FiscalDocumentRequestRequest Request) : ICommand<FiscalDocumentRequestResponse>;

internal sealed class RequestFiscalDocumentValidator : AbstractValidator<RequestFiscalDocument>
{
    public RequestFiscalDocumentValidator()
    {
        RuleFor(c => c.Request.SourceType).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.SourceId).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Revision).GreaterThanOrEqualTo(0).When(c => c.Request.Revision is not null);
        RuleFor(c => c.Request.Lines).NotEmpty();
        RuleForEach(c => c.Request.Lines).ChildRules(line => line.RuleFor(l => l.FiscalCategoryKey).NotEmpty().MaximumLength(128));
    }
}

/// <summary>
/// Builds, numbers and transmits one fiscal document through the bound <see cref="IFiscalDocumentChannel"/> (SL-BIL
/// subset of W5-CMP-01):
/// <list type="bullet">
/// <item>idempotent on the fiscal key source type + source id + role + revision (REQ-CMP-031): a repeated request returns
/// the existing document and creates nothing;</item>
/// <item>CMP alone issues the series and number (D3, REQ-CMP-038): the series comes from the channel's <c>series</c>, the
/// number from <c>cmp.fiscal_series</c> under a row lock in the caller's transaction (gapless);</item>
/// <item>the document type is the <see cref="FiscalDocuments.PlaceholderDocumentType"/> placeholder: myDATA document
/// types are open (OQ-012) and are never invented;</item>
/// <item>the channel is called inline (the stub makes no external call); a Registered outcome publishes
/// <c>FiscalDocRegistered</c>, a Rejected one <c>FiscalDocRejected</c>, a Queued one leaves the document Pending.
/// Micro-batch transmission by the worker (REQ-CMP-042) replaces the inline call with the real channel.</item>
/// </list>
/// No channel bound (Production until a real myDATA adapter exists) is CMP-ERR-TRANSPORT-UNAVAILABLE.
/// </summary>
internal sealed class RequestFiscalDocumentHandler(
    ComplianceDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    IServiceProvider services) : ICommandHandler<RequestFiscalDocument, FiscalDocumentRequestResponse>
{
    public async Task<Result<FiscalDocumentRequestResponse>> HandleAsync(RequestFiscalDocument command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!FiscalDocuments.SourceTypes.Contains(request.SourceType))
        {
            return DomainError.Of(ModuleCode.CMP, "SOURCE-UNKNOWN", $"Fiscal source type '{request.SourceType}' is not one REQ-CMP-030 lists.");
        }

        var currency = request.Lines[0].Amount.Currency;
        if (request.Lines.Any(l => l.Amount.Currency != currency || !l.Amount.IsRoundedToMinorUnits))
        {
            return DomainError.Of(ModuleCode.CMP, "FISCAL-TOTAL", "Fiscal lines must share one currency and be rounded to minor units (REQ-CMP-039).");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var role = request.Role switch
        {
            FiscalDocumentRequestRequest.RoleValue.Issue => FiscalDocumentRole.Issue,
            FiscalDocumentRequestRequest.RoleValue.Credit => FiscalDocumentRole.Credit,
            _ => FiscalDocumentRole.Cancellation,
        };
        var roleCode = FiscalDocuments.Code(role);
        var revision = request.Revision ?? 0;

        // Credit notes (SL3-CMP-CREDIT, REQ-CMP-032): the correlated document is the original ISSUE the credit corrects.
        if (role != FiscalDocumentRole.Credit && request.CorrelatedDocumentId is not null)
        {
            return DomainError.Of(ModuleCode.CMP, "VALIDATION", "correlatedDocumentId applies to role CREDIT only.");
        }

        if (request.CorrelatedDocumentId is { } given && request.OriginalFiscalDocumentId is { } original && given != original.Value)
        {
            return DomainError.Of(ModuleCode.CMP, "VALIDATION", "correlatedDocumentId and originalFiscalDocumentId must be equal when both are given.");
        }

        var existing = await db.FiscalDocuments.AsNoTracking().SingleOrDefaultAsync(
            d => d.LegalEntityId == legalEntity && d.SourceType == request.SourceType && d.SourceId == request.SourceId && d.Role == roleCode && d.Revision == revision,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new FiscalDocumentRequestResponse { FiscalDocumentId = existing.FiscalDocumentId, Status = existing.Status, DocumentType = existing.DocumentType };
        }

        // Replays returned above before this check, so a replayed credit stays stable. A new credit needs a Registered ISSUE
        // original of the same legal entity and counterparty; a credit of a credit has Role CREDIT and is refused here.
        Guid? correlatedId = role == FiscalDocumentRole.Credit ? request.CorrelatedDocumentId ?? request.OriginalFiscalDocumentId?.Value : null;
        string? correlatedMark = null;
        if (correlatedId is { } correlated)
        {
            var target = new FiscalDocumentId(correlated);
            var originalRow = await db.FiscalDocuments.AsNoTracking().SingleOrDefaultAsync(
                d => d.FiscalDocumentId == target && d.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
            if (originalRow is null
                || originalRow.Role != FiscalDocuments.Code(FiscalDocumentRole.Issue)
                || originalRow.Status != FiscalDocuments.Code(FiscalDocumentStatus.Registered)
                || originalRow.CounterpartyPartyId != request.CounterpartyPartyId)
            {
                return DomainError.Of(ModuleCode.CMP, "CORRELATED-NOT-FOUND", "The correlated document does not exist, is not a Registered ISSUE document, or belongs to another counterparty.");
            }

            correlatedMark = originalRow.Mark;
        }

        var channel = services.GetService<IFiscalDocumentChannel>();
        if (channel is null)
        {
            return DomainError.Of(ModuleCode.CMP, "TRANSPORT-UNAVAILABLE", "No fiscal-document channel is bound for this legal entity.");
        }

        var total = Money.Sum(request.Lines.Select(l => l.Amount), currency);
        var source = new FiscalSource
        {
            LegalEntityId = legalEntity.Value,
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            DocumentType = FiscalDocuments.PlaceholderDocumentType,
            IssueDate = request.IssueDate.Value,
            CounterpartyRef = request.CounterpartyPartyId.Value.ToString("D"),
            Lines = [.. request.Lines.Select(l => new FiscalSourceLine(l.FiscalCategoryKey, new SpiMoney(l.Amount.Amount, l.Amount.Currency.Code), []))],
        };

        var now = clock.Now;
        var series = await channel.SeriesAsync(FiscalDocuments.PlaceholderDocumentType, cancellationToken).ConfigureAwait(false);

        // CMP is the sole issuer (REQ-CMP-038) and keeps credits in their own gapless series.
        var seriesId = role == FiscalDocumentRole.Credit ? series.SeriesId + FiscalDocuments.CreditSeriesSuffix : series.SeriesId;
        var number = await NextNumberAsync(legalEntity, seriesId, cancellationToken).ConfigureAwait(false);
        var built = await channel.BuildAsync(source, cancellationToken).ConfigureAwait(false);
        var document = built with { Series = seriesId, Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        var row = new FiscalDocumentRow
        {
            FiscalDocumentId = FiscalDocumentId.New(),
            LegalEntityId = legalEntity,
            Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value,
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            Role = roleCode,
            Revision = revision,
            CorrelatedDocumentId = correlatedId,
            Status = FiscalDocuments.Code(FiscalDocumentStatus.Pending),
            DocumentType = document.DocumentType,
            DocumentTypeIsPlaceholder = string.Equals(document.DocumentType, FiscalDocuments.PlaceholderDocumentType, StringComparison.Ordinal),
            IssueDate = request.IssueDate,
            CounterpartyPartyId = request.CounterpartyPartyId,
            Total = total.Amount,
            Currency = currency.Code,
            Lines = JsonSerializer.Serialize(request.Lines, SharedKernelJson.Options),
            Series = document.Series,
            Number = document.Number,
            Channel = channel.GetType().Name,
            Stub = FiscalDocuments.IsStub(document),
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };

        if (!context.DryRun)
        {
            var key = $"{request.SourceType}|{request.SourceId}|{roleCode}|{revision}";
            var result = await channel.SubmitAsync(document, key, cancellationToken).ConfigureAwait(false);
            Apply(row, result, now, correlatedMark);
        }

        db.FiscalDocuments.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new FiscalDocumentRequestResponse { FiscalDocumentId = row.FiscalDocumentId, Status = row.Status, DocumentType = row.DocumentType };
    }

    private void Apply(FiscalDocumentRow row, FiscalSubmissionResult result, Instant now, string? correlatedMark)
    {
        var keys = BusinessKeys.Empty.With("fiscalDocumentId", row.FiscalDocumentId.Value.ToString());
        switch (result.Status)
        {
            case FiscalSubmissionStatus.Registered when result.RegistrationId is { Length: > 0 } mark && result.Uid is { Length: > 0 } uid:
                row.Status = FiscalDocuments.Code(FiscalDocumentStatus.Registered);
                row.Mark = mark;
                row.Uid = uid;
                row.QrPayloadRef = $"cmp:fiscal-document:{row.FiscalDocumentId.Value:D}:qr";
                row.RegisteredAt = now;
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(FiscalDocRegisteredV1.Descriptor), "FiscalDocument", row.FiscalDocumentId.Value.ToString(),
                    new FiscalDocRegisteredV1
                    {
                        SourceType = row.SourceType, SourceId = row.SourceId, DocumentType = row.DocumentType, Mark = mark, Uid = uid,
                        QrPayloadRef = row.QrPayloadRef, CorrelatedMark = correlatedMark,
                    },
                    keys) { OccurredAt = now });
                break;
            case FiscalSubmissionStatus.Rejected:
                row.Status = FiscalDocuments.Code(FiscalDocumentStatus.Rejected);
                row.RejectionCodes = [.. result.Rejections.Select(r => r.Code)];
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(FiscalDocRejectedV1.Descriptor), "FiscalDocument", row.FiscalDocumentId.Value.ToString(),
                    new FiscalDocRejectedV1
                    {
                        SourceType = row.SourceType, SourceId = row.SourceId,
                        CauseGroup = result.Rejections.Count > 0 ? result.Rejections[0].Category.ToString().ToUpperInvariant() : "UNSPECIFIED",
                        Codes = row.RejectionCodes,
                    },
                    keys) { OccurredAt = now });
                break;
            default:
                // Queued (channel outage, REQ-CMP-056) or an unexpected outcome: the document stays Pending.
                break;
        }
    }

    private async Task<long> NextNumberAsync(LegalEntityId legalEntity, string seriesId, CancellationToken cancellationToken)
    {
        // Gapless: the row lock is held until the caller's transaction ends; a rollback releases the number.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO cmp.fiscal_series (legal_entity_id, series_id, last_number) VALUES ({legalEntity.Value}, {seriesId}, 0) ON CONFLICT DO NOTHING",
            cancellationToken).ConfigureAwait(false);
        var next = await db.Database.SqlQuery<long>(
            $"UPDATE cmp.fiscal_series SET last_number = last_number + 1 WHERE legal_entity_id = {legalEntity.Value} AND series_id = {seriesId} RETURNING last_number AS \"Value\"")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return next.Single();
    }
}

/// <summary>Audit facts of <c>cmp.FiscalDocument.request</c>: the document and its source.</summary>
internal sealed class RequestFiscalDocumentAuditor : ICommandAuditor<RequestFiscalDocument, FiscalDocumentRequestResponse>
{
    public CommandAuditFacts Describe(RequestFiscalDocument command, Result<FiscalDocumentRequestResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("fiscalSourceId", command.Request.SourceId);
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CMP, "FiscalDocument", success.Value.FiscalDocumentId),
            BusinessKeys = keys.With("fiscalDocumentId", success.Value.FiscalDocumentId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { status = success.Value.Status, sourceType = command.Request.SourceType, lines = command.Request.Lines.Count }),
        };
    }
}
