using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Compliance.Api;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Modules.Compliance.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance.Reconciliation;

/// <summary>REST facade of <c>cmp.FiscalReconciliation.run</c> / <c>breaks</c> and <c>cmp.FiscalRule.certify</c> (BO-CMP-07).</summary>
[ApiController]
#pragma warning disable CA1822 // Not static: ASP.NET does not route static controller methods.
internal sealed class FiscalReconciliationController : ControllerBase
{
    /// <summary>cmp.FiscalReconciliation.run: compare the legal entity's fiscal documents with the channel status and record breaks.</summary>
    [HttpPost("api/cmp/v1/fiscal-reconciliation/run")]
    [Authorize(Policy = "cmp.FiscalReconciliation.run")]
    public async Task<IResult> RunAsync(
        [FromBody] FiscalReconciliationRunRequest request,
        [FromServices] ICommandHandler<RunFiscalReconciliation, FiscalReconciliationRunResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RunFiscalReconciliation(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>cmp.FiscalReconciliation.breaks: the open reconciliation breaks of the legal entity, with their documents.</summary>
    [HttpGet("api/cmp/v1/fiscal-reconciliation/breaks")]
    [Authorize(Policy = "cmp.FiscalReconciliation.breaks")]
    public async Task<IResult> BreaksAsync(
        [FromServices] ComplianceDbContext db, [FromQuery] string? date, [FromQuery] int? limit, [FromQuery] string? cursor, [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        var legalEntity = services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity!.Value);
        var take = KeysetPaging.Clamp(limit);
        Guid? after = null;
        if (cursor is not null)
        {
            if (!KeysetPaging.TryDecode(cursor, 1, out var parts) || parts is null || !Guid.TryParse(parts[0], out var parsed))
            {
                return HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "VALIDATION", "The cursor is malformed."), HttpContext);
            }

            after = parsed;
        }

        var wanted = string.Equals(status, FiscalBreakStatuses.Resolved, StringComparison.OrdinalIgnoreCase) ? FiscalBreakStatuses.Resolved : FiscalBreakStatuses.Open;
        var query = from b in db.Set<FiscalReconciliationBreakRow>().AsNoTracking()
                    join d in db.FiscalDocuments.AsNoTracking() on b.FiscalDocumentId equals d.FiscalDocumentId
                    where b.LegalEntityId == legalEntity && d.LegalEntityId == legalEntity && b.Status == wanted
                    select new { Break = b, Document = d };
        if (BusinessDate.TryParse(date, out var upTo))
        {
            query = query.Where(x => x.Document.IssueDate <= upTo);
        }

        if (after is { } last)
        {
            query = query.Where(x => x.Break.BreakId > last);
        }

        var rows = await query.OrderBy(x => x.Break.BreakId).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.Break.BreakId)));
        return Results.Ok(new FiscalReconciliationBreaksPage
        {
            Items = [.. page.Select(r =>
            {
                // The document view the UI already renders (document + identifiers), plus the break's own facts.
                var node = JsonSerializer.SerializeToNode(FiscalDocumentReader.ToResponse(r.Document), SharedKernelJson.Options)!.AsObject();
                node["breakId"] = r.Break.BreakId;
                node["kind"] = r.Break.Kind;
                node["detail"] = r.Break.Detail;
                node["status"] = r.Break.Status;
                node["detectedAt"] = r.Break.DetectedAt.ToString();
                node["lastSeenAt"] = r.Break.LastSeenAt.ToString();
                return new FiscalReconciliationBreaksItem { Breaks = JsonSerializer.SerializeToElement(node, SharedKernelJson.Options) };
            })],
            NextCursor = next,
            Limit = take,
        });
    }

    /// <summary>cmp.FiscalRule.certify: the MARK certification rule over a pack candidate (pure, fail closed).</summary>
    [HttpPost("api/cmp/v1/fiscal-rules/certify")]
    [Authorize(Policy = "cmp.FiscalRule.certify")]
    public IResult Certify([FromBody] FiscalRuleCertifyRequest request) =>
        request?.PackCandidate is null
            ? HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "VALIDATION", "packCandidate is required."), HttpContext)
            : Results.Ok(FiscalRuleCertifier.Certify(request.PackCandidate));
}
