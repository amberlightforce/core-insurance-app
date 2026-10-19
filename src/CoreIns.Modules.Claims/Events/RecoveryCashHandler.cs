using System.Globalization;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Commands.Recoveries;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Modules.Claims.Services;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Events;

/// <summary>
/// <c>bil.CashAllocated</c> for a claim recovery receivable → a system transaction set of RECOVERY lines through the claim
/// financial engine (REQ-CLM-145, -151; D-SL4-06) → <c>RecoveryRecorded</c> on approval. Evidence = receipt x receivable, so a
/// replay creates no second set. The case closes when the recovered total reaches the demanded amount.
/// </summary>
internal sealed class CashAllocatedHandler(
    ClaimsDbContext db, CoreIns.Platform.Context.RequestContext context, RecoveryCashBuffer buffer, IClaimFinancialEngine engine, RecoveryReader reader, IClock clock, IOptions<ClaimsOptions> options)
    : IEventHandler<CashAllocatedV1>
{
    public const string Name = "CLM.CashAllocated.RecordRecovery";

    public async Task HandleAsync(EventEnvelope envelope, CashAllocatedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.RecoveryId is not { } rawRecovery || payload.ClaimId is not { } claimId || payload.ReceivableId is not { } receivableId
            || payload.ReceivableAmount is not { } amount || !amount.IsPositive)
        {
            return;
        }

        var recoveryId = new RecoveryId(rawRecovery);
        var recovery = await db.Recoveries.SingleOrDefaultAsync(r => r.RecoveryId == recoveryId && r.ClaimId == claimId && r.ReceivableId == receivableId, cancellationToken)
            .ConfigureAwait(false);
        if (recovery is null)
        {
            return;
        }

        var reference = string.Create(CultureInfo.InvariantCulture, $"{payload.ReceiptId.Value:D}:{receivableId:D}");
        buffer.Put(reference, recoveryId, amount.Amount);
        // The outbox marker makes the event exactly-once; the pipeline still wants a key for the nested build/submit, derived from the evidence.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("clm.recovery.cash|" + reference));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        using (CoreIns.Platform.Context.InProcessCommands.Use(context, IdempotencyKey.From(new Guid(bytes)), dryRun: false))
        {
            await engine.SubmitSystemSetAsync(claimId, new ClaimFinancialEvidence("BIL_ALLOCATION", reference), cancellationToken).ConfigureAwait(false);
        }

        if (RecoverySupport.IsOpen(recovery.Status))
        {
            var recovered = (await reader.TotalsAsync([recoveryId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(recoveryId).Recovered;
            var now = clock.Now;
            var today = options.Value.DateOf(now);
            RecoverySupport.AddMilestone(recovery, "CASH_RECEIVED", today, now, "SERVICE:clm-financial-engine", reference);
            if (recovered >= recovery.ExpectedAmount)
            {
                recovery.Status = "CLOSED";
                RecoverySupport.AddMilestone(recovery, "FULLY_RECOVERED", today, now, "SERVICE:clm-financial-engine", null);
            }

            RecoverySupport.Touch(recovery, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
