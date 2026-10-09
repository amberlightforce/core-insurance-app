using System.Globalization;
using CoreIns.Modules.Finance.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>Where a journal comes from (REQ-FIN-079 lineage).</summary>
internal sealed record JournalSource(
    LegalEntityId LegalEntityId, string LegalEntityCode, string Jurisdiction, string SourceModule, string SourceEventType,
    IReadOnlyList<Guid> SourceEventIds, string SourceRef);

/// <summary>A journal as written, with its <c>JournalPosted</c> event (the caller publishes it once its unit of work cannot fail any more).</summary>
internal sealed record WrittenJournal(Guid JournalId, string JournalNumber, OutgoingEvent Posted);

/// <summary>
/// Writes journals (REQ-FIN-067): checks the double-entry invariant in code (the deferred database constraint checks it
/// again at commit, REQ-FIN-068), takes a gapless journal number from PLT inside the transaction (REQ-FIN-069), assigns
/// the calendar-month period, inserts header and lines (append-only, REQ-FIN-070), runs the database balance check at
/// once (so a failure surfaces inside the caller's savepoint), and returns <c>JournalPosted</c> for the caller to publish
/// through the outbox in the same transaction (REQ-FIN-085).
/// </summary>
internal sealed class JournalWriter(
    FinanceDbContext db,
    DbSession session,
    INumberingService numbering,
    RequestContext context,
    IClock clock)
{
    public async Task<WrittenJournal> WriteAsync(JournalDraft draft, JournalSource source, Currency functionalCurrency, Guid? reverses, string? reason, CancellationToken cancellationToken)
    {
        var problems = Journals.Check(draft.Lines);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"{Contracts.FinanceErrorCodes.Unbalanced}: {string.Join(" ", problems)}");
        }

        var now = clock.Now;
        var periodCode = draft.AccountingDate.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var periodId = await PeriodAsync(source.LegalEntityId, periodCode, now, cancellationToken).ConfigureAwait(false);
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Journal, draft.AccountingDate), cancellationToken).ConfigureAwait(false);
        var journalId = Guid.CreateVersion7();

        db.Journals.Add(new JournalEntryRow
        {
            JournalId = journalId,
            JournalNumber = number.Value,
            LegalEntityId = source.LegalEntityId.Value,
            LegalEntityCode = source.LegalEntityCode,
            Jurisdiction = source.Jurisdiction,
            Book = draft.Book,
            AccountingDate = draft.AccountingDate,
            BusinessDate = draft.BusinessDate,
            PeriodId = periodId,
            SourceType = draft.SourceType,
            SourceModule = source.SourceModule,
            SourceEventType = source.SourceEventType,
            SourceEventIds = [.. source.SourceEventIds],
            SourceRef = source.SourceRef,
            RuleSetId = draft.RuleSetId,
            RuleSetVersion = draft.RuleSetVersion,
            RuleCodes = [.. draft.RuleCodes],
            ReversesJournalId = reverses,
            Reason = reason,
            FunctionalCurrency = functionalCurrency.Code,
            CorrelationId = context.CorrelationId.Value,
            PostedAt = now,
            PostedBy = context.Actor.Id,
        });
        foreach (var line in draft.Lines)
        {
            db.JournalLines.Add(new JournalLineRow
            {
                LineId = Guid.CreateVersion7(),
                JournalId = journalId,
                LineNo = line.LineNo,
                LegalEntityId = source.LegalEntityId.Value,
                Book = draft.Book,
                AccountCode = line.Account,
                Side = line.Side,
                Amount = line.Amount.Amount,
                Currency = line.Amount.Currency.Code,
                AmountFunctional = line.FunctionalAmount.Amount,
                FunctionalCurrency = line.FunctionalAmount.Currency.Code,
                RuleCode = line.RuleCode,
                BusinessDate = draft.BusinessDate,
                ProductCode = line.Dimensions.ProductCode,
                ProductVersion = line.Dimensions.ProductVersion,
                CoverageCode = line.Dimensions.CoverageCode,
                ChargeType = line.Dimensions.ChargeType,
                ChargeCategory = line.Dimensions.ChargeCategory,
                GlKey = line.Dimensions.GlKey,
                PolicyId = line.Dimensions.PolicyId,
                PolicyNumber = line.Dimensions.PolicyNumber,
                PolicyTermId = line.Dimensions.PolicyTermId,
                PolicyTransactionId = line.Dimensions.PolicyTransactionId,
                ChargeId = line.Dimensions.ChargeId,
                BillingAccountId = line.Dimensions.BillingAccountId,
                InvoiceId = line.Dimensions.InvoiceId,
                ReceiptId = line.Dimensions.ReceiptId,
                ClaimId = line.Dimensions.ClaimId,
                ExposureId = line.Dimensions.ExposureId,
                ReserveLineId = line.Dimensions.ReserveLineId,
                CostType = line.Dimensions.CostType,
                CostCategory = line.Dimensions.CostCategory,
                ClaimPaymentId = line.Dimensions.ClaimPaymentId,
                DisbursementId = line.Dimensions.DisbursementId,
                RefundId = line.Dimensions.RefundId,
            });
        }

        // Insert now: the header must exist before a later journal of this transaction reverses it, and the caller reads its own writes.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(FinanceSql.CheckBalanceNow, transaction: session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var posted = new OutgoingEvent(
            EventDescriptor.From(JournalPostedV1.Descriptor), "LegalEntity", source.LegalEntityCode,
            new JournalPostedV1
            {
                Journals = [new JournalRef { JournalId = new JournalId(journalId), JournalNumber = JournalNumber.Parse(number.Value) }],
                Book = draft.Book,
                AccountingDate = draft.AccountingDate,
                PeriodId = new FinancialPeriodId(periodId),
                SourceType = draft.SourceType,
                SourceRefs = [source.SourceRef, .. source.SourceEventIds.Select(id => id.ToString("D"))],
                TotalsPerCurrency = draft.Totals,
            },
            BusinessKeys.Empty.With("journalId", journalId.ToString("D")).With("periodId", periodId.ToString("D")));

        return new WrittenJournal(journalId, number.Value, posted);
    }

    private async Task<Guid> PeriodAsync(LegalEntityId legalEntity, string periodCode, Instant now, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at)
            VALUES (@id, @le, @code, 'OPEN', @now) ON CONFLICT (legal_entity_id, period_code) DO NOTHING
            """, new { id = Guid.CreateVersion7(), le = legalEntity.Value, code = periodCode, now = now.ToUtcDateTime() },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT period_id FROM fin.financial_period WHERE legal_entity_id = @le AND period_code = @code",
            new { le = legalEntity.Value, code = periodCode }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>Loads a written journal's lines back as drafts (for reversal).</summary>
internal static class JournalRows
{
    public static async Task<(JournalEntryRow Entry, IReadOnlyList<JournalLineDraft> Lines)?> LoadAsync(FinanceDbContext db, LegalEntityId legalEntity, Guid journalId, CancellationToken cancellationToken)
    {
        var entry = await db.Journals.AsNoTracking().SingleOrDefaultAsync(j => j.JournalId == journalId && j.LegalEntityId == legalEntity.Value, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        var lines = await db.JournalLines.AsNoTracking().Where(l => l.JournalId == journalId).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (entry, [.. lines.Select(l => new JournalLineDraft(
            l.LineNo, l.AccountCode, l.Side, Money.Of(l.Amount, l.Currency), Money.Of(l.AmountFunctional, l.FunctionalCurrency), l.RuleCode,
            new LineDimensions
            {
                ProductCode = l.ProductCode, ProductVersion = l.ProductVersion, CoverageCode = l.CoverageCode, ChargeType = l.ChargeType,
                ChargeCategory = l.ChargeCategory, GlKey = l.GlKey, PolicyId = l.PolicyId, PolicyNumber = l.PolicyNumber, PolicyTermId = l.PolicyTermId,
                PolicyTransactionId = l.PolicyTransactionId, ChargeId = l.ChargeId, BillingAccountId = l.BillingAccountId, InvoiceId = l.InvoiceId, ReceiptId = l.ReceiptId,
                ClaimId = l.ClaimId, ExposureId = l.ExposureId, ReserveLineId = l.ReserveLineId, CostType = l.CostType, CostCategory = l.CostCategory,
                ClaimPaymentId = l.ClaimPaymentId, DisbursementId = l.DisbursementId, RefundId = l.RefundId,
            }))]);
    }
}
