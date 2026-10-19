using CoreIns.Modules.Compliance.Api;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Compliance.AiRegister;

/// <summary>REST facade of the cmp.AiSystem operations (contracts/openapi/cmp.yaml).</summary>
[ApiController]
[Route("api/cmp/v1/ai-systems")]
internal sealed class AiSystemsController : ControllerBase
{
    private const string Prefix = "cmp.AiSystem.";

    [HttpPost("register")]
    [Authorize(Policy = Prefix + "register")]
    public async Task<IResult> RegisterAsync([FromBody] AiSystemRegisterRequest request, [FromServices] ICommandHandler<RegisterAiSystem, AiSystemView> handler, CancellationToken cancellationToken) =>
        Map(
            await handler.HandleAsync(new RegisterAiSystem(request.FeatureId, request.Name, request.AiActClass), cancellationToken).ConfigureAwait(false),
            v => new AiSystemRegisterResponse
            {
                FeatureId = v.FeatureId,
                Name = v.Name,
                AiActClass = v.AiActClass,
                Status = Enum.Parse<AiSystemRegisterResponse.StatusValue>(Pascal(v.Status)),
                SubmittedBy = v.SubmittedBy,
                ApprovedBy = v.ApprovedBy,
                RecordVersion = v.RecordVersion,
            });

    [HttpPost("submit-for-review")]
    [Authorize(Policy = Prefix + "submitForReview")]
    public async Task<IResult> SubmitAsync([FromBody] AiSystemSubmitForReviewRequest request, [FromServices] ICommandHandler<SubmitAiSystem, AiSystemView> handler, CancellationToken cancellationToken) =>
        Map(
            await handler.HandleAsync(new SubmitAiSystem(request.FeatureId, request.Reason), cancellationToken).ConfigureAwait(false),
            v => new AiSystemSubmitForReviewResponse
            {
                FeatureId = v.FeatureId,
                Name = v.Name,
                AiActClass = v.AiActClass,
                Status = Enum.Parse<AiSystemSubmitForReviewResponse.StatusValue>(Pascal(v.Status)),
                SubmittedBy = v.SubmittedBy,
                ApprovedBy = v.ApprovedBy,
                RecordVersion = v.RecordVersion,
            });

    [HttpPost("approve")]
    [Authorize(Policy = Prefix + "approve")]
    public async Task<IResult> ApproveAsync([FromBody] AiSystemApproveRequest request, [FromServices] ICommandHandler<ApproveAiSystem, AiSystemView> handler, CancellationToken cancellationToken) =>
        Map(
            await handler.HandleAsync(new ApproveAiSystem(request.FeatureId, request.Reason), cancellationToken).ConfigureAwait(false),
            v => new AiSystemApproveResponse
            {
                FeatureId = v.FeatureId,
                Name = v.Name,
                AiActClass = v.AiActClass,
                Status = Enum.Parse<AiSystemApproveResponse.StatusValue>(Pascal(v.Status)),
                SubmittedBy = v.SubmittedBy,
                ApprovedBy = v.ApprovedBy,
                RecordVersion = v.RecordVersion,
            });

    [HttpPost("suspend")]
    [Authorize(Policy = Prefix + "suspend")]
    public async Task<IResult> SuspendAsync([FromBody] AiSystemSuspendRequest request, [FromServices] ICommandHandler<SuspendAiSystem, AiSystemView> handler, CancellationToken cancellationToken) =>
        Map(
            await handler.HandleAsync(new SuspendAiSystem(request.FeatureId, request.Reason), cancellationToken).ConfigureAwait(false),
            v => new AiSystemSuspendResponse
            {
                FeatureId = v.FeatureId,
                Name = v.Name,
                AiActClass = v.AiActClass,
                Status = Enum.Parse<AiSystemSuspendResponse.StatusValue>(Pascal(v.Status)),
                SubmittedBy = v.SubmittedBy,
                ApprovedBy = v.ApprovedBy,
                RecordVersion = v.RecordVersion,
            });

    [HttpPost("retire")]
    [Authorize(Policy = Prefix + "retire")]
    public async Task<IResult> RetireAsync([FromBody] AiSystemRetireRequest request, [FromServices] ICommandHandler<RetireAiSystem, AiSystemView> handler, CancellationToken cancellationToken) =>
        Map(
            await handler.HandleAsync(new RetireAiSystem(request.FeatureId, request.Reason), cancellationToken).ConfigureAwait(false),
            v => new AiSystemRetireResponse
            {
                FeatureId = v.FeatureId,
                Name = v.Name,
                AiActClass = v.AiActClass,
                Status = Enum.Parse<AiSystemRetireResponse.StatusValue>(Pascal(v.Status)),
                SubmittedBy = v.SubmittedBy,
                ApprovedBy = v.ApprovedBy,
                RecordVersion = v.RecordVersion,
            });

    [HttpGet("{id}")]
    [Authorize(Policy = Prefix + "get")]
    public async Task<IResult> GetAsync(string id, [FromServices] AiRegisterReader reader, CancellationToken cancellationToken)
    {
        var found = await reader.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return found is null
            ? HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The AI system is not in the register."), HttpContext)
            : Results.Ok(new AiSystemGetResponse
            {
                FeatureId = found.FeatureId,
                Name = found.Name,
                AiActClass = found.AiActClass,
                Status = Enum.Parse<AiSystemGetResponse.StatusValue>(Pascal(found.Status)),
                SubmittedBy = found.SubmittedBy,
                ApprovedBy = found.ApprovedBy,
                RecordVersion = found.RecordVersion,
            });
    }

    [HttpGet]
    [Authorize(Policy = Prefix + "list")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "ASP.NET actions are instance methods.")]
    public async Task<IResult> ListAsync([FromServices] AiRegisterReader reader, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var (items, next) = await reader.ListAsync(cursor, limit ?? 50, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new AiSystemListPage
        {
            Items = [.. items.Select(v => new AiSystemListItem
            {
                FeatureId = v.FeatureId,
                Name = v.Name,
                AiActClass = v.AiActClass,
                Status = Enum.Parse<AiSystemListItem.StatusValue>(Pascal(v.Status)),
                SubmittedBy = v.SubmittedBy,
                ApprovedBy = v.ApprovedBy,
                RecordVersion = v.RecordVersion,
            })],
            NextCursor = next,
            Limit = limit ?? 50,
        });
    }

    private static string Pascal(string status) => string.Concat(status.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));

    private IResult Map<TView, TResponse>(Result<TView> result, Func<TView, TResponse> map) =>
        result.IsSuccess ? Results.Ok(map(result.Value)) : result.ToHttpResult(HttpContext);
}

/// <summary>Development-only seed: approves the pilot AI features so E2E can enable them (404 elsewhere).</summary>
[ApiController]
[Route("api/cmp/v1/dev/ai-systems")]
internal sealed class DevAiSystemsController : ControllerBase
{
    [HttpPost("approve-all")]
    [Authorize(Policy = "cmp.AiSystem.approve")]
    public async Task<IResult> ApproveAllAsync([FromServices] ICommandHandler<DevApproveAllAiSystems, DevApproveAllResult> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DevApproveAllAiSystems(), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
