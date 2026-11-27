using CoreIns.Modules.Compliance.Commands;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance.Api;

/// <summary>Permission names of the fiscal-document operations (<c>x-permission</c>).</summary>
internal static class CompliancePermissions
{
    public const string FiscalDocumentRequest = "cmp.FiscalDocument.request";
    public const string FiscalDocumentGet = "cmp.FiscalDocument.get";
}

/// <summary>REST facade of <c>cmp.FiscalDocument.request</c> and <c>get</c> (contracts/openapi/cmp.yaml).</summary>
[ApiController]
[Route("api/cmp/v1/fiscal-documents")]
internal sealed class FiscalDocumentsController : ControllerBase
{
    /// <summary>cmp.FiscalDocument.request.</summary>
    [HttpPost("request")]
    [Authorize(Policy = CompliancePermissions.FiscalDocumentRequest)]
    public async Task<IResult> RequestAsync(
        [FromBody] FiscalDocumentRequestRequest request,
        [FromServices] ICommandHandler<RequestFiscalDocument, FiscalDocumentRequestResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RequestFiscalDocument(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>cmp.FiscalDocument.listBySource: the legal entity's fiscal documents, optionally for one source.</summary>
    [HttpGet("list-by-source")]
    [Authorize(Policy = "cmp.FiscalDocument.listBySource")]
    public async Task<IResult> ListBySourceAsync(
        [FromServices] FiscalDocumentReader reader, [FromQuery] string? sourceType, [FromQuery] string? sourceId, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var legalEntity = ResolveLegalEntity();
        var take = limit ?? 100;
        var rows = await reader.ListAsync(legalEntity, sourceType, sourceId, take, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new FiscalDocumentListBySourcePage
        {
            Items = [.. rows.Select(r => new FiscalDocumentListBySourceItem
            {
                Documents = [System.Text.Json.JsonSerializer.SerializeToElement(r, CoreIns.SharedKernel.Json.SharedKernelJson.Options)],
            })],
            NextCursor = null,
            Limit = take,
        });
    }

    /// <summary>cmp.FiscalReconciliation.breaks: documents the channel has not registered (rejected or pending).</summary>
    [HttpGet("/api/cmp/v1/fiscal-reconciliation/breaks")]
    [Authorize(Policy = "cmp.FiscalReconciliation.breaks")]
    public async Task<IResult> BreaksAsync(
        [FromServices] FiscalDocumentReader reader, [FromQuery] string? date, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        CoreIns.SharedKernel.BusinessDate? upTo = CoreIns.SharedKernel.BusinessDate.TryParse(date, out var d) ? d : null;
        var take = limit ?? 100;
        var rows = await reader.BreaksAsync(ResolveLegalEntity(), upTo, take, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new FiscalReconciliationBreaksPage
        {
            Items = [.. rows.Select(r => new FiscalReconciliationBreaksItem
            {
                Breaks = System.Text.Json.JsonSerializer.SerializeToElement(r, CoreIns.SharedKernel.Json.SharedKernelJson.Options),
            })],
            NextCursor = null,
            Limit = take,
        });
    }

    private LegalEntityId ResolveLegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity!.Value);
    }

    /// <summary>cmp.FiscalDocument.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = CompliancePermissions.FiscalDocumentGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] FiscalDocumentReader reader, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        var legalEntity = services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity!.Value);
        var found = Guid.TryParse(id, out var guid)
            ? await reader.GetAsync(legalEntity, new FiscalDocumentId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return found is null
            ? HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The fiscal document does not exist in your legal entity."), HttpContext)
            : Results.Ok(found);
    }
}

/// <summary>Problem Details from a domain error.</summary>
internal static class HttpResults
{
    public static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
