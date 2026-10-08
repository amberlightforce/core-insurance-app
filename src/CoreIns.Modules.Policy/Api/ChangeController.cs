using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>Permission names of SL3-POL-CHANGE (granted to roles in <c>Platform:Permissions</c>).</summary>
internal static class ChangePermissions
{
    public const string Create = "pol.PolicyChange.create";
    public const string Change = ChangeNames.Permission;
}

/// <summary>
/// The servicing preview of a change job as the API returns it (stand-in for the contract field <c>servicingPreview</c> that
/// SL3-CONTRACTS adds to <c>pol.Job.quote</c> and <c>pol.Job.bind</c>; the UI reads this until then).
/// </summary>
internal sealed record PolicyChangePreviewResponse(ServicingPreview? ServicingPreview, IReadOnlyList<DiffEntry> Diff, IReadOnlyList<DiffSection> Sections);

/// <summary>The diff grouped by section (REQ-POL-193).</summary>
internal sealed record DiffSection(string Section, IReadOnlyList<DiffEntry> Entries);

/// <summary>REST facade of <c>pol.PolicyChange.create</c> and the change preview (contracts/openapi/pol.yaml): thin, commands through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class ChangeController : ControllerBase
{
    /// <summary>pol.PolicyChange.create → 201, Location = the job. Dry run supported.</summary>
    [HttpPost("policy-changes")]
    [Authorize(Policy = ChangePermissions.Create)]
    public async Task<IResult> CreateAsync(
        [FromBody] PolicyChangeCreateRequest request, [FromServices] ICommandHandler<CreatePolicyChange, PolicyChangeCreateResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreatePolicyChange(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId!.Value.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>The diff of the edit and, for a Quoted version, the servicing preview. Reads only; the same code as the bind.</summary>
    [HttpGet("policy-changes/{jobId}/preview")]
    [Authorize(Policy = ChangePermissions.Change)]
    public async Task<IResult> PreviewAsync(string jobId, [FromQuery] int? versionNo, [FromServices] ChangePreviewService preview, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(jobId, out var id))
        {
            return HttpResults.Problem(JobSupport.NotFound("change job"), HttpContext);
        }

        var result = await preview.GetAsync(new JobId(id), versionNo, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return HttpResults.Problem(result.Error!, HttpContext);
        }

        var (servicing, diff) = result.Value;
        return Results.Ok(new PolicyChangePreviewResponse(
            servicing, diff, [.. diff.GroupBy(d => d.Section, StringComparer.Ordinal).Select(g => new DiffSection(g.Key, [.. g]))]));
    }
}
