using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// POL's in-process path to uw.Rules.evaluate (the only one that changes a job's issues, D-UW-01): the test plays POL,
/// with the acting user and the job's participants it would pass.
/// </summary>
internal static class UwTestSupport
{
    public static async Task<JsonNode> EvaluateInProcessAsync(
        IServiceProvider services, object body, string actor = "uw-anna", IReadOnlyList<string>? participants = null)
    {
        var request = JsonSerializer.Deserialize<RulesEvaluateRequest>(JsonSerializer.Serialize(body, SharedKernelJson.Options), SharedKernelJson.Options)!;
        request = request with { JobParticipants = participants ?? ["USER:" + actor] };
        await using var scope = Scope(services);
        scope.ServiceProvider.GetRequiredService<RequestContext>().Actor = ActorRef.User(actor);
        var response = await scope.ServiceProvider.GetRequiredService<IUnderwritingRulesService>()
            .EvaluateAsync(request, CommandOptions.New(), TestContext.Current.CancellationToken);
        return JsonNode.Parse(JsonSerializer.Serialize(response, SharedKernelJson.Options))!;
    }
}
