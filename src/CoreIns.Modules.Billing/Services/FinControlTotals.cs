using System.Globalization;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Finance.Contracts;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Telemetry;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// The BIL side of the BIL-vs-FIN reconciliation (BO-BIL-12, REQ-FIN-244, REQ-BIL-314). For one accounting day it totals the sub-ledger lines per
/// account, entry type and currency. The figure FIN compares is the billed receivable LA-02: FIN posts every LA-02 line of every entry kind
/// (BILLED, ALLOCATED, ALLOCATION_REVERSED, WRITTEN_OFF, CREDIT_BILLED, REFUND_APPROVED; rule set v9) to GL-1210, one journal line per
/// source line, so the net debit and the line count per currency must equal FIN movement of that account. Entry kinds that never touch
/// LA-02 (RECEIVED, RECEIPT_RETURNED, MONEY_TRANSFERRED, disbursements) are in the per-account totals for investigation only.
/// </summary>
internal sealed class FinControlTotals(LedgerReader reader, LedgerWriter ledger, RequestContext context, ILegalEntityDirectory legalEntities) : IBillingReconciliationService
{
    /// <summary>The sub-ledger account FIN holds as GL-1210.</summary>
    public const string ControlAccount = LedgerAccounts.BilledReceivable;

    public async Task<Result<ReconciliationFinResponse>> ComputeAsync(
        LegalEntityId legalEntity, BusinessDate date, string? account, string? eventType, bool includeEntryIds, CancellationToken cancellationToken)
    {
        if (date > ledger.Today)
        {
            return DomainError.Of(ModuleCode.BIL, "DAY-NOT-CLOSED", $"The accounting day {date} has not started; control totals exist for today and earlier days.");
        }

        var day = new LedgerFilter(legalEntity, Account: account, EntryType: eventType, From: date, To: date);
        var totals = await reader.TotalsAsync(day, includeEntryIds, cancellationToken).ConfigureAwait(false);
        var control = await reader.TotalsAsync(new LedgerFilter(legalEntity, Account: ControlAccount, From: date, To: date), false, cancellationToken).ConfigureAwait(false);
        return new ReconciliationFinResponse
        {
            AccountingDate = date,
            Totals = [.. totals.Select(t =>
            {
                var currency = Currency.FromCode(t.Currency);
                return new ReconciliationFinTotal
                {
                    Account = t.Account,
                    EventType = t.EntryType,
                    Currency = t.Currency,
                    Debit = new Money(t.Debit, currency),
                    Credit = new Money(t.Credit, currency),
                    Net = new Money(t.Debit - t.Credit, currency),
                    LineCount = t.LineCount,
                    EntryIds = t.EntryIds,
                };
            })],
            ReceivableControl = [.. control.GroupBy(t => t.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new ReconciliationFinControl
            {
                Currency = g.Key,
                Amount = new Money(g.Sum(t => t.Debit - t.Credit), Currency.FromCode(g.Key)),
                Count = g.Sum(t => t.LineCount),
            })],
        };
    }

    public async Task<ReconciliationFinResponse> FinAsync(ReconciliationFinRequest request, CancellationToken cancellationToken = default)
    {
        var legalEntity = InProcess.LegalEntity(context, legalEntities);
        var result = await ComputeAsync(
            legalEntity, request.AccountingDate, request.SubLedgerAccount, request.EventType, request.IncludeEntryIds == true, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
    }
}

/// <summary>
/// One push of the control totals of the last days to <c>fin.Reconciliation.exchange</c> (source BIL), as the service actor
/// <c>bil-fin-control-totals</c>. A day pushed again replaces its earlier figures in FIN, so late postings correct themselves.
/// </summary>
internal sealed partial class FinControlPublisher(IServiceScopeFactory scopes, IOptions<StampOptions> stamp, IOptions<BillingOptions> options, ILogger<FinControlPublisher> logger)
{
    public const string ActorId = "bil-fin-control-totals";

    /// <summary>Pushes the last <paramref name="days"/> days ending at <paramref name="lastDay"/> (today when null); returns the number of days FIN accepted.</summary>
    public async Task<int> RunAsync(int? days, BusinessDate? lastDay, CancellationToken cancellationToken)
    {
        // TODO(hardening): one pass per legal entity once a catalogue of entities is available; today the stamp names the single one.
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service(ActorId);
            context.LegalEntity = legalEntity;
            context.Jurisdiction = jurisdiction;
            context.Origin = EventOrigin.Live;
            context.ConfigurationHash = await scope.ServiceProvider.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);
            var finance = scope.ServiceProvider.GetService<IFinanceReconciliationService>();
            if (finance is null)
            {
                return 0;
            }

            var totals = scope.ServiceProvider.GetRequiredService<FinControlTotals>();
            var entity = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>().Resolve(legalEntity);
            var last = lastDay ?? scope.ServiceProvider.GetRequiredService<LedgerWriter>().Today;
            var accepted = 0;
            for (var back = Math.Max(1, days ?? options.Value.FinControlTotalsDays) - 1; back >= 0; back--)
            {
                var day = last.AddDays(-back);
                var computed = await totals.ComputeAsync(entity, day, null, null, false, cancellationToken).ConfigureAwait(false);
                if (computed.IsFailure)
                {
                    LogFailed(logger, day.ToString(), computed.Error!.ToString());
                    continue;
                }

                var control = computed.Value.ReceivableControl;
                if (control.Count == 0)
                {
                    continue;
                }

                try
                {
                    await finance.ExchangeAsync(
                        new ReconciliationExchangeRequest
                        {
                            Source = ReconciliationExchangeRequest.SourceValue.Bil,
                            Date = day,
                            Totals = [.. control.Select(c => new ReconSourceTotal
                            {
                                Currency = c.Currency,
                                Amount = c.Amount.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                                Count = c.Count,
                            })],
                        },
                        CommandOptions.New(),
                        cancellationToken).ConfigureAwait(false);
                    accepted++;
                }
                catch (DomainException ex)
                {
                    // A locked period refuses the push (PERIOD-LOCKED): its figures are final, so the day is skipped.
                    LogFailed(logger, day.ToString(), ex.Error.ToString());
                }
            }

            return accepted;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The BIL control totals of {Day} were not pushed to FIN: {Reason}")]
    private static partial void LogFailed(ILogger logger, string day, string reason);
}

/// <summary>The hosted job: pushes the control totals every <see cref="BillingOptions.FinControlTotalsIntervalSeconds"/> seconds (worker role only).</summary>
internal sealed partial class FinControlSweeper(FinControlPublisher publisher, IOptions<BillingOptions> options, ILogger<FinControlSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.FinControlTotalsIntervalSeconds)));
        do
        {
            try
            {
                await publisher.RunAsync(null, null, stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("bil-fin-control-totals");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("bil-fin-control-totals");
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The push of the BIL control totals failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
