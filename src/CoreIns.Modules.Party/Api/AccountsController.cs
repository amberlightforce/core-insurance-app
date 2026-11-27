using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.Account.*</c> (BO-PTY-04, REQ-PTY-004).</summary>
[ApiController]
[Route("api/pty/v1/accounts")]
internal sealed class AccountsController : ControllerBase
{
    /// <summary>pty.Account.create → 201.</summary>
    [HttpPost]
    [Authorize(Policy = "pty.Account.create")]
    public async Task<IResult> CreateAsync(
        [FromBody] AccountCreateRequest request, [FromServices] ICommandHandler<CreateAccount, AccountCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateAccount(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pty/v1/accounts/{result.Value.Account.AccountId.Value}", result.Value)
            : HttpResults.Problem(result.Error, HttpContext);
    }

    /// <summary>pty.Account.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "pty.Account.get")]
    public async Task<IResult> GetAsync(
        string id, [FromServices] PartyDbContext db, [FromServices] PartyProtection protection, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "The account id is not a valid identifier."), HttpContext);
        }

        var account = await AccountSupport.LoadAsync(db, protection.Current(context), new AccountId(guid), cancellationToken).ConfigureAwait(false);
        return account is null
            ? HttpResults.Problem(AccountSupport.NotFound(), HttpContext)
            : Results.Ok(new AccountGetResponse { Account = await AccountSupport.ViewAsync(db, account, cancellationToken).ConfigureAwait(false) });
    }

    /// <summary>
    /// pty.Account.search: by holder or current member (<c>partyId</c>), status or account number. This is how CHN resolves
    /// "which accounts can this customer see": the accounts a party holds or belongs to.
    /// </summary>
    [HttpGet("search")]
    [Authorize(Policy = "pty.Account.search")]
    public async Task<IResult> SearchAsync(
        [FromQuery] Guid? partyId, [FromQuery] string? status, [FromQuery] string? accountNumber, [FromQuery] int? limit, [FromQuery] string? cursor,
        [FromServices] PartyDbContext db, [FromServices] PartyProtection protection, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        var offset = PartySearch.DecodeCursor(cursor);
        if (offset is null || limit is < 1 or > 200 || (status is not null && status is not ("PENDING" or "ACTIVE" or "WITHDRAWN")))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "cursor is malformed, limit outside 1..200 or status unknown."), HttpContext);
        }

        var take = limit ?? 25;
        var legalEntity = protection.Current(context);
        var query = db.Accounts.AsNoTracking().Where(a => a.LegalEntityId == legalEntity);
        if (partyId is { } id)
        {
            var party = new PartyId(id);
            query = query.Where(a => a.HolderPartyId == party || db.AccountMembers.Any(m => m.AccountId == a.AccountId && m.PartyId == party && m.RemovedAt == null));
        }

        if (status is not null)
        {
            query = query.Where(a => a.Status == status);
        }

        if (accountNumber is not null)
        {
            query = query.Where(a => a.AccountNumber == accountNumber);
        }

        var rows = await query.OrderBy(a => a.AccountNumber).Skip(offset.Value).Take(take + 1)
            .Select(a => new
            {
                Account = a,
                MemberCount = db.AccountMembers.Count(m => m.AccountId == a.AccountId && m.RemovedAt == null),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = rows.Take(take).Select(r => new AccountSearchItem
        {
            AccountId = r.Account.AccountId,
            AccountNumber = AccountNumber.Parse(r.Account.AccountNumber),
            AccountType = Enum.Parse<AccountSearchItem.AccountTypeValue>(r.Account.AccountType, ignoreCase: true),
            Name = r.Account.Name,
            Status = Enum.Parse<AccountSearchItem.StatusValue>(r.Account.Status, ignoreCase: true),
            HolderPartyId = r.Account.HolderPartyId,
            MemberCount = r.MemberCount,
        }).ToList();
        return Results.Ok(new AccountSearchPage { Items = items, NextCursor = rows.Count > take ? PartySearch.EncodeCursor(offset.Value + take) : null, Limit = take });
    }

    /// <summary>pty.Account.setHolder.</summary>
    [HttpPost("set-holder")]
    [Authorize(Policy = "pty.Account.setHolder")]
    public async Task<IResult> SetHolderAsync(
        [FromBody] AccountSetHolderRequest request, [FromServices] ICommandHandler<SetAccountHolder, AccountSetHolderResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SetAccountHolder(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Account.addMember.</summary>
    [HttpPost("add-member")]
    [Authorize(Policy = "pty.Account.addMember")]
    public async Task<IResult> AddMemberAsync(
        [FromBody] AccountAddMemberRequest request, [FromServices] ICommandHandler<AddAccountMember, AccountAddMemberResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddAccountMember(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Account.removeMember.</summary>
    [HttpPost("remove-member")]
    [Authorize(Policy = "pty.Account.removeMember")]
    public async Task<IResult> RemoveMemberAsync(
        [FromBody] AccountRemoveMemberRequest request, [FromServices] ICommandHandler<RemoveAccountMember, AccountRemoveMemberResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RemoveAccountMember(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.Account.withdraw.</summary>
    [HttpPost("withdraw")]
    [Authorize(Policy = "pty.Account.withdraw")]
    public async Task<IResult> WithdrawAsync(
        [FromBody] AccountWithdrawRequest request, [FromServices] ICommandHandler<WithdrawAccount, AccountWithdrawResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new WithdrawAccount(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
