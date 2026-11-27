using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Services.Dsar;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.PartyRole.assign</c> (BO-PTY-03).</summary>
internal sealed record AssignPartyRole(PartyRoleAssignRequest Request) : ICommand<PartyRoleAssignResponse>;

/// <summary><c>pty.PartyRole.end</c>: end-dates a role bitemporally.</summary>
internal sealed record EndPartyRole(PartyRoleEndRequest Request) : ICommand<PartyRoleEndResponse>;

/// <summary><c>pty.Relationship.add</c>.</summary>
internal sealed record AddRelationship(RelationshipAddRequest Request) : ICommand<RelationshipAddResponse>;

/// <summary><c>pty.Relationship.end</c>.</summary>
internal sealed record EndRelationship(RelationshipEndRequest Request) : ICommand<RelationshipEndResponse>;

internal sealed class AssignPartyRoleValidator : AbstractValidator<AssignPartyRole>
{
    public AssignPartyRoleValidator()
    {
        RuleFor(c => c.Request.RoleType).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.ContextRef!.Id).NotEmpty().MaximumLength(200).When(c => c.Request.ContextRef is not null);
    }
}

internal sealed class EndPartyRoleValidator : AbstractValidator<EndPartyRole>
{
    public EndPartyRoleValidator() => RuleFor(c => c.Request.RoleId).NotEmpty();
}

internal sealed class AddRelationshipValidator : AbstractValidator<AddRelationship>
{
    public AddRelationshipValidator() => RuleFor(c => c.Request.RelationshipType).NotEmpty().MaximumLength(128);
}

internal sealed class EndRelationshipValidator : AbstractValidator<EndRelationship>
{
    public EndRelationshipValidator() => RuleFor(c => c.Request.RelationshipId).NotEmpty();
}

/// <summary>Shared guards, mapping and events of the role and relationship commands.</summary>
internal static class RoleSupport
{
    /// <summary>The party must exist, not be merged and not be DSAR-restricted (same rules as <c>pty.Party.update</c>).</summary>
    public static async Task<DomainError?> GuardPartyAsync(PartyDbContext db, LegalEntityId legalEntity, PartyId partyId, CancellationToken cancellationToken)
    {
        var party = await db.Parties.AsNoTracking().SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.PartyId == partyId, cancellationToken)
            .ConfigureAwait(false);
        if (party is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist.");
        }

        if (party.Status == Codes.Of(PartyStatus.Merged))
        {
            return DomainError.Of(ModuleCode.PTY, "MERGED", "The party was merged; use the surviving party.");
        }

        if (await db.DsarRestrictions.AnyAsync(r => r.PartyId == partyId && r.RecordClass == PartyDsarWork.PartyRecord, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PTY, "RESTRICTED", "The party record is restricted by a data-subject request and cannot be changed.");
        }

        return null;
    }

    public static BusinessDate Today(IClock clock) => new(DateOnly.FromDateTime(clock.Now.ToUtcDateTime()));

    public static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), message) { FieldErrors = [new FieldError(field, code, "pty." + code.ToLowerInvariant(), message)] };

    public static ObjectRef? ContextOf(PartyRoleRow r) =>
        r.ContextType is null ? null : new ObjectRef(Enum.Parse<ModuleCode>(r.ContextModule!), r.ContextType, r.ContextId!);

    public static PartyRoleView ToView(PartyRoleRow r) => new()
    {
        RoleId = r.RoleId, PartyId = r.PartyId, RoleType = r.RoleType, ContextRef = ContextOf(r), ValidPeriod = new DateRange(r.ValidFrom, r.ValidTo),
    };

    public static RelationshipView ToView(PartyRelationshipRow r) => new()
    {
        RelationshipId = r.RelationshipId, FromPartyId = r.FromPartyId, ToPartyId = r.ToPartyId, RelationshipType = r.RelationshipType,
        ValidPeriod = new DateRange(r.ValidFrom, r.ValidTo),
    };

    public static void PublishRole(IEventPublisher events, PartyRoleRow r) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PartyRoleChangedV1.Descriptor), "Party", r.PartyId.Value.ToString(),
            new PartyRoleChangedV1
            {
                PartyId = r.PartyId, RoleType = r.RoleType, ContextRef = ContextOf(r) ?? ObjectRef.For(ModuleCode.PTY, "Party", r.PartyId),
                ValidPeriod = new DateRange(r.ValidFrom, r.ValidTo),
            },
            BusinessKeys.Empty.With("partyId", r.PartyId.Value.ToString())));

    public static void PublishRelationship(IEventPublisher events, PartyRelationshipRow r) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PartyRelationshipChangedV1.Descriptor), "Party", r.FromPartyId.Value.ToString(),
            new PartyRelationshipChangedV1
            {
                FromPartyId = r.FromPartyId, ToPartyId = r.ToPartyId, RelationshipType = r.RelationshipType, ValidPeriod = new DateRange(r.ValidFrom, r.ValidTo),
            },
            BusinessKeys.Empty.With("partyId", r.FromPartyId.Value.ToString())));
}

