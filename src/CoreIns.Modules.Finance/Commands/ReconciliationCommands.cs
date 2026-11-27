using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Services;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Finance.Commands;

/// <summary><c>fin.Reconciliation.run</c>: compare the control totals of a source with the ledger for a period (one definition, or all).</summary>
internal sealed record RunReconciliation(string Period, string? Definition) : ICommand<ReconciliationRunResponse>;

internal sealed class RunReconciliationHandler(ReconciliationService service) : ICommandHandler<RunReconciliation, ReconciliationRunResponse>
{
    public Task<Result<ReconciliationRunResponse>> HandleAsync(RunReconciliation command, CancellationToken cancellationToken) =>
        service.RunAsync(command.Definition, command.Period, cancellationToken);
}

/// <summary><c>fin.Reconciliation.match</c>: an open break is explained and cleared by hand, with a reason.</summary>
internal sealed record MatchReconciliationBreak(Guid BreakId, string Reason) : ICommand<ReconciliationMatchResponse>;

internal sealed class MatchReconciliationBreakHandler(ReconciliationService service) : ICommandHandler<MatchReconciliationBreak, ReconciliationMatchResponse>
{
    public Task<Result<ReconciliationMatchResponse>> HandleAsync(MatchReconciliationBreak command, CancellationToken cancellationToken) =>
        service.MatchAsync(command.BreakId, command.Reason, cancellationToken);
}

/// <summary><c>fin.Reconciliation.exchange</c>: store the control totals a source pushed and compare them with the ledger.</summary>
internal sealed record ExchangeReconciliationTotals(ReconciliationExchangeRequest Request) : ICommand<ReconciliationExchangeResponse>;

internal sealed class ExchangeReconciliationTotalsHandler(ReconciliationService service) : ICommandHandler<ExchangeReconciliationTotals, ReconciliationExchangeResponse>
{
    public Task<Result<ReconciliationExchangeResponse>> HandleAsync(ExchangeReconciliationTotals command, CancellationToken cancellationToken) =>
        service.ExchangeAsync(command.Request, cancellationToken);
}

/// <summary>Blocks the RECONCILIATIONS close task while the period has open reconciliation breaks (BO-FIN-04 handoff); an accepted break does not block.</summary>
internal sealed class ReconciliationsCloseGate(ReconciliationService service) : ICloseTaskGate
{
    public string Task => PeriodClose.Reconciliations;

    public async Task<string?> BlockerAsync(Guid legalEntity, string period, CancellationToken cancellationToken)
    {
        var open = await service.OpenBreaksAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        return open == 0 ? null : $"{open} reconciliation break(s) are open for period {period}; match them or accept them with a reason.";
    }
}
