using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Inbound;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Work.Api;

/// <summary>REST facade of <c>wrk.InboundDocument.*</c> (contracts/openapi/wrk.yaml): intake with malware scan, manual keying, split, link, redact, export.</summary>
[ApiController]
[Route("api/wrk/v1/inbound-documents")]
internal sealed class InboundDocumentsController : ControllerBase
{
    public const string SubmitPermission = "wrk.InboundDocument.submit";
    public const string GetPermission = "wrk.InboundDocument.get";
    public const string ListPermission = "wrk.InboundDocument.list";
    public const string VerifyPermission = "wrk.InboundDocument.verify";
    public const string SplitPermission = "wrk.InboundDocument.split";
    public const string LinkPermission = "wrk.InboundDocument.link";
    public const string RelinkPermission = "wrk.InboundDocument.relink";
    public const string RejectPermission = "wrk.InboundDocument.reject";
    public const string RedactPermission = "wrk.InboundDocument.redact";
    public const string ReprocessPermission = "wrk.InboundDocument.reprocess";
    public const string ExportPermission = "wrk.InboundDocument.export";

    /// <summary>wrk.InboundDocument.submit.</summary>
    [HttpPost("submit")]
    [Authorize(Policy = SubmitPermission)]
    public async Task<IResult> SubmitAsync(
        [FromBody] InboundDocumentSubmitRequest request, [FromServices] ICommandHandler<SubmitInbound, InboundDocumentSubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = GetPermission)]
    public async Task<IResult> GetAsync(string id, [FromServices] InboundService inbound, CancellationToken cancellationToken) =>
        (await inbound.GetAsync(id, cancellationToken).ConfigureAwait(false)).Map(v => new InboundDocumentGetResponse { Document = v.ToElement() }).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.list (optional <c>state</c> and <c>objectRef</c> filters).</summary>
    [HttpGet]
    [Authorize(Policy = ListPermission)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? state, [FromQuery] string? objectRef, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] InboundService inbound,
        CancellationToken cancellationToken) =>
        (await inbound.ListAsync(state, objectRef, cursor, limit, cancellationToken).ConfigureAwait(false))
            .Map(p => new InboundDocumentListPage { Items = [.. p.Items.Select(v => new InboundDocumentListItem { Document = v.ToElement() })], NextCursor = p.NextCursor, Limit = p.Limit })
            .ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.verify.</summary>
    [HttpPost("verify")]
    [Authorize(Policy = VerifyPermission)]
    public async Task<IResult> VerifyAsync(
        [FromBody] InboundVerifyBody request, [FromServices] ICommandHandler<VerifyInbound, InboundDocumentVerifyResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new VerifyInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.split.</summary>
    [HttpPost("split")]
    [Authorize(Policy = SplitPermission)]
    public async Task<IResult> SplitAsync(
        [FromBody] InboundSplitBody request, [FromServices] ICommandHandler<SplitInbound, InboundDocumentSplitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SplitInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.link.</summary>
    [HttpPost("link")]
    [Authorize(Policy = LinkPermission)]
    public async Task<IResult> LinkAsync(
        [FromBody] InboundLinkBody request, [FromServices] ICommandHandler<LinkInbound, InboundDocumentLinkResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new LinkInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.relink.</summary>
    [HttpPost("relink")]
    [Authorize(Policy = RelinkPermission)]
    public async Task<IResult> RelinkAsync(
        [FromBody] InboundLinkBody request, [FromServices] ICommandHandler<RelinkInbound, InboundDocumentRelinkResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RelinkInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.reject.</summary>
    [HttpPost("reject")]
    [Authorize(Policy = RejectPermission)]
    public async Task<IResult> RejectAsync(
        [FromBody] InboundRejectBody request, [FromServices] ICommandHandler<RejectInbound, InboundDocumentRejectResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RejectInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.redact.</summary>
    [HttpPost("redact")]
    [Authorize(Policy = RedactPermission)]
    public async Task<IResult> RedactAsync(
        [FromBody] InboundRedactBody request, [FromServices] ICommandHandler<RedactInbound, InboundDocumentRedactResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RedactInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.reprocess.</summary>
    [HttpPost("reprocess")]
    [Authorize(Policy = ReprocessPermission)]
    public async Task<IResult> ReprocessAsync(
        [FromBody] InboundReprocessBody request, [FromServices] ICommandHandler<ReprocessInbound, InboundDocumentReprocessResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReprocessInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.InboundDocument.export.</summary>
    [HttpPost("export")]
    [Authorize(Policy = ExportPermission)]
    public async Task<IResult> ExportAsync(
        [FromBody] InboundExportBody request, [FromServices] ICommandHandler<ExportInbound, InboundDocumentExportResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExportInbound(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
