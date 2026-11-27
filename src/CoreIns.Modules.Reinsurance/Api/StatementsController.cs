using CoreIns.Modules.Reinsurance.Accounting;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>Permission names of the reinsurer statement operations (BO-RI-14), granted in <c>permissions/ri.json</c>.</summary>
internal static class StatementPermissions
{
    public const string Generate = "ri.Statement.generate";
    public const string Submit = "ri.Statement.submit";
    public const string Approve = "ri.Statement.approve";
    public const string Issue = "ri.Statement.issue";
    public const string RecordResponse = "ri.Statement.recordResponse";
    public const string Reissue = "ri.Statement.reissue";
}

/// <summary>REST facade of the reinsurer statements (BO-RI-14, contracts/openapi/ri.yaml).</summary>
[ApiController]
[Route("api/ri/v1/statements")]
internal sealed class StatementsController : ControllerBase
{
    /// <summary>ri.Statement.generate.</summary>
    [HttpPost("generate")]
    [Authorize(Policy = StatementPermissions.Generate)]
    public async Task<IResult> GenerateAsync(
        [FromBody] StatementGenerateRequest request, [FromServices] ICommandHandler<GenerateStatement, StatementGenerateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new GenerateStatement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Statement.submit.</summary>
    [HttpPost("submit")]
    [Authorize(Policy = StatementPermissions.Submit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] StatementSubmitRequest request, [FromServices] ICommandHandler<SubmitStatement, StatementSubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitStatement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Statement.approve.</summary>
    [HttpPost("approve")]
    [Authorize(Policy = StatementPermissions.Approve)]
    public async Task<IResult> ApproveAsync(
        [FromBody] StatementApproveRequest request, [FromServices] ICommandHandler<ApproveStatement, StatementApproveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveStatement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Statement.issue.</summary>
    [HttpPost("issue")]
    [Authorize(Policy = StatementPermissions.Issue)]
    public async Task<IResult> IssueAsync(
        [FromBody] StatementIssueRequest request, [FromServices] ICommandHandler<IssueStatement, StatementIssueResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new IssueStatement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Statement.recordResponse.</summary>
    [HttpPost("record-response")]
    [Authorize(Policy = StatementPermissions.RecordResponse)]
    public async Task<IResult> RecordResponseAsync(
        [FromBody] StatementRecordResponseRequest request, [FromServices] ICommandHandler<RecordStatementResponse, StatementRecordResponseResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordStatementResponse(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Statement.reissue.</summary>
    [HttpPost("reissue")]
    [Authorize(Policy = StatementPermissions.Reissue)]
    public async Task<IResult> ReissueAsync(
        [FromBody] StatementReissueRequest request, [FromServices] ICommandHandler<ReissueStatement, StatementReissueResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReissueStatement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
