using System.Globalization;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
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
using DomainStatus = CoreIns.Modules.Party.Domain.IntermediaryStatus;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.AgencyUser.create</c> (BO-PTY-12, REQ-PTY-243/246).</summary>
internal sealed record CreateAgencyUser(AgencyUserCreateRequest Request) : ICommand<AgencyUserResponse>;

/// <summary><c>pty.AgencyUser.changeRole</c> (REQ-PTY-244).</summary>
internal sealed record ChangeAgencyUserRole(AgencyUserChangeRoleRequest Request) : ICommand<AgencyUserResponse>;

/// <summary><c>pty.AgencyUser.grantAccess</c> (REQ-PTY-245).</summary>
internal sealed record GrantAgencyAccess(AgencyUserGrantAccessRequest Request) : ICommand<AgencyUserResponse>;

/// <summary><c>pty.AgencyUser.revokeAccess</c> (REQ-PTY-245).</summary>
internal sealed record RevokeAgencyAccess(AgencyUserRevokeAccessRequest Request) : ICommand<AgencyUserResponse>;

/// <summary><c>pty.AgencyUser.suspend</c> (REQ-PTY-246).</summary>
internal sealed record SuspendAgencyUser(AgencyUserSuspendRequest Request) : ICommand<AgencyUserResponse>;

/// <summary><c>pty.AgencyUser.reactivate</c> (REQ-PTY-246).</summary>
internal sealed record ReactivateAgencyUser(AgencyUserReactivateRequest Request) : ICommand<AgencyUserResponse>;

/// <summary><c>pty.AgencyUser.removeProducer</c> (REQ-PTY-247).</summary>
internal sealed record RemoveAgencyProducer(AgencyUserRemoveProducerRequest Request) : ICommand<AgencyUserRemoveProducerResponse>;

internal sealed class CreateAgencyUserValidator : AbstractValidator<CreateAgencyUser>
{
    public CreateAgencyUserValidator()
    {
        RuleFor(c => c.Request.IntermediaryId.Value).NotEmpty();
        RuleFor(c => c.Request.ExternalSubject).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.ProducerCodes).Must(p => p is null || p.Count <= 100).WithMessage("At most 100 producer codes at once.");
    }
}

internal sealed class ChangeAgencyUserRoleValidator : AbstractValidator<ChangeAgencyUserRole>
{
    public ChangeAgencyUserRoleValidator() => RuleFor(c => c.Request.AgencyUserId).NotEmpty();
}

internal sealed class GrantAgencyAccessValidator : AbstractValidator<GrantAgencyAccess>
{
    public GrantAgencyAccessValidator()
    {
        RuleFor(c => c.Request.AgencyUserId).NotEmpty();
        RuleFor(c => c.Request.ProducerCodes).NotEmpty().Must(p => p.Count <= 100).WithMessage("At most 100 producer codes at once.");
    }
}

internal sealed class RevokeAgencyAccessValidator : AbstractValidator<RevokeAgencyAccess>
{
    public RevokeAgencyAccessValidator()
    {
        RuleFor(c => c.Request.AgencyUserId).NotEmpty();
        RuleFor(c => c.Request.ProducerCodes).NotEmpty().Must(p => p.Count <= 100).WithMessage("At most 100 producer codes at once.");
    }
}

