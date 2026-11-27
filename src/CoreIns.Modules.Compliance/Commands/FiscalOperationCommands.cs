using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Domain;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Compliance.Commands;

/// <summary><c>cmp.FiscalDocument.resubmit</c>: send a Rejected or Pending fiscal document to the channel again (REQ-CMP-049).</summary>
internal sealed record ResubmitFiscalDocument(FiscalDocumentResubmitRequest Request) : ICommand<FiscalDocumentResubmitResponse>;

/// <summary><c>cmp.FiscalDocument.bulkResubmit</c>: resubmit up to <see cref="FiscalDocuments.MaxBulkResubmit"/> documents with a per-item result.</summary>
internal sealed record BulkResubmitFiscalDocuments(FiscalDocumentBulkResubmitRequest Request) : ICommand<FiscalDocumentBulkResubmitResponse>;

/// <summary><c>cmp.FiscalDocument.cancel</c>: cancel a fiscal document (linked CANCELLATION document when it was registered).</summary>
internal sealed record CancelFiscalDocument(FiscalDocumentCancelRequest Request) : ICommand<FiscalDocumentCancelResponse>;

/// <summary><c>cmp.FiscalDocument.correct</c>: reverse a registered document by a linked credit and issue its corrected replacement.</summary>
internal sealed record CorrectFiscalDocument(FiscalDocumentCorrectRequest Request) : ICommand<FiscalDocumentCorrectResponse>;

internal sealed class ResubmitFiscalDocumentValidator : AbstractValidator<ResubmitFiscalDocument>
{
    public ResubmitFiscalDocumentValidator() => RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
}