/// <summary>
/// Assigns a role: the type must be in <see cref="RoleCatalogue"/>, the same party, type and context cannot overlap in time,
/// the vendor-master role needs an AFM, and the first role turns a Prospect party Active. Publishes <c>PartyRoleChanged</c>.
/// </summary>
internal sealed class AssignPartyRoleHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<AssignPartyRole, PartyRoleAssignResponse>
{
    public async Task<Result<PartyRoleAssignResponse>> HandleAsync(AssignPartyRole command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        if (!RoleCatalogue.Roles.Contains(request.RoleType))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "ROLE-TYPE"), $"Unknown role type {request.RoleType}.");
        }

        if (await RoleSupport.GuardPartyAsync(db, legalEntity, request.PartyId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var now = clock.Now;
        var from = request.ValidFrom ?? RoleSupport.Today(clock);
        if (request.ValidTo is { } to && to <= from)
        {
            return RoleSupport.Invalid("validTo", "PERIOD", "The role must end after it starts.");
        }

        var period = new DateRange(from, request.ValidTo);
        var (module, type, id) = (request.ContextRef?.Module.ToString(), request.ContextRef?.Type, request.ContextRef?.Id);
        var same = await db.Roles
            .Where(r => r.PartyId == request.PartyId && r.RoleType == request.RoleType && r.RecordedTo == null && r.ContextType == type && r.ContextId == id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (same.Any(r => new DateRange(r.ValidFrom, r.ValidTo).Overlaps(period)))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "ROLE-DUPLICATE"), "The party already holds this role in this context for an overlapping period.");
        }

        if (request.RoleType == RoleCatalogue.Vendor
            && !await db.Identifiers.AnyAsync(
                i => i.PartyId == request.PartyId && i.Scheme == "AFM" && i.RecordedTo == null && i.ValidTo == null && i.VerificationStatus != "VERIFICATION_FAILED",
                cancellationToken).ConfigureAwait(false))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "ROLE-DATA-MISSING"), "A vendor needs a valid AFM on the party record.")
            {
                FieldErrors = [new FieldError("partyId", "AFM_REQUIRED", "pty.afm_required", "A vendor needs a valid AFM on the party record.")],
            };
        }

        var row = new PartyRoleRow
        {
            RowId = Guid.CreateVersion7(), RoleId = Guid.CreateVersion7(), PartyId = request.PartyId, LegalEntityId = legalEntity, RoleType = request.RoleType,
            ContextModule = module, ContextType = type, ContextId = id, ValidFrom = from, ValidTo = request.ValidTo, RecordedFrom = now,
            CreatedBy = context.Actor.ToString(),
        };
        db.Roles.Add(row);

        var party = await db.Parties.SingleAsync(p => p.PartyId == request.PartyId, cancellationToken).ConfigureAwait(false);
        if (party.Status == Codes.Of(PartyStatus.Prospect))
        {
            party.Status = Codes.Of(PartyStatus.Active);
            party.RecordVersion++;
            party.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        RoleSupport.PublishRole(events, row);
        return new PartyRoleAssignResponse { Role = RoleSupport.ToView(row) };
    }
}

