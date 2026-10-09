using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>
/// Moves one contract across a period boundary when it is due (internal, no HTTP; run by <see cref="LifecycleScanner"/>):
/// Approved → Active once the period has started and Active → Expired once it has ended, both by the Athens business date
/// of <c>IClock.Now</c> (REQ-RI-058, PITFALLS 13, 14). Idempotent and race-safe: the contract is row-locked and only the
/// state it finds decides what happens, so a scanner run twice, two scanners at once, or an approval that already
/// activated the contract do nothing the second time.
/// </summary>
internal sealed record ApplyDueLifecycle(RiContractId ContractId) : ICommand<string>;

/// <summary>What a run did to a contract.</summary>
internal static class LifecycleOutcome
{
    public const string Activated = "ACTIVATED";
    public const string ActivatedAndExpired = "ACTIVATED_AND_EXPIRED";
    public const string Expired = "EXPIRED";
    public const string NotDue = "NOT_DUE";
    public const string ApprovalMismatch = "APPROVAL_MISMATCH";
}

internal sealed partial class ApplyDueLifecycleHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    ContractLifecycleService lifecycle,
    IOptions<ReinsuranceOptions> options,
    ILogger<ApplyDueLifecycleHandler> logger) : ICommandHandler<ApplyDueLifecycle, string>
{
    public async Task<Result<string>> HandleAsync(ApplyDueLifecycle command, CancellationToken cancellationToken)
    {
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var contract = await ContractSupport.LockAsync(db, legalEntities.Resolve(legalEntityCode), command.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        var now = clock.Now;
        var today = now.ToBusinessDate(options.Value.Zone);
        var status = ContractStateModel.Parse(contract.Status);
        if (status is not (ContractStatus.Approved or ContractStatus.Active))
        {
            return LifecycleOutcome.NotDue;
        }

        var version = await ContractSupport.CurrentVersionAsync(db, contract.ContractId, cancellationToken).ConfigureAwait(false);
        var outcome = LifecycleOutcome.NotDue;
        if (status == ContractStatus.Approved && version.ValidFrom <= today)
        {
            var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
            if (await lifecycle.VerifyApprovalAsync(contract, version, content, cancellationToken).ConfigureAwait(false) is { } mismatch)
            {
                // Fail closed: an approved contract whose content or approval no longer matches is not activated.
                LogMismatch(logger, contract.ContractId.Value, mismatch.Detail ?? mismatch.Code.ToString());
                return LifecycleOutcome.ApprovalMismatch;
            }

            var activated = await lifecycle.ActivateAsync(contract, version, content, now, cancellationToken).ConfigureAwait(false);
            if (activated.IsFailure)
            {
                return activated.Error!;
            }

            status = ContractStatus.Active;
            outcome = LifecycleOutcome.Activated;
        }

        if (status == ContractStatus.Active && version.ValidTo <= today)
        {
            var expired = await lifecycle.ExpireAsync(contract, version, now, cancellationToken).ConfigureAwait(false);
            if (expired.IsFailure)
            {
                return expired.Error!;
            }

            outcome = outcome == LifecycleOutcome.Activated ? LifecycleOutcome.ActivatedAndExpired : LifecycleOutcome.Expired;
        }

        return outcome;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: reinsurance contract {ContractId} was not activated: {Reason}")]
    private static partial void LogMismatch(ILogger logger, Guid contractId, string reason);
}

/// <summary>Audit facts of the scanner's per-contract command.</summary>
internal sealed class ApplyDueLifecycleAuditor : ICommandAuditor<ApplyDueLifecycle, string>
{
    public CommandAuditFacts Describe(ApplyDueLifecycle command, Result<string>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.RI, "Contract", command.ContractId),
        BusinessKeys = BusinessKeys.Empty.With("riContractId", command.ContractId.Value.ToString("D")),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(null, new { lifecycle = ok.Value }) : [],
    };
}

/// <summary>
/// The recurring scanner (D-ARC-04): finds the contracts that are due and runs <see cref="ApplyDueLifecycle"/> for each in
/// its own scope and transaction, so one contract's failure never blocks the others. It runs as the service actor
/// <c>ri-lifecycle-scanner</c> in the stamp's legal entity. The due list is read with the same Athens date the command uses.
/// </summary>
internal sealed partial class LifecycleScanner(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptions<ReinsuranceOptions> options,
    IOptions<StampOptions> stamp,
    ILogger<LifecycleScanner> logger)
{
    /// <summary>The service actor of the scanner.</summary>
    public const string ActorId = "ri-lifecycle-scanner";

    private const int BatchSize = 100;

    /// <summary>Runs one pass; returns how many contracts it activated or expired.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var today = clock.Now.ToBusinessDate(options.Value.Zone);

        IReadOnlyList<RiContractId> due;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            Prepare(scope.ServiceProvider, legalEntity, jurisdiction);
            var db = scope.ServiceProvider.GetRequiredService<ReinsuranceDbContext>();
            var legalEntityId = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            due = await (
                    from c in db.Contracts.AsNoTracking()
                    join v in db.Versions.AsNoTracking() on c.ContractId equals v.ContractId
                    where c.LegalEntityId == legalEntityId && v.KnownTo == null
                        && ((c.Status == "APPROVED" && v.ValidFrom <= today) || (c.Status == "ACTIVE" && v.ValidTo <= today))
                    orderby v.ValidFrom, c.ContractNumber
                    select c.ContractId)
                .Take(BatchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var changed = 0;
        foreach (var id in due)
        {
            try
            {
                var inner = scopes.CreateAsyncScope();
                await using (inner.ConfigureAwait(false))
                {
                    Prepare(inner.ServiceProvider, legalEntity, jurisdiction);
                    var handler = inner.ServiceProvider.GetRequiredService<ICommandHandler<ApplyDueLifecycle, string>>();
                    var result = await handler.HandleAsync(new ApplyDueLifecycle(id), cancellationToken).ConfigureAwait(false);
                    if (result.IsSuccess && result.Value is LifecycleOutcome.Activated or LifecycleOutcome.Expired or LifecycleOutcome.ActivatedAndExpired)
                    {
                        changed++;
                    }
                    else if (result.IsFailure)
                    {
                        LogFailed(logger, id.Value, result.Error!.ToString());
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, id.Value, ex.GetType().Name);
            }
        }

        return changed;
    }

    private static void Prepare(IServiceProvider services, LegalEntityCode legalEntity, Jurisdiction jurisdiction)
    {
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service(ActorId);
        context.LegalEntity = legalEntity;
        context.Jurisdiction = jurisdiction;
        context.Origin = EventOrigin.Live;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Reinsurance lifecycle scan failed for contract {ContractId}: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid contractId, string reason);
}

/// <summary>The Hangfire entry point of the recurring scanner (a public type with a public method, as Hangfire requires).</summary>
public sealed class ReinsuranceLifecycleJob(IServiceScopeFactory scopes)
{
    /// <summary>The recurring job id.</summary>
    public const string JobId = "ri-contract-lifecycle";

    /// <summary>Runs one scan.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<LifecycleScanner>().RunAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
