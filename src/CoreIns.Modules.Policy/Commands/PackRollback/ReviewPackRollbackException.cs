using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Commands.PackRollback;

/// <summary><c>pol.PackRollbackException.review</c>: records NO_ACTION or CORRECTION_REQUIRED with a reason (REQ-POL-354 subset, D-SL5-10).</summary>
internal sealed record ReviewPackRollbackException(PackRollbackExceptionReviewRequest Request) : ICommand<PackRollbackExceptionView>;

internal sealed class ReviewPackRollbackExceptionValidator : AbstractValidator<ReviewPackRollbackException>
{
    public ReviewPackRollbackExceptionValidator()
    {
        RuleFor(c => c.Request.ExceptionId).NotEmpty().WithErrorCode("EXCEPTION_REQUIRED");
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1000).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>
/// Closes an OPEN exception. A second review is POL-ERR-ILLEGAL-TRANSITION. Only the exception row changes: no segment, charge or
/// term is touched and nothing is re-rated; a correction goes through a reviewed out-of-sequence change (out of slice 5, D-SL3-02).
/// </summary>
internal sealed class ReviewPackRollbackExceptionHandler(
    PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, PackRollbackReader reader)
    : ICommandHandler<ReviewPackRollbackException, PackRollbackExceptionView>
{
    public async Task<Result<PackRollbackExceptionView>> HandleAsync(ReviewPackRollbackException command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var row = await db.PackRollbackExceptions.SingleOrDefaultAsync(e => e.ExceptionId == request.ExceptionId && e.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return JobSupport.NotFound("pack rollback exception");
        }

        if (row.Status != "OPEN")
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The exception has already been reviewed.");
        }

        row.Status = "REVIEWED";
        row.ReviewOutcome = request.Outcome == PackRollbackExceptionReviewRequest.OutcomeValue.NoAction ? "NO_ACTION" : "CORRECTION_REQUIRED";
        row.ReviewReason = request.Reason.Trim();
        row.ReviewedBy = context.Actor.ToString();
        row.ReviewedAt = clock.Now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The exception has already been reviewed.");
        }

        return await reader.ToViewAsync(row, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Audit facts of the review: the exception and its id as business key.</summary>
internal sealed class ReviewPackRollbackExceptionAuditor : ICommandAuditor<ReviewPackRollbackException, PackRollbackExceptionView>
{
    public CommandAuditFacts Describe(ReviewPackRollbackException command, Result<PackRollbackExceptionView>? result) =>
        new()
        {
            ObjectRef = new ObjectRef(ModuleCode.POL, "PackRollbackException", command.Request.ExceptionId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("exceptionId", command.Request.ExceptionId.ToString()),
        };
}