/// <summary>Ends a role: the current row closes in record time and a successor carrying the end date is recorded.</summary>
internal sealed class EndPartyRoleHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<EndPartyRole, PartyRoleEndResponse>
{
    public async Task<Result<PartyRoleEndResponse>> HandleAsync(EndPartyRole command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var current = await db.Roles.SingleOrDefaultAsync(
            r => r.RoleId == command.Request.RoleId && r.LegalEntityId == legalEntity && r.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The role does not exist.");
        }

        if (await RoleSupport.GuardPartyAsync(db, legalEntity, current.PartyId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var to = command.Request.ValidTo ?? RoleSupport.Today(clock);
        if (to <= current.ValidFrom || (current.ValidTo is { } existing && to >= existing))
        {
            return RoleSupport.Invalid("validTo", "PERIOD", "The end date must fall inside the role's current period.");
        }

        var now = clock.Now;
        current.RecordedTo = now;
        var successor = new PartyRoleRow
        {
            RowId = Guid.CreateVersion7(), RoleId = current.RoleId, PartyId = current.PartyId, LegalEntityId = current.LegalEntityId, RoleType = current.RoleType,
            ContextModule = current.ContextModule, ContextType = current.ContextType, ContextId = current.ContextId, ValidFrom = current.ValidFrom, ValidTo = to,
            RecordedFrom = now, CreatedBy = context.Actor.ToString(),
        };
        db.Roles.Add(successor);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        RoleSupport.PublishRole(events, successor);
        return new PartyRoleEndResponse { Role = RoleSupport.ToView(successor) };
    }
}

/// <summary>
/// Adds a relationship: both parties must exist and be changeable, a pair and type cannot overlap, symmetric types are stored once,
/// and hierarchical types refuse a cycle (the target must not already reach the source). Publishes <c>PartyRelationshipChanged</c>.
/// </summary>
internal sealed class AddRelationshipHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<AddRelationship, RelationshipAddResponse>
{
    public async Task<Result<RelationshipAddResponse>> HandleAsync(AddRelationship command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        if (!RoleCatalogue.Relationships.Contains(request.RelationshipType))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "ROLE-TYPE"), $"Unknown relationship type {request.RelationshipType}.");
        }

        if (request.FromPartyId == request.ToPartyId)
        {
            return RoleSupport.Invalid("toPartyId", "SELF_RELATIONSHIP", "A party cannot be related to itself.");
        }

        foreach (var id in new[] { request.FromPartyId, request.ToPartyId })
        {
            if (await RoleSupport.GuardPartyAsync(db, legalEntity, id, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }
        }

        var from = request.ValidFrom ?? RoleSupport.Today(clock);
        if (request.ValidTo is { } end && end <= from)
        {
            return RoleSupport.Invalid("validTo", "PERIOD", "The relationship must end after it starts.");
        }

        var period = new DateRange(from, request.ValidTo);
        var (a, b) = (request.FromPartyId, request.ToPartyId);
        if (RoleCatalogue.Symmetric.Contains(request.RelationshipType) && a.Value.CompareTo(b.Value) > 0)
        {
            (a, b) = (b, a);
        }

        var pair = await db.Relationships
            .Where(r => r.LegalEntityId == legalEntity && r.RelationshipType == request.RelationshipType && r.RecordedTo == null
                && r.FromPartyId == a && r.ToPartyId == b)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pair.Any(r => new DateRange(r.ValidFrom, r.ValidTo).Overlaps(period)))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "OVERLAP"), "The same relationship already exists for an overlapping period.");
        }

        if (RoleCatalogue.HierarchicalRelationships.Contains(request.RelationshipType)
            && await ReachesAsync(legalEntity, request.RelationshipType, request.ToPartyId, request.FromPartyId, period, cancellationToken).ConfigureAwait(false))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "RELATIONSHIP-CYCLE"), "The relationship would close a cycle.");
        }

        var row = new PartyRelationshipRow
        {
            RowId = Guid.CreateVersion7(), RelationshipId = Guid.CreateVersion7(), LegalEntityId = legalEntity, FromPartyId = a, ToPartyId = b,
            RelationshipType = request.RelationshipType, ValidFrom = from, ValidTo = request.ValidTo, RecordedFrom = clock.Now, CreatedBy = context.Actor.ToString(),
        };
        db.Relationships.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        RoleSupport.PublishRelationship(events, row);
        return new RelationshipAddResponse { Relationship = RoleSupport.ToView(row) };
    }

    /// <summary>True when <paramref name="start"/> already reaches <paramref name="target"/> over current edges of the type that overlap <paramref name="period"/>.</summary>
    private async Task<bool> ReachesAsync(LegalEntityId legalEntity, string type, PartyId start, PartyId target, DateRange period, CancellationToken cancellationToken)
    {
        // TODO(hardening): loads the whole type's edge set; use a recursive CTE once hierarchies get large.
        var edges = (await db.Relationships.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.RelationshipType == type && r.RecordedTo == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => new DateRange(r.ValidFrom, r.ValidTo).Overlaps(period))
            .ToLookup(r => r.FromPartyId, r => r.ToPartyId);
        var seen = new HashSet<PartyId> { start };
        var queue = new Queue<PartyId>([start]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node == target)
            {
                return true;
            }

            foreach (var next in edges[node].Where(seen.Add))
            {
                queue.Enqueue(next);
            }
        }

        return false;
    }
}

