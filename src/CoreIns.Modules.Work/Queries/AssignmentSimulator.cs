using System.Text.Json;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Work.Queries;

/// <summary><c>wrk.Assignment.simulate</c>: explains where an activity of a pattern would be routed, without creating or changing anything.</summary>
internal sealed class AssignmentSimulator(AssignmentRouter router, WorkCatalogue catalogue)
{
    public async Task<Result<AssignmentSimulateResponse>> SimulateAsync(AssignmentSimulateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = request.DraftActivity is { ValueKind: JsonValueKind.Object } draft
            && draft.TryGetProperty("patternCode", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var pattern = code is null ? null : (await catalogue.GetAsync(cancellationToken).ConfigureAwait(false)).Pattern(code);
        if (pattern is null)
        {
            return WorkErrors.Invalid("draftActivity is {patternCode} of a known pattern.");
        }

        var decision = await router.RouteAsync(pattern.Code, pattern.Queue, cancellationToken).ConfigureAwait(false);
        object target = decision.UserId is null
            ? new { type = "QUEUE", id = decision.QueueCode }
            : new { type = "USER", id = decision.UserId };
        return new AssignmentSimulateResponse
        {
            Target = Json(target),
            RulePath = Json(decision.RulePath.Select(s => new { rule = s.Rule, outcome = s.Outcome })),
            RejectedCandidates = Json(decision.Rejected.Select(r => new { userId = r.UserId, reason = r.Reason })),
        };
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, SharedKernelJson.Options);
}
