using System.Globalization;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Contracts.Events;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Accounting;

/// <summary>The statuses of a cash call (BO-RI-15).</summary>
internal static class CashCallStatus
{
    public const string Raised = "RAISED";
    public const string Sent = "SENT";
    public const string PartiallySettled = "PARTIALLY_SETTLED";
    public const string Settled = "SETTLED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>Reading, locking and mapping cash calls.</summary>
internal static class CashCallSupport
{
    /// <summary>Source kind of the positive RECOVERY account item a receipt posts.</summary>
    public const string ReceiptSourceKind = "CASH_CALL_RECEIPT";

    /// <summary>The call row, tracked and row-locked until the command's transaction ends; null when not found in the legal entity.</summary>
    public static async Task<CashCallRow?> LockAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, Guid cashCallId, CancellationToken cancellationToken)
    {
        var rows = await db.CashCalls
            .FromSql($"SELECT * FROM ri.cash_call WHERE cash_call_id = {cashCallId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public static async Task<CashCallView> ViewAsync(ReinsuranceDbContext db, CashCallRow row, BusinessDate today, CancellationToken cancellationToken)
    {
        var lines = await db.CashCallLines.AsNoTracking().Where(l => l.CashCallId == row.CashCallId).OrderBy(l => l.ReinsurerPartyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var receipts = await db.CashCallReceipts.AsNoTracking().Where(r => r.CashCallId == row.CashCallId).OrderBy(r => r.CreatedAt).ThenBy(r => r.ReceiptId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToView(row, lines, receipts, today);
    }

    public static CashCallView ToView(CashCallRow row, IReadOnlyList<CashCallLineRow> lines, IReadOnlyList<CashCallReceiptRow> receipts, BusinessDate today)
    {
        var currency = row.Currency;
        var received = lines.Sum(l => l.Received);
        var outstanding = row.Status == CashCallStatus.Cancelled ? 0m : row.Total - received;
        var days = outstanding > 0m ? Math.Max(0, today.Value.DayNumber - row.DueDate.Value.DayNumber) : 0;
        return new CashCallView
        {
            CashCallId = row.CashCallId, ContractId = row.ContractId, Occurrence = row.SourceKey, Status = Enum.Parse<CashCallView.StatusValue>(row.Status.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
            Total = AccountingViews.Eur(row.Total, currency), Threshold = AccountingViews.Eur(row.Threshold, currency), Received = AccountingViews.Eur(received, currency),
            Outstanding = AccountingViews.Eur(outstanding, currency), DueDate = row.DueDate, DaysOverdue = days, AgingBucket = Bucket(outstanding, days),
            Lines =
            [
                .. lines.Select(l => new CashCallLine
                {
                    ReinsurerPartyId = new PartyId(l.ReinsurerPartyId), Amount = AccountingViews.Eur(l.Amount, currency), Received = AccountingViews.Eur(l.Received, currency),
                    Outstanding = AccountingViews.Eur(row.Status == CashCallStatus.Cancelled ? 0m : l.Amount - l.Received, currency),
                }),
            ],
            Receipts =
            [
                .. receipts.Select(r => new CashCallReceipt
                {
                    ReceiptId = new PaymentId(r.ReceiptId), ReinsurerPartyId = new PartyId(r.ReinsurerPartyId), Amount = AccountingViews.Eur(r.Amount, currency), BankReference = r.BankReference,
                    ReceivedOn = r.ReceivedOn, ItemId = r.ItemId, CreatedAt = r.CreatedAt, CreatedBy = r.CreatedBy,
                }),
            ],
            RaisedAt = row.RaisedAt, RaisedBy = row.RaisedBy, SentAt = row.SentAt, SentBy = row.SentBy, CancelledAt = row.CancelledAt, CancelledBy = row.CancelledBy,
            CancelReason = row.CancelReason, SettledAt = row.SettledAt,
        };
    }

    private static CashCallView.AgingBucketValue Bucket(decimal outstanding, int days) => outstanding <= 0m
        ? CashCallView.AgingBucketValue.Closed
        : days switch
        {
            0 => CashCallView.AgingBucketValue.NotDue,
            <= 30 => CashCallView.AgingBucketValue.D130,
            <= 60 => CashCallView.AgingBucketValue.D3160,
            <= 90 => CashCallView.AgingBucketValue.D6190,
            _ => CashCallView.AgingBucketValue.D90Plus,
        };

    public static CommandAuditFacts Facts(Guid cashCallId, Guid? contract, object before, object after, bool ok) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.RI, "CashCall", cashCallId.ToString("D")),
        BusinessKeys = contract is { } id
            ? BusinessKeys.Empty.With("cashCallId", cashCallId.ToString("D")).With("riContractId", id.ToString("D"))
            : BusinessKeys.Empty.With("cashCallId", cashCallId.ToString("D")),
        Changes = ok ? AuditDiff.Compute(before, after) : AuditDiff.Compute(before, before),
    };

    public static DomainError Wrong(CashCallRow row, string action) => AccountingGuards.State($"The cash call is {row.Status}; it cannot be {action}.");
}

/// <summary>Reads of the cash calls with their aging (the reminder list: what is open and how late it is).</summary>
internal sealed class CashCallReader(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options)
{
    public async Task<Result<CashCallListResponse>> ListAsync(Guid? contractId, string? status, bool openOnly, int? limit, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        var query = db.CashCalls.AsNoTracking().Where(c => c.LegalEntityId == legalEntity);
        if (contractId is { } contract)
        {
            var id = new RiContractId(contract);
            query = query.Where(c => c.ContractId == id);
        }

        if (!string.IsNullOrEmpty(status))
        {
            var wanted = status.ToUpperInvariant();
            if (wanted is not (CashCallStatus.Raised or CashCallStatus.Sent or CashCallStatus.PartiallySettled or CashCallStatus.Settled or CashCallStatus.Cancelled))
            {
                return RiErrors.Validation("status", "STATUS_INVALID", "The status filter is not valid.");
            }

            query = query.Where(c => c.Status == wanted);
        }

        if (openOnly)
        {
            query = query.Where(c => c.Status == CashCallStatus.Raised || c.Status == CashCallStatus.Sent || c.Status == CashCallStatus.PartiallySettled);
        }

        var rows = await query.OrderByDescending(c => c.RaisedAt).ThenByDescending(c => c.CashCallId).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(r => r.CashCallId).ToList();
        var lines = (await db.CashCallLines.AsNoTracking().Where(l => ids.Contains(l.CashCallId)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(l => l.CashCallId);
        var receipts = (await db.CashCallReceipts.AsNoTracking().Where(r => ids.Contains(r.CashCallId)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(r => r.CashCallId);
        return new CashCallListResponse
        {
            Items = [.. rows.Select(r => CashCallSupport.ToView(r, [.. lines[r.CashCallId].OrderBy(l => l.ReinsurerPartyId)], [.. receipts[r.CashCallId].OrderBy(x => x.CreatedAt)], today))],
        };
    }
}

/// <summary><c>ri.CashCall.raise</c> as a command of the platform pipeline.</summary>
internal sealed record RaiseCashCall(CashCallRaiseRequest Request) : ICommand<CashCallRaiseResponse>;

/// <summary>Shape rules: the occurrence is a claim id.</summary>
internal sealed class RaiseCashCallValidator : AbstractValidator<RaiseCashCall>
{
    public RaiseCashCallValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Occurrence).Must(o => Guid.TryParse(o, out _)).WithErrorCode("OCCURRENCE").When(c => c.Request is not null);
    }
}

/// <summary>
/// Raises the cash call of one claim on a contract when its paid recoveries exceed the threshold (BO-RI-15). The amounts are the net RECOVERY
/// account items per reinsurer (BO-RI-11; each recovery was already split over the participants with the shared cent allocator, so the parts sum
/// to the total), reversals included. Idempotent on (contract, claim): raising again returns the live call. Publishes <c>CashCallRaised</c>.
/// TODO(hardening): the threshold is one configured default per legal entity (legalStatus Unverified); read it per contract and layer from the version in force at the loss date.
/// </summary>
internal sealed class RaiseCashCallHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IOptions<ReinsuranceOptions> options,
    IEventPublisher events) : ICommandHandler<RaiseCashCall, CashCallRaiseResponse>
{
    public async Task<Result<CashCallRaiseResponse>> HandleAsync(RaiseCashCall command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var contract = await ContractSupport.LockAsync(db, legalEntity, request.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        var claim = Guid.Parse(request.Occurrence);
        var key = claim.ToString("D");
        var existing = await db.CashCalls.SingleOrDefaultAsync(c => c.ContractId == contract.ContractId && c.SourceKey == key && c.Status != CashCallStatus.Cancelled, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new CashCallRaiseResponse { CashCall = await CashCallSupport.ViewAsync(db, existing, today, cancellationToken).ConfigureAwait(false), Replayed = true };
        }

        var due = request.DueDate ?? new BusinessDate(today.Value.AddDays(options.Value.CashCallDueDays));
        if (due < today)
        {
            return RiErrors.Validation("dueDate", "DUE_DATE", "The due date cannot be in the past.");
        }

        var recoveryIds = await db.Recoveries.AsNoTracking().Where(r => r.ContractId == contract.ContractId.Value && r.ClaimId == claim).Select(r => r.RecoveryId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var sourceIds = recoveryIds.Select(i => i.ToString("D")).ToList();
        var items = await db.AccountItems.AsNoTracking()
            .Where(i => i.ContractId == contract.ContractId && i.Kind == AccountItemKinds.Recovery && i.SourceKind == "RECOVERY" && sourceIds.Contains(i.SourceId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return AccountingGuards.State("The claim has no recovery account items on the contract; generate the technical account items first.");
        }

        var itemIds = items.Select(i => i.ItemId).ToList();
        var reversals = await db.AccountItems.AsNoTracking().Where(i => i.ReversalOf != null && itemIds.Contains(i.ReversalOf.Value)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var perReinsurer = items.Concat(reversals).GroupBy(i => i.ReinsurerPartyId).Select(g => (Party: g.Key, Amount: -g.Sum(i => i.Amount))).Where(p => p.Amount > 0m)
            .OrderBy(p => p.Party).ToList();
        var total = perReinsurer.Sum(p => p.Amount);
        var threshold = options.Value.CashCallThreshold;
        if (total <= threshold)
        {
            return AccountingGuards.State($"The paid recoveries of the claim ({AccountingViews.Fixed(total)}) do not exceed the cash call threshold ({AccountingViews.Fixed(threshold)}).");
        }

        var currency = contract.Currency.Trim();
        var row = new CashCallRow
        {
            CashCallId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ContractId = contract.ContractId, SourceKey = key, Currency = currency, Total = total, Threshold = threshold,
            DueDate = due, Status = CashCallStatus.Raised, RaisedAt = clock.Now, RaisedBy = context.Actor.ToString(),
        };
        db.CashCalls.Add(row);
        db.CashCallLines.AddRange(perReinsurer.Select(p => new CashCallLineRow { CashCallId = row.CashCallId, ReinsurerPartyId = p.Party, Amount = p.Amount, Received = 0m }));
        var currencyValue = Currency.FromCode(currency);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(CashCallRaisedV1.Descriptor),
            ContractLifecycleService.AggregateType,
            contract.ContractId.Value.ToString("D"),
            new CashCallRaisedV1
            {
                CashCallId = row.CashCallId, Occurrence = key, DueDate = due,
                AmountsPerParticipant = [.. perReinsurer.Select(p => new ParticipantTotal { ParticipantPartyId = new PartyId(p.Party), Amount = new Money(p.Amount, currencyValue) })],
            },
            BusinessKeys.Empty.With("riContractId", contract.ContractId.Value.ToString("D")).With("cashCallId", row.CashCallId.ToString("D"))));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("Another request raised the cash call; try again.");
        }

        return new CashCallRaiseResponse { CashCall = await CashCallSupport.ViewAsync(db, row, today, cancellationToken).ConfigureAwait(false), Replayed = false };
    }
}

/// <summary>Audit facts of <c>ri.CashCall.raise</c>.</summary>
internal sealed class RaiseCashCallAuditor : ICommandAuditor<RaiseCashCall, CashCallRaiseResponse>
{
    public CommandAuditFacts Describe(RaiseCashCall command, Result<CashCallRaiseResponse>? result) =>
        result is { IsSuccess: true } ok
            ? CashCallSupport.Facts(
                ok.Value.CashCall.CashCallId, command.Request.ContractId.Value, new { status = "NONE" },
                new { status = "RAISED", total = ok.Value.CashCall.Total.Amount.ToString(CultureInfo.InvariantCulture), replayed = ok.Value.Replayed }, true)
            : new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.RI, "CashCall", "new"),
                BusinessKeys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D")),
            };
}

/// <summary><c>ri.CashCall.issue</c> as a command of the platform pipeline.</summary>
internal sealed record IssueCashCall(CashCallIssueRequest Request) : ICommand<CashCallIssueResponse>;

/// <summary>Shape rules: a body.</summary>
internal sealed class IssueCashCallValidator : AbstractValidator<IssueCashCall>
{
    public IssueCashCallValidator() => RuleFor(c => c.Request).NotNull();
}

/// <summary>
/// Sends a RAISED cash call to the reinsurers (RAISED to SENT).
/// TODO(hardening): render the cash call notice as a DOC document through the Documents contract once the template catalogue (BO-DOC-05) and the composer (BO-DOC-06) are in.
/// </summary>
internal sealed class IssueCashCallHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options)
    : ICommandHandler<IssueCashCall, CashCallIssueResponse>
{
    public async Task<Result<CashCallIssueResponse>> HandleAsync(IssueCashCall command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await CashCallSupport.LockAsync(db, legalEntity, command.Request.CashCallId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("cash call");
        }

        if (row.Status != CashCallStatus.Raised)
        {
            return CashCallSupport.Wrong(row, "sent");
        }

        row.Status = CashCallStatus.Sent;
        row.SentAt = clock.Now;
        row.SentBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CashCallIssueResponse { CashCall = await CashCallSupport.ViewAsync(db, row, clock.Now.ToBusinessDate(options.Value.Zone), cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of <c>ri.CashCall.issue</c>.</summary>
internal sealed class IssueCashCallAuditor : ICommandAuditor<IssueCashCall, CashCallIssueResponse>
{
    public CommandAuditFacts Describe(IssueCashCall command, Result<CashCallIssueResponse>? result) =>
        CashCallSupport.Facts(command.Request.CashCallId, null, new { status = "RAISED" }, new { status = "SENT" }, result is { IsSuccess: true });
}

/// <summary><c>ri.CashCall.recordReceipt</c> as a command of the platform pipeline.</summary>
internal sealed record RecordCashCallReceipt(CashCallRecordReceiptRequest Request) : ICommand<CashCallRecordReceiptResponse>;

/// <summary>Shape rules: a bank reference.</summary>
internal sealed class RecordCashCallReceiptValidator : AbstractValidator<RecordCashCallReceipt>
{
    public RecordCashCallReceiptValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.BankReference).NotEmpty().MaximumLength(100).WithErrorCode("BANK_REFERENCE").When(c => c.Request is not null);
    }
}

/// <summary>
/// Records cash received from one reinsurer on a SENT or part-settled call (BO-RI-15). Idempotent on the bank reference per reinsurer (NX-EC-08): the
/// same reference with the same call and amount is a replay that books nothing; a different one is refused. A partial receipt keeps the call
/// open (PARTIALLY_SETTLED); the last one settles it. The cash posts a positive RECOVERY account item that lowers what the reinsurer owes: it
/// lands in the receipt period, or in the period after the latest live statement of the reinsurer, so an issued statement is never edited.
/// TODO(hardening): post the receipt through the BIL receivable (RI_SETTLEMENT source, same pattern as the CLM recoveries, D-SL4-06) once BIL serves that source.
/// </summary>
internal sealed class RecordCashCallReceiptHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options)
    : ICommandHandler<RecordCashCallReceipt, CashCallRecordReceiptResponse>
{
    public async Task<Result<CashCallRecordReceiptResponse>> HandleAsync(RecordCashCallReceipt command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await CashCallSupport.LockAsync(db, legalEntity, request.CashCallId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("cash call");
        }

        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        var party = request.ReinsurerPartyId.Value;
        var reference = request.BankReference.Trim();
        var previous = await db.CashCallReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.LegalEntityId == legalEntity && r.ReinsurerPartyId == party && r.BankReference == reference, cancellationToken)
            .ConfigureAwait(false);
        if (previous is not null)
        {
            return previous.CashCallId == row.CashCallId && previous.Amount == request.Amount.Amount
                ? new CashCallRecordReceiptResponse { CashCall = await CashCallSupport.ViewAsync(db, row, today, cancellationToken).ConfigureAwait(false), Replayed = true }
                : AccountingGuards.State("The bank reference was already recorded for a different receipt.");
        }

        if (row.Status is not (CashCallStatus.Sent or CashCallStatus.PartiallySettled))
        {
            return CashCallSupport.Wrong(row, "settled by a receipt");
        }

        if (request.Amount.Currency.Code != row.Currency.Trim())
        {
            return RiErrors.Validation("amount", "CURRENCY_MISMATCH", $"The receipt must be in the cash call currency {row.Currency.Trim()}.");
        }

        if (AccountingGuards.CheckCents("amount", request.Amount, allowZero: false) is { } amountError)
        {
            return amountError;
        }

        var receivedOn = request.ReceivedOn ?? today;
        if (receivedOn > today)
        {
            return RiErrors.Validation("receivedOn", "RECEIVED_ON", "The receipt date cannot be in the future.");
        }

        var lines = await db.CashCallLines.Where(l => l.CashCallId == row.CashCallId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var line = lines.SingleOrDefault(l => l.ReinsurerPartyId == party);
        if (line is null)
        {
            return RiErrors.Validation("reinsurerPartyId", "NOT_A_PARTICIPANT", "The reinsurer has no share in the cash call.");
        }

        if (request.Amount.Amount > line.Amount - line.Received)
        {
            return AccountingGuards.State($"The receipt exceeds what the reinsurer still owes ({AccountingViews.Fixed(line.Amount - line.Received)}).");
        }

        var contractYear = await db.Contracts.AsNoTracking().Where(c => c.ContractId == row.ContractId).Select(c => c.ContractYear).SingleAsync(cancellationToken).ConfigureAwait(false);
        var period = await PeriodAsync(legalEntity, party, row.Currency, receivedOn, cancellationToken).ConfigureAwait(false);
        var receiptId = Guid.CreateVersion7();
        var item = new AccountItemRow
        {
            ItemId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ContractId = row.ContractId, ContractYear = contractYear, LayerNo = null, ReinsurerPartyId = party,
            Kind = AccountItemKinds.Recovery, SourceKind = CashCallSupport.ReceiptSourceKind, SourceId = receiptId.ToString("D"), SourceGroup = receiptId.ToString("D"),
            Currency = row.Currency.Trim(), Amount = request.Amount.Amount, Period = period, ItemDate = receivedOn, Reason = "Cash call " + row.CashCallId.ToString("D"),
            CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(),
        };
        db.AccountItems.Add(item);
        db.CashCallReceipts.Add(new CashCallReceiptRow
        {
            ReceiptId = receiptId, CashCallId = row.CashCallId, LegalEntityId = legalEntity, ReinsurerPartyId = party, Amount = request.Amount.Amount, BankReference = reference,
            ReceivedOn = receivedOn, ItemId = item.ItemId, CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(),
        });
        line.Received += request.Amount.Amount;
        var settled = lines.All(l => l.Received == l.Amount);
        row.Status = settled ? CashCallStatus.Settled : CashCallStatus.PartiallySettled;
        row.SettledAt = settled ? clock.Now : null;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("The bank reference was recorded by another request; try again.");
        }

        return new CashCallRecordReceiptResponse { CashCall = await CashCallSupport.ViewAsync(db, row, today, cancellationToken).ConfigureAwait(false), Replayed = false };
    }

    /// <summary>The period of the receipt date, moved to the month after the reinsurer's latest live statement when that statement already covers it.</summary>
    private async Task<string> PeriodAsync(LegalEntityId legalEntity, Guid party, string currency, BusinessDate receivedOn, CancellationToken cancellationToken)
    {
        var period = AccountItemViews.PeriodOf(receivedOn);
        var latest = await db.Statements.AsNoTracking()
            .Where(s => s.LegalEntityId == legalEntity && s.ReinsurerPartyId == party && s.Currency == currency && s.Status != StatementStatus.Superseded)
            .Select(s => s.Period).OrderByDescending(p => p).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null || string.CompareOrdinal(latest, period) < 0)
        {
            return period;
        }

        var year = int.Parse(latest.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var month = int.Parse(latest.AsSpan(5, 2), CultureInfo.InvariantCulture);
        return new DateOnly(year, month, 1).AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }
}

/// <summary>Audit facts of <c>ri.CashCall.recordReceipt</c>.</summary>
internal sealed class RecordCashCallReceiptAuditor : ICommandAuditor<RecordCashCallReceipt, CashCallRecordReceiptResponse>
{
    public CommandAuditFacts Describe(RecordCashCallReceipt command, Result<CashCallRecordReceiptResponse>? result) =>
        CashCallSupport.Facts(
            command.Request.CashCallId, null, new { received = "0" },
            new { received = command.Request.Amount.Amount.ToString(CultureInfo.InvariantCulture), bankReference = command.Request.BankReference, reinsurerPartyId = command.Request.ReinsurerPartyId.Value.ToString("D") },
            result is { IsSuccess: true, Value.Replayed: false });
}

/// <summary><c>ri.CashCall.cancel</c> as a command of the platform pipeline.</summary>
internal sealed record CancelCashCall(CashCallCancelRequest Request) : ICommand<CashCallCancelResponse>;

/// <summary>Shape rules: a reason.</summary>
internal sealed class CancelCashCallValidator : AbstractValidator<CancelCashCall>
{
    public CancelCashCallValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1000).WithErrorCode("REASON").When(c => c.Request is not null);
    }
}