internal sealed class BulkResubmitFiscalDocumentsValidator : AbstractValidator<BulkResubmitFiscalDocuments>
{
    public BulkResubmitFiscalDocumentsValidator()
    {
        RuleFor(c => c.Request.Ids).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class CancelFiscalDocumentValidator : AbstractValidator<CancelFiscalDocument>
{
    public CancelFiscalDocumentValidator() => RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
}

internal sealed class CorrectFiscalDocumentValidator : AbstractValidator<CorrectFiscalDocument>
{
    public CorrectFiscalDocumentValidator()
    {
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
        RuleFor(c => c.Request.Corrections).NotEmpty();
        RuleForEach(c => c.Request.Corrections).ChildRules(line => line.RuleFor(l => l.FiscalCategoryKey).NotEmpty().MaximumLength(128));
    }
}

internal static class FiscalOperationNames
{
    public static TEnum Status<TEnum>(string code)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(FiscalDocumentReaderNames.Pascal(code));
}

internal sealed class ResubmitFiscalDocumentHandler(FiscalDocumentOperations operations, ComplianceDbContext db)
    : ICommandHandler<ResubmitFiscalDocument, FiscalDocumentResubmitResponse>
{
    public async Task<Result<FiscalDocumentResubmitResponse>> HandleAsync(ResubmitFiscalDocument command, CancellationToken cancellationToken)
    {
        var result = await operations.ResubmitAsync(command.Request.FiscalDocumentId.Value, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var row = result.Value.Row;
        return new FiscalDocumentResubmitResponse
        {
            FiscalDocumentId = row.FiscalDocumentId,
            Status = FiscalOperationNames.Status<FiscalDocumentResubmitResponse.StatusValue>(row.Status),
            RejectionCodes = row.RejectionCodes.Length == 0 ? null : row.RejectionCodes,
            Resubmitted = result.Value.Resubmitted,
        };
    }
}

internal sealed class BulkResubmitFiscalDocumentsHandler(FiscalDocumentOperations operations, ComplianceDbContext db)
    : ICommandHandler<BulkResubmitFiscalDocuments, FiscalDocumentBulkResubmitResponse>
{
    public async Task<Result<FiscalDocumentBulkResubmitResponse>> HandleAsync(BulkResubmitFiscalDocuments command, CancellationToken cancellationToken)
    {
        var ids = command.Request.Ids.Distinct().ToList();
        if (ids.Count > FiscalDocuments.MaxBulkResubmit)
        {
            return DomainError.Of(ModuleCode.CMP, "BULK-LIMIT", $"A bulk resubmit takes at most {FiscalDocuments.MaxBulkResubmit} documents.");
        }

        var results = new List<FiscalDocumentBulkResubmitResponse.ResultItem>();
        foreach (var id in ids)
        {
            var one = await operations.ResubmitAsync(id, command.Request.Reason, cancellationToken).ConfigureAwait(false);
            if (one.IsSuccess)
            {
                var row = one.Value.Row;
                results.Add(new FiscalDocumentBulkResubmitResponse.ResultItem
                {
                    FiscalDocumentId = row.FiscalDocumentId,
                    Outcome = one.Value.Resubmitted ? FiscalDocumentBulkResubmitResponse.ResultItem.OutcomeValue.Resubmitted : FiscalDocumentBulkResubmitResponse.ResultItem.OutcomeValue.Skipped,
                    Status = FiscalOperationNames.Status<FiscalDocumentBulkResubmitResponse.ResultItem.StatusValue>(row.Status),
                });
            }
            else if (one.Error!.Code.ToString() == "CMP-ERR-TRANSPORT-UNAVAILABLE")
            {
                return one.Error; // no channel: nothing can be resubmitted
            }
            else
            {
                results.Add(new FiscalDocumentBulkResubmitResponse.ResultItem
                {
                    FiscalDocumentId = new FiscalDocumentId(id),
                    Outcome = FiscalDocumentBulkResubmitResponse.ResultItem.OutcomeValue.Skipped,
                    ErrorCode = one.Error.Code.ToString(),
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var resubmitted = results.Count(r => r.Outcome == FiscalDocumentBulkResubmitResponse.ResultItem.OutcomeValue.Resubmitted);
        return new FiscalDocumentBulkResubmitResponse { Requested = ids.Count, Resubmitted = resubmitted, Skipped = ids.Count - resubmitted, Results = results };
    }
}

internal sealed class CancelFiscalDocumentHandler(FiscalDocumentOperations operations, ComplianceDbContext db)
    : ICommandHandler<CancelFiscalDocument, FiscalDocumentCancelResponse>
{
    public async Task<Result<FiscalDocumentCancelResponse>> HandleAsync(CancelFiscalDocument command, CancellationToken cancellationToken)
    {
        var result = await operations.CancelAsync(command.Request.FiscalDocumentId.Value, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new FiscalDocumentCancelResponse
        {
            FiscalDocumentId = result.Value.Original.FiscalDocumentId,
            Status = FiscalOperationNames.Status<FiscalDocumentCancelResponse.StatusValue>(result.Value.Original.Status),
            CancellationFiscalDocumentId = result.Value.Cancellation?.FiscalDocumentId.Value,
        };
    }
}

internal sealed class CorrectFiscalDocumentHandler(FiscalDocumentOperations operations, ComplianceDbContext db)
    : ICommandHandler<CorrectFiscalDocument, FiscalDocumentCorrectResponse>
{
    public async Task<Result<FiscalDocumentCorrectResponse>> HandleAsync(CorrectFiscalDocument command, CancellationToken cancellationToken)
    {
        var result = await operations.CorrectAsync(command.Request.FiscalDocumentId.Value, command.Request.Corrections, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var (original, credit, corrected) = (result.Value.Original, result.Value.Credit, result.Value.Corrected);
        return new FiscalDocumentCorrectResponse
        {
            OriginalFiscalDocumentId = original.FiscalDocumentId,
            CreditFiscalDocumentId = credit.FiscalDocumentId.Value,
            CreditStatus = FiscalOperationNames.Status<FiscalDocumentCorrectResponse.CreditStatusValue>(credit.Status),
            CorrectedFiscalDocumentId = corrected.FiscalDocumentId.Value,
            CorrectedStatus = FiscalOperationNames.Status<FiscalDocumentCorrectResponse.CorrectedStatusValue>(corrected.Status),
        };
    }
}

/// <summary>Audit facts of a fiscal-document operation: the document acted on and the reason (BO-CMP-06).</summary>
internal static class FiscalOperationAudit
{
    public static CommandAuditFacts Facts(FiscalDocumentId id, string reason, object? outcome)
    {
        var keys = BusinessKeys.Empty.With("fiscalDocumentId", id.Value.ToString());
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CMP, "FiscalDocument", id),
            BusinessKeys = keys,
            Changes = AuditDiff.Compute(null, new { reason, outcome }),
        };
    }
}

internal sealed class ResubmitFiscalDocumentAuditor : ICommandAuditor<ResubmitFiscalDocument, FiscalDocumentResubmitResponse>
{
    public CommandAuditFacts Describe(ResubmitFiscalDocument command, Result<FiscalDocumentResubmitResponse>? result) =>
        FiscalOperationAudit.Facts(command.Request.FiscalDocumentId, command.Request.Reason,
            result is { IsSuccess: true } ok ? new { status = ok.Value.Status.ToString(), resubmitted = ok.Value.Resubmitted } : null);
}

internal sealed class BulkResubmitFiscalDocumentsAuditor : ICommandAuditor<BulkResubmitFiscalDocuments, FiscalDocumentBulkResubmitResponse>
{
    public CommandAuditFacts Describe(BulkResubmitFiscalDocuments command, Result<FiscalDocumentBulkResubmitResponse>? result) =>
        new()
        {
            BusinessKeys = BusinessKeys.Empty,
            Changes = AuditDiff.Compute(null, new
            {
                reason = command.Request.Reason,
                ids = command.Request.Ids.Select(i => i.ToString()).ToArray(),
                resubmitted = result is { IsSuccess: true } ok ? ok.Value.Resubmitted : (int?)null,
            }),
        };
}

internal sealed class CancelFiscalDocumentAuditor : ICommandAuditor<CancelFiscalDocument, FiscalDocumentCancelResponse>
{
    public CommandAuditFacts Describe(CancelFiscalDocument command, Result<FiscalDocumentCancelResponse>? result) =>
        FiscalOperationAudit.Facts(command.Request.FiscalDocumentId, command.Request.Reason,
            result is { IsSuccess: true } ok ? new { status = ok.Value.Status.ToString(), cancellation = ok.Value.CancellationFiscalDocumentId?.ToString() } : null);
}

internal sealed class CorrectFiscalDocumentAuditor : ICommandAuditor<CorrectFiscalDocument, FiscalDocumentCorrectResponse>
{
    public CommandAuditFacts Describe(CorrectFiscalDocument command, Result<FiscalDocumentCorrectResponse>? result) =>
        FiscalOperationAudit.Facts(command.Request.FiscalDocumentId, command.Request.Reason,
            result is { IsSuccess: true } ok
                ? new { credit = ok.Value.CreditFiscalDocumentId.ToString(), corrected = ok.Value.CorrectedFiscalDocumentId.ToString(), lines = command.Request.Corrections.Count }
                : null);
}
