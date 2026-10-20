using CoreIns.Modules.Compliance.Api;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Compliance.Complaints;

/// <summary>REST facade of <c>cmp.Complaint.*</c> (contracts/openapi/cmp.yaml): thin, commands go through the pipeline.</summary>
[ApiController]
#pragma warning disable CA1822 // Not static: ASP.NET does not route static controller methods.
internal sealed class ComplaintsController : ControllerBase
{
    private const string P = "cmp.Complaint.";

    private async Task<IResult> RunHandler<TCommand, TResponse>(TCommand command, ICommandHandler<TCommand, TResponse> handler, CancellationToken ct)
        where TCommand : ICommand<TResponse> =>
        (await handler.HandleAsync(command, ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/complaints")]
    [Authorize(Policy = P + "create")]
    public Task<IResult> CreateAsync([FromBody] ComplaintCreateRequest request, [FromServices] ICommandHandler<CreateComplaint, ComplaintCreateResponse> handler, CancellationToken ct) =>
        RunHandler(new CreateComplaint(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/classify")]
    [Authorize(Policy = P + "classify")]
    public Task<IResult> ClassifyAsync([FromBody] ComplaintClassifyRequest request, [FromServices] ICommandHandler<ClassifyComplaint, ComplaintClassifyResponse> handler, CancellationToken ct) =>
        RunHandler(new ClassifyComplaint(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/link")]
    [Authorize(Policy = P + "link")]
    public Task<IResult> LinkAsync([FromBody] ComplaintLinkRequest request, [FromServices] ICommandHandler<LinkComplaint, ComplaintLinkResponse> handler, CancellationToken ct) =>
        RunHandler(new LinkComplaint(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/acknowledge")]
    [Authorize(Policy = P + "acknowledge")]
    public Task<IResult> AcknowledgeAsync([FromBody] ComplaintAcknowledgeRequest request, [FromServices] ICommandHandler<AcknowledgeComplaint, ComplaintAcknowledgeResponse> handler, CancellationToken ct) =>
        RunHandler(new AcknowledgeComplaint(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/add-action")]
    [Authorize(Policy = P + "addAction")]
    public Task<IResult> AddActionAsync([FromBody] ComplaintAddActionRequest request, [FromServices] ICommandHandler<AddComplaintAction, ComplaintAddActionResponse> handler, CancellationToken ct) =>
        RunHandler(new AddComplaintAction(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/submit-reply")]
    [Authorize(Policy = P + "submitReply")]
    public Task<IResult> SubmitReplyAsync([FromBody] ComplaintSubmitReplyRequest request, [FromServices] ICommandHandler<SubmitComplaintReply, ComplaintSubmitReplyResponse> handler, CancellationToken ct) =>
        RunHandler(new SubmitComplaintReply(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/approve-reply")]
    [Authorize(Policy = P + "approveReply")]
    public Task<IResult> ApproveReplyAsync([FromBody] ComplaintApproveReplyRequest request, [FromServices] ICommandHandler<ApproveComplaintReply, ComplaintApproveReplyResponse> handler, CancellationToken ct) =>
        RunHandler(new ApproveComplaintReply(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/escalate")]
    [Authorize(Policy = P + "escalate")]
    public Task<IResult> EscalateAsync([FromBody] ComplaintEscalateRequest request, [FromServices] ICommandHandler<EscalateComplaint, ComplaintEscalateResponse> handler, CancellationToken ct) =>
        RunHandler(new EscalateComplaint(request), handler, ct);

    [HttpPost("api/cmp/v1/complaints/close")]
    [Authorize(Policy = P + "close")]
    public Task<IResult> CloseAsync([FromBody] ComplaintCloseRequest request, [FromServices] ICommandHandler<CloseComplaint, ComplaintCloseResponse> handler, CancellationToken ct) =>
        RunHandler(new CloseComplaint(request), handler, ct);

    [HttpGet("api/cmp/v1/complaints/query")]
    [Authorize(Policy = P + "query")]
    public async Task<IResult> QueryAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? status, [FromServices] ComplaintReader reader, CancellationToken ct)
    {
        var (items, next, take) = await reader.QueryAsync(cursor, limit, status, ct).ConfigureAwait(false);
        return Results.Ok(new ComplaintQueryPage
        {
            Items = [.. items.Select(c => { var (complaint, number, deadlines) = ComplaintViews.Of(c); return new ComplaintQueryItem { Complaint = complaint, Number = number, Deadlines = deadlines }; })],
            NextCursor = next,
            Limit = take,
        });
    }

    [HttpGet("api/cmp/v1/complaints/procedure-text")]
    [Authorize(Policy = P + "procedureText")]
    public IResult ProcedureText([FromQuery] string? entity, [FromQuery] string? language)
    {
        _ = entity; // TODO(hardening): one published procedure per legal entity.
        return Results.Ok(ComplaintReader.ProcedureText(language));
    }

    [HttpGet("api/cmp/v1/complaints/{id}")]
    [Authorize(Policy = P + "get")]
    public async Task<IResult> GetAsync(string id, [FromServices] ComplaintReader reader, CancellationToken ct)
    {
        var found = Guid.TryParse(id, out var guid) ? await reader.GetAsync(guid, ct).ConfigureAwait(false) : null;
        if (found is null)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The complaint does not exist in your legal entity."), HttpContext);
        }

        var (complaint, number, deadlines) = ComplaintViews.Of(found);
        return Results.Ok(new ComplaintGetResponse { Complaint = complaint, Number = number, Deadlines = deadlines });
    }
}
