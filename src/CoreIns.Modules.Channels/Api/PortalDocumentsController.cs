using CoreIns.Modules.Channels.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Channels.Api;

/// <summary>REST facade of <c>chn.PortalDocument.*</c> (BO-CHN-04): the customer document vault.</summary>
[ApiController]
[Route("api/chn/v1/portal/documents")]
internal sealed class PortalDocumentsController(PortalDocumentReader reader) : ControllerBase
{
    /// <summary>chn.PortalDocument.list.</summary>
    [HttpGet]
    [Authorize(Policy = "chn.PortalDocument.list")]
    public async Task<IResult> ListAsync([FromQuery] string? policyId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        await reader.ListAsync(policyId, cursor, limit, cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.PortalDocument.download: the archived bytes with the content hash.</summary>
    [HttpGet("{id}/download")]
    [Authorize(Policy = "chn.PortalDocument.download")]
    public async Task<IResult> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        if (await reader.DownloadAsync(id, cancellationToken).ConfigureAwait(false) is not { } result)
        {
            return PortalProblems.NotFound(HttpContext);
        }

        if (result.Denied || result.Content is null)
        {
            return PortalProblems.NotPermitted(HttpContext);
        }

        HttpContext.Response.Headers["X-Content-SHA256"] = result.Content.Hash;
        return Results.File(result.Content.Content, result.Content.ContentType);
    }
}
