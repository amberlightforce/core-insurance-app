using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Account.create</c> (BO-PTY-04).</summary>
internal sealed record CreateAccount(AccountCreateRequest Request) : ICommand<AccountCreateResponse>;

/// <summary><c>pty.Account.setHolder</c>.</summary>
internal sealed record SetAccountHolder(AccountSetHolderRequest Request) : ICommand<AccountSetHolderResponse>;

/// <summary><c>pty.Account.addMember</c>.</summary>
internal sealed record AddAccountMember(AccountAddMemberRequest Request) : ICommand<AccountAddMemberResponse>;

/// <summary><c>pty.Account.removeMember</c>.</summary>
internal sealed record RemoveAccountMember(AccountRemoveMemberRequest Request) : ICommand<AccountRemoveMemberResponse>;

/// <summary><c>pty.Account.withdraw</c>.</summary>
internal sealed record WithdrawAccount(AccountWithdrawRequest Request) : ICommand<AccountWithdrawResponse>;

internal sealed class CreateAccountValidator : AbstractValidator<CreateAccount>
{
    public CreateAccountValidator()
    {
        RuleFor(c => c.Request.HolderPartyId.Value).NotEmpty();
        RuleFor(c => c.Request.Name).MaximumLength(200);
    }
}

internal sealed class SetAccountHolderValidator : AbstractValidator<SetAccountHolder>
{
    public SetAccountHolderValidator()
    {
        RuleFor(c => c.Request.AccountId.Value).NotEmpty();
        RuleFor(c => c.Request.HolderPartyId.Value).NotEmpty();
    }
}

internal sealed class AddAccountMemberValidator : AbstractValidator<AddAccountMember>
{
    public AddAccountMemberValidator()
    {
        RuleFor(c => c.Request.AccountId.Value).NotEmpty();
        RuleFor(c => c.Request.PartyId.Value).NotEmpty();
        RuleFor(c => c.Request.MemberRole).NotEmpty().MaximumLength(64);
    }
}

internal sealed class RemoveAccountMemberValidator : AbstractValidator<RemoveAccountMember>
{
    public RemoveAccountMemberValidator()
    {
        RuleFor(c => c.Request.AccountId.Value).NotEmpty();
        RuleFor(c => c.Request.PartyId.Value).NotEmpty();
    }
}

internal sealed class WithdrawAccountValidator : AbstractValidator<WithdrawAccount>
{
    public WithdrawAccountValidator()
    {
        RuleFor(c => c.Request.AccountId.Value).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>Account states and catalogues (REQ-PTY-004, D-CON-05).</summary>
internal static class AccountCatalogue
{
    public const string Pending = "PENDING";
    public const string Active = "ACTIVE";
    public const string Withdrawn = "WITHDRAWN";
    public const string Household = "HOUSEHOLD";
    public const string PolicyholderRole = "POLICYHOLDER";

    public static IReadOnlySet<string> MemberRoles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SPOUSE", "PARTNER", "CHILD", "DEPENDANT", "EMPLOYEE", "OTHER",
    };

    public static string TypeCode(AccountCreateRequest.AccountTypeValue type) => type switch
    {
        AccountCreateRequest.AccountTypeValue.Individual => "INDIVIDUAL",
        AccountCreateRequest.AccountTypeValue.Household => Household,
        _ => "BUSINESS",
    };
}

/// <summary>Shared guards, view mapping and events of the account commands.</summary>
internal static class AccountSupport
{
    public static DomainError NotFound() => DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The account does not exist.");

    public static DomainError Withdrawn() => new(ErrorCode.For(ModuleCode.PTY, "ACCOUNT-WITHDRAWN"), "The account was withdrawn and cannot be changed.");

