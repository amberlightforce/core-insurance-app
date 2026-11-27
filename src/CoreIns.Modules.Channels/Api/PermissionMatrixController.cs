using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Api;

/// <summary><c>chn.Permission.evaluate</c> and <c>chn.PermissionMatrix.*</c> (REQ-CHN-047..052, contracts/openapi/chn.yaml).</summary>
[ApiController]
internal sealed class PermissionMatrixController : ControllerBase
{
    /// <summary>chn.Permission.evaluate: a query (POST for the body), so no idempotency key.</summary>
    [HttpPost("api/chn/v1/permissions/evaluate")]
    [SkipIdempotency]
    [Authorize(Policy = "chn.Permission.evaluate")]
    public async Task<IResult> EvaluateAsync([FromBody] PermissionEvaluateRequest request, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken)
    {
        var transaction = Text(request.TransactionType);
        if (transaction is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["transactionType"] = ["Required."] });
        }

        decimal? amount = null;
        string? product = null;
        if (request.ProposedValues is { ValueKind: JsonValueKind.Object } p)
        {
            if (p.TryGetProperty("amount", out var a))
            {
                amount = a.ValueKind == JsonValueKind.Number && a.TryGetDecimal(out var n) ? n
                    : a.ValueKind == JsonValueKind.String && decimal.TryParse(a.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : null;
            }

            product = p.TryGetProperty("productLine", out var pl) && pl.ValueKind == JsonValueKind.String ? pl.GetString() : null;
        }

        var raise = request.RaiseTicket ?? true;
        var result = await service.EvaluateAsync(new PermissionCheck(transaction, Text(request.Channel), Text(request.Actor), product, amount, request.ObjectRef, raise), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Problem(result.Error);
        }

        var answer = result.Value;
        var d = answer.Decision;
        return Results.Ok(new
        {
            outcome = d.Outcome,
            limits = d.MaxAmount is { } max ? new { maxAmount = max.ToString(CultureInfo.InvariantCulture), currency = "EUR" } : null,
            entry = d.Entry is null ? null : MatrixEngine.Describe(d.Entry),
            version = answer.Version,
            reasons = d.Reasons,
            explanationKey = d.ExplanationKey,
            ticketId = answer.TicketId,
        });
    }

    [HttpPost("api/chn/v1/permission-matrices/create-draft")]
    [Authorize(Policy = "chn.PermissionMatrix.createDraft")]
    public async Task<IResult> CreateDraftAsync([FromBody] PermissionMatrixCreateDraftRequest request, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken) =>
        Map(await service.CreateDraftAsync(request.Version, request.Entries, request.Scenarios, cancellationToken).ConfigureAwait(false));

    [HttpPost("api/chn/v1/permission-matrices/update-entry")]
    [Authorize(Policy = "chn.PermissionMatrix.updateEntry")]
    public async Task<IResult> UpdateEntryAsync([FromBody] PermissionMatrixUpdateEntryRequest request, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken) =>
        Map(await service.UpdateEntryAsync(Id(request.MatrixId), request.Entries, request.Scenarios, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes nothing; no idempotency key needed.</summary>
    [HttpPost("api/chn/v1/permission-matrices/run-scenarios")]
    [SkipIdempotency]
    [Authorize(Policy = "chn.PermissionMatrix.runScenarios")]
    public async Task<IResult> RunScenariosAsync([FromBody] PermissionMatrixRunScenariosRequest request, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken) =>
        Map(await service.RunScenariosAsync(Id(request.MatrixId), request.Entries, request.Scenarios, cancellationToken).ConfigureAwait(false));

    [HttpPost("api/chn/v1/permission-matrices/submit")]
    [Authorize(Policy = "chn.PermissionMatrix.submit")]
    public async Task<IResult> SubmitAsync([FromBody] PermissionMatrixSubmitRequest request, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken) =>
        Map(await service.SubmitAsync(Id(request.MatrixId), cancellationToken).ConfigureAwait(false));

    [HttpPost("api/chn/v1/permission-matrices/schedule")]
    [Authorize(Policy = "chn.PermissionMatrix.schedule")]
    public async Task<IResult> ScheduleAsync([FromBody] PermissionMatrixScheduleRequest request, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken) =>
        Map(await service.ScheduleAsync(Id(request.MatrixId), request.EffectiveFrom, cancellationToken).ConfigureAwait(false));

    /// <summary>A matrix by id, or <c>active</c> for the one in force.</summary>
    [HttpGet("api/chn/v1/permission-matrices/{id}")]
    [Authorize(Policy = "chn.PermissionMatrix.get")]
    public async Task<IResult> GetAsync(string id, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken) =>
        Map(await service.GetAsync(id, cancellationToken).ConfigureAwait(false));

    [HttpGet("api/chn/v1/permission-matrices/diff")]
    [Authorize(Policy = "chn.PermissionMatrix.diff")]
    public async Task<IResult> DiffAsync([FromQuery] string id, [FromQuery] string? against, [FromServices] PermissionMatrixService service, CancellationToken cancellationToken) =>
        Map(await service.DiffAsync(id, against, cancellationToken).ConfigureAwait(false));

    private static string? Text(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s && !string.IsNullOrWhiteSpace(s.GetString()) ? s.GetString()
        : e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static Guid? Id(string? text) => Guid.TryParse(text, out var g) ? g : null;

    private IResult Map(Result<object> result) => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);

    private IResult Problem(DomainError error) => HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, HttpContext);
}
