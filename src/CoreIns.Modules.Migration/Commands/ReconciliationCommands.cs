using CoreIns.Modules.Migration.Services.Reconciliation;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Recon.loadControlTotals: loads (replaces) the legacy control totals of a scope.</summary>
internal sealed record LoadControlTotals(ReconLoadBody Body) : ICommand<ReconLoadView>;

internal sealed class LoadControlTotalsHandler(ReconciliationService service) : ICommandHandler<LoadControlTotals, ReconLoadView>
{
    public Task<Result<ReconLoadView>> HandleAsync(LoadControlTotals command, CancellationToken cancellationToken) => service.LoadAsync(command.Body, cancellationToken);
}

/// <summary>mig.Recon.run: compares the loaded control totals with the modules (read-only on the modules) and stores the result rows.</summary>
internal sealed record RunReconciliation(ReconRunBody Body) : ICommand<ReconReportView>;

internal sealed class RunReconciliationHandler(ReconciliationService service) : ICommandHandler<RunReconciliation, ReconReportView>
{
    public Task<Result<ReconReportView>> HandleAsync(RunReconciliation command, CancellationToken cancellationToken) => service.RunAsync(command.Body, cancellationToken);
}