/// <summary>Maker-checker: a RAISED or SENT call without receipts is cancelled by someone who neither raised nor sent it (<c>RI-ERR-SOD</c>); refused once cash arrived.</summary>
internal sealed class CancelCashCallHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options)
    : ICommandHandler<CancelCashCall, CashCallCancelResponse>
{
    public async Task<Result<CashCallCancelResponse>> HandleAsync(CancelCashCall command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await CashCallSupport.LockAsync(db, legalEntity, command.Request.CashCallId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("cash call");
        }

        if (row.Status is not (CashCallStatus.Raised or CashCallStatus.Sent) || await db.CashCallReceipts.AnyAsync(r => r.CashCallId == row.CashCallId, cancellationToken).ConfigureAwait(false))
        {
            return AccountingGuards.State($"The cash call is {row.Status}; it cannot be cancelled once cash was received or it is closed.");
        }

        var keys = ContractSupport.ActorKeys(context);
        if (keys.Contains(row.RaisedBy, StringComparer.Ordinal) || row.SentBy is { } sender && keys.Contains(sender, StringComparer.Ordinal))
        {
            return RiErrors.Sod("The person who raised or sent the cash call cannot cancel it.");
        }

        row.Status = CashCallStatus.Cancelled;
        row.CancelledAt = clock.Now;
        row.CancelledBy = context.Actor.ToString();
        row.CancelReason = command.Request.Reason.Trim();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CashCallCancelResponse { CashCall = await CashCallSupport.ViewAsync(db, row, clock.Now.ToBusinessDate(options.Value.Zone), cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of <c>ri.CashCall.cancel</c>.</summary>
internal sealed class CancelCashCallAuditor : ICommandAuditor<CancelCashCall, CashCallCancelResponse>
{
    public CommandAuditFacts Describe(CancelCashCall command, Result<CashCallCancelResponse>? result) =>
        CashCallSupport.Facts(command.Request.CashCallId, null, new { status = "OPEN" }, new { status = "CANCELLED", reason = command.Request.Reason }, result is { IsSuccess: true });
}
