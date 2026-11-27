using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Services;

/// <summary>
/// The in-process contract <see cref="IPartyAccountService"/> (BO-CHN-01): the reads CHN needs to resolve "which accounts can this
/// customer see" (search by held or current-member party, get with members). The commands stay on the REST facade until a consumer needs them.
/// </summary>
internal sealed class PartyAccountService(PartyDbContext db, PartyProtection protection, RequestContext context) : IPartyAccountService
{
    public async Task<AccountSearchPage> SearchAsync(
        string? cursor = null, int? limit = null, PartyId? partyId = null, AccountSearchStatus? status = null, AccountNumber? accountNumber = null,
        CancellationToken cancellationToken = default)
    {
        var offset = PartySearch.DecodeCursor(cursor) ?? throw new DomainException(DomainError.Of(ModuleCode.PTY, "VALIDATION", "The cursor is malformed."));
        var take = Math.Clamp(limit ?? 25, 1, 200);
        var legalEntity = protection.Current(context);
        var query = db.Accounts.AsNoTracking().Where(a => a.LegalEntityId == legalEntity);
        if (partyId is { } party)
        {
            query = query.Where(a => a.HolderPartyId == party || db.AccountMembers.Any(m => m.AccountId == a.AccountId && m.PartyId == party && m.RemovedAt == null));
        }

        if (status is { } s)
        {
            var code = s.ToString().ToUpperInvariant();
            query = query.Where(a => a.Status == code);
        }

        if (accountNumber is { } number)
        {
            var text = number.Value;
            query = query.Where(a => a.AccountNumber == text);
        }

        var rows = await query.OrderBy(a => a.AccountNumber).Skip(offset).Take(take + 1)
            .Select(a => new { Account = a, MemberCount = db.AccountMembers.Count(m => m.AccountId == a.AccountId && m.RemovedAt == null) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new AccountSearchPage
        {
            Items = [.. rows.Take(take).Select(r => new AccountSearchItem
            {
                AccountId = r.Account.AccountId,
                AccountNumber = AccountNumber.Parse(r.Account.AccountNumber),
                AccountType = Enum.Parse<AccountSearchItem.AccountTypeValue>(r.Account.AccountType, ignoreCase: true),
                Name = r.Account.Name,
                Status = Enum.Parse<AccountSearchItem.StatusValue>(r.Account.Status, ignoreCase: true),
                HolderPartyId = r.Account.HolderPartyId,
                MemberCount = r.MemberCount,
            })],
            NextCursor = rows.Count > take ? PartySearch.EncodeCursor(offset + take) : null,
            Limit = take,
        };
    }

    public async Task<AccountGetResponse> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var account = Guid.TryParse(id, out var guid)
            ? await AccountSupport.LoadAsync(db, protection.Current(context), new AccountId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return account is null
            ? throw new DomainException(AccountSupport.NotFound())
            : new AccountGetResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) };
    }

    public Task<AccountAddMemberResponse> AddMemberAsync(AccountAddMemberRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.addMember");

    public Task<AccountCreateResponse> CreateAsync(AccountCreateRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.create");

    public Task<AccountMergeResponse> MergeAsync(AccountMergeRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.merge");

    public Task<AccountRemoveMemberResponse> RemoveMemberAsync(AccountRemoveMemberRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.removeMember");

    public Task<AccountRequestPolicyMoveResponse> RequestPolicyMoveAsync(AccountRequestPolicyMoveRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.requestPolicyMove");

    public Task<AccountSetHolderResponse> SetHolderAsync(AccountSetHolderRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.setHolder");

    public Task<AccountSplitResponse> SplitAsync(AccountSplitRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.split");

    public Task<AccountWithdrawResponse> WithdrawAsync(AccountWithdrawRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Account.withdraw");
}
