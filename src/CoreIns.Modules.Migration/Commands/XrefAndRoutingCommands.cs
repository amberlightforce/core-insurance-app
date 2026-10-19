using CoreIns.Modules.Migration.Contracts.Events;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Xref.register: records a legacy key to core id link. Natural key: legal entity + source + object type + legacy key.</summary>
internal sealed record RegisterXref(XrefRegisterBody Body) : ICommand<XrefView>;

internal sealed class RegisterXrefValidator : AbstractValidator<RegisterXref>
{
    public RegisterXrefValidator()
    {
        RuleFor(c => c.Body.Source).NotEmpty().MaximumLength(8);
        RuleFor(c => c.Body.ObjectType).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Body.LegacyKey).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Body.CoreId).NotEmpty();
    }
}

internal sealed class RegisterXrefHandler(MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<RegisterXref, XrefView>
{
    public async Task<Result<XrefView>> HandleAsync(RegisterXref command, CancellationToken cancellationToken)
    {
        var body = command.Body;
        var source = body.Source.ToUpperInvariant();
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var existing = await db.Xrefs.SingleOrDefaultAsync(
            x => x.LegalEntityId == legalEntity && x.Source == source && x.ObjectType == body.ObjectType && x.LegacyKey == body.LegacyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is { Status: "ACTIVE" })
        {
            return existing.CoreId == body.CoreId
                ? MigrationReader.ToView(existing)
                : DomainError.Of(ModuleCode.MIG, "XREF-CONFLICT", $"{source}/{body.ObjectType}/{body.LegacyKey} is already linked to another core object.");
        }

        if (existing is not null)
        {
            existing.CoreId = body.CoreId;
            existing.BatchId = body.BatchId;
            existing.Status = "ACTIVE";
        }
        else
        {
            existing = new XrefRow
            {
                XrefId = Guid.CreateVersion7(), LegalEntityId = legalEntity, Source = source, ObjectType = body.ObjectType, LegacyKey = body.LegacyKey,
                CoreId = body.CoreId, BatchId = body.BatchId, CreatedAt = clock.Now,
            };
            db.Xrefs.Add(existing);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return MigrationReader.ToView(existing);
    }
}

/// <summary>
/// mig.Routing.override: sets the master of one object (policy) from a date. At most one master per object and date: a new
/// period may only start inside the open-ended last period (which then ends where the new one starts); any other overlap is refused.
/// </summary>
internal sealed record OverrideRouting(RoutingOverrideBody Body) : ICommand<RoutingView>;

internal sealed class OverrideRoutingValidator : AbstractValidator<OverrideRouting>
{
    public OverrideRoutingValidator()
    {
        RuleFor(c => c.Body.ObjectType).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Body.Key).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Body.MasterState).Must(s => RouteState.All.Contains(s)).WithMessage("MasterState must be NONE, TRANSITIONING or CORE.");
        RuleFor(c => c.Body.From).Must(d => DateOnly.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, out _)).WithMessage("From must be a date (yyyy-MM-dd).");
        RuleFor(c => c.Body.To).Must(d => d is null || DateOnly.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, out _)).WithMessage("To must be a date (yyyy-MM-dd).");
        RuleFor(c => c.Body.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class OverrideRoutingHandler(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEventPublisher events)
    : ICommandHandler<OverrideRouting, RoutingView>
{
    public async Task<Result<RoutingView>> HandleAsync(OverrideRouting command, CancellationToken cancellationToken)
    {
        var body = command.Body;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var from = new BusinessDate(DateOnly.Parse(body.From, System.Globalization.CultureInfo.InvariantCulture));
        BusinessDate? to = body.To is null ? null : new BusinessDate(DateOnly.Parse(body.To, System.Globalization.CultureInfo.InvariantCulture));
        if (to is { } end && end <= from)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "To must be after From (To is exclusive).");
        }

        // Serialise changes of one object so two concurrent overrides cannot both pass the overlap check.
        var lockKey = $"mig.routing|{legalEntity.Value}|{body.ObjectType}|{body.Key}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", cancellationToken).ConfigureAwait(false);

        var periods = await db.Routes
            .Where(r => r.LegalEntityId == legalEntity && r.ObjectType == body.ObjectType && r.Key == body.Key)
            .OrderBy(r => r.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false);
        var overlapping = periods.Where(p => p.ValidFrom < (to ?? BusinessDate.MaxValue) && (p.ValidTo is null || p.ValidTo > from)).ToList();

        RoutingRow? truncate = null;
        if (overlapping.Count > 0)
        {
            // Allowed: the only overlapped period is the open-ended last one and started before the new one.
            if (overlapping.Count == 1 && overlapping[0] is { ValidTo: null } last && last.ValidFrom < from)
            {
                truncate = last;
            }
            else
            {
                return DomainError.Of(ModuleCode.MIG, MigrationErrors.RoutingOverlap, $"{body.ObjectType} {body.Key} already has a master for part of {body.From}..{body.To ?? "open"}.");
            }
        }

        var oldState = periods.FirstOrDefault(p => p.ValidFrom <= from && (p.ValidTo is null || p.ValidTo > from))?.MasterState ?? RouteState.None;
        var now = clock.Now;
        if (truncate is not null)
        {
            truncate.ValidTo = from;
        }

        var route = new RoutingRow
        {
            RouteId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ObjectType = body.ObjectType, Key = body.Key, ValidFrom = from,
            ValidTo = to, MasterState = body.MasterState, Reason = body.Reason, CreatedBy = context.Actor.ToString(), CreatedAt = now,
        };
        db.Routes.Add(route);

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(CoexistenceMasterChangedV1.Descriptor), "CoexistenceRoute", route.RouteId.ToString(),
            new CoexistenceMasterChangedV1
            {
                RouteId = route.RouteId, ObjectType = body.ObjectType, LegacyKey = body.Key, OldMasterState = oldState, NewMasterState = body.MasterState,
                EffectiveFrom = Instant.FromUtc(from.Year, from.Month, from.Day), Reason = body.Reason,
            },
            BusinessKeys.Empty.With("routeId", route.RouteId.ToString())) { OccurredAt = now });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return MigrationReader.ToView(route);
    }
}
