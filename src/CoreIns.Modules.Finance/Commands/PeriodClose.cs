using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Modules.Finance.Services;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance.Commands;

/// <summary>A command of the period close workflow (<c>fin.Close.*</c>); the reason is mandatory and audited except for a sign-off.</summary>
internal interface IPeriodCloseCommand : ICommand<ClosePeriodView>
{
    string Period { get; }

    string? Reason { get; }

    bool ReasonRequired { get; }

    Task<Result<ClosePeriodView>> ExecuteAsync(PeriodCloseService service, CancellationToken cancellationToken);
}

internal sealed record SignOffCloseTask(string Period, string Task, string? Reason) : IPeriodCloseCommand
{
    public bool ReasonRequired => false;

    public Task<Result<ClosePeriodView>> ExecuteAsync(PeriodCloseService service, CancellationToken cancellationToken) => service.SignOffAsync(Period, Task, Reason, breakAccepted: false, cancellationToken);
}

internal sealed record AcceptCloseBreak(string Period, string? Reason) : IPeriodCloseCommand
{
    public bool ReasonRequired => true;

    public Task<Result<ClosePeriodView>> ExecuteAsync(PeriodCloseService service, CancellationToken cancellationToken) =>
        service.SignOffAsync(Period, PeriodClose.Reconciliations, Reason, breakAccepted: true, cancellationToken);
}

internal sealed record SoftClosePeriod(string Period, string? Reason) : IPeriodCloseCommand
{
    public bool ReasonRequired => true;

    public Task<Result<ClosePeriodView>> ExecuteAsync(PeriodCloseService service, CancellationToken cancellationToken) => service.SoftCloseAsync(Period, cancellationToken);
}

internal sealed record LockPeriod(string Period, string? Reason) : IPeriodCloseCommand
{
    public bool ReasonRequired => true;

    public Task<Result<ClosePeriodView>> ExecuteAsync(PeriodCloseService service, CancellationToken cancellationToken) => service.LockAsync(Period, cancellationToken);
}

internal sealed record ReopenPeriod(string Period, string? Reason) : IPeriodCloseCommand
{
    public bool ReasonRequired => true;

    public Task<Result<ClosePeriodView>> ExecuteAsync(PeriodCloseService service, CancellationToken cancellationToken) => service.ReopenAsync(Period, Reason!, cancellationToken);
}

internal sealed class PeriodCloseValidator<TCommand> : AbstractValidator<TCommand>
    where TCommand : IPeriodCloseCommand
{
    public PeriodCloseValidator()
    {
        RuleFor(c => c.Period).Must(PeriodClose.IsPeriodCode).WithMessage("period must be yyyy-MM.");
        RuleFor(c => c.Reason).MaximumLength(1000);
        RuleFor(c => c.Reason).NotEmpty().When(c => c.ReasonRequired).WithMessage("A reason is required.");
    }
}

internal sealed class PeriodCloseAuditor<TCommand> : ICommandAuditor<TCommand, ClosePeriodView>
    where TCommand : IPeriodCloseCommand
{
    public CommandAuditFacts Describe(TCommand command, Result<ClosePeriodView>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.FIN, "FinancialPeriod", command.Period),
        BusinessKeys = BusinessKeys.Empty.With("period", command.Period),
    };
}

internal sealed class PeriodCloseHandler<TCommand>(PeriodCloseService service, RequestContext context) : ICommandHandler<TCommand, ClosePeriodView>
    where TCommand : IPeriodCloseCommand
{
    public Task<Result<ClosePeriodView>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        context.Reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();
        return command.ExecuteAsync(service, cancellationToken);
    }
}

/// <summary>Blocks the INTAKE_EXCEPTIONS task while the legal entity still has suspended business events.</summary>
internal sealed class IntakeExceptionsGate(FinanceDbContext db) : ICloseTaskGate
{
    public string Task => PeriodClose.IntakeExceptions;

    public async Task<string?> BlockerAsync(Guid legalEntity, string period, CancellationToken cancellationToken)
    {
        var open = await db.BusinessEvents.AsNoTracking().CountAsync(b => b.LegalEntityId == legalEntity && b.Status == BusinessEventStatus.Suspended, cancellationToken).ConfigureAwait(false);
        return open == 0 ? null : $"{open} suspended business event(s) are still in the intake exception queue.";
    }
}

