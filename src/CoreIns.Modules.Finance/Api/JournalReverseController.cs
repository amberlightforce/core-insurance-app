using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace CoreIns.Modules.Finance.Api;

/// <summary>REST facade of <c>fin.Journal.reverse</c> (BO-FIN-02). <c>POST api/fin/v1/journals/reverse</c> with <c>{journalId, reason, date?}</c>.</summary>
[ApiController]
[Route("api/fin/v1/journals/reverse")]
internal sealed class JournalReverseController : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "fin.Journal.reverse")]
    public async Task<IResult> ReverseAsync([FromBody] JournalReverseRequest request, [FromServices] ICommandHandler<ReverseJournal, JournalReverseResponse> handler, CancellationToken cancellationToken)
    {
        if (request.JournalId is not { } id || request.Reason is not { ValueKind: JsonValueKind.String } reason || string.IsNullOrWhiteSpace(reason.GetString()))
        {
            return this.Problem(FinanceHttp.Invalid("journalId and a non-empty reason are required."));
        }

        return (await handler.HandleAsync(new ReverseJournal(id.Value, reason.GetString()!, request.Date), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }
}
