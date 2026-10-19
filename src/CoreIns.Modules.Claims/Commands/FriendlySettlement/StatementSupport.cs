using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Claims.Commands.Recoveries;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using BilLine = CoreIns.Modules.Billing.Contracts.Api.FsClearingLine;
using Line = CoreIns.Modules.Claims.Contracts.Api.FsStatementLine;

namespace CoreIns.Modules.Claims.Commands.FriendlySettlement;

/// <summary>
/// Statement rows and views (REQ-CLM-160, SL4-CLM-FS-STATEMENT). One <c>clm.fs_statement</c> row exists per legal entity, calendar
/// month and counterparty (the net settlement); the API's statement is the set of the rows of a month, and its id is the id of the
/// first row. The id of a counterparty row, as lower-case "D" text, is the <b>statement reference</b> shared with BIL (FS_CLEARING
/// statement reference / receivable statement ref) and FIN (GL-2515 netting).
/// </summary>
internal static class FsStatements
{
    public const string Pending = "PENDING";
    public const string Requested = "REQUESTED";
    public const string Settled = "SETTLED";
    public const string Payable = "PAYABLE";
    public const string Receivable = "RECEIVABLE";
    public const string EvidenceKind = "FS_STATEMENT_LINE";

    /// <summary>The statement reference of a counterparty row.</summary>
    public static string Ref(Guid statementId) => statementId.ToString("D", CultureInfo.InvariantCulture);

    public static string PeriodCode(DateOnly firstDay) => firstDay.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>True when the range is one calendar month <c>[first day, first day of the next month)</c>.</summary>
    public static bool TryMonth(DateRange range, out DateOnly firstDay)
    {
        firstDay = range.Start.Value;
        return range.End is { } end && firstDay.Day == 1 && end.Value == firstDay.AddMonths(1);
    }

    public static DateRange PeriodOf(string code)
    {
        var first = DateOnly.ParseExact(code + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return DateRange.Of(new BusinessDate(first), new BusinessDate(first.AddMonths(1)));
    }

    /// <summary>Σ receivable lines − Σ payable lines, as an absolute amount and a direction.</summary>
    public static (decimal Amount, string Direction) NetOf(IEnumerable<FsStatementLineRow> lines)
    {
        var net = lines.Sum(l => l.Direction == Receivable ? l.Amount : -l.Amount);
        return (Math.Abs(net), net < 0m ? Payable : Receivable);
    }

    public static bool IsMatched(FsStatementLineRow line) => line.MatchStatus is "MATCHED" or "SETTLED";

    /// <summary>A row accepts new lines and a net only while it is Pending and has no open approval.</summary>
    public static bool IsOpen(FsStatementRow row) => row.Status == Pending && row.ApprovalRequestId is null;

    /// <summary>A deterministic key for the nested commands of one statement line (an in-process command needs its own key).</summary>
    public static IdempotencyKey KeyFor(string scope, Guid id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(scope + "|" + Ref(id))).AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return IdempotencyKey.From(new Guid(bytes));
    }

