using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Compliance.AiRegister;

/// <summary>AI register statuses (SL6-CMP-AIREG, D-SL6-08). Only APPROVED lets Platform enable a feature.</summary>
internal static class AiStatuses
{
    public const string Draft = "DRAFT";
    public const string InReview = "IN_REVIEW";
    public const string Approved = AiRegisterStatuses.Approved;
    public const string Suspended = "SUSPENDED";
    public const string Retired = "RETIRED";

    /// <summary>Old status carried by the event that creates an entry.</summary>
    public const string None = "NONE";
}

/// <summary>An entry of the AI system register (<c>cmp.ai_system</c>). Global, not per legal entity: the AI gate is global.</summary>
internal sealed class AiSystemRow
{
    public string FeatureId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? AiActClass { get; set; }

    public string Status { get; set; } = AiStatuses.Draft;

    public string? SubmittedBy { get; set; }

    public string? ApprovedBy { get; set; }

    public string? Reason { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>Read model of an entry; the controller maps it to the per-operation response types.</summary>
internal sealed record AiSystemView(AiFeatureId FeatureId, string Name, string? AiActClass, string Status, string? SubmittedBy, string? ApprovedBy, int RecordVersion)
{
    public static AiSystemView From(AiSystemRow row) =>
        new(AiFeatureId.Parse(row.FeatureId), row.Name, row.AiActClass, row.Status, row.SubmittedBy, row.ApprovedBy, row.RecordVersion);
}

/// <summary>Base of the register commands.</summary>
internal abstract record AiRegisterCommand(AiFeatureId FeatureId, string? Reason) : ICommand<AiSystemView>;

internal sealed record RegisterAiSystem(AiFeatureId FeatureId, string Name, string? AiActClass) : AiRegisterCommand(FeatureId, null);

internal sealed record SubmitAiSystem(AiFeatureId FeatureId, string? Reason) : AiRegisterCommand(FeatureId, Reason);

internal sealed record ApproveAiSystem(AiFeatureId FeatureId, string? Reason) : AiRegisterCommand(FeatureId, Reason);

internal sealed record SuspendAiSystem(AiFeatureId FeatureId, string? Reason) : AiRegisterCommand(FeatureId, Reason);

internal sealed record RetireAiSystem(AiFeatureId FeatureId, string? Reason) : AiRegisterCommand(FeatureId, Reason);

/// <summary>Development only: approves every pilot entry so E2E can enable the features (SL6-CMP-AIREG).</summary>
internal sealed record DevApproveAllAiSystems : ICommand<DevApproveAllResult>;

internal sealed record DevApproveAllResult(int Approved);

internal sealed class RegisterAiSystemValidator : AbstractValidator<RegisterAiSystem>
{
    public RegisterAiSystemValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.AiActClass).MaximumLength(64);
    }
}

internal sealed class AiReasonValidator<T> : AbstractValidator<T>
    where T : AiRegisterCommand
{
    public AiReasonValidator() => RuleFor(c => c.Reason).MaximumLength(512);
}

/// <summary>Shared mechanics: change status, stage <c>AiSystemStatusChanged</c>.</summary>
internal static class AiTransitions
{
    public static AiSystemView Apply(AiSystemRow row, string newStatus, string? reason, Instant now, IEventPublisher events)
    {
        var old = row.Status;
        row.Status = newStatus;
        row.Reason = reason;
        row.UpdatedAt = now;
        row.RecordVersion++;
        Publish(row, old, now, events);
        return AiSystemView.From(row);
    }

    public static void Publish(AiSystemRow row, string oldStatus, Instant now, IEventPublisher events) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(AiSystemStatusChangedV1.Descriptor), "AiFeature", row.FeatureId,
            new AiSystemStatusChangedV1
            {
                FeatureId = AiFeatureId.Parse(row.FeatureId),
                OldStatus = oldStatus,
                NewStatus = row.Status,
                AiActClass = row.AiActClass ?? "UNCLASSIFIED",
            },
            BusinessKeys.Empty.With("featureId", row.FeatureId)) { OccurredAt = now });

    public static DomainError NotFound(AiFeatureId id) =>
        DomainError.Of(ModuleCode.CMP, "NOT-FOUND", $"AI system {id} is not in the register.");

    public static DomainError BadMove(AiSystemRow row, string wanted) =>
        DomainError.Of(ModuleCode.CMP, "VALIDATION", $"AI system {row.FeatureId} is {row.Status}; it cannot move to {wanted}.");
}

