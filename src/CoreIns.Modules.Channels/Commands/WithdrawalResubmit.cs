using CoreIns.Modules.Channels.Contracts.Events;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Channels.Commands;

/// <summary><c>chn.Withdrawal.resubmit</c> (sweeper only): hands a request that is still CONFIRMED to POL again.</summary>
internal sealed record ResubmitWithdrawal(Guid WithdrawalRequestId) : ICommand<string>;

/// <summary>
/// Re-submits one CONFIRMED request. POL is idempotent on the channel reference (R-89), so a repeat is one POL effect. The row is
/// re-read in the command's own transaction and only a CONFIRMED one moves. The acknowledgement letter is requested again too
/// (DOC de-duplicates). Returns the resulting status, or <see cref="Skipped"/> when nothing was due.
/// </summary>
internal sealed class ResubmitWithdrawalHandler(
    ChannelsDbContext db,
    IClock clock,
    IEventPublisher events,
    IWithdrawalPolicyPort policies,
    IServiceProvider services,
    IOptions<ChannelsOptions> options) : ICommandHandler<ResubmitWithdrawal, string>
{
    public const string Skipped = "SKIPPED";

    public async Task<Result<string>> HandleAsync(ResubmitWithdrawal command, CancellationToken cancellationToken)
    {
        var row = await db.WithdrawalRequests.SingleOrDefaultAsync(r => r.WithdrawalRequestId == command.WithdrawalRequestId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.Status != WithdrawalStatuses.Confirmed)
        {
            return Skipped;
        }

        var now = clock.Now;
        switch (await policies.SubmitAsync(row.PolicyId, row.ConfirmedAt ?? now, row.ChannelRef, cancellationToken).ConfigureAwait(false))
        {
            case PolicySubmitOutcome.Accepted:
                row.Status = WithdrawalStatuses.Submitted;
                row.PolSubmittedAt = now;
                break;
            case PolicySubmitOutcome.Expired:
                row.Status = WithdrawalStatuses.Rejected;
                row.DeclineCode = WithdrawalStatuses.RightExpired;
                row.ClosedAt = now;
                WithdrawalSupport.PublishClosed(events, row, WithdrawalRequestClosedV1.OutcomeValue.Rejected, now);
                break;
            default:
                // Still no POL binding: stays CONFIRMED for the next sweep.
                break;
        }

        row.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WithdrawalSupport.AcknowledgeAsync(services, policies, options.Value, row, row.ConfirmedAt ?? now, cancellationToken).ConfigureAwait(false);
        return row.Status;
    }
}
