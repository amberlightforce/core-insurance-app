using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>Permission names (the operations' <c>x-permission</c>), granted to roles in <c>Platform:Permissions</c> (<c>permissions/ri.json</c>).</summary>
internal static class ReinsurancePermissions
{
    public const string ContractCreate = "ri.Contract.create";
    public const string ContractUpdate = "ri.Contract.update";
    public const string ContractSubmit = "ri.Contract.submit";
    public const string ContractApprove = "ri.Contract.approve";
    public const string ContractGet = "ri.Contract.get";
    public const string ContractList = "ri.Contract.list";
    public const string ContractApplicable = "ri.Contract.applicable";
}

/// <summary>
/// REST facade of <c>ri.Contract.*</c> (contracts/openapi/ri.yaml): thin; commands go through the pipeline, queries through
/// <see cref="ContractReader"/>. The contract id, the type, the subject and the authority of an approval are never
/// taken from the client (PITFALLS 4); the route and the stored contract decide.
/// </summary>
[ApiController]
[Route("api/ri/v1/contracts")]
internal sealed class ContractsController : ControllerBase
{
    /// <summary>ri.Contract.create → 201 (REQ-RI-030..032).</summary>
    [HttpPost]
    [Authorize(Policy = ReinsurancePermissions.ContractCreate)]
    public async Task<IResult> CreateAsync(
        [FromBody] ContractCreateRequest request, [FromServices] ICommandHandler<CreateContract, ContractCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateContract(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/ri/v1/contracts/{result.Value.Contract.ContractId.Value:D}", result.Value)
            : RiHttp.Problem(result.Error!, HttpContext);
    }

    /// <summary>ri.Contract.update (PATCH; a Draft contract only).</summary>
    [HttpPatch("{id}")]
    [Authorize(Policy = ReinsurancePermissions.ContractUpdate)]
    public async Task<IResult> UpdateAsync(
        string id, [FromBody] ContractUpdateRequest request, [FromServices] ICommandHandler<UpdateContract, ContractUpdateResponse> handler, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var contractId) || contractId == Guid.Empty)
        {
            return RiHttp.Problem(RiErrors.NotFound(), HttpContext);
        }

        return (await handler.HandleAsync(new UpdateContract(new RiContractId(contractId), request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>ri.Contract.submit: Draft → PendingApproval with an in-process PLT approval request (REQ-RI-056, -057).</summary>
    [HttpPost("submit")]
    [Authorize(Policy = ReinsurancePermissions.ContractSubmit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] ContractSubmitRequest request, [FromServices] ICommandHandler<SubmitContract, ContractSubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitContract(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Contract.approve: the checker's APPROVE or RETURN (REQ-RI-057, -058).</summary>
    [HttpPost("approve")]
    [Authorize(Policy = ReinsurancePermissions.ContractApprove)]
    public async Task<IResult> ApproveAsync(
        [FromBody] ContractApproveRequest request, [FromServices] ICommandHandler<ApproveContract, ContractApproveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveContract(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Contract.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ReinsurancePermissions.ContractGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] ContractReader reader, CancellationToken cancellationToken)
    {
        var view = Guid.TryParse(id, out var contractId) ? await reader.GetAsync(contractId, cancellationToken).ConfigureAwait(false) : null;
        return view is null ? RiHttp.Problem(RiErrors.NotFound(), HttpContext) : Results.Ok(new ContractGetResponse { Contract = view });
    }

    /// <summary>ri.Contract.list.</summary>
    [HttpGet]
    [Authorize(Policy = ReinsurancePermissions.ContractList)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] int? contractYear, [FromQuery] RiContractStatus? status, [FromQuery] RiContractType? contractType,
        [FromServices] ContractReader reader, CancellationToken cancellationToken) =>
        (await reader.ListAsync(cursor, limit, contractYear, status, contractType, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Contract.applicable: the Active contracts for a loss instant, product and coverage (REQ-RI-001, -116).</summary>
    [HttpGet("applicable")]
    [Authorize(Policy = ReinsurancePermissions.ContractApplicable)]
    public async Task<IResult> ApplicableAsync(
        [FromQuery] string? productCode, [FromQuery] string? coverageCode, [FromQuery] string? validAt, [FromServices] ContractReader reader, CancellationToken cancellationToken)
    {
        if (!TryValidAt(validAt, out var at))
        {
            return RiHttp.Problem(RiErrors.Validation("validAt", "VALID_AT", "validAt must be a date or an instant."), HttpContext);
        }

        return (await reader.ApplicableAsync(productCode ?? string.Empty, coverageCode ?? string.Empty, at, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    private static bool TryValidAt(string? text, out ValidAt? validAt)
    {
        validAt = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (Instant.TryParse(text, out var instant))
        {
            validAt = ValidAt.From(instant);
            return true;
        }

        if (BusinessDate.TryParse(text, out var date))
        {
            validAt = ValidAt.From(date);
            return true;
        }

        return false;
    }
}

/// <summary>Problem Details from a domain error.</summary>
internal static class RiHttp
{
    public static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