    /// <summary>
    /// A holder counts as verified when it has a current identifier that did not fail or expire (D-CON-05).
    /// TODO(hardening): require DOCUMENT_VERIFIED or REGISTRY_VERIFIED once a verification workflow can raise the status; self-declared passes today.
    /// </summary>
    public static Task<bool> IsVerifiedAsync(PartyDbContext db, PartyId party, CancellationToken cancellationToken) =>
        db.Identifiers.AnyAsync(
            i => i.PartyId == party && i.RecordedTo == null && i.ValidTo == null
                && (i.VerificationStatus == "SELF_DECLARED" || i.VerificationStatus == "DOCUMENT_VERIFIED" || i.VerificationStatus == "REGISTRY_VERIFIED"),
            cancellationToken);

    public static async Task<AccountRow?> LoadAsync(PartyDbContext db, LegalEntityId legalEntity, AccountId accountId, CancellationToken cancellationToken) =>
        await db.Accounts.SingleOrDefaultAsync(a => a.LegalEntityId == legalEntity && a.AccountId == accountId, cancellationToken).ConfigureAwait(false);

    public static async Task<AccountView> ViewAsync(PartyDbContext db, AccountRow a, CancellationToken cancellationToken)
    {
        var members = await db.AccountMembers.AsNoTracking()
            .Where(m => m.AccountId == a.AccountId && m.RemovedAt == null)
            .OrderBy(m => m.AddedAt).ThenBy(m => m.MemberId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new AccountView
        {
            AccountId = a.AccountId,
            AccountNumber = AccountNumber.Parse(a.AccountNumber),
            AccountType = Enum.Parse<AccountView.AccountTypeValue>(a.AccountType, ignoreCase: true),
            Name = a.Name,
            Status = Enum.Parse<AccountView.StatusValue>(a.Status, ignoreCase: true),
            HolderPartyId = a.HolderPartyId,
            Members = [.. members.Select(m => new AccountMemberView { PartyId = m.PartyId, MemberRole = m.MemberRole, AddedOn = m.AddedOn })],
            WithdrawnOn = a.WithdrawnOn,
            WithdrawReason = a.WithdrawReason,
        };
    }

    public static PartyRoleRow HolderRole(AccountRow a, PartyId holder, BusinessDate from, Instant now, string actor) => new()
    {
        RowId = Guid.CreateVersion7(), RoleId = Guid.CreateVersion7(), PartyId = holder, LegalEntityId = a.LegalEntityId, RoleType = AccountCatalogue.PolicyholderRole,
        ContextModule = nameof(ModuleCode.PTY), ContextType = "Account", ContextId = a.AccountId.Value.ToString("D"), ValidFrom = from, ValidTo = null,
        RecordedFrom = now, CreatedBy = actor,
    };

    /// <summary>Ends the current holder role of the account (closes it, or drops it when it never took effect) and publishes the change.</summary>
    public static async Task EndHolderRoleAsync(PartyDbContext db, IEventPublisher events, AccountRow a, PartyId holder, BusinessDate to, Instant now, string actor, CancellationToken cancellationToken)
    {
        var id = a.AccountId.Value.ToString("D");
        var current = await db.Roles.SingleOrDefaultAsync(
            r => r.PartyId == holder && r.RoleType == AccountCatalogue.PolicyholderRole && r.ContextType == "Account" && r.ContextId == id && r.RecordedTo == null && r.ValidTo == null,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        current.RecordedTo = now;
        if (to <= current.ValidFrom)
        {
            return;
        }

        var successor = new PartyRoleRow
        {
            RowId = Guid.CreateVersion7(), RoleId = current.RoleId, PartyId = current.PartyId, LegalEntityId = current.LegalEntityId, RoleType = current.RoleType,
            ContextModule = current.ContextModule, ContextType = current.ContextType, ContextId = current.ContextId, ValidFrom = current.ValidFrom, ValidTo = to,
            RecordedFrom = now, CreatedBy = actor,
        };
        db.Roles.Add(successor);
        RoleSupport.PublishRole(events, successor);
    }

    public static void PublishStatus(IEventPublisher events, AccountRow a, string oldStatus) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(AccountStatusChangedV1.Descriptor), "Account", a.AccountId.Value.ToString(),
            new AccountStatusChangedV1 { AccountId = a.AccountId, OldStatus = oldStatus, NewStatus = a.Status },
            BusinessKeys.Empty.With("accountId", a.AccountId.Value.ToString())));
}

/// <summary>
/// Creates an account for a holder party: number from the PLT numbering service, status PENDING until the holder is verified (D-CON-05),
/// the POLICYHOLDER role in the account's context, <c>AccountCreated</c> (and <c>AccountStatusChanged</c> when it starts ACTIVE).
/// </summary>
internal sealed class CreateAccountHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, INumberingService numbering)
    : ICommandHandler<CreateAccount, AccountCreateResponse>
{
    public async Task<Result<AccountCreateResponse>> HandleAsync(CreateAccount command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var holder = request.HolderPartyId;
        if (await RoleSupport.GuardPartyAsync(db, legalEntity, holder, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var now = clock.Now;
        var today = RoleSupport.Today(clock);
        var actor = context.Actor.ToString();
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Account, today), cancellationToken).ConfigureAwait(false);
        var verified = await AccountSupport.IsVerifiedAsync(db, holder, cancellationToken).ConfigureAwait(false);
        var account = new AccountRow
        {
            AccountId = AccountId.New(), LegalEntityId = legalEntity, AccountNumber = number.Value, AccountType = AccountCatalogue.TypeCode(request.AccountType),
            Name = request.Name, Status = AccountCatalogue.Pending, HolderPartyId = holder, RecordVersion = 1, CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
        };
        db.Accounts.Add(account);
        var role = AccountSupport.HolderRole(account, holder, today, now, actor);
        db.Roles.Add(role);

        var party = await db.Parties.SingleAsync(p => p.PartyId == holder, cancellationToken).ConfigureAwait(false);
        if (party.Status == Codes.Of(PartyStatus.Prospect))
        {
            party.Status = Codes.Of(PartyStatus.Active);
            party.RecordVersion++;
            party.UpdatedAt = now;
        }

        if (verified)
        {
            account.Status = AccountCatalogue.Active;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(AccountCreatedV1.Descriptor), "Account", account.AccountId.Value.ToString(),
            new AccountCreatedV1 { AccountId = account.AccountId, AccountNumber = AccountNumber.Parse(account.AccountNumber), AccountType = account.AccountType, HolderPartyId = holder },
            BusinessKeys.Empty.With("accountId", account.AccountId.Value.ToString())));
        RoleSupport.PublishRole(events, role);
        if (verified)
        {
            AccountSupport.PublishStatus(events, account, AccountCatalogue.Pending);
        }

        return new AccountCreateResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Changes the holder: the previous holder's POLICYHOLDER role ends today and the new holder's starts today, so exactly one holder is current.
/// A holder that is not verified puts an ACTIVE account back to PENDING.
/// </summary>
internal sealed class SetAccountHolderHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<SetAccountHolder, AccountSetHolderResponse>
{
    public async Task<Result<AccountSetHolderResponse>> HandleAsync(SetAccountHolder command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var account = await AccountSupport.LoadAsync(db, legalEntity, request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AccountSupport.NotFound();
        }

        if (account.Status == AccountCatalogue.Withdrawn)
        {
            return AccountSupport.Withdrawn();
        }

        var holder = request.HolderPartyId;
        if (holder == account.HolderPartyId)
        {
            return new AccountSetHolderResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) };
        }

        if (await RoleSupport.GuardPartyAsync(db, legalEntity, holder, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var now = clock.Now;
        var today = RoleSupport.Today(clock);
        var actor = context.Actor.ToString();
        await AccountSupport.EndHolderRoleAsync(db, events, account, account.HolderPartyId, today, now, actor, cancellationToken).ConfigureAwait(false);

        // A current member that becomes the holder leaves the member list.
        var asMember = await db.AccountMembers.SingleOrDefaultAsync(m => m.AccountId == account.AccountId && m.PartyId == holder && m.RemovedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (asMember is not null)
        {
            asMember.RemovedAt = now;
        }

        var role = AccountSupport.HolderRole(account, holder, today, now, actor);
        db.Roles.Add(role);
        var party = await db.Parties.SingleAsync(p => p.PartyId == holder, cancellationToken).ConfigureAwait(false);
        if (party.Status == Codes.Of(PartyStatus.Prospect))
        {
            party.Status = Codes.Of(PartyStatus.Active);
            party.RecordVersion++;
            party.UpdatedAt = now;
        }

        var old = account.Status;
        account.HolderPartyId = holder;
        account.Status = await AccountSupport.IsVerifiedAsync(db, holder, cancellationToken).ConfigureAwait(false) ? AccountCatalogue.Active : AccountCatalogue.Pending;
        account.RecordVersion++;
        account.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        RoleSupport.PublishRole(events, role);
        if (old != account.Status)
        {
            AccountSupport.PublishStatus(events, account, old);
        }

        return new AccountSetHolderResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Adds a member: not the holder, not already a member, within the member limit, and a HOUSEHOLD member belongs to one household only.
/// </summary>
internal sealed class AddAccountMemberHandler(
    PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection, IOptions<PartyOptions> options)
    : ICommandHandler<AddAccountMember, AccountAddMemberResponse>
{
    public async Task<Result<AccountAddMemberResponse>> HandleAsync(AddAccountMember command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        if (!AccountCatalogue.MemberRoles.Contains(request.MemberRole))
        {
            return RoleSupport.Invalid("memberRole", "MEMBER_ROLE", $"Unknown member role {request.MemberRole}.");
        }

        var account = await AccountSupport.LoadAsync(db, legalEntity, request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AccountSupport.NotFound();
        }

        if (account.Status == AccountCatalogue.Withdrawn)
        {
            return AccountSupport.Withdrawn();
        }

        var party = request.PartyId;
        if (await RoleSupport.GuardPartyAsync(db, legalEntity, party, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        if (party == account.HolderPartyId)
        {
            return RoleSupport.Invalid("partyId", "MEMBER_IS_HOLDER", "The holder is not a member of its own account.");
        }

        var current = await db.AccountMembers.Where(m => m.AccountId == account.AccountId && m.RemovedAt == null).Select(m => m.PartyId).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current.Contains(party))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "MEMBER-DUPLICATE"), "The party is already a member of this account.");
        }

        if (current.Count >= options.Value.AccountMemberLimit)
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "MEMBER-LIMIT"), $"An account can have at most {options.Value.AccountMemberLimit} members.");
        }

        if (account.AccountType == AccountCatalogue.Household
            && await db.AccountMembers.AnyAsync(
                m => m.PartyId == party && m.RemovedAt == null && m.AccountId != account.AccountId
                    && db.Accounts.Any(o => o.AccountId == m.AccountId && o.AccountType == AccountCatalogue.Household && o.Status != AccountCatalogue.Withdrawn),
                cancellationToken).ConfigureAwait(false))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "MEMBER-IN-USE"), "The party already belongs to another household account.");
        }

        var now = clock.Now;
        db.AccountMembers.Add(new AccountMemberRow
        {
            MemberId = Guid.CreateVersion7(), AccountId = account.AccountId, LegalEntityId = legalEntity, PartyId = party, MemberRole = request.MemberRole,
            AddedOn = RoleSupport.Today(clock), AddedAt = now, CreatedBy = context.Actor.ToString(),
        });
        account.RecordVersion++;
        account.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new AccountAddMemberResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Removes a current member (the row is kept with <c>removed_at</c>).</summary>
