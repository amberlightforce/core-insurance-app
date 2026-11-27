using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Services.Marts;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Data.Commands;

/// <summary><c>dat.Mart.run</c>.</summary>
internal sealed record RunMart(MartRunRequest Request) : ICommand<MartRunResponse>;

/// <summary>Computes and stores a mart run in the caller's legal entity; a dry run computes and rolls back (BO-DAT-03).</summary>
internal sealed class RunMartHandler(MartEngine engine, RequestContext context) : ICommandHandler<RunMart, MartRunResponse>
{
    public async Task<Result<MartRunResponse>> HandleAsync(RunMart command, CancellationToken cancellationToken)
    {
        if (context.LegalEntity?.Value is not { } legalEntity)
        {
            return DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "No legal entity in scope.");
        }

        return await engine.RunAsync(legalEntity, $"{context.Actor.KindCode}:{context.Actor.Id}", command.Request, cancellationToken).ConfigureAwait(false);
    }
}
