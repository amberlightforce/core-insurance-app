using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services.SignOff;
using CoreIns.Modules.Migration.Services.Waves;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Cutover;

internal sealed record CutoverPlanBody(string? Name, string? Kind, string? Scope, IReadOnlyList<Guid>? BatchIds);

internal sealed record CutoverStepBody(Guid? PlanId, int? Seq, string? Note);

internal sealed record CutoverRollbackBody(Guid? PlanId, bool DryRun);

internal sealed record CutoverStepView(
    int Seq, string Kind, Guid? BatchId, string Status, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, long? DurationMs, string? Result, string? Note);

internal sealed record CutoverRollbackOutcome(Guid BatchId, string Status, int Reversed, int Blocked, int RoutingRestored);

internal sealed record CutoverPlanView(
    Guid PlanId, string Name, string Kind, string Scope, string Status, IReadOnlyList<CutoverStepView> Steps, long TotalDurationMs, DateTimeOffset? RollbackPreviewedAt,
    DateTimeOffset? RolledBackAt, IReadOnlyList<CutoverRollbackOutcome> Rollback, string CreatedBy);

/// <summary>
/// Rehearsal, cut-over and rollback plan (BO-MIG-12). A plan is an ordered runbook: a go/no-go step read from the latest reconciliation and the sign-off
/// status of its scope, then one step per batch. Steps run strictly in order and store their start, completion and duration. A rehearsal is the same plan
/// run against synthetic data (D-ARC-09); it records a NO_GO instead of refusing it. Rollback reverses the batches that ran, last first, through the batch
/// reverse operation (module reversers) and sets the CORE routing of the wave terms they converted back to the legacy master.
/// </summary>
internal sealed class CutoverService(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, SignOffService signOff, RoutingPeriods routing, IServiceProvider services)
{
    public const int MaxBatches = 50;

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, detail);

    public async Task<Result<CutoverPlanView>> PlanAsync(CutoverPlanBody body, CancellationToken cancellationToken)
    {
        var name = body.Name?.Trim() ?? string.Empty;
        var kind = body.Kind?.Trim().ToUpperInvariant() ?? string.Empty;
        var scope = body.Scope?.Trim() ?? string.Empty;
        var batchIds = body.BatchIds ?? [];
        if (name.Length is 0 or > 200 || scope.Length is 0 or > 64 || kind is not (CutoverKind.Rehearsal or CutoverKind.Cutover)
            || batchIds.Count is 0 or > MaxBatches || batchIds.Distinct().Count() != batchIds.Count)
        {
            return Invalid($"Give a plan name, kind REHEARSAL or CUTOVER, a scope of at most 64 characters and 1 to {MaxBatches} distinct batch ids.");
        }

        var legalEntity = LegalEntity;
        var existing = await db.CutoverPlans.FirstOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.Name == name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var steps = await LoadStepsAsync(existing.PlanId, cancellationToken).ConfigureAwait(false);
            var sameBatches = steps.Where(s => s.BatchId is not null).Select(s => s.BatchId!.Value).SequenceEqual(batchIds);
            if (existing.Kind != kind || existing.Scope != scope || !sameBatches)
            {
                return MigrationErrors.State($"Cut-over plan {name} already exists with a different definition; plan under a new name.");
            }

            return ToView(existing, steps, []);
        }

        var known = await db.Batches.AsNoTracking().Where(b => b.LegalEntityId == legalEntity && batchIds.Contains(b.BatchId)).Select(b => b.BatchId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (known.Count != batchIds.Count)
        {
            return MigrationErrors.NotFound("A batch of the plan");
        }

        var now = clock.Now;
        var plan = new CutoverPlanRow
        {
            PlanId = Guid.CreateVersion7(), LegalEntityId = legalEntity, Name = name, Kind = kind, Scope = scope, Status = CutoverStatus.Planned,
            CreatedBy = context.Actor.ToString(), CreatedAt = now, RecordVersion = 1,
        };
        db.CutoverPlans.Add(plan);
        var rows = new List<CutoverStepRow> { new() { StepId = Guid.CreateVersion7(), PlanId = plan.PlanId, Seq = 1, Kind = StepKind.GoNoGo } };
        rows.AddRange(batchIds.Select((id, i) => new CutoverStepRow { StepId = Guid.CreateVersion7(), PlanId = plan.PlanId, Seq = i + 2, Kind = StepKind.BatchRun, BatchId = id }));
        db.CutoverSteps.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(plan, rows, []);
    }

    public async Task<Result<CutoverPlanView>> StartStepAsync(CutoverStepBody body, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(body.PlanId, body.Seq, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var (plan, steps, step) = loaded.Value;
        if (plan.Status is CutoverStatus.RolledBack or CutoverStatus.RollbackBlocked or CutoverStatus.Completed)
        {
            return MigrationErrors.State($"Cut-over plan {plan.Name} is {plan.Status}; no step can start.");
        }

        if (step!.Status != StepStatus.Pending)
        {
            return MigrationErrors.State($"Step {step.Seq} is {step.Status}.");
        }

        if (steps.Any(s => s.Seq < step.Seq && s.Status != StepStatus.Done) || steps.Any(s => s.Status == StepStatus.InProgress))
        {
            return MigrationErrors.State($"Step {step.Seq} cannot start before the steps in front of it are done, and not while another step runs.");
        }

        step.Status = StepStatus.InProgress;
        step.StartedAt = clock.Now;
        plan.Status = CutoverStatus.InProgress;
        plan.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(plan, steps, []);
    }

    public async Task<Result<CutoverPlanView>> CompleteStepAsync(CutoverStepBody body, CancellationToken cancellationToken)
    {
        if (body.Note?.Length > 1000)
        {
            return Invalid("note is at most 1000 characters.");
        }

        var loaded = await LoadAsync(body.PlanId, body.Seq, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var (plan, steps, step) = loaded.Value;
        if (step!.Status != StepStatus.InProgress)
        {
            return MigrationErrors.State($"Step {step.Seq} is {step.Status}; start it first.");
        }

        var note = body.Note;
        if (step.Kind == StepKind.GoNoGo)
        {
            // The go/no-go checklist is the sign-off status of the scope: a reconciliation run with every variance matched or waived and every signatory approved.
            var status = await signOff.StatusAsync(plan.Scope, cancellationToken).ConfigureAwait(false);
            if (!status.IsSuccess)
            {
                return status.Error;
            }

            var blockers = status.Value.Blockers;
            if (blockers.Count > 0 && plan.Kind == CutoverKind.Cutover)
            {
                return MigrationErrors.State($"No-go for cut-over {plan.Name}: {string.Join(", ", blockers)}.");
            }

            step.Result = blockers.Count == 0 ? "GO" : "NO_GO";
            note = blockers.Count == 0 ? note : Truncate($"{string.Join(", ", blockers)}{(string.IsNullOrWhiteSpace(note) ? string.Empty : " | " + note)}");
        }
        else
        {
            var batch = await Batches.LoadAsync(db, plan.LegalEntityId, step.BatchId!.Value, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                return MigrationErrors.NotFound("The batch of the step");
            }

            if (batch.Status != BatchStatus.Completed)
            {
                return MigrationErrors.State($"Batch {batch.Name} is {batch.Status}; the step completes when the batch is COMPLETED.");
            }
        }

        var now = clock.Now;
        step.Status = StepStatus.Done;
        step.CompletedAt = now;
        step.DurationMs = Math.Max(0L, (now.ToDateTimeOffset() - step.StartedAt!.Value.ToDateTimeOffset()).Ticks / TimeSpan.TicksPerMillisecond);
        step.Note = note;
        if (steps.All(s => s.Status == StepStatus.Done))
        {
            plan.Status = CutoverStatus.Completed;
        }

        plan.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(plan, steps, []);
    }

    public async Task<Result<CutoverPlanView>> RollbackAsync(CutoverRollbackBody body, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(body.PlanId, null, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var (plan, steps, _) = loaded.Value;
        if (plan.Status is CutoverStatus.RolledBack)
        {
            return MigrationErrors.State($"Cut-over plan {plan.Name} is already rolled back.");
        }

        var started = steps.Where(s => s.Kind == StepKind.BatchRun && s.Status is StepStatus.Done or StepStatus.InProgress).OrderByDescending(s => s.Seq).ToList();
        if (started.Count == 0)
        {
            return MigrationErrors.State($"Cut-over plan {plan.Name} has not run a batch; there is nothing to roll back.");
        }

        if (!body.DryRun && plan.RollbackPreviewedAt is null)
        {
            return DomainError.Of(ModuleCode.MIG, "DRYRUN-REQUIRED", "Preview the rollback with dryRun first.");
        }

        var reverse = services.GetService(typeof(ReverseBatchHandler)) as ReverseBatchHandler ?? throw new InvalidOperationException("The batch reverse handler is not registered.");
        var outcomes = new List<CutoverRollbackOutcome>();
        var blocked = false;
        var now = clock.Now;
        foreach (var step in started)
        {
            var result = await reverse.HandleAsync(new ReverseBatch(new ReverseBatchRequest(step.BatchId!.Value, body.DryRun)), cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return result.Error;
            }

            var response = result.Value;
            var reversed = response.Outcomes.Count(o => o.Outcome == ItemOutcome.Reversed);
            var stuck = response.Outcomes.Count - reversed;
            var fully = response.Batch.Status == BatchStatus.Reversed;
            var restored = 0;
            if (fully && !body.DryRun)
            {
                restored = await RestoreRoutingAsync(plan, step.BatchId.Value, cancellationToken).ConfigureAwait(false);
                step.Status = StepStatus.RolledBack;
            }
            else if (fully)
            {
                restored = await db.WaveTerms.AsNoTracking().CountAsync(t => t.BatchId == step.BatchId && t.State == TermState.Converted, cancellationToken).ConfigureAwait(false);
            }

            outcomes.Add(new CutoverRollbackOutcome(step.BatchId.Value, response.Batch.Status, reversed, stuck, restored));
            if (!fully)
            {
                blocked = true;
                break; // keep the batches in front of a blocked one: reversing them past the blocker would leave a mixed state
            }
        }

        if (body.DryRun)
        {
            plan.RollbackPreviewedAt = now;
        }
        else
        {
            plan.Status = blocked ? CutoverStatus.RollbackBlocked : CutoverStatus.RolledBack;
            plan.RolledBackAt = blocked ? null : now;
        }

        plan.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(plan, steps, outcomes);
    }

    /// <summary>Wave terms the batch converted go back to the legacy master and become NOT_CONVERTED (free for a later wave).</summary>
    private async Task<int> RestoreRoutingAsync(CutoverPlanRow plan, Guid batchId, CancellationToken cancellationToken)
    {
        var terms = await db.WaveTerms.Where(t => t.BatchId == batchId && t.State == TermState.Converted).ToListAsync(cancellationToken).ConfigureAwait(false);
        var restored = 0;
        var now = clock.Now;
        foreach (var term in terms)
        {
            var from = new BusinessDate(term.TermEnd);
            var route = await db.Routes.FirstOrDefaultAsync(
                r => r.LegalEntityId == term.LegalEntityId && r.ObjectType == term.ObjectType && r.Key == term.LegacyKey && r.MasterState == RouteState.Core && r.ValidFrom == from,
                cancellationToken).ConfigureAwait(false);
            if (route is not null)
            {
                routing.StageRestore(route, $"Rolled back by cut-over {plan.Name}");
                restored++;
            }

            term.State = TermState.NotConverted;
            term.CoreId = null;
            term.Reason = Truncate($"Rolled back by cut-over {plan.Name}");
            term.UpdatedAt = now;
        }

        return restored;
    }

    private async Task<Result<(CutoverPlanRow Plan, List<CutoverStepRow> Steps, CutoverStepRow? Step)>> LoadAsync(Guid? planId, int? seq, CancellationToken cancellationToken)
    {
        if (planId is not { } id || (seq is { } s && s < 1))
        {
            return Invalid("Give a plan id and a step sequence.");
        }

        var legalEntity = LegalEntity;
        var plan = await db.CutoverPlans.FirstOrDefaultAsync(p => p.PlanId == id && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return MigrationErrors.NotFound("The cut-over plan");
        }

        var steps = await db.CutoverSteps.Where(x => x.PlanId == id).OrderBy(x => x.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var step = seq is null ? null : steps.FirstOrDefault(x => x.Seq == seq);
        if (seq is not null && step is null)
        {
            return MigrationErrors.NotFound("The step");
        }

        return (plan, steps, step);
    }

    private Task<List<CutoverStepRow>> LoadStepsAsync(Guid planId, CancellationToken cancellationToken) =>
        db.CutoverSteps.AsNoTracking().Where(s => s.PlanId == planId).OrderBy(s => s.Seq).ToListAsync(cancellationToken);

    private static string Truncate(string text) => text.Length <= 1000 ? text : text[..1000];

    private static CutoverPlanView ToView(CutoverPlanRow plan, IReadOnlyList<CutoverStepRow> steps, IReadOnlyList<CutoverRollbackOutcome> rollback) =>
        new(plan.PlanId, plan.Name, plan.Kind, plan.Scope, plan.Status,
            [.. steps.OrderBy(s => s.Seq).Select(s => new CutoverStepView(
                s.Seq, s.Kind, s.BatchId, s.Status, s.StartedAt?.ToDateTimeOffset(), s.CompletedAt?.ToDateTimeOffset(), s.DurationMs, s.Result, s.Note))],
            steps.Sum(s => s.DurationMs ?? 0), plan.RollbackPreviewedAt?.ToDateTimeOffset(), plan.RolledBackAt?.ToDateTimeOffset(), rollback, plan.CreatedBy);
}