internal sealed class RegisterAiSystemHandler(ComplianceDbContext db, IClock clock, IEventPublisher events)
    : ICommandHandler<RegisterAiSystem, AiSystemView>
{
    public async Task<Result<AiSystemView>> HandleAsync(RegisterAiSystem command, CancellationToken cancellationToken)
    {
        var id = command.FeatureId.Value;
        var existing = await db.AiSystems.SingleOrDefaultAsync(a => a.FeatureId == id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Name == command.Name
                ? AiSystemView.From(existing)
                : DomainError.Of(ModuleCode.CMP, "IDEMPOTENCY-MISMATCH", $"AI system {id} is already registered under another name.");
        }

        var now = clock.Now;
        var row = new AiSystemRow
        {
            FeatureId = id,
            Name = command.Name,
            AiActClass = command.AiActClass,
            Status = AiStatuses.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            RecordVersion = 1,
        };
        db.AiSystems.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AiTransitions.Publish(row, AiStatuses.None, now, events);
        return AiSystemView.From(row);
    }
}

/// <summary>Moves one entry from one of <see cref="From"/> to <see cref="To"/>.</summary>
internal abstract class AiMoveHandler<TCommand>(ComplianceDbContext db, RequestContext context, IClock clock, IEventPublisher events)
    : ICommandHandler<TCommand, AiSystemView>
    where TCommand : AiRegisterCommand
{
    protected abstract string[] From { get; }

    protected abstract string To { get; }

    /// <summary>Extra checks and field changes before the move.</summary>
    protected virtual DomainError? Before(AiSystemRow row, string actor) => null;

    public async Task<Result<AiSystemView>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var id = command.FeatureId.Value;
        var row = await db.AiSystems.SingleOrDefaultAsync(a => a.FeatureId == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AiTransitions.NotFound(command.FeatureId);
        }

        if (!From.Contains(row.Status))
        {
            return AiTransitions.BadMove(row, To);
        }

        var actor = context.Actor.Id;
        if (Before(row, actor) is { } refused)
        {
            return refused;
        }

        var view = AiTransitions.Apply(row, To, command.Reason, clock.Now, events);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return view;
    }
}

internal sealed class SubmitAiSystemHandler(ComplianceDbContext db, RequestContext context, IClock clock, IEventPublisher events)
    : AiMoveHandler<SubmitAiSystem>(db, context, clock, events)
{
    protected override string[] From => [AiStatuses.Draft, AiStatuses.Suspended];

    protected override string To => AiStatuses.InReview;

    protected override DomainError? Before(AiSystemRow row, string actor)
    {
        row.SubmittedBy = actor;
        row.ApprovedBy = null;
        return null;
    }
}

internal sealed class ApproveAiSystemHandler(ComplianceDbContext db, RequestContext context, IClock clock, IEventPublisher events)
    : AiMoveHandler<ApproveAiSystem>(db, context, clock, events)
{
    protected override string[] From => [AiStatuses.InReview];

    protected override string To => AiStatuses.Approved;

    protected override DomainError? Before(AiSystemRow row, string actor)
    {
        // Four eyes: the approver must differ from the submitter.
        if (string.Equals(row.SubmittedBy, actor, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.CMP, "APPROVAL-SAME-USER", "The approver must differ from the user who submitted the AI system for review.");
        }

        row.ApprovedBy = actor;
        return null;
    }
}

internal sealed class SuspendAiSystemHandler(ComplianceDbContext db, RequestContext context, IClock clock, IEventPublisher events)
    : AiMoveHandler<SuspendAiSystem>(db, context, clock, events)
{
    protected override string[] From => [AiStatuses.Approved];

    protected override string To => AiStatuses.Suspended;
}

internal sealed class RetireAiSystemHandler(ComplianceDbContext db, RequestContext context, IClock clock, IEventPublisher events)
    : AiMoveHandler<RetireAiSystem>(db, context, clock, events)
{
    protected override string[] From => [AiStatuses.Draft, AiStatuses.InReview, AiStatuses.Approved, AiStatuses.Suspended];

    protected override string To => AiStatuses.Retired;
}

/// <summary>Dev-only: approves all non-retired entries without the submit/approve ceremony. Refuses outside Development.</summary>
internal sealed class DevApproveAllAiSystemsHandler(ComplianceDbContext db, RequestContext context, IClock clock, IEventPublisher events, IHostEnvironment environment)
    : ICommandHandler<DevApproveAllAiSystems, DevApproveAllResult>
{
    public async Task<Result<DevApproveAllResult>> HandleAsync(DevApproveAllAiSystems command, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "Not found.");
        }

        var rows = await db.AiSystems.Where(a => a.Status != AiStatuses.Approved && a.Status != AiStatuses.Retired).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        foreach (var row in rows)
        {
            row.SubmittedBy ??= "dev-seed";
            row.ApprovedBy = context.Actor.Id;
            AiTransitions.Apply(row, AiStatuses.Approved, "Development seed", now, events);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DevApproveAllResult(rows.Count);
    }
}

