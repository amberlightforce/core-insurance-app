using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Ai;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Platform.Ai;

/// <summary>Outcome of <see cref="AiAssist.InvokeAsync"/>: the invocation, or the neutral refusal to return as is.</summary>
public sealed record AiAssistResult(AiInvokeResponse? Response, IResult? Refusal)
{
    /// <summary>True when the AI was allowed and the (fake) provider answered.</summary>
    public bool IsAllowed => Response is not null;
}

/// <summary>
/// The standard way for a module endpoint to use an AI feature (SL6-AI-FEATURES, D-SL6-08): ask <see cref="IAiGate"/> first;
/// disabled means 503 <c>PLT-ERR-AI-DISABLED</c> with a neutral message and the module's manual path untouched; allowed means
/// <c>plt.Ai.invoke</c>, whose interaction id the caller returns with its suggestion. The output is a suggestion only.
/// </summary>
public static class AiAssist
{
    /// <summary>Gate check, then invoke. Never throws for a disabled feature.</summary>
    public static async Task<AiAssistResult> InvokeAsync(HttpContext http, string featureId, JsonObject inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(inputs);
        var services = http.RequestServices;
        var decision = await services.GetRequiredService<IAiGate>().CheckAsync(featureId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new AiAssistResult(null, Refuse(http, decision.Reason));
        }

        try
        {
            var request = new AiInvokeRequest { FeatureId = AiFeatureId.Parse(featureId), Inputs = JsonSerializer.SerializeToElement(inputs) };
            var response = await services.GetRequiredService<IPlatformAiService>().InvokeAsync(request, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            return new AiAssistResult(response, null);
        }
        catch (DomainException exception) when (exception.Error.Code.Value.EndsWith(AiErrors.Disabled, StringComparison.Ordinal))
        {
            // Switched off while the call was in flight: same neutral refusal.
            return new AiAssistResult(null, Refuse(http, "DISABLED_IN_FLIGHT"));
        }
    }

    private static IResult Refuse(HttpContext http, string? reason) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(
            DomainError.Of(ModuleCode.PLT, AiErrors.Disabled, $"The AI feature is not available right now ({reason}). Continue on the manual path; nothing was changed."),
            http);
}
