using CoreIns.Modules.Migration.Services.Cutover;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Cutover.plan: defines a rehearsal or cut-over runbook (BO-MIG-12).</summary>
internal sealed record PlanCutover(CutoverPlanBody Body) : ICommand<CutoverPlanView>;

/// <summary>mig.Cutover.startStep: starts the next step and stores the start time.</summary>
internal sealed record StartCutoverStep(CutoverStepBody Body) : ICommand<CutoverPlanView>;

/// <summary>mig.Cutover.completeStep: completes a running step; the go/no-go step reads reconciliation and sign-off.</summary>
internal sealed record CompleteCutoverStep(CutoverStepBody Body) : ICommand<CutoverPlanView>;

/// <summary>mig.Cutover.rollback: reverses the batches of the plan, last first, and restores routing.</summary>
internal sealed record RollbackCutover(CutoverRollbackBody Body) : ICommand<CutoverPlanView>;

internal sealed class CutoverCommandHandler(CutoverService service)
    : ICommandHandler<PlanCutover, CutoverPlanView>, ICommandHandler<StartCutoverStep, CutoverPlanView>,
      ICommandHandler<CompleteCutoverStep, CutoverPlanView>, ICommandHandler<RollbackCutover, CutoverPlanView>
{
    public Task<Result<CutoverPlanView>> HandleAsync(PlanCutover command, CancellationToken cancellationToken) => service.PlanAsync(command.Body, cancellationToken);

    public Task<Result<CutoverPlanView>> HandleAsync(StartCutoverStep command, CancellationToken cancellationToken) => service.StartStepAsync(command.Body, cancellationToken);

    public Task<Result<CutoverPlanView>> HandleAsync(CompleteCutoverStep command, CancellationToken cancellationToken) => service.CompleteStepAsync(command.Body, cancellationToken);

    public Task<Result<CutoverPlanView>> HandleAsync(RollbackCutover command, CancellationToken cancellationToken) => service.RollbackAsync(command.Body, cancellationToken);
}