    /// <summary>The month's rows, first row first (stable order: creation, then id).</summary>
    public static async Task<List<FsStatementRow>> RowsAsync(ClaimsDbContext db, LegalEntityId entity, Guid anyRowId, CancellationToken cancellationToken)
    {
        var period = await db.FsStatements.AsNoTracking().Where(s => s.FsStatementId == anyRowId && s.LegalEntityId == entity)
            .Select(s => s.Period).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return period is null
            ? []
            : await db.FsStatements.AsNoTracking().Where(s => s.LegalEntityId == entity && s.Period == period)
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.FsStatementId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<FsStatementView?> ViewAsync(ClaimsDbContext db, LegalEntityId entity, Guid anyRowId, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(db, entity, anyRowId, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : await ViewOfAsync(db, rows, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<FsStatementView> ViewOfAsync(ClaimsDbContext db, IReadOnlyList<FsStatementRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.FsStatementId).ToList();
        var lines = await db.FsStatementLines.AsNoTracking().Where(l => ids.Contains(l.FsStatementId)).OrderBy(l => l.ExternalReference)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var caseIds = lines.Where(l => l.FsCaseId != null).Select(l => l.FsCaseId!.Value).Distinct().ToList();
        var values = await db.FsCases.AsNoTracking().Where(c => caseIds.Contains(c.FsCaseId)).ToDictionaryAsync(c => c.FsCaseId, c => c.ClearingValue, cancellationToken)
            .ConfigureAwait(false);
        var codes = rows.ToDictionary(r => r.FsStatementId, r => FsCases.Code(r.CounterpartyInsurerPartyId));
        var exceptions = lines.Count(l => !IsMatched(l));
        return new FsStatementView
        {
            FsStatementId = rows[0].FsStatementId,
            Period = PeriodOf(rows[0].Period),
            Reconciliation = exceptions == 0 ? FsStatementView.ReconciliationValue.Matched : FsStatementView.ReconciliationValue.ExceptionsOpen,
            Lines = [.. lines.Select(l => LineView(l, codes[l.FsStatementId], values))],
            Nets = [.. rows.OrderBy(r => codes[r.FsStatementId], StringComparer.Ordinal).Select(NetView)],
            ExceptionCount = exceptions,
            ImportedAt = rows[0].CreatedAt,
            RecordVersion = rows.Sum(r => r.RecordVersion),
        };
    }

    private static Line LineView(FsStatementLineRow l, string code, Dictionary<Guid, decimal?> values)
    {
        var status = IsMatched(l) ? Line.MatchStatusValue.Matched : l.MatchStatus == "EXCEPTION" ? Line.MatchStatusValue.Difference : Line.MatchStatusValue.Unmatched;
        Money? difference = null;
        if (status == Line.MatchStatusValue.Difference && l.FsCaseId is { } caseId && values.TryGetValue(caseId, out var value) && value is { } clearing)
        {
            difference = new Money(l.Amount - clearing, Currency.EUR);
        }

        return new Line
        {
            LineRef = l.ExternalReference, ClearingReference = l.ClearingReference,
            Direction = l.Direction == Payable ? Line.DirectionValue.Payable : Line.DirectionValue.Receivable,
            Amount = new Money(l.Amount, Currency.EUR), CounterpartyCode = code, MatchStatus = status,
            FsCaseId = l.FsCaseId is { } c ? new FsCaseId(c) : null, ClaimId = l.ClaimId, Difference = difference,
        };
    }

    public static FsNetView NetView(FsStatementRow r) => new()
    {
        CounterpartyCode = FsCases.Code(r.CounterpartyInsurerPartyId),
        CounterpartyPartyId = r.CounterpartyInsurerPartyId,
        Direction = r.Direction == Payable ? FsNetView.DirectionValue.Payable : FsNetView.DirectionValue.Receivable,
        Amount = new Money(r.NetAmount, Currency.EUR),
        Status = r.Status switch
        {
            Requested => FsNetStatus.Requested,
            Settled => FsNetStatus.Settled,
            "REJECTED" => FsNetStatus.Rejected,
            _ => r.ApprovalRequestId is null ? FsNetStatus.Pending : FsNetStatus.ApprovalRequested,
        },
        ApprovalRequestId = r.ApprovalRequestId,
        StatementRef = Ref(r.FsStatementId),
        BilReferenceType = r.DisbursementId is not null ? FsNetView.BilReferenceTypeValue.Disbursement : r.ReceivableId is not null ? FsNetView.BilReferenceTypeValue.Receivable : null,
        BilReferenceId = r.DisbursementId?.Value ?? r.ReceivableId,
    };
}

/// <summary>
/// Matches a statement line to its case by the clearing reference (REQ-CLM-160) and, when it matches, records the line's effect:
/// an own-insurer line records the FS recovery (a system set from <c>FS_STATEMENT_LINE</c> evidence), an at-fault line issues the
/// CLEARING payment (no disbursement; <c>PaymentIssued</c> with the statement and counterparty, which FIN posts against GL-2515).
/// Anything that does not fit is an exception with a reason code and blocks the counterparty's net.
/// </summary>
internal sealed class FsStatementMatcher(
    ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, IClaimFinancialEngine engine, IOptions<ClaimsOptions> options)
{
    public async Task MatchAsync(FsStatementLineRow line, FsStatementRow row, CancellationToken cancellationToken)
    {
        var entity = row.LegalEntityId;
        var fsCase = await db.FsCases.SingleOrDefaultAsync(c => c.LegalEntityId == entity && c.ClearingReference == line.ClearingReference, cancellationToken).ConfigureAwait(false);
        if (fsCase is null)
        {
            Mark(line, "UNMATCHED", "NO_CASE", null);
            return;
        }

        var own = fsCase.Role == FsCases.OwnRole;
        var reason =
            line.Direction != (own ? FsStatements.Receivable : FsStatements.Payable) ? "DIRECTION_MISMATCH"
            : fsCase.CounterpartyInsurerPartyId != row.CounterpartyInsurerPartyId ? "COUNTERPARTY_MISMATCH"
            : fsCase.StatementLineId is { } other && other != line.FsStatementLineId ? "CASE_ALREADY_MATCHED"
            : fsCase.ClearingValue != line.Amount ? "AMOUNT_DIFFERENCE"
            : null;
        if (reason is null && fsCase.StatementLineId is null && fsCase.Status != (own ? "SUBMITTED" : "ACCEPTED"))
        {
            reason = "CASE_STATE";
        }

        if (reason is not null)
        {
            Mark(line, "EXCEPTION", reason, fsCase);
            return;
        }

        var claim = await ClaimSupport.LoadAsync(db, entity, fsCase.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            Mark(line, "EXCEPTION", "CLAIM_NOT_FOUND", fsCase);
            return;
        }

        var now = clock.Now;
        if (own)
        {
            var recovery = fsCase.RecoveryId is { } recoveryId
                ? await db.Recoveries.SingleOrDefaultAsync(r => r.RecoveryId == recoveryId && r.ClaimId == fsCase.ClaimId, cancellationToken).ConfigureAwait(false)
                : null;
            if (recovery is null || fsCase.ExposureId is null || !RecoverySupport.IsOpen(recovery.Status)
                || (recovery.FsStatementId is { } known && known != row.FsStatementId))
            {
                Mark(line, "EXCEPTION", "RECOVERY_UNAVAILABLE", fsCase);
                return;
            }

            recovery.FsStatementId = row.FsStatementId;
            RecoverySupport.Touch(recovery, now);
            line.RecoveryId = recovery.RecoveryId;
            Link(line, fsCase, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // Evidence = this line; the amount is the stored line's, never a client's (PITFALLS 7). A replay creates no second set.
            using (context.Use(FsStatements.KeyFor("clm.fs.statement.line", line.FsStatementLineId), dryRun: false))
            {
                await engine.SubmitSystemSetAsync(fsCase.ClaimId, new ClaimFinancialEvidence(FsStatements.EvidenceKind, FsStatements.Ref(line.FsStatementLineId)), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var approved = Codes.Of(PaymentStatus.Approved);
        var payment = await db.ClaimPayments.Where(p => p.ClaimId == fsCase.ClaimId && p.ExposureId == fsCase.ExposureId && p.Method == "CLEARING" && p.LegalEntityId == entity)
            .OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (payment is null || payment.Status != approved)
        {
            Mark(line, "EXCEPTION", "PAYMENT_NOT_APPROVED", fsCase);
            return;
        }

        payment.Status = Codes.Of(PaymentStatus.Issued);
        payment.FsStatementId = row.FsStatementId;
        payment.CounterpartyInsurerPartyId = row.CounterpartyInsurerPartyId;
        payment.IssuedAt = now;
        payment.UpdatedAt = now;
        payment.RecordVersion++;
        line.ClaimPaymentId = payment.ClaimPaymentId;
        Link(line, fsCase, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishIssuedAsync(claim, payment, row, now, cancellationToken).ConfigureAwait(false);
    }

    private static void Link(FsStatementLineRow line, FsCaseRow fsCase, Instant now)
    {
        line.MatchStatus = "MATCHED";
        line.ExceptionReason = null;
        line.FsCaseId = fsCase.FsCaseId;
        line.ClaimId = fsCase.ClaimId;
        line.UpdatedAt = now;
        line.RecordVersion++;
        fsCase.StatementLineId = line.FsStatementLineId;
        fsCase.Status = "SETTLED";
        fsCase.UpdatedAt = now;
        fsCase.RecordVersion++;
    }

    private void Mark(FsStatementLineRow line, string status, string reason, FsCaseRow? fsCase)
    {
        line.MatchStatus = status;
        line.ExceptionReason = reason;
        line.FsCaseId = fsCase?.FsCaseId;
        line.ClaimId = fsCase?.ClaimId;
        line.UpdatedAt = clock.Now;
        line.RecordVersion++;
    }

    private async Task PublishIssuedAsync(ClaimRow claim, ClaimPaymentRow payment, FsStatementRow row, Instant now, CancellationToken cancellationToken)
    {
        var transactions = await db.FinancialTransactions.AsNoTracking().Where(t => t.ClaimPaymentId == payment.ClaimPaymentId).OrderBy(t => t.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lineIds = transactions.Select(t => t.ReserveLineId).ToList();
        var lines = await db.ReserveLines.AsNoTracking().Where(l => lineIds.Contains(l.ReserveLineId)).ToDictionaryAsync(l => l.ReserveLineId, cancellationToken).ConfigureAwait(false);
        var claimKey = claim.ClaimId.Value.ToString();
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PaymentIssuedV1.Descriptor),
            ClaimEvents.AggregateType,
            claimKey,
            new PaymentIssuedV1
            {
                ClaimId = claim.ClaimId,
                PaymentId = new PaymentId(payment.ClaimPaymentId.Value),
                TransactionIds = [.. transactions.Select(t => new PolicyTransactionId(t.TxnId))],
                Lines = [.. transactions.Select(t => new ClaimPaymentLine
                {
                    LineKey = t.TxnId.ToString("D"), Amount = SetLifecycle.Three(t.Amount, t.Currency), ReserveLineId = t.ReserveLineId.Value, ExposureId = t.ExposureId,
                    CostType = lines[t.ReserveLineId].CostType, CostCategory = lines[t.ReserveLineId].CostCategory, Eroding = t.Eroding ?? false,
                })],
                Amount = SetLifecycle.Three(transactions.Sum(t => t.Amount), payment.Currency),
                PayeePartyId = payment.PayeePartyId,
                Method = payment.Method,
                DisbursementId = null,
                ExGratia = false,
                ComplaintRef = null,
                AccountingDate = options.Value.DateOf(now),
                FsStatementId = row.FsStatementId,
                CounterpartyInsurerPartyId = row.CounterpartyInsurerPartyId.Value,
            },
            BusinessKeys.Empty.With("claimId", claimKey).With("paymentId", payment.ClaimPaymentId.Value.ToString()).With("fsStatementId", FsStatements.Ref(row.FsStatementId))));
    }
}

/// <summary>
/// Hands an approved net to BIL, the only cash executor (D-SL4-02, D-SL4-12): a payable net becomes one <c>FS_CLEARING</c>
/// disbursement (method CLEARING, claim-level lines that sum to the net, statement reference = the row id), a receivable net
/// becomes a receivable with source <c>FS_CLEARING</c> and the same statement reference.
/// </summary>
internal sealed class FsNetHandOver(ClaimsDbContext db, IClock clock, IOptions<ClaimsOptions> options, IServiceProvider services)
{
    /// <summary>
    /// The payable lines reduced by the receivable total (largest first), so that the lines sum to the net payable. Each line keeps
    /// its own claim and case (BIL needs unique claim/case pairs).
    /// </summary>
    public static IReadOnlyList<(FsStatementLineRow Line, decimal Amount)> NetLines(IReadOnlyList<FsStatementLineRow> lines)
    {
        var offset = lines.Where(l => l.Direction == FsStatements.Receivable).Sum(l => l.Amount);
        var result = new List<(FsStatementLineRow, decimal)>();
        foreach (var line in lines.Where(l => l.Direction == FsStatements.Payable).OrderByDescending(l => l.Amount).ThenBy(l => l.ExternalReference, StringComparer.Ordinal))
        {
            var take = Math.Min(offset, line.Amount);
            offset -= take;
            if (line.Amount - take > 0m)
            {
                result.Add((line, line.Amount - take));
            }
        }

        return result;
    }

    /// <summary>The clearing payee account of the counterparty, from configuration; null when none is configured.</summary>
    public Guid? PayeeAccountOf(FsStatementRow row) =>
        options.Value.FriendlySettlement.ClearingPayeeAccounts.TryGetValue(FsCases.Code(row.CounterpartyInsurerPartyId), out var account) && account != Guid.Empty ? account : null;

    /// <summary>The hash the approver sees and BIL recomputes (REQ-BIL-198); a receivable has no payee account.</summary>
    public static Sha256Hash ContentHash(FsStatementRow row, Guid? account) =>
        DisbursementContent.Hash(DisbursementCodes.FsClearing, FsStatements.Ref(row.FsStatementId), row.CounterpartyInsurerPartyId, account ?? Guid.Empty, new Money(row.NetAmount, Currency.EUR));

    public async Task<Result<bool>> RequestAsync(FsStatementRow row, string evidenceRef, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var amount = new Money(row.NetAmount, Currency.EUR);
        var reference = FsStatements.Ref(row.FsStatementId);
        var lines = await db.FsStatementLines.AsNoTracking().Where(l => l.FsStatementId == row.FsStatementId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (row.Direction == FsStatements.Payable)
        {
            if (PayeeAccountOf(row) is not { } account)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "No clearing payee account is configured for the counterparty (Claims:FriendlySettlement:ClearingPayeeAccounts).");
            }

            if (services.GetService<IBillingDisbursementService>() is not { } disbursements)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The billing disbursement service is not available.");
            }

            var netLines = NetLines(lines);
            try
            {
                var response = await disbursements.RequestAsync(new DisbursementRequestRequest
                {
                    SourceType = DisbursementCodes.FsClearing, SourceId = reference, PayeePartyId = row.CounterpartyInsurerPartyId, PayeeAccountId = account,
                    Amount = amount, Method = DisbursementCodes.Clearing, ApprovalEvidenceRef = evidenceRef,
                    ApprovalContentHash = ContentHash(row, account), StatementReference = reference,
                    PurposeText = $"Friendly Settlement {row.Period} net",
                    Lines = [.. netLines.Select(n => new BilLine { ClaimId = n.Line.ClaimId!.Value, FsCaseId = new FsCaseId(n.Line.FsCaseId!.Value), Amount = new Money(n.Amount, Currency.EUR) })],
                }, new CommandOptions(FsStatements.KeyFor("clm.fs.net.disbursement", row.FsStatementId)), cancellationToken).ConfigureAwait(false);
                row.DisbursementId = response.DisbursementId;
            }
            catch (DomainException ex)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", $"Billing refused the net disbursement: {ex.Error.Code.Value}.");
            }
        }
        else
        {
            if (services.GetService<IBillingReceivableService>() is not { } receivables)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The billing receivable service is not available.");
            }

            try
            {
                var response = await receivables.RegisterAsync(new ReceivableRegisterRequest
                {
                    SourceType = ReceivableSourceType.FsClearing, SourceId = reference, CounterpartyPartyId = row.CounterpartyInsurerPartyId, StatementRef = reference,
                    Purpose = ReceivablePurpose.FsNet, Amount = amount,
                    DueDate = options.Value.DateOf(now).AddDays(options.Value.FriendlySettlement.ReceivableDueDays),
                }, new CommandOptions(FsStatements.KeyFor("clm.fs.net.receivable", row.FsStatementId)), cancellationToken).ConfigureAwait(false);
                row.ReceivableId = response.ReceivableId;
            }
            catch (DomainException ex)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", $"Billing refused the net receivable: {ex.Error.Code.Value}.");
            }
        }

        row.Status = FsStatements.Requested;
        row.UpdatedAt = now;
        row.RecordVersion++;
        return true;
    }
}

/// <summary>Settles a net once BIL reports the cash (disbursement cleared, receivable collected): lines, cases, CLEARING payments and recoveries close.</summary>
internal sealed class FsNetSettler(ClaimsDbContext db, IClock clock, IOptions<ClaimsOptions> options)
{
    public async Task SettleAsync(FsStatementRow row, CancellationToken cancellationToken)
    {
        if (row.Status == FsStatements.Settled)
        {
            return;
        }

        var now = clock.Now;
        var today = options.Value.DateOf(now);
        var lines = await db.FsStatementLines.Where(l => l.FsStatementId == row.FsStatementId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var caseIds = lines.Where(l => l.FsCaseId != null).Select(l => l.FsCaseId!.Value).ToList();
        foreach (var fsCase in await db.FsCases.Where(c => caseIds.Contains(c.FsCaseId)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            fsCase.Status = "RECONCILED";
            fsCase.UpdatedAt = now;
            fsCase.RecordVersion++;
        }

        var recoveryIds = lines.Where(l => l.RecoveryId != null).Select(l => l.RecoveryId!.Value).ToList();
        foreach (var recovery in await db.Recoveries.Where(r => recoveryIds.Contains(r.RecoveryId)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (RecoverySupport.IsOpen(recovery.Status))
            {
                recovery.Status = "CLOSED";
                RecoverySupport.AddMilestone(recovery, "FULLY_RECOVERED", today, now, "SERVICE:clm-fs-statement", FsStatements.Ref(row.FsStatementId));
                RecoverySupport.Touch(recovery, now);
            }
        }

        var issued = Codes.Of(PaymentStatus.Issued);
        foreach (var payment in await db.ClaimPayments.Where(p => p.FsStatementId == row.FsStatementId && p.Method == "CLEARING" && p.Status == issued).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            payment.Status = Codes.Of(PaymentStatus.Cleared);
            payment.ClearedAt = now;
            payment.UpdatedAt = now;
            payment.RecordVersion++;
        }

        foreach (var line in lines)
        {
            line.MatchStatus = "SETTLED";
            line.UpdatedAt = now;
            line.RecordVersion++;
        }

        row.Status = FsStatements.Settled;
        row.UpdatedAt = now;
        row.RecordVersion++;
    }
}