/// <summary>Ends a relationship (bitemporal, like roles).</summary>
internal sealed class EndRelationshipHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<EndRelationship, RelationshipEndResponse>
{
    public async Task<Result<RelationshipEndResponse>> HandleAsync(EndRelationship command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var current = await db.Relationships.SingleOrDefaultAsync(
            r => r.RelationshipId == command.Request.RelationshipId && r.LegalEntityId == legalEntity && r.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The relationship does not exist.");
        }

        foreach (var id in new[] { current.FromPartyId, current.ToPartyId })
        {
            if (await RoleSupport.GuardPartyAsync(db, legalEntity, id, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }
        }

        var to = command.Request.ValidTo ?? RoleSupport.Today(clock);
        if (to <= current.ValidFrom || (current.ValidTo is { } existing && to >= existing))
        {
            return RoleSupport.Invalid("validTo", "PERIOD", "The end date must fall inside the relationship's current period.");
        }

        var now = clock.Now;
        current.RecordedTo = now;
        var successor = new PartyRelationshipRow
        {
            RowId = Guid.CreateVersion7(), RelationshipId = current.RelationshipId, LegalEntityId = current.LegalEntityId, FromPartyId = current.FromPartyId,
            ToPartyId = current.ToPartyId, RelationshipType = current.RelationshipType, ValidFrom = current.ValidFrom, ValidTo = to, RecordedFrom = now,
            CreatedBy = context.Actor.ToString(),
        };
        db.Relationships.Add(successor);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        RoleSupport.PublishRelationship(events, successor);
        return new RelationshipEndResponse { Relationship = RoleSupport.ToView(successor) };
    }
}

/// <summary>Audit facts of the role and relationship commands: the object only, no personal values.</summary>
internal sealed class RoleAuditor :
    ICommandAuditor<AssignPartyRole, PartyRoleAssignResponse>,
    ICommandAuditor<EndPartyRole, PartyRoleEndResponse>,
    ICommandAuditor<AddRelationship, RelationshipAddResponse>,
    ICommandAuditor<EndRelationship, RelationshipEndResponse>
{
    public CommandAuditFacts Describe(AssignPartyRole command, Result<PartyRoleAssignResponse>? result) =>
        Role(result is { IsSuccess: true } ok ? ok.Value.Role : null);

    public CommandAuditFacts Describe(EndPartyRole command, Result<PartyRoleEndResponse>? result) =>
        Role(result is { IsSuccess: true } ok ? ok.Value.Role : null);

    public CommandAuditFacts Describe(AddRelationship command, Result<RelationshipAddResponse>? result) =>
        Relationship(result is { IsSuccess: true } ok ? ok.Value.Relationship : null);

    public CommandAuditFacts Describe(EndRelationship command, Result<RelationshipEndResponse>? result) =>
        Relationship(result is { IsSuccess: true } ok ? ok.Value.Relationship : null);

    private static CommandAuditFacts Role(PartyRoleView? role) => role is null
        ? new CommandAuditFacts()
        : new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, "PartyRole", role.RoleId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("partyId", role.PartyId.Value.ToString()),
        };

    private static CommandAuditFacts Relationship(RelationshipView? link) => link is null
        ? new CommandAuditFacts()
        : new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, "PartyRelationship", link.RelationshipId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("partyId", link.FromPartyId.Value.ToString()),
        };
}
