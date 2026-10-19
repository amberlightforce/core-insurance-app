using CoreIns.Modules.Finance.Contracts;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Commands.Change.Oos;

/// <summary>
/// <see cref="IOosFinancePeriods"/> over FIN's in-process <see cref="IFinancePeriodService"/> (<c>fin.Period.status</c>, REQ-POL-108 G5).
/// Resolved per call: a deployment without FIN treats no period as locked.
/// </summary>
internal sealed class FinancePeriodsAdapter(IServiceProvider services, RequestContext context) : IOosFinancePeriods
{
    public async Task<bool> IsLockedAsync(BusinessDate date, CancellationToken cancellationToken)
    {
        var periods = services.GetService<IFinancePeriodService>();
        if (periods is null || context.LegalEntity is not { } legalEntity)
        {
            return false;
        }

        var status = await periods.StatusAsync(legalEntity.Value, ValidAt.From(date), cancellationToken).ConfigureAwait(false);
        return status.ClosedForPosting == true;
    }
}
