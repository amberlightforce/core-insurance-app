using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Commands.FriendlySettlement;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Events;

/// <summary>Finds the net row an event names: the statement reference is the row id in "D" text (shared with BIL and FIN).</summary>
internal static class FsNetRows
{
    public static async Task<FsStatementRow?> ByReferenceAsync(ClaimsDbContext db, string? reference, CancellationToken cancellationToken) =>
        Guid.TryParseExact(reference, "D", out var id)
            ? await db.FsStatements.SingleOrDefaultAsync(s => s.FsStatementId == id, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Back to Pending (REQ-CLM-264): a rejected or stopped net can be approved again.</summary>
    public static void ReturnToPending(FsStatementRow row, Instant now)
    {
        row.Status = FsStatements.Pending;
        row.DisbursementId = null;
        row.ReceivableId = null;
        row.ApprovalRequestId = null;
        row.UpdatedAt = now;
        row.RecordVersion++;
    }
}

/// <summary>
/// <c>plt.ApprovalDecided</c> for <c>CLM.FS_NET_SETTLEMENT</c> (REQ-CLM-264): approved → the net goes to BIL with the approval as
/// evidence; rejected, withdrawn or expired → the net stays Pending without the approval, so it can be approved again.
/// </summary>
internal sealed class FsNetApprovalDecidedHandler(ClaimsDbContext db, FsNetHandOver handOver, IClock clock) : IEventHandler<ApprovalDecidedV1>
{
    public const string Name = "CLM.ApprovalDecided.ApplyFsNet";

    public async Task HandleAsync(EventEnvelope envelope, ApprovalDecidedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.ApprovalType != DisbursementApproval.FsNetType)
        {
            return;
        }

        var requestId = new ApprovalRequestId(payload.RequestId);
        var row = await db.FsStatements.SingleOrDefaultAsync(s => s.ApprovalRequestId == requestId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.Status != FsStatements.Pending)
        {
            return; // replay or another module's request
        }

        if (payload.Decision != "APPROVED")
        {
            FsNetRows.ReturnToPending(row, clock.Now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var requested = await handOver.RequestAsync(row, $"PLT/ApprovalRequest/{payload.RequestId:D}", cancellationToken).ConfigureAwait(false);
        if (requested.IsFailure)
        {
            // BIL refused (e.g. no verified clearing account): throw so the event is retried / dead-lettered, never silently dropped.
            throw new DomainException(requested.Error!);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>bil.DisbursementCleared</c> for an FS_CLEARING net → the net is Settled and its cases reconciled (REQ-CLM-160).</summary>
internal sealed class FsDisbursementClearedHandler(ClaimsDbContext db, FsNetSettler settler) : IEventHandler<DisbursementClearedV1>
{
    public const string Name = "CLM.DisbursementCleared.SettleFsNet";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementClearedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SourceType != DisbursementCodes.FsClearing)
        {
            return;
        }

        var row = await FsNetRows.ByReferenceAsync(db, payload.SourceId, cancellationToken).ConfigureAwait(false);
        if (row is not null && row.Status == FsStatements.Requested && row.DisbursementId == payload.DisbursementId)
        {
            await settler.SettleAsync(row, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary><c>bil.DisbursementRejected</c> for an FS_CLEARING net (release refused) → the net returns to Pending (REQ-CLM-264).</summary>
internal sealed class FsDisbursementRejectedHandler(ClaimsDbContext db, IClock clock) : IEventHandler<DisbursementRejectedV1>
{
    public const string Name = "CLM.DisbursementRejected.ReturnFsNet";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementRejectedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SourceType != DisbursementCodes.FsClearing)
        {
            return;
        }

        var row = await FsNetRows.ByReferenceAsync(db, payload.SourceId, cancellationToken).ConfigureAwait(false);
        if (row is not null && row.Status == FsStatements.Requested && row.DisbursementId == payload.DisbursementId)
        {
            FsNetRows.ReturnToPending(row, clock.Now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary><c>bil.DisbursementStopped</c> for an FS_CLEARING net → the net returns to Pending (REQ-CLM-264).</summary>
internal sealed class FsDisbursementStoppedHandler(ClaimsDbContext db, IClock clock) : IEventHandler<DisbursementStoppedV1>
{
    public const string Name = "CLM.DisbursementStopped.ReturnFsNet";

    public async Task HandleAsync(EventEnvelope envelope, DisbursementStoppedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SourceType != DisbursementCodes.FsClearing)
        {
            return;
        }

        var row = await FsNetRows.ByReferenceAsync(db, payload.SourceId, cancellationToken).ConfigureAwait(false);
        if (row is not null && row.Status == FsStatements.Requested && row.DisbursementId == payload.DisbursementId)
        {
            FsNetRows.ReturnToPending(row, clock.Now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// <c>bil.CashAllocated</c> for an FS net receivable (source FS_CLEARING, statement reference) → the net is Settled once the cash covers it.
/// TODO(hardening): partial collections keep the net Requested; only a full allocation settles it.
/// </summary>
internal sealed class FsReceivableCollectedHandler(ClaimsDbContext db, FsNetSettler settler) : IEventHandler<CashAllocatedV1>
{
    public const string Name = "CLM.CashAllocated.SettleFsNet";

    public async Task HandleAsync(EventEnvelope envelope, CashAllocatedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.SourceType != DisbursementCodes.FsClearing || payload.ReceivableId is not { } receivableId || payload.ReceivableAmount is not { } cash)
        {
            return;
        }

        var row = await FsNetRows.ByReferenceAsync(db, payload.StatementRef, cancellationToken).ConfigureAwait(false);
        if (row is not null && row.Status == FsStatements.Requested && row.ReceivableId == receivableId && cash.Amount >= row.NetAmount)
        {
            await settler.SettleAsync(row, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