internal sealed class SuspendAgencyUserValidator : AbstractValidator<SuspendAgencyUser>
{
    public SuspendAgencyUserValidator()
    {
        RuleFor(c => c.Request.AgencyUserId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class ReactivateAgencyUserValidator : AbstractValidator<ReactivateAgencyUser>
{
    public ReactivateAgencyUserValidator()
    {
        RuleFor(c => c.Request.AgencyUserId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class RemoveAgencyProducerValidator : AbstractValidator<RemoveAgencyProducer>
{
    public RemoveAgencyProducerValidator()
    {
        RuleFor(c => c.Request.AgencyUserId).NotEmpty();
        RuleFor(c => c.Request.ReassignToProducerCode).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>Wire codes of the agency roles and statuses (check constraints of <c>pty.agency_user</c>).</summary>
internal static class AgencyCodes
{
    public const string Admin = "AGENCY_ADMIN";
    public const string Manager = "OFFICE_MANAGER";
    public const string Producer = "PRODUCER";
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Removed = "REMOVED";

    /// <summary>Insurer staff who administer any agency; everybody else acts inside the agency they administer.</summary>
    public static readonly string[] StaffRoles = ["Staff.UnderwritingManager", "Platform.Admin"];
}

/// <summary>
/// The delegated-administration rules (BO-PTY-12, REQ-PTY-243..252). An agency administrator acts only inside their own agency (the agency of
/// their active <c>AGENCY_ADMIN</c> user row, found by the request actor); insurer staff act in any agency. Grants are pushed to PLT as the
/// ABAC attributes of the user with <c>ExternalUserAccessChanged</c> (PLT applies them within 60 s, REQ-PTY-252); a suspended user has none.
/// Every action is audited by <see cref="AgencyUserAuditor"/>.
/// </summary>
internal sealed class AgencyAdminService(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
{
    public static DomainError OutOfAgency() =>
        DomainError.Of(ModuleCode.PTY, "OUT-OF-AGENCY", "The action is outside the agency you administer.");

    public static DomainError NotFound() => DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The agency or agency user does not exist.");

    public LegalEntityId LegalEntity => protection.Current(context);

    /// <summary>Null when the caller may administer <paramref name="agency"/>: insurer staff, or an active administrator of that agency.</summary>
    public async Task<DomainError?> AuthorizeAsync(IntermediaryId agency, bool readOnly, CancellationToken cancellationToken)
    {
        if (context.Roles.Any(r => AgencyCodes.StaffRoles.Contains(r, StringComparer.Ordinal)))
        {
            return null;
        }

        var legalEntity = LegalEntity;
        var subject = context.Actor.Id;
        var caller = await db.AgencyUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.LegalEntityId == legalEntity && u.ExternalSubject == subject && u.Status == AgencyCodes.Active, cancellationToken).ConfigureAwait(false);
        var allowed = caller is not null && caller.IntermediaryId == agency
            && (caller.Role == AgencyCodes.Admin || (readOnly && caller.Role == AgencyCodes.Manager));
        return allowed ? null : OutOfAgency();
    }

    /// <summary>The agency of an operation: it exists, belongs to the legal entity and is not terminated.</summary>
    public async Task<Result<IntermediaryRow>> AgencyAsync(IntermediaryId id, bool allowTerminated, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var agency = await db.Intermediaries.AsNoTracking().SingleOrDefaultAsync(i => i.IntermediaryId == id && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (agency is null)
        {
            return NotFound();
        }

        if (!allowTerminated && agency.Status == Codes.Of(DomainStatus.Terminated))
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "The agency is terminated.");
        }

        return agency;
    }

    /// <summary>Loads the user and authorises the caller against the user's agency (a user of another agency is out of agency, not unknown).</summary>
    public async Task<Result<AgencyUserRow>> UserAsync(Guid agencyUserId, bool tracked, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var query = tracked ? db.AgencyUsers : db.AgencyUsers.AsNoTracking();
        var user = await query.SingleOrDefaultAsync(u => u.AgencyUserId == agencyUserId && u.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            // Non-staff callers cannot tell an unknown id from another agency's user.
            return context.Roles.Any(r => AgencyCodes.StaffRoles.Contains(r, StringComparer.Ordinal)) ? NotFound() : OutOfAgency();
        }

        return await AuthorizeAsync(user.IntermediaryId, false, cancellationToken).ConfigureAwait(false) is { } denied ? denied : user;
    }

    public async Task<bool> IsLastAdminAsync(AgencyUserRow user, CancellationToken cancellationToken) =>
        user.Role == AgencyCodes.Admin && user.Status == AgencyCodes.Active
        && !await db.AgencyUsers.AsNoTracking().AnyAsync(
            u => u.IntermediaryId == user.IntermediaryId && u.AgencyUserId != user.AgencyUserId && u.Role == AgencyCodes.Admin && u.Status == AgencyCodes.Active, cancellationToken)
            .ConfigureAwait(false);

    public static DomainError LastAdmin() =>
        DomainError.Of(ModuleCode.PTY, "LAST-ADMIN", "The agency needs at least one active administrator.");

    /// <summary>Resolves producer codes of the agency; a code of another agency is out of agency, an unknown one not found, an inactive one a state error.</summary>
    public async Task<Result<IReadOnlyList<string>>> AgencyCodesAsync(IntermediaryId agency, IEnumerable<string> codes, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var wanted = codes.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.ProducerCodes.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && wanted.Contains(c.Code)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var code in wanted)
        {
            var row = rows.SingleOrDefault(r => r.Code == code);
            if (row is null)
            {
                return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", $"The producer code {code} does not exist.");
            }

            if (row.IntermediaryId != agency)
            {
                return OutOfAgency();
            }

            if (row.Status != Codes.Of(ProducerCodeStatus.Active))
            {
                return DomainError.Of(ModuleCode.PTY, "STATE", $"The producer code {code} is not active.");
            }
        }

        return wanted;
    }

    public async Task<IReadOnlyList<string>> OpenGrantsAsync(Guid agencyUserId, CancellationToken cancellationToken) =>
        await db.AgencyGrants.AsNoTracking().Where(g => g.AgencyUserId == agencyUserId && g.RevokedAt == null).Select(g => g.ProducerCode).OrderBy(c => c).ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void AddGrants(AgencyUserRow user, IEnumerable<string> codes, Instant now)
    {
        foreach (var code in codes)
        {
            db.AgencyGrants.Add(new AgencyGrantRow
            {
                GrantId = Guid.CreateVersion7(), AgencyUserId = user.AgencyUserId, LegalEntityId = user.LegalEntityId, IntermediaryId = user.IntermediaryId,
                ProducerCode = code, GrantedAt = now, GrantedBy = context.Actor.ToString(),
            });
        }
    }

    public async Task<AgencyUserView> ViewAsync(AgencyUserRow user, CancellationToken cancellationToken) =>
        View(user, await OpenGrantsAsync(user.AgencyUserId, cancellationToken).ConfigureAwait(false));

    public static AgencyUserView View(AgencyUserRow u, IReadOnlyList<string> grants) => new()
    {
        AgencyUserId = u.AgencyUserId,
        IntermediaryId = u.IntermediaryId,
        ExternalSubject = u.ExternalSubject,
        DisplayName = u.DisplayName,
        Role = PorWire.Parse<AgencyUserView.RoleValue>(u.Role),
        Status = PorWire.Parse<AgencyUserView.StatusValue>(u.Status),
        StatusReason = u.StatusReason,
        ProducerCodes = grants,
        CreatedAt = u.CreatedAt,
    };

    public async Task<AgencyUserListResponse> ListAsync(IntermediaryId agency, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var users = await db.AgencyUsers.AsNoTracking().Where(u => u.LegalEntityId == legalEntity && u.IntermediaryId == agency && u.Status != AgencyCodes.Removed)
            .OrderBy(u => u.DisplayName).ThenBy(u => u.AgencyUserId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = users.Select(u => u.AgencyUserId).ToList();
        var grants = (await db.AgencyGrants.AsNoTracking().Where(g => ids.Contains(g.AgencyUserId) && g.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(g => g.AgencyUserId, g => g.ProducerCode);
        return new AgencyUserListResponse { IntermediaryId = agency, Users = users.Select(u => View(u, grants[u.AgencyUserId].Order(StringComparer.Ordinal).ToList())).ToList() };
    }

    /// <summary>Pushes the change of a user's grants to PLT (REQ-PTY-252). An empty change (role change) is still published so PLT refreshes the role attribute.</summary>
    public void PublishAccess(AgencyUserRow user, IReadOnlyList<string> added, IReadOnlyList<string> removed) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ExternalUserAccessChangedV1.Descriptor), "Intermediary", user.IntermediaryId.Value.ToString("D"),
            new ExternalUserAccessChangedV1 { IntermediaryId = user.IntermediaryId, UserId = new UserId(user.AgencyUserId), GrantsAdded = added, GrantsRemoved = removed },
            BusinessKeys.Empty.With("intermediaryId", user.IntermediaryId.Value.ToString("D"))));

    public Instant Now => clock.Now;

    public BusinessDate Today => RoleSupport.Today(clock);

    public string Actor => context.Actor.ToString();
}

internal sealed class CreateAgencyUserHandler(AgencyAdminService service, PartyDbContext db) : ICommandHandler<CreateAgencyUser, AgencyUserResponse>
{
    public async Task<Result<AgencyUserResponse>> HandleAsync(CreateAgencyUser command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        if (await service.AuthorizeAsync(r.IntermediaryId, false, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var agency = await service.AgencyAsync(r.IntermediaryId, false, cancellationToken).ConfigureAwait(false);
        if (agency.IsFailure)
        {
            return agency.Error;
        }

        var codes = await service.AgencyCodesAsync(r.IntermediaryId, r.ProducerCodes ?? [], cancellationToken).ConfigureAwait(false);
        if (codes.IsFailure)
        {
            return codes.Error;
        }

        var subject = r.ExternalSubject.Trim();
        var legalEntity = service.LegalEntity;
        if (await db.AgencyUsers.AnyAsync(u => u.LegalEntityId == legalEntity && u.ExternalSubject == subject && u.Status != AgencyCodes.Removed, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "That external user is already linked to an agency.");
        }

        var now = service.Now;
        var user = new AgencyUserRow
        {
            AgencyUserId = Guid.CreateVersion7(), LegalEntityId = legalEntity, IntermediaryId = r.IntermediaryId, ExternalSubject = subject, DisplayName = r.DisplayName.Trim(),
            Role = PorWire.Of(r.Role), Status = AgencyCodes.Active, CreatedAt = now, CreatedBy = service.Actor, UpdatedAt = now,
        };
        db.AgencyUsers.Add(user);
        service.AddGrants(user, codes.Value, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        service.PublishAccess(user, codes.Value, []);
        return new AgencyUserResponse { User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class ChangeAgencyUserRoleHandler(AgencyAdminService service, PartyDbContext db) : ICommandHandler<ChangeAgencyUserRole, AgencyUserResponse>
{
    public async Task<Result<AgencyUserResponse>> HandleAsync(ChangeAgencyUserRole command, CancellationToken cancellationToken)
    {
        var loaded = await service.UserAsync(command.Request.AgencyUserId, true, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (user.Status == AgencyCodes.Removed)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "The user was removed from the agency.");
        }

        var role = PorWire.Of(command.Request.Role);
        if (role == user.Role)
        {
            return new AgencyUserResponse { User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false) };
        }

        if (role != AgencyCodes.Admin && await service.IsLastAdminAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return AgencyAdminService.LastAdmin();
        }

        user.Role = role;
        user.UpdatedAt = service.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        service.PublishAccess(user, [], []);
        return new AgencyUserResponse { User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class GrantAgencyAccessHandler(AgencyAdminService service, PartyDbContext db) : ICommandHandler<GrantAgencyAccess, AgencyUserResponse>
{
    public async Task<Result<AgencyUserResponse>> HandleAsync(GrantAgencyAccess command, CancellationToken cancellationToken)
    {
        var loaded = await service.UserAsync(command.Request.AgencyUserId, true, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (user.Status != AgencyCodes.Active)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "Only an active user can be granted access; reactivate the user first.");
        }

        var codes = await service.AgencyCodesAsync(user.IntermediaryId, command.Request.ProducerCodes, cancellationToken).ConfigureAwait(false);
        if (codes.IsFailure)
        {
            return codes.Error;
        }

        var held = await service.OpenGrantsAsync(user.AgencyUserId, cancellationToken).ConfigureAwait(false);
        var added = codes.Value.Where(c => !held.Contains(c, StringComparer.Ordinal)).ToList();
        if (added.Count > 0)
        {
            service.AddGrants(user, added, service.Now);
            user.UpdatedAt = service.Now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            service.PublishAccess(user, added, []);
        }

        return new AgencyUserResponse { User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class RevokeAgencyAccessHandler(AgencyAdminService service, PartyDbContext db) : ICommandHandler<RevokeAgencyAccess, AgencyUserResponse>
{
    public async Task<Result<AgencyUserResponse>> HandleAsync(RevokeAgencyAccess command, CancellationToken cancellationToken)
    {
        var loaded = await service.UserAsync(command.Request.AgencyUserId, true, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (user.Status == AgencyCodes.Removed)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "The user was removed from the agency.");
        }

        var wanted = command.Request.ProducerCodes.Select(c => c.Trim()).ToList();
        var open = await db.AgencyGrants.Where(g => g.AgencyUserId == user.AgencyUserId && g.RevokedAt == null && wanted.Contains(g.ProducerCode)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (open.Count > 0)
        {
            var now = service.Now;
            foreach (var grant in open)
            {
                grant.RevokedAt = now;
                grant.RevokedBy = service.Actor;
            }

            user.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // A suspended user holds nothing in PLT already, so there is nothing to push.
            if (user.Status == AgencyCodes.Active)
            {
                service.PublishAccess(user, [], open.Select(g => g.ProducerCode).Order(StringComparer.Ordinal).ToList());
            }
        }

        return new AgencyUserResponse { User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class SuspendAgencyUserHandler(AgencyAdminService service, PartyDbContext db) : ICommandHandler<SuspendAgencyUser, AgencyUserResponse>
{
    public async Task<Result<AgencyUserResponse>> HandleAsync(SuspendAgencyUser command, CancellationToken cancellationToken)
    {
        var loaded = await service.UserAsync(command.Request.AgencyUserId, true, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (user.Status != AgencyCodes.Active)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "Only an active user can be suspended.");
        }

        if (await service.IsLastAdminAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return AgencyAdminService.LastAdmin();
        }

        user.Status = AgencyCodes.Suspended;
        user.StatusReason = command.Request.Reason.Trim();
        user.UpdatedAt = service.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        service.PublishAccess(user, [], await service.OpenGrantsAsync(user.AgencyUserId, cancellationToken).ConfigureAwait(false));
        return new AgencyUserResponse { User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class ReactivateAgencyUserHandler(AgencyAdminService service, PartyDbContext db) : ICommandHandler<ReactivateAgencyUser, AgencyUserResponse>
{
    public async Task<Result<AgencyUserResponse>> HandleAsync(ReactivateAgencyUser command, CancellationToken cancellationToken)
    {
        var loaded = await service.UserAsync(command.Request.AgencyUserId, true, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (user.Status != AgencyCodes.Suspended)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "Only a suspended user can be reactivated.");
        }

        var agency = await service.AgencyAsync(user.IntermediaryId, false, cancellationToken).ConfigureAwait(false);
        if (agency.IsFailure)
        {
            return agency.Error;
        }

        user.Status = AgencyCodes.Active;
        user.StatusReason = command.Request.Reason.Trim();
        user.UpdatedAt = service.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        service.PublishAccess(user, await service.OpenGrantsAsync(user.AgencyUserId, cancellationToken).ConfigureAwait(false), []);
        return new AgencyUserResponse { User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Removes a producer from the agency (REQ-PTY-247): the producer's book (every current producer-of-record row on the producer's granted codes)
/// is moved to <c>reassignToProducerCode</c> through the producer-of-record rules (BO-PTY-09), one result per term. Only when every term moved
/// is the user removed and the grants revoked; otherwise the user stays active and the call can be repeated. TODO(hardening): a policy whose
/// term carries the producer code but has no producer-of-record row is not found here (needs a POL listing by producer code).
/// </summary>
internal sealed class RemoveAgencyProducerHandler(AgencyAdminService service, PartyDbContext db, ProducerOfRecordWriter writer, IEventPublisher events)
    : ICommandHandler<RemoveAgencyProducer, AgencyUserRemoveProducerResponse>
{
    public async Task<Result<AgencyUserRemoveProducerResponse>> HandleAsync(RemoveAgencyProducer command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var loaded = await service.UserAsync(r.AgencyUserId, true, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (user.Role != AgencyCodes.Producer || user.Status == AgencyCodes.Removed)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "Only a producer of the agency can be removed this way.");
        }

        var target = await service.AgencyCodesAsync(user.IntermediaryId, [r.ReassignToProducerCode], cancellationToken).ConfigureAwait(false);
        if (target.IsFailure)
        {
            return target.Error;
        }

        var own = await service.OpenGrantsAsync(user.AgencyUserId, cancellationToken).ConfigureAwait(false);
        var targetCode = target.Value[0];
        if (own.Contains(targetCode, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.PTY, "REASSIGNMENT-REQUIRED", "The book must be re-assigned to a producer code the removed producer does not hold.");
        }

        var today = service.Today;
        var terms = await db.ProducerOfRecords.AsNoTracking()
            .Where(p => p.LegalEntityId == service.LegalEntity && p.IntermediaryId == user.IntermediaryId && p.RecordedTo == null && own.Contains(p.ProducerCode) && (p.ValidTo == null || p.ValidTo > today))
            .Select(p => new { p.PolicyTermId, p.ValidFrom }).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Each term moves from the day the producer's segment starts (today when it is already running), so a future-dated segment is replaced too.
        var starts = terms.GroupBy(t => t.PolicyTermId).ToDictionary(g => g.Key, g => g.Min(t => t.ValidFrom) > today ? g.Min(t => t.ValidFrom) : today);

        var transferId = Guid.CreateVersion7();
        var change = new PorChange(
            targetCode, today, false, "INTERMEDIARY_LEFT", "Producer removed from the agency", "OLD_PRODUCER_KEEPS_EARNED", null, null, transferId);
        var results = new List<ProducerOfRecordBulkTransferResult>();
        foreach (var (termId, start) in starts)
        {
            var outcome = await writer.ChangeAsync(termId, change with { EffectiveDate = start }, cancellationToken).ConfigureAwait(false);
            results.Add(outcome.IsSuccess
                ? new ProducerOfRecordBulkTransferResult { TermId = termId, Outcome = ProducerOfRecordBulkTransferResult.OutcomeValue.Transferred, Entry = outcome.Value.Entry }
                : new ProducerOfRecordBulkTransferResult
                {
                    TermId = termId, Outcome = ProducerOfRecordBulkTransferResult.OutcomeValue.Refused, ErrorCode = outcome.Error.Code.Value, Message = outcome.Error.Detail,
                });
        }

        var transferred = results.Count(x => x.Outcome == ProducerOfRecordBulkTransferResult.OutcomeValue.Transferred);
        var refused = results.Count - transferred;
        if (starts.Count > 0)
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(BookTransferCompletedV1.Descriptor), "BookTransfer", transferId.ToString("D"),
                new BookTransferCompletedV1
                {
                    TransferId = transferId, TransferredCount = transferred, FailureCount = refused, Mode = "PRODUCER_REMOVAL", SourceProducerCodes = own, TargetProducerCode = targetCode,
                },
                BusinessKeys.Empty.With("transferId", transferId.ToString("D"))));
        }

        var removed = refused == 0;
        if (removed)
        {
            var now = service.Now;
            var open = await db.AgencyGrants.Where(g => g.AgencyUserId == user.AgencyUserId && g.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var grant in open)
            {
                grant.RevokedAt = now;
                grant.RevokedBy = service.Actor;
            }

            var wasActive = user.Status == AgencyCodes.Active;
            user.Status = AgencyCodes.Removed;
            user.StatusReason = r.Reason.Trim();
            user.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (wasActive)
            {
                service.PublishAccess(user, [], own);
            }
        }

        return new AgencyUserRemoveProducerResponse
        {
            Removed = removed,
            User = await service.ViewAsync(user, cancellationToken).ConfigureAwait(false),
            TransferId = starts.Count > 0 ? transferId : null,
            Transferred = transferred,
            Refused = refused,
            Results = results,
        };
    }
}

/// <summary>Audit facts of every delegated-administration command (REQ-PTY-251): actor and time come from the pipeline; here the agency, the target user and the change (no personal values).</summary>
internal sealed class AgencyUserAuditor :
    ICommandAuditor<CreateAgencyUser, AgencyUserResponse>,
    ICommandAuditor<ChangeAgencyUserRole, AgencyUserResponse>,
    ICommandAuditor<GrantAgencyAccess, AgencyUserResponse>,
    ICommandAuditor<RevokeAgencyAccess, AgencyUserResponse>,
    ICommandAuditor<SuspendAgencyUser, AgencyUserResponse>,
    ICommandAuditor<ReactivateAgencyUser, AgencyUserResponse>,
    ICommandAuditor<RemoveAgencyProducer, AgencyUserRemoveProducerResponse>
{
    public CommandAuditFacts Describe(CreateAgencyUser command, Result<AgencyUserResponse>? result) => Facts("create", null, result, null);

    public CommandAuditFacts Describe(ChangeAgencyUserRole command, Result<AgencyUserResponse>? result) => Facts("changeRole", command.Request.AgencyUserId, result, null);

    public CommandAuditFacts Describe(GrantAgencyAccess command, Result<AgencyUserResponse>? result) =>
        Facts("grantAccess", command.Request.AgencyUserId, result, string.Join(',', command.Request.ProducerCodes));

    public CommandAuditFacts Describe(RevokeAgencyAccess command, Result<AgencyUserResponse>? result) =>
        Facts("revokeAccess", command.Request.AgencyUserId, result, string.Join(',', command.Request.ProducerCodes));

    public CommandAuditFacts Describe(SuspendAgencyUser command, Result<AgencyUserResponse>? result) => Facts("suspend", command.Request.AgencyUserId, result, null);

    public CommandAuditFacts Describe(ReactivateAgencyUser command, Result<AgencyUserResponse>? result) => Facts("reactivate", command.Request.AgencyUserId, result, null);

    public CommandAuditFacts Describe(RemoveAgencyProducer command, Result<AgencyUserRemoveProducerResponse>? result) =>
        result is { IsSuccess: true } ok
            ? Facts("removeProducer", ok.Value.User, command.Request.ReassignToProducerCode, ok.Value.Removed.ToString(CultureInfo.InvariantCulture), ok.Value.Transferred, ok.Value.Refused)
            : Facts("removeProducer", command.Request.AgencyUserId);

    private static CommandAuditFacts Facts(string action, Guid? userId, Result<AgencyUserResponse>? result, string? codes) =>
        result is { IsSuccess: true } ok ? Facts(action, ok.Value.User, codes, null, 0, 0) : Facts(action, userId);

    private static CommandAuditFacts Facts(string action, Guid? userId) =>
        new() { ObjectRef = userId is { } id ? new ObjectRef(ModuleCode.PTY, "AgencyUser", id.ToString("D")) : null, BusinessKeys = userId is { } u ? BusinessKeys.Empty.With("agencyUserId", u.ToString("D")) : BusinessKeys.Empty };

    private static CommandAuditFacts Facts(string action, AgencyUserView user, string? codes, string? removed, int transferred, int refused) =>
        new()
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, "AgencyUser", user.AgencyUserId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("agencyUserId", user.AgencyUserId.ToString("D")).With("intermediaryId", user.IntermediaryId.Value.ToString("D")),
            Changes = AuditDiff.Compute(
                new { action = (string?)null },
                new
                {
                    action,
                    role = PorWire.Of(user.Role),
                    status = PorWire.Of(user.Status),
                    codes = codes ?? string.Join(',', user.ProducerCodes),
                    removed,
                    transferred = transferred.ToString(CultureInfo.InvariantCulture),
                    refused = refused.ToString(CultureInfo.InvariantCulture),
                }),
        };
}
