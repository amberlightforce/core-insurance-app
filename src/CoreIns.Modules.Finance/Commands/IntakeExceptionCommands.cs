using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Outcome = CoreIns.Modules.Finance.Contracts.Api.IntakeExceptionOutcome.OutcomeValue;

namespace CoreIns.Modules.Finance.Commands;

/// <summary>The three actions on the intake exception queue (<c>fin.IntakeException.rerun / markDuplicate / reject</c>, REQ-FIN-080).</summary>
internal enum IntakeExceptionAction
{
    Rerun,
    MarkDuplicate,
    Reject,
}

/// <summary>An action on a set of suspended business events; the reason is mandatory (and audited) for markDuplicate and reject.</summary>
internal interface IIntakeExceptionCommand
{
    IntakeExceptionAction Action { get; }

    IReadOnlyList<Guid> ItemIds { get; }

    string? Reason { get; }
}

internal sealed record RerunIntakeExceptions(IReadOnlyList<Guid> ItemIds, string? Reason) : ICommand<IntakeExceptionRerunResponse>, IIntakeExceptionCommand
{
    public IntakeExceptionAction Action => IntakeExceptionAction.Rerun;
}

internal sealed record MarkDuplicateIntakeExceptions(IReadOnlyList<Guid> ItemIds, string? Reason) : ICommand<IntakeExceptionRerunResponse>, IIntakeExceptionCommand
{
    public IntakeExceptionAction Action => IntakeExceptionAction.MarkDuplicate;
}

internal sealed record RejectIntakeExceptions(IReadOnlyList<Guid> ItemIds, string? Reason) : ICommand<IntakeExceptionRerunResponse>, IIntakeExceptionCommand
{
    public IntakeExceptionAction Action => IntakeExceptionAction.Reject;
}

internal sealed class IntakeExceptionValidator<TCommand> : AbstractValidator<TCommand>
    where TCommand : IIntakeExceptionCommand
{
    public IntakeExceptionValidator()
    {
        RuleFor(c => c.ItemIds).NotEmpty().Must(ids => ids.Count <= 200).WithMessage("At most 200 items per request.");
        RuleFor(c => c.Reason).MaximumLength(1000);
        RuleFor(c => c.Reason).NotEmpty().When(c => c.Action != IntakeExceptionAction.Rerun).WithMessage("A reason is required.");
    }
}

internal sealed class IntakeExceptionAuditor<TCommand> : ICommandAuditor<TCommand, IntakeExceptionRerunResponse>
    where TCommand : IIntakeExceptionCommand
{
    public CommandAuditFacts Describe(TCommand command, Result<IntakeExceptionRerunResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.FIN, "BusinessEvent", command.ItemIds.Count == 1 ? command.ItemIds[0].ToString("D") : "queue"),
        BusinessKeys = command.ItemIds.Count == 1 ? BusinessKeys.Empty.With("businessEventId", command.ItemIds[0].ToString("D")) : BusinessKeys.Empty,
    };
}

/// <summary>
/// Runs an intake exception action per event, in the caller's legal entity (REQ-FIN-080). rerun re-dispatches the stored event once; an event that
/// is no longer suspended (posted by an earlier run, closed as duplicate or rejected) is a no-op, so a repeated rerun never posts twice.
/// markDuplicate and reject close the event with the reason, actor and time; nothing is posted for it.
/// </summary>
internal sealed class IntakeExceptionHandler<TCommand>(FinanceDbContext db, Intake intake, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<TCommand, IntakeExceptionRerunResponse>
    where TCommand : IIntakeExceptionCommand, ICommand<IntakeExceptionRerunResponse>
{
    public async Task<Result<IntakeExceptionRerunResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();
        context.Reason = reason;
        LegalEntityId? legalEntity = null;
        if (context.LegalEntity is { } code)
        {
            try
            {
                legalEntity = legalEntities.Resolve(code);
            }
            catch (InvalidOperationException)
            {
                // a legal entity this stamp does not serve owns no events: every item is NOT_FOUND
            }
        }

        var outcomes = new List<IntakeExceptionOutcome>();
        foreach (var id in command.ItemIds.Distinct())
        {
            var row = legalEntity is { } le
                ? await db.BusinessEvents.SingleOrDefaultAsync(b => b.BusinessEventId == id && b.LegalEntityId == le.Value, cancellationToken).ConfigureAwait(false)
                : null;
            if (row is null)
            {
                outcomes.Add(new IntakeExceptionOutcome { EventId = id, Outcome = Outcome.NotFound });
                continue;
            }

            if (row.Status != BusinessEventStatus.Suspended)
            {
                outcomes.Add(new IntakeExceptionOutcome { EventId = id, Outcome = Outcome.NoOp, Detail = row.Status });
                continue;
            }

            switch (command.Action)
            {
                case IntakeExceptionAction.Rerun:
                    var after = await intake.RerunAsync(id, reason, cancellationToken).ConfigureAwait(false);
                    outcomes.Add(new IntakeExceptionOutcome
                    {
                        EventId = id,
                        Outcome = after.Status switch
                        {
                            BusinessEventStatus.Suspended => Outcome.StillSuspended,
                            BusinessEventStatus.Waiting => Outcome.Waiting,
                            _ => Outcome.Posted,
                        },
                        Detail = after.Status == BusinessEventStatus.Suspended ? $"{after.ExceptionReason}: {after.ExceptionDetail}" : null,
                    });
                    break;
                default:
                    var duplicate = command.Action == IntakeExceptionAction.MarkDuplicate;
                    row.Status = duplicate ? BusinessEventStatus.Duplicate : BusinessEventStatus.Rejected;
                    row.ResolutionReason = reason;
                    row.ResolvedBy = context.Actor.Id;
                    row.ResolvedAt = clock.Now;
                    row.ExceptionReason = null;
                    row.ExceptionDetail = null;
                    row.UpdatedAt = clock.Now;
                    row.RecordVersion++;
                    outcomes.Add(new IntakeExceptionOutcome { EventId = id, Outcome = duplicate ? Outcome.MarkedDuplicate : Outcome.Rejected });
                    break;
            }

            // Per item: a later rerun that fails clears the change tracker, so this item's change must already be written.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new IntakeExceptionRerunResponse { Outcomes = outcomes };
    }
}
