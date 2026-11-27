using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Services;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Commands;

/// <summary><c>fin.Earning.run</c>: earn premium up to the as-of date and post the UPR movement into the open period of that date.</summary>
internal sealed record RunEarning(BusinessDate AsOf) : ICommand<EarningRunResponse>;

internal sealed class RunEarningHandler(EarningEngine engine) : ICommandHandler<RunEarning, EarningRunResponse>
{
    public Task<Result<EarningRunResponse>> HandleAsync(RunEarning command, CancellationToken cancellationToken) =>
        engine.RunAsync(command.AsOf, simulate: false, cancellationToken);
}

/// <summary>Blocks the EARNING_RUN close task until a run up to the last day of the period is done (BO-FIN-04 handoff).</summary>
internal sealed class EarningRunGate(FinanceDbContext db) : ICloseTaskGate
{
    public string Task => PeriodClose.EarningRun;

    public async Task<string?> BlockerAsync(Guid legalEntity, string period, CancellationToken cancellationToken)
    {
        var parts = period.Split('-');
        var lastDay = new BusinessDate(new DateOnly(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), 1).AddMonths(1).AddDays(-1));
        var done = await db.EarningRuns.AsNoTracking().AnyAsync(r => r.LegalEntityId == legalEntity && r.PeriodCode == period && r.State == Earning.Done && r.AsOf == lastDay, cancellationToken).ConfigureAwait(false);
        return done ? null : $"No earning run up to {lastDay} is done for period {period}.";
    }
}
