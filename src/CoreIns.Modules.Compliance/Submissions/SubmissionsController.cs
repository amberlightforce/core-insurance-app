using CoreIns.Modules.Compliance.Api;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Compliance.Submissions;

/// <summary>REST facade of <c>cmp.Submission.*</c> (contracts/openapi/cmp.yaml): thin, commands go through the pipeline.</summary>
[ApiController]
#pragma warning disable CA1822 // Not static: ASP.NET does not route static controller methods.
internal sealed class SubmissionsController : ControllerBase
{
    private const string P = "cmp.Submission.";

    private async Task<IResult> RunHandler<TCommand, TResponse>(TCommand command, ICommandHandler<TCommand, TResponse> handler, CancellationToken ct)
        where TCommand : ICommand<TResponse> =>
        (await handler.HandleAsync(command, ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/submissions")]
    [Authorize(Policy = P + "create")]
    public Task<IResult> CreateAsync([FromBody] SubmissionCreateRequest request, [FromServices] ICommandHandler<CreateSubmission, SubmissionCreateResponse> handler, CancellationToken ct) =>
        RunHandler(new CreateSubmission(request), handler, ct);

    [HttpPost("api/cmp/v1/submissions/attach-content")]
    [Authorize(Policy = P + "attachContent")]
    public Task<IResult> AttachContentAsync([FromBody] SubmissionAttachContentRequest request, [FromServices] ICommandHandler<AttachSubmissionContent, SubmissionAttachContentResponse> handler, CancellationToken ct) =>
        RunHandler(new AttachSubmissionContent(request), handler, ct);

    [HttpPost("api/cmp/v1/submissions/comment")]
    [Authorize(Policy = P + "comment")]
    public Task<IResult> CommentAsync([FromBody] SubmissionCommentRequest request, [FromServices] ICommandHandler<CommentSubmission, SubmissionCommentResponse> handler, CancellationToken ct) =>
        RunHandler(new CommentSubmission(request), handler, ct);

    [HttpPost("api/cmp/v1/submissions/approve")]
    [Authorize(Policy = P + "approve")]
    public Task<IResult> ApproveAsync([FromBody] SubmissionApproveRequest request, [FromServices] ICommandHandler<ApproveSubmission, SubmissionApproveResponse> handler, CancellationToken ct) =>
        RunHandler(new ApproveSubmission(request), handler, ct);

    [HttpPost("api/cmp/v1/submissions/record-filing")]
    [Authorize(Policy = P + "recordFiling")]
    public Task<IResult> RecordFilingAsync([FromBody] SubmissionRecordFilingRequest request, [FromServices] ICommandHandler<RecordSubmissionFiling, SubmissionRecordFilingResponse> handler, CancellationToken ct) =>
        RunHandler(new RecordSubmissionFiling(request), handler, ct);

    [HttpPost("api/cmp/v1/submissions/record-response")]
    [Authorize(Policy = P + "recordResponse")]
    public Task<IResult> RecordResponseAsync([FromBody] SubmissionRecordResponseRequest request, [FromServices] ICommandHandler<RecordSubmissionResponse, SubmissionRecordResponseResponse> handler, CancellationToken ct) =>
        RunHandler(new RecordSubmissionResponse(request), handler, ct);

    [HttpPost("api/cmp/v1/submissions/evidence-pack")]
    [Authorize(Policy = P + "evidencePack")]
    public Task<IResult> EvidencePackAsync([FromBody] SubmissionEvidencePackRequest request, [FromServices] ICommandHandler<ExportSubmissionEvidencePack, SubmissionEvidencePackResponse> handler, CancellationToken ct) =>
        RunHandler(new ExportSubmissionEvidencePack(request), handler, ct);

    [HttpGet("api/cmp/v1/submissions/query")]
    [Authorize(Policy = P + "query")]
    public async Task<IResult> QueryAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? status, [FromQuery] string? submissionType, [FromQuery] string? regulator,
        [FromServices] SubmissionEngine engine, CancellationToken ct)
    {
        var (items, next, take) = await engine.QueryAsync(cursor, limit, status, submissionType, regulator, ct).ConfigureAwait(false);
        return Results.Ok(new SubmissionQueryPage { Items = items, NextCursor = next, Limit = take });
    }

    [HttpGet("api/cmp/v1/submissions/archive-completeness")]
    [Authorize(Policy = P + "archiveCompleteness")]
    public async Task<IResult> ArchiveCompletenessAsync([FromQuery] string? from, [FromQuery] string? to, [FromServices] SubmissionEngine engine, CancellationToken ct) =>
        (await engine.ArchiveCompletenessAsync(from, to, ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("api/cmp/v1/submissions/{id}")]
    [Authorize(Policy = P + "get")]
    public async Task<IResult> GetAsync(string id, [FromServices] SubmissionEngine engine, CancellationToken ct)
    {
        var found = Guid.TryParse(id, out var guid) ? await engine.FindViewAsync(guid, ct).ConfigureAwait(false) : null;
        return found is null
            ? HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The submission does not exist in your legal entity."), HttpContext)
            : Results.Ok(new SubmissionGetResponse { Submission = found });
    }
}
