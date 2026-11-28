using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Contracts;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;

namespace CoreIns.Modules.Finance.Services;

/// <summary>In-process <see cref="IFinanceReconciliationService"/> (BO-BIL-12): a source module pushes its control totals through the command pipeline of <c>fin.Reconciliation.exchange</c>.</summary>
internal sealed class FinanceReconciliationContract(RequestContext context, ICommandHandler<ExchangeReconciliationTotals, ReconciliationExchangeResponse> handler) : IFinanceReconciliationService
{
    public async Task<ReconciliationExchangeResponse> ExchangeAsync(ReconciliationExchangeRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await handler.HandleAsync(new ExchangeReconciliationTotals(request), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
        }
    }
}