/// <summary>
/// The period close state machine (OPEN, SOFT_CLOSED, LOCKED) and checklist. Soft close needs every task signed off; lock needs a second
/// person than the one who soft-closed; reopen is requested with a reason and approved by a second person. Every state change is audited by the
/// command pipeline, and lock / reopen publish PeriodClosed / PeriodReopened.
/// </summary>
internal sealed class PeriodCloseService(
    FinanceDbContext db, IEnumerable<ICloseTaskGate> gates, ReconciliationService reconciliation, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events)
{
    private static readonly DomainError NoEntity = DomainError.Of(ModuleCode.FIN, "VALIDATION", "The legal entity is not served by this stamp.");

    public async Task<Result<ClosePeriodView>> TasksAsync(string period, CancellationToken cancellationToken)
    {
        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var row = await FindAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        return await ViewAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ClosePeriodView>> SignOffAsync(string period, string task, string? reason, bool breakAccepted, CancellationToken cancellationToken)
    {
        if (!PeriodClose.IsTask(task))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "Unknown close task.");
        }

        if (!HasRole(PeriodClose.RoleOf(task)))
        {
            return DomainError.Of(ModuleCode.FIN, "APPROVAL-REQUIRED", $"Task {task} is signed off by {PeriodClose.RoleOf(task)}.");
        }

        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var row = await EnsureAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        if (row.Status != PeriodClose.Open)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Period {period} is {row.Status}; tasks are signed off while it is open.");
        }

        if (breakAccepted)
        {
            // BO-FIN-08: the accepted breaks are the ones the reconciliations found; they are committed together with the sign-off below.
            if (await reconciliation.AcceptOpenBreaksAsync(legalEntity.Value, period, reason ?? string.Empty, cancellationToken).ConfigureAwait(false) is { } rejected)
            {
                return rejected;
            }
        }
        else if (await BlockerAsync(legalEntity, period, task, cancellationToken).ConfigureAwait(false) is { } blocker)
        {
            return DomainError.Of(ModuleCode.FIN, "GATES-FAILED", blocker);
        }

        var sign = await db.PeriodCloseTasks.SingleOrDefaultAsync(t => t.PeriodId == row.PeriodId && t.TaskCode == task, cancellationToken).ConfigureAwait(false);
        if (sign is null)
        {
            sign = new PeriodCloseTaskRow { PeriodId = row.PeriodId, TaskCode = task };
            db.PeriodCloseTasks.Add(sign);
        }

        sign.Status = PeriodClose.TaskSignedOff;
        sign.SignedOffBy = context.Actor.Id;
        sign.SignedOffAt = clock.Now;
        sign.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        sign.BreakAccepted = breakAccepted;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ClosePeriodView>> SoftCloseAsync(string period, CancellationToken cancellationToken)
    {
        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var row = await EnsureAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        if (row.Status != PeriodClose.Open)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Period {period} is {row.Status}; only an open period can be soft-closed.");
        }

        if (await UnfinishedAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false) is { } unfinished)
        {
            return DomainError.Of(ModuleCode.FIN, "GATES-FAILED", unfinished);
        }

        row.Status = PeriodClose.SoftClosed;
        row.SoftClosedBy = context.Actor.Id;
        row.SoftClosedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ClosePeriodView>> LockAsync(string period, CancellationToken cancellationToken)
    {
        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var row = await EnsureAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        if (row.Status == PeriodClose.Locked)
        {
            return DomainError.Of(ModuleCode.FIN, "PERIOD-LOCKED", $"Period {period} is already locked.");
        }

        if (row.Status != PeriodClose.SoftClosed)
        {
            return DomainError.Of(ModuleCode.FIN, "GATES-FAILED", $"Period {period} must be soft-closed before it is locked.");
        }

        if (row.SoftClosedBy == context.Actor.Id)
        {
            return DomainError.Of(ModuleCode.FIN, "APPROVAL-REQUIRED", "A different person than the one who soft-closed the period must lock it.");
        }

        if (await UnfinishedAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false) is { } unfinished)
        {
            return DomainError.Of(ModuleCode.FIN, "GATES-FAILED", unfinished);
        }

        var now = clock.Now;
        row.Status = PeriodClose.Locked;
        row.LockedBy = context.Actor.Id;
        row.LockedAt = now;
        row.LockCount++;
        row.ReopenRequestedBy = null;
        row.ReopenRequestedAt = null;
        row.ReopenReason = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var books = await db.JournalLines.AsNoTracking()
            .Join(db.Journals.Where(j => j.PeriodId == row.PeriodId), l => l.JournalId, j => j.JournalId, (l, _) => l.Book)
            .Distinct().OrderBy(b => b).ToListAsync(cancellationToken).ConfigureAwait(false);
        Publish(
            new PeriodClosedV1
            {
                PeriodId = new FinancialPeriodId(row.PeriodId),
                Kind = "MONTH",
                Books = books,
                LockTime = now,
                KeyTotals = await TotalsAsync(row.PeriodId, cancellationToken).ConfigureAwait(false),
                Relock = row.LockCount > 1,
            },
            PeriodClosedV1.Descriptor, row.PeriodId);
        return await ViewAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ClosePeriodView>> ReopenAsync(string period, string reason, CancellationToken cancellationToken)
    {
        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var row = await EnsureAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        if (row.Status == PeriodClose.Open)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Period {period} is open; there is nothing to reopen.");
        }

        var actor = context.Actor.Id;
        if (row.ReopenRequestedBy is null)
        {
            // First person: records the request and its reason; the period stays as it is until a second person approves.
            row.ReopenRequestedBy = actor;
            row.ReopenRequestedAt = clock.Now;
            row.ReopenReason = reason.Trim();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return await ViewAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false);
        }

        if (row.ReopenRequestedBy == actor)
        {
            return DomainError.Of(ModuleCode.FIN, "APPROVAL-REQUIRED", "A different person than the one who requested the reopen must approve it.");
        }

        var wasLocked = row.Status == PeriodClose.Locked;
        var requestedReason = row.ReopenReason ?? reason.Trim();
        var requester = row.ReopenRequestedBy;
        var lockedBy = row.LockedBy;
        row.Status = PeriodClose.Open;
        row.SoftClosedBy = null;
        row.SoftClosedAt = null;
        row.ReopenRequestedBy = null;
        row.ReopenRequestedAt = null;
        row.ReopenReason = null;

        // The checklist starts again: every task is signed off again after the corrections.
        db.PeriodCloseTasks.RemoveRange(await db.PeriodCloseTasks.Where(t => t.PeriodId == row.PeriodId).ToListAsync(cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (wasLocked)
        {
            Publish(
                new PeriodReopenedV1
                {
                    PeriodId = new FinancialPeriodId(row.PeriodId),
                    Reason = requestedReason,
                    ApproverUserIds = [.. new[] { requester, actor }.Select(a => Guid.TryParse(a, out var g) && g != Guid.Empty ? new UserId(g) : (UserId?)null).Where(u => u is not null).Select(u => u!.Value)],
                    ImpactSummary = System.Text.Json.JsonSerializer.SerializeToElement(new { period, lockedBy }),
                },
                PeriodReopenedV1.Descriptor, row.PeriodId);
        }

        return await ViewAsync(legalEntity, period, row, cancellationToken).ConfigureAwait(false);
    }

    private void Publish<TPayload>(TPayload payload, EventContract contract, Guid periodId)
        where TPayload : class
    {
        var code = context.LegalEntity!.Value;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(contract), "LegalEntity", code.Value, payload,
            BusinessKeys.Empty.With("periodId", periodId.ToString("D")))
        {
            LegalEntity = code,
            Jurisdiction = context.Jurisdiction,
        });
    }

    private async Task<IReadOnlyDictionary<string, Money>> TotalsAsync(Guid periodId, CancellationToken cancellationToken)
    {
        var lines = await db.JournalLines.AsNoTracking()
            .Join(db.Journals.Where(j => j.PeriodId == periodId), l => l.JournalId, j => j.JournalId, (l, _) => new { l.Side, l.Currency, l.Amount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var totals = new Dictionary<string, Money>();
        foreach (var group in lines.GroupBy(l => (l.Side, l.Currency)))
        {
            totals[$"total{(group.Key.Side == Sides.Debit ? "Debit" : "Credit")}:{group.Key.Currency}"] = Money.Of(group.Sum(l => l.Amount), group.Key.Currency);
        }

        return totals;
    }

    private LegalEntityId? ResolveEntity()
    {
        if (context.LegalEntity is not { } code)
        {
            return null;
        }

        try
        {
            return legalEntities.Resolve(code);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private bool HasRole(string role) => context.Roles.Contains(role) || context.Roles.Contains("Platform.Admin");

    private Task<FinancialPeriodRow?> FindAsync(LegalEntityId legalEntity, string period, CancellationToken cancellationToken) =>
        db.Periods.SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity.Value && p.PeriodCode == period, cancellationToken);

    private async Task<FinancialPeriodRow> EnsureAsync(LegalEntityId legalEntity, string period, CancellationToken cancellationToken)
    {
        var row = await FindAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new FinancialPeriodRow { PeriodId = Guid.CreateVersion7(), LegalEntityId = legalEntity.Value, PeriodCode = period, Status = PeriodClose.Open, CreatedAt = clock.Now };
            db.Periods.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return row;
    }

    private async Task<string?> BlockerAsync(LegalEntityId legalEntity, string period, string task, CancellationToken cancellationToken)
    {
        foreach (var gate in gates.Where(g => g.Task == task))
        {
            if (await gate.BlockerAsync(legalEntity.Value, period, cancellationToken).ConfigureAwait(false) is { } blocker)
            {
                return blocker;
            }
        }

        return null;
    }

    /// <summary>Why the period cannot move on: the first task that is not signed off or that a gate blocks; null when all is done.</summary>
    private async Task<string?> UnfinishedAsync(LegalEntityId legalEntity, string period, FinancialPeriodRow row, CancellationToken cancellationToken)
    {
        var signed = await db.PeriodCloseTasks.AsNoTracking().Where(t => t.PeriodId == row.PeriodId && t.Status == PeriodClose.TaskSignedOff).Select(t => t.TaskCode).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (task, _) in PeriodClose.Tasks)
        {
            if (!signed.Contains(task))
            {
                return $"Task {task} is not signed off.";
            }

            // A reconciliation break accepted with a reason stands in for a clean reconciliation, so that gate is not re-run for it.
            if (task != PeriodClose.Reconciliations && await BlockerAsync(legalEntity, period, task, cancellationToken).ConfigureAwait(false) is { } blocker)
            {
                return $"Task {task}: {blocker}";
            }
        }

        return null;
    }

    private async Task<ClosePeriodView> ViewAsync(LegalEntityId legalEntity, string period, FinancialPeriodRow? row, CancellationToken cancellationToken)
    {
        var signs = row is null
            ? []
            : await db.PeriodCloseTasks.AsNoTracking().Where(t => t.PeriodId == row.PeriodId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tasks = new List<CloseTaskView>();
        foreach (var (task, role) in PeriodClose.Tasks)
        {
            var sign = signs.FirstOrDefault(s => s.TaskCode == task);
            var done = sign is { Status: PeriodClose.TaskSignedOff };
            tasks.Add(new CloseTaskView
            {
                Task = task switch
                {
                    PeriodClose.IntakeExceptions => CloseTaskCode.IntakeExceptions,
                    PeriodClose.Reconciliations => CloseTaskCode.Reconciliations,
                    PeriodClose.EarningRun => CloseTaskCode.EarningRun,
                    _ => CloseTaskCode.ManualJournals,
                },
                Status = done ? CloseTaskView.StatusValue.SignedOff : CloseTaskView.StatusValue.Open,
                SignOffRole = role,
                Blocker = done ? null : await BlockerAsync(legalEntity, period, task, cancellationToken).ConfigureAwait(false),
                SignedOffBy = sign?.SignedOffBy,
                SignedOffAt = sign?.SignedOffAt,
                Reason = sign?.Reason,
            });
        }

        var state = row?.Status ?? PeriodClose.Open;
        return new ClosePeriodView
        {
            Period = period,
            State = state switch { PeriodClose.Locked => ClosePeriodView.StateValue.Locked, PeriodClose.SoftClosed => ClosePeriodView.StateValue.SoftClosed, _ => ClosePeriodView.StateValue.Open },
            ClosedForPosting = state == PeriodClose.Locked,
            Tasks = tasks,
            SoftClosedBy = row?.SoftClosedBy,
            LockedBy = row?.LockedBy,
            ReopenRequestedBy = row?.ReopenRequestedBy,
            ReopenRequestedAt = row?.ReopenRequestedAt,
            ReopenReason = row?.ReopenReason,
        };
    }
}

/// <summary>Registration of the period close commands, gates and service (called once from <c>FinanceModule</c>).</summary>
internal static class PeriodCloseRegistration
{
    public static IServiceCollection AddPeriodClose(this IServiceCollection services)
    {
        services.AddScoped<PeriodCloseService>();
        services.AddScoped<ICloseTaskGate, IntakeExceptionsGate>();
        Register<SignOffCloseTask>(services, "fin.Close.signOff");
        Register<AcceptCloseBreak>(services, "fin.Close.acceptBreak");
        Register<SoftClosePeriod>(services, "fin.Close.softClose");
        Register<LockPeriod>(services, "fin.Close.lock");
        Register<ReopenPeriod>(services, "fin.Close.reopen");
        return services;
    }

    private static void Register<TCommand>(IServiceCollection services, string operation)
        where TCommand : class, IPeriodCloseCommand
    {
        services.AddScoped<IValidator<TCommand>, PeriodCloseValidator<TCommand>>();
        services.AddCommandAuditor<TCommand, ClosePeriodView, PeriodCloseAuditor<TCommand>>();
        services.AddCommand<TCommand, ClosePeriodView, PeriodCloseHandler<TCommand>>(CommandDescriptor.For(operation));
    }
}
