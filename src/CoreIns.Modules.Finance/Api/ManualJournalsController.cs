using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the manual journal operations (the operations' <c>x-permission</c>).</summary>
internal static class ManualJournalPermissions
{
    public const string Create = "fin.ManualJournal.create";
    public const string List = "fin.ManualJournal.list";
    public const string Get = "fin.ManualJournal.get";
    public const string Withdraw = "fin.ManualJournal.withdraw";
}

/// <summary>
/// REST facade of the manual journals (BO-FIN-01a): create a balanced, evidenced DRAFT, list, get and withdraw. Submit and decide
/// (maker-checker posting) are BO-FIN-01b. A manual journal is a proposal; nothing here writes the ledger.
/// </summary>
[ApiController]
[Route("api/fin/v1/manual-journals")]
internal sealed class ManualJournalsController : ControllerBase
{
    /// <summary>fin.ManualJournal.create → 201 with the DRAFT journal (a dry run validates and stores nothing).</summary>
    [HttpPost]
    [Authorize(Policy = ManualJournalPermissions.Create)]
    public async Task<IResult> CreateAsync([FromBody] ManualJournalCreateInput request, [FromServices] ICommandHandler<CreateManualJournal, ManualJournalEnvelope> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateManualJournal(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created) : result.ToHttpResult(HttpContext);
    }

    /// <summary>fin.ManualJournal.withdraw: the maker takes a DRAFT or SUBMITTED journal back.</summary>
    [HttpPost("withdraw")]
    [Authorize(Policy = ManualJournalPermissions.Withdraw)]
    public async Task<IResult> WithdrawAsync([FromBody] ManualJournalWithdrawInput request, [FromServices] ICommandHandler<WithdrawManualJournal, ManualJournalEnvelope> handler, CancellationToken cancellationToken)
    {
        if (request.Id is not { } id)
        {
            return this.Problem(FinanceHttp.Invalid("id is required."));
        }

        return (await handler.HandleAsync(new WithdrawManualJournal(id), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>fin.ManualJournal.list: newest first, optionally by status.</summary>
    [HttpGet]
    [Authorize(Policy = ManualJournalPermissions.List)]
    public async Task<IResult> ListAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? status, [FromServices] ManualJournalReader reader, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > FinanceHttp.MaxLimit || (status is not null && status is not (ManualJournalStatus.Draft or ManualJournalStatus.Submitted or ManualJournalStatus.Approved or ManualJournalStatus.Rejected or ManualJournalStatus.Withdrawn or ManualJournalStatus.Posted)))
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit} and status a manual journal status."));
        }

        var take = limit ?? FinanceHttp.DefaultLimit;
        if (FinanceHttp.LegalEntity(HttpContext) is not (var legalEntity, _))
        {
            return Results.Ok(new OpsPage<ManualJournalEnvelope>([], null, take));
        }

        try
        {
            return Results.Ok(await reader.ListAsync(legalEntity, status, cursor, take, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException)
        {
            return this.Problem(FinanceHttp.Invalid("cursor is malformed."));
        }
    }

    /// <summary>fin.ManualJournal.get by id.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ManualJournalPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] ManualJournalReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid) && FinanceHttp.LegalEntity(HttpContext) is (var legalEntity, _)
            ? await reader.GetAsync(legalEntity, guid, cancellationToken).ConfigureAwait(false)
            : null;
        return found is null
            ? this.Problem(DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The manual journal does not exist."))
            : Results.Ok(found);
    }
}