internal sealed class AiRegisterAuditor<TCommand> : ICommandAuditor<TCommand, AiSystemView>
    where TCommand : AiRegisterCommand
{
    public CommandAuditFacts Describe(TCommand command, Result<AiSystemView>? result)
    {
        var keys = BusinessKeys.Empty.With("featureId", command.FeatureId.Value);
        return result is not { IsSuccess: true } success
            ? new CommandAuditFacts { BusinessKeys = keys }
            : new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.CMP, "AiSystem", command.FeatureId.Value),
                BusinessKeys = keys,
                Changes = AuditDiff.Compute(null, new { status = success.Value.Status, reason = command.Reason }),
            };
    }
}

internal sealed class DevApproveAllAuditor : ICommandAuditor<DevApproveAllAiSystems, DevApproveAllResult>
{
    public CommandAuditFacts Describe(DevApproveAllAiSystems command, Result<DevApproveAllResult>? result) =>
        new() { ObjectRef = new ObjectRef(ModuleCode.CMP, "AiSystem", "dev-seed"), BusinessKeys = BusinessKeys.Empty };
}

/// <summary>
/// The real <see cref="IAiRegisterLookup"/> (D-SL6-08): the register status of a feature, null when it is not registered.
/// Replaces Platform's deny-all default. A read failure propagates, so Platform treats the register as unreadable (fail closed).
/// </summary>
internal sealed class AiRegisterLookup(ComplianceDbContext db) : IAiRegisterLookup
{
    public async Task<string?> GetStatusAsync(string featureId, CancellationToken cancellationToken) =>
        await db.AiSystems.AsNoTracking().Where(a => a.FeatureId == featureId).Select(a => a.Status).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class AiRegisterReader(ComplianceDbContext db)
{
    public async Task<AiSystemView?> GetAsync(string featureId, CancellationToken cancellationToken)
    {
        var row = await db.AiSystems.AsNoTracking().SingleOrDefaultAsync(a => a.FeatureId == featureId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : AiSystemView.From(row);
    }

    public async Task<(IReadOnlyList<AiSystemView> Items, string? NextCursor)> ListAsync(string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 200);
        var query = db.AiSystems.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(cursor))
        {
            query = query.Where(a => a.FeatureId.CompareTo(cursor) > 0);
        }

        var rows = await query.OrderBy(a => a.FeatureId).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).Select(AiSystemView.From).ToList();
        return (page, rows.Count > limit ? page[^1].FeatureId.Value : null);
    }
}

internal static class AiRegisterRegistration
{
    public static IServiceCollection AddAiRegister(this IServiceCollection services)
    {
        // Replaces Platform's deny-all default (registered with TryAdd in AddPlatformModule).
        services.Replace(ServiceDescriptor.Scoped<IAiRegisterLookup, AiRegisterLookup>());
        services.AddScoped<AiRegisterReader>();

        services.AddScoped<IValidator<RegisterAiSystem>, RegisterAiSystemValidator>();
        Add<RegisterAiSystem, RegisterAiSystemHandler>(services, "cmp.AiSystem.register");
        Add<SubmitAiSystem, SubmitAiSystemHandler>(services, "cmp.AiSystem.submitForReview");
        Add<ApproveAiSystem, ApproveAiSystemHandler>(services, "cmp.AiSystem.approve");
        Add<SuspendAiSystem, SuspendAiSystemHandler>(services, "cmp.AiSystem.suspend");
        Add<RetireAiSystem, RetireAiSystemHandler>(services, "cmp.AiSystem.retire");

        services.AddCommandAuditor<DevApproveAllAiSystems, DevApproveAllResult, DevApproveAllAuditor>();
        services.AddCommand<DevApproveAllAiSystems, DevApproveAllResult, DevApproveAllAiSystemsHandler>(CommandDescriptor.For("cmp.AiSystem.devApproveAll"));
        return services;
    }

    private static void Add<TCommand, THandler>(IServiceCollection services, string name)
        where TCommand : AiRegisterCommand
        where THandler : class, ICommandHandler<TCommand, AiSystemView>
    {
        if (typeof(TCommand) != typeof(RegisterAiSystem))
        {
            services.AddScoped<IValidator<TCommand>, AiReasonValidator<TCommand>>();
        }

        services.AddCommandAuditor<TCommand, AiSystemView, AiRegisterAuditor<TCommand>>();
        services.AddCommand<TCommand, AiSystemView, THandler>(CommandDescriptor.For(name));
    }
}