internal sealed class RemoveAccountMemberHandler(PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection)
    : ICommandHandler<RemoveAccountMember, AccountRemoveMemberResponse>
{
    public async Task<Result<AccountRemoveMemberResponse>> HandleAsync(RemoveAccountMember command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var account = await AccountSupport.LoadAsync(db, legalEntity, request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AccountSupport.NotFound();
        }

        if (account.Status == AccountCatalogue.Withdrawn)
        {
            return AccountSupport.Withdrawn();
        }

        var party = request.PartyId;
        var member = await db.AccountMembers.SingleOrDefaultAsync(m => m.AccountId == account.AccountId && m.PartyId == party && m.RemovedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party is not a member of this account.");
        }

        var now = clock.Now;
        member.RemovedAt = now;
        account.RecordVersion++;
        account.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new AccountRemoveMemberResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Withdraws an account (terminal): refused while the holder has a policy scheduled, in force or pending cancellation (read through the
/// POL in-process contract). Ends the holder role, removes the members and publishes <c>AccountStatusChanged</c>.
/// </summary>
internal sealed class WithdrawAccountHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, IPolicyPolicyService policies)
    : ICommandHandler<WithdrawAccount, AccountWithdrawResponse>
{
    public async Task<Result<AccountWithdrawResponse>> HandleAsync(WithdrawAccount command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var account = await AccountSupport.LoadAsync(db, legalEntity, request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AccountSupport.NotFound();
        }

        if (account.Status == AccountCatalogue.Withdrawn)
        {
            return AccountSupport.Withdrawn();
        }

        if (await HasActivePolicyAsync(account.HolderPartyId, cancellationToken).ConfigureAwait(false))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "ACCOUNT-HAS-JOBS"), "The holder still has a policy in force, scheduled or pending cancellation.");
        }

        var now = clock.Now;
        var today = RoleSupport.Today(clock);
        var actor = context.Actor.ToString();
        await AccountSupport.EndHolderRoleAsync(db, events, account, account.HolderPartyId, today, now, actor, cancellationToken).ConfigureAwait(false);
        foreach (var member in await db.AccountMembers.Where(m => m.AccountId == account.AccountId && m.RemovedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            member.RemovedAt = now;
        }

        var old = account.Status;
        account.Status = AccountCatalogue.Withdrawn;
        account.WithdrawnOn = today;
        account.WithdrawReason = request.Reason;
        account.RecordVersion++;
        account.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AccountSupport.PublishStatus(events, account, old);
        return new AccountWithdrawResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>
    /// TODO(hardening): policies are found by the holder as insured party (pol.Policy.searchByCriteria); search by account id once POL
    /// exposes it, and include policies where the holder is only the payer.
    /// </summary>
    private async Task<bool> HasActivePolicyAsync(PartyId holder, CancellationToken cancellationToken)
    {
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var result = await policies.SearchByCriteriaAsync(new PolicySearchCriteria { InsuredPartyId = holder.Value }, null, cursor, 200, cancellationToken).ConfigureAwait(false);
            if (result.Items.Any(p => p.Status is TermStateCode.Scheduled or TermStateCode.InForce or TermStateCode.PendingCancellation))
            {
                return true;
            }

            cursor = result.NextCursor;
            if (cursor is null)
            {
                return false;
            }
        }

        return true; // fail closed when the holder has an implausible number of policies
    }
}

