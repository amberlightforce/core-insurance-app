using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Services.Marts;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts;
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

/// <summary>The actor keys (actor and, if delegated, the principal) used for segregation of duties.</summary>
internal static class MartActors
{
    public static IReadOnlyList<string> Keys(RequestContext context) =>
        context.OnBehalfOf is { } principal ? [$"{context.Actor.KindCode}:{context.Actor.Id}", $"{principal.KindCode}:{principal.Id}"] : [$"{context.Actor.KindCode}:{context.Actor.Id}"];
}

/// <summary><c>dat.Mart.adjust</c>.</summary>
internal sealed record AdjustMart(MartAdjustRequest Request) : ICommand<MartAdjustResponse>;

internal sealed class AdjustMartHandler(MartGovernance governance, RequestContext context) : ICommandHandler<AdjustMart, MartAdjustResponse>
{
    public async Task<Result<MartAdjustResponse>> HandleAsync(AdjustMart command, CancellationToken cancellationToken) =>
        context.LegalEntity?.Value is { } legalEntity
            ? await governance.AdjustAsync(legalEntity, MartActors.Keys(context)[0], command.Request, cancellationToken).ConfigureAwait(false)
            : DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "No legal entity in scope.");
}

/// <summary>Audit facts of <c>dat.Mart.adjust</c>: the reason and evidence ref go into the trail.</summary>
internal sealed class AdjustMartAuditor : ICommandAuditor<AdjustMart, MartAdjustResponse>
{
    public CommandAuditFacts Describe(AdjustMart command, Result<MartAdjustResponse>? result)
    {
        var run = command.Request.RunId?.ToString("D") ?? "unknown";
        var keys = BusinessKeys.Empty.With("martRunId", run);
        var subject = new ObjectRef(ModuleCode.DAT, "MartRun", run);
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            BusinessKeys = keys.With("adjustmentId", ok.Value.AdjustmentId?.ToString("D") ?? string.Empty),
            Changes = AuditDiff.Compute(
                new { figureKey = ok.Value.FigureKey },
                new { figureKey = ok.Value.FigureKey, amount = command.Request.Amount?.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), reason = command.Request.Reason, evidence = command.Request.Evidence, adjustedValue = ok.Value.AdjustedValue }),
        };
    }
}

/// <summary><c>dat.Mart.signOff</c>.</summary>
internal sealed record SignOffMart(MartSignOffRequest Request) : ICommand<MartSignOffResponse>;

internal sealed class SignOffMartHandler(MartGovernance governance, RequestContext context) : ICommandHandler<SignOffMart, MartSignOffResponse>
{
    public async Task<Result<MartSignOffResponse>> HandleAsync(SignOffMart command, CancellationToken cancellationToken) =>
        context.LegalEntity?.Value is { } legalEntity
            ? await governance.SignOffAsync(legalEntity, MartActors.Keys(context), command.Request, cancellationToken).ConfigureAwait(false)
            : DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "No legal entity in scope.");
}

/// <summary>Audit facts of <c>dat.Mart.signOff</c>.</summary>
internal sealed class SignOffMartAuditor : ICommandAuditor<SignOffMart, MartSignOffResponse>
{
    public CommandAuditFacts Describe(SignOffMart command, Result<MartSignOffResponse>? result)
    {
        var run = command.Request.RunId?.ToString("D") ?? "unknown";
        var keys = BusinessKeys.Empty.With("martRunId", run);
        var subject = new ObjectRef(ModuleCode.DAT, "MartRun", run);
        return result is not { IsSuccess: true } ok
            ? new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys }
            : new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys, Changes = AuditDiff.Compute(new { status = "COMPLETED" }, new { status = ok.Value.Status, boundHash = ok.Value.BoundHash?.Value, comment = command.Request.Comment }) };
    }
}

/// <summary><c>dat.Mart.export</c>.</summary>
internal sealed record ExportMart(MartExportRequest Request) : ICommand<MartExportResponse>;

internal sealed class ExportMartHandler(MartGovernance governance, RequestContext context) : ICommandHandler<ExportMart, MartExportResponse>
{
    public async Task<Result<MartExportResponse>> HandleAsync(ExportMart command, CancellationToken cancellationToken) =>
        context.LegalEntity?.Value is { } legalEntity
            ? await governance.ExportAsync(legalEntity, command.Request, cancellationToken).ConfigureAwait(false)
            : DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "No legal entity in scope.");
}

/// <summary>Audit facts of <c>dat.Mart.export</c>.</summary>
internal sealed class ExportMartAuditor : ICommandAuditor<ExportMart, MartExportResponse>
{
    public CommandAuditFacts Describe(ExportMart command, Result<MartExportResponse>? result)
    {
        var run = command.Request.RunId?.ToString("D") ?? "unknown";
        var keys = BusinessKeys.Empty.With("martRunId", run);
        var subject = new ObjectRef(ModuleCode.DAT, "MartRun", run);
        return result is not { IsSuccess: true } ok
            ? new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys }
            : new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys, Changes = AuditDiff.Compute(new { exported = false }, new { exported = true, manifestHash = ok.Value.ManifestHash?.Value, format = command.Request.Format?.ToString() }) };
    }
}