/// <summary>Audit facts of the account commands: the account only, no personal values.</summary>
internal sealed class AccountAuditor :
    ICommandAuditor<CreateAccount, AccountCreateResponse>,
    ICommandAuditor<SetAccountHolder, AccountSetHolderResponse>,
    ICommandAuditor<AddAccountMember, AccountAddMemberResponse>,
    ICommandAuditor<RemoveAccountMember, AccountRemoveMemberResponse>,
    ICommandAuditor<WithdrawAccount, AccountWithdrawResponse>
{
    public CommandAuditFacts Describe(CreateAccount command, Result<AccountCreateResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Account : null);

    public CommandAuditFacts Describe(SetAccountHolder command, Result<AccountSetHolderResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Account : null);

    public CommandAuditFacts Describe(AddAccountMember command, Result<AccountAddMemberResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Account : null);

    public CommandAuditFacts Describe(RemoveAccountMember command, Result<AccountRemoveMemberResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Account : null);

    public CommandAuditFacts Describe(WithdrawAccount command, Result<AccountWithdrawResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Account : null);

    private static CommandAuditFacts Facts(AccountView? account) => account is null
        ? new CommandAuditFacts()
        : new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, "Account", account.AccountId.Value.ToString("D")),
            ObjectNumber = account.AccountNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("accountId", account.AccountId.Value.ToString()),
        };
}
