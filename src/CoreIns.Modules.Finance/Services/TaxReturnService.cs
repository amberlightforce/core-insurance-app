using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Finance.Services;

/// <summary>
/// IPT returns (BO-FIN-09). A return is computed from the credits and debits of the IPT payable account in a SOFT_CLOSED or LOCKED period and
/// reconciled to the part posted from BIL billing entries. DRAFT: recalculated and adjusted (audited); the preparer submits it, which freezes the
/// content hash and requests a <c>plt.Approval</c>; a different person approves it (REVIEWED). The file is generated from the frozen content, filing
/// locks it (FILED, <c>TaxReturnFiled</c>) and the payment is a BIL TAX_REMITTANCE disbursement. A later difference in the ledger never edits a
/// reviewed return: it becomes the next version, linked to the original. The IPT pack values must be Settled in Production (D-REG-02, OQ-011).
/// </summary>
internal sealed partial class TaxReturnService(
    FinanceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IServiceProvider services, IEventPublisher events, IHostEnvironment environment)
{
    private static readonly DomainError NoEntity = DomainError.Of(ModuleCode.FIN, "VALIDATION", "The legal entity is not served by this stamp.");

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,31}$", RegexOptions.None, 1000)]
    private static partial Regex TaxCodePattern();

    private readonly record struct Computed(string Currency, decimal Ledger, decimal Bil, int Entries);

    private readonly record struct Legal(string Status, bool Provisional);

    public async Task<Result<TaxReturnListPage>> ListAsync(string? period, string? status, string? cursor, int limit, CancellationToken cancellationToken)
    {
        Guid? before = null;
        if ((period is not null && !PeriodClose.IsPeriodCode(period)) || (status is not null && status is not (TaxReturns.Draft or TaxReturns.Reviewed or TaxReturns.Filed or TaxReturns.Paid)))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "period must be yyyy-MM and status one of DRAFT, REVIEWED, FILED, PAID.");
        }

        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var g))
            {
                return DomainError.Of(ModuleCode.FIN, "VALIDATION", "cursor is malformed.");
            }

            before = g;
        }

        if (ResolveEntity() is not { } legalEntity)
        {
            return new TaxReturnListPage { Items = [], NextCursor = null, Limit = limit };
        }

        var query = db.TaxReturns.AsNoTracking().Where(r => r.LegalEntityId == legalEntity.Value);
        if (period is not null)
        {
            query = query.Where(r => r.PeriodCode == period);
        }

        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        if (before is { } id)
        {
            query = query.Where(r => r.ReturnId.CompareTo(id) < 0);
        }

        var rows = await query.OrderByDescending(r => r.ReturnId).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        return new TaxReturnListPage
        {
            Items = [.. page.Select(ToListItem)],
            NextCursor = rows.Count > limit ? page[^1].ReturnId.ToString("D") : null,
            Limit = limit,
        };
    }

    public async Task<Result<TaxReturnGetResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, tracked: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return NotFound;
        }

        await RefreshPaymentAsync(row, cancellationToken).ConfigureAwait(false);
        return await ResponseAsync(row, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<TaxReturnRecalculateResponse>> RecalculateAsync(string period, string? taxCode, CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(taxCode) ? TaxReturns.DefaultTaxCode : taxCode.Trim().ToUpperInvariant();
        if (!PeriodClose.IsPeriodCode(period) || !TaxCodePattern().IsMatch(code))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "period must be yyyy-MM and taxCode an upper-case code.");
        }

        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        if (await PeriodGateAsync(legalEntity, period, cancellationToken).ConfigureAwait(false) is { } closed)
        {
            return closed;
        }

        var (legalValue, legalError) = await LegalAsync(cancellationToken).ConfigureAwait(false);
        if (legalError is not null)
        {
            return legalError;
        }

        var periodRow = await db.Periods.AsNoTracking().SingleAsync(p => p.LegalEntityId == legalEntity.Value && p.PeriodCode == period, cancellationToken).ConfigureAwait(false);
        var computed = await ComputeAsync(legalEntity, periodRow.PeriodId, code, cancellationToken).ConfigureAwait(false);
        if (computed.Error is { } computeError)
        {
            return computeError;
        }

        var figures = computed.Value;
        var latest = await db.TaxReturns.Where(r => r.LegalEntityId == legalEntity.Value && r.PeriodCode == period && r.TaxCode == code)
            .OrderByDescending(r => r.VersionNo).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        TaxReturnRow row;
        if (latest is null)
        {
            row = NewRow(legalEntity, period, code, 1, null, figures, legalValue, now);
            db.TaxReturns.Add(row);
        }
        else if (latest.Status == TaxReturns.Draft)
        {
            if (latest.ApprovalRequestId is not null)
            {
                return DomainError.Of(ModuleCode.FIN, "STATE", "The return is submitted for review; reject it before it is recalculated.");
            }

            row = latest;
            Apply(row, figures, legalValue);
            row.RecordVersion++;
        }
        else if (latest.Currency.Trim() == figures.Currency && Same(latest, figures))
        {
            // Nothing changed since the reviewed or filed version: it stays the current one.
            return new TaxReturnRecalculateResponse { Return = await ViewAsync(latest, cancellationToken).ConfigureAwait(false), PaymentStatus = latest.PaymentStatus };
        }
        else
        {
            // The ledger moved after review: an amendment is a new version that links to the original; the original is never edited.
            row = NewRow(legalEntity, period, code, latest.VersionNo + 1, latest.ReturnId, figures, legalValue, now);
            db.TaxReturns.Add(row);
            latest.SupersededByReturnId = row.ReturnId;
            latest.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var carried = await db.TaxReturnAdjustments.AsNoTracking().Where(a => a.ReturnId == latest.ReturnId).OrderBy(a => a.AdjustmentId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var adjustment in carried)
            {
                db.TaxReturnAdjustments.Add(new TaxReturnAdjustmentRow
                {
                    AdjustmentId = Guid.CreateVersion7(), ReturnId = row.ReturnId, Amount = adjustment.Amount, Reason = adjustment.Reason, EvidenceRef = adjustment.EvidenceRef,
                    ExplainsReconciliation = adjustment.ExplainsReconciliation, CreatedBy = adjustment.CreatedBy, CreatedAt = adjustment.CreatedAt,
                });
            }

            row.AdjustmentsTotal = TaxReturns.Money(carried.Sum(a => a.Amount));
            row.NetPayable = TaxReturns.Net(row.LedgerAmount, row.AdjustmentsTotal);
            row.ReconExplanation = latest.ReconExplanation;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return new TaxReturnRecalculateResponse { Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }

    public async Task<Result<TaxReturnAddAdjustmentResponse>> AddAdjustmentAsync(Guid id, string amountText, string reason, string? evidenceRef, bool explains, CancellationToken cancellationToken)
    {
        if (!TaxReturns.IsAmount(amountText, out var amount) || string.IsNullOrWhiteSpace(reason))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "amount must be a non-zero decimal with at most 2 places and a reason is required.");
        }

        if (await FindAsync(id, tracked: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return NotFound;
        }

        if (row.Status != TaxReturns.Draft || row.ApprovalRequestId is not null)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"A {row.Status} return that is submitted or reviewed cannot be adjusted; amend it as a new version.");
        }

        db.TaxReturnAdjustments.Add(new TaxReturnAdjustmentRow
        {
            AdjustmentId = Guid.CreateVersion7(), ReturnId = row.ReturnId, Amount = TaxReturns.Money(amount), Reason = reason.Trim(), EvidenceRef = string.IsNullOrWhiteSpace(evidenceRef) ? null : evidenceRef.Trim(),
            ExplainsReconciliation = explains, CreatedBy = context.Actor.Id, CreatedAt = clock.Now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var total = await db.TaxReturnAdjustments.AsNoTracking().Where(a => a.ReturnId == id).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
        row.AdjustmentsTotal = TaxReturns.Money(total);
        row.NetPayable = TaxReturns.Net(row.LedgerAmount, row.AdjustmentsTotal);
        if (explains)
        {
            row.ReconExplanation = reason.Trim();
        }

        row.RecordVersion++;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return new TaxReturnAddAdjustmentResponse { Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }

    public async Task<Result<TaxReturnGenerateFileResponse>> GenerateFileAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, tracked: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return NotFound;
        }

        if (row.Status == TaxReturns.Draft || row.ContentHash is null)
        {
            return DomainError.Of(ModuleCode.FIN, "RETURN-NOT-APPROVED", "The file is generated from a reviewed return; the return is still a draft.");
        }

        if ((await LegalAsync(cancellationToken).ConfigureAwait(false)).Error is { } refused)
        {
            return refused;
        }

        var adjustments = await AdjustmentsAsync(row.ReturnId, cancellationToken).ConfigureAwait(false);
        if (TaxReturns.Hash(row, adjustments).Value != row.ContentHash)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The return no longer matches the content that was approved.");
        }

        var code = context.LegalEntity!.Value.Value;
        var content = TaxReturns.FileContent(code, row, adjustments);
        var fileHash = Sha256Hash.ComputeUtf8(content).Value;
        if (row.FileHash is not null && row.FileHash != fileHash)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The generated file differs from the one already generated for this return.");
        }

        if (row.FileHash is null)
        {
            row.FileName = TaxReturns.FileName(code, row);
            row.FileHash = fileHash;
            row.RecordVersion++;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return new TaxReturnGenerateFileResponse
        {
            Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false),
            File = new TaxReturnFileView { FileName = row.FileName!, FileHash = row.FileHash!, ContentType = TaxReturns.ContentType, Content = content },
            PaymentStatus = row.PaymentStatus,
        };
    }

    public async Task<Result<TaxReturnTransitionResponse>> TransitionAsync(Guid id, TaxReturnTransitionRequest.ActionValue action, string? comment, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, tracked: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return NotFound;
        }

        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (action == TaxReturnTransitionRequest.ActionValue.Reject && text is null)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "A rejection needs a comment.");
        }

        var result = action switch
        {
            TaxReturnTransitionRequest.ActionValue.Submit => await SubmitAsync(row, text, cancellationToken).ConfigureAwait(false),
            TaxReturnTransitionRequest.ActionValue.Approve => await DecideAsync(row, approve: true, text, cancellationToken).ConfigureAwait(false),
            _ => await DecideAsync(row, approve: false, text, cancellationToken).ConfigureAwait(false),
        };
        if (result is not null)
        {
            return result.Value;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return new TaxReturnTransitionResponse { Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }

    public async Task<Result<TaxReturnRecordFilingResponse>> RecordFilingAsync(Guid id, string reference, BusinessDate filedOn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "filingReference is required.");
        }

        if (await FindAsync(id, tracked: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return NotFound;
        }

        if (row.Status is TaxReturns.Filed or TaxReturns.Paid)
        {
            // A repeated recording of the same filing is a replay; another reference would be an edit of a locked return.
            return string.Equals(row.FilingReference, reference.Trim(), StringComparison.Ordinal)
                ? new TaxReturnRecordFilingResponse { Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false), PaymentStatus = row.PaymentStatus }
                : DomainError.Of(ModuleCode.FIN, "STATE", "The return is already filed and locked; amend it as a new version.");
        }

        if (row.Status != TaxReturns.Reviewed || row.ContentHash is null)
        {
            return DomainError.Of(ModuleCode.FIN, "RETURN-NOT-APPROVED", "Only a reviewed return can be filed.");
        }

        if (row.FileHash is null)
        {
            return DomainError.Of(ModuleCode.FIN, "RETURN-NOT-APPROVED", "Generate the file before the filing is recorded.");
        }

        if (row.SupersededByReturnId is not null)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "A later version amends this return; file the current version.");
        }

        if ((await LegalAsync(cancellationToken).ConfigureAwait(false)).Error is { } refused)
        {
            return refused;
        }

        var adjustments = await AdjustmentsAsync(row.ReturnId, cancellationToken).ConfigureAwait(false);
        if (TaxReturns.Hash(row, adjustments).Value != row.ContentHash)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The return no longer matches the content that was approved.");
        }

        row.Status = TaxReturns.Filed;
        row.FiledBy = context.Actor.Id;
        row.FiledAt = clock.Now;
        row.FiledOn = filedOn;
        row.FilingReference = reference.Trim();
        row.RecordVersion++;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        var currency = Currency.FromCode(row.Currency.Trim());
        var code = context.LegalEntity!.Value;
        var start = DateOnly.ParseExact(row.PeriodCode + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(TaxReturnFiledV1.Descriptor), "LegalEntity", code.Value,
            new TaxReturnFiledV1
            {
                ReturnId = row.ReturnId,
                TaxCode = row.TaxCode,
                Period = new DateRange(new BusinessDate(start), new BusinessDate(start.AddMonths(1).AddDays(-1))),
                Totals = new Dictionary<string, Money> { [$"netPayable:{currency.Code}"] = new(row.NetPayable, currency), [$"ledger:{currency.Code}"] = new(row.LedgerAmount, currency) },
                FilingReference = row.FilingReference,
            },
            BusinessKeys.Empty.With("returnId", row.ReturnId.ToString("D")))
        {
            LegalEntity = code,
            Jurisdiction = context.Jurisdiction,
        });
        return new TaxReturnRecordFilingResponse { Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }

    public async Task<Result<TaxReturnRequestPaymentResponse>> RequestPaymentAsync(Guid id, Guid payeePartyId, Guid payeeAccountId, BusinessDate? valueDate, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, tracked: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return NotFound;
        }

        if (row.DisbursementId is not null)
        {
            await RefreshPaymentAsync(row, cancellationToken).ConfigureAwait(false);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return new TaxReturnRequestPaymentResponse { Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false), PaymentStatus = row.PaymentStatus };
        }

        if (row.Status != TaxReturns.Filed)
        {
            return DomainError.Of(ModuleCode.FIN, "RETURN-NOT-APPROVED", "The tax is paid on a filed return.");
        }

        if (row.NetPayable <= 0m)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "The return has no tax payable; there is nothing to remit.");
        }

        if ((await LegalAsync(cancellationToken).ConfigureAwait(false)).Error is { } refused)
        {
            return refused;
        }

        if (services.GetService<IBillingDisbursementService>() is not { } bil)
        {
            return DomainError.Of(ModuleCode.FIN, "SPI-UNBOUND", "The disbursement service is not available.");
        }

        try
        {
            // One payment per return: the source id is stable (authority and period of this return), so a retry is a BIL duplicate, not a second payment.
            var response = await bil.RequestAsync(
                new DisbursementRequestRequest
                {
                    SourceType = DisbursementCodes.TaxRemittance,
                    SourceId = $"{row.TaxCode}:{row.PeriodCode}:{payeePartyId:D}:v{row.VersionNo}",
                    PayeePartyId = new PartyId(payeePartyId),
                    PayeeAccountId = payeeAccountId,
                    Amount = new Money(row.NetPayable, Currency.FromCode(row.Currency.Trim())),
                    Method = DisbursementCodes.SepaCreditTransfer,
                    RequestedValueDate = valueDate,
                    PurposeText = $"{row.TaxCode} {row.PeriodCode} {row.FilingReference}",
                },
                new CommandOptions(IdempotencyKey.From(StableKey($"{row.ReturnId:D}|payment"))),
                cancellationToken).ConfigureAwait(false);
            row.DisbursementId = response.DisbursementId.Value;
            row.PaymentStatus = response.Status.ToString();
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        row.RecordVersion++;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return new TaxReturnRequestPaymentResponse { Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false), PaymentStatus = row.PaymentStatus };
    }

    private static DomainError NotFound => DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The tax return does not exist.");

    private static Guid StableKey(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text))[..16]);

    private static bool Same(TaxReturnRow row, Computed figures) =>
        TaxReturns.Money(row.LedgerAmount) == TaxReturns.Money(figures.Ledger) && TaxReturns.Money(row.BilAmount) == TaxReturns.Money(figures.Bil) && row.EntryCount == figures.Entries;

    private static void Apply(TaxReturnRow row, Computed figures, Legal legal)
    {
        row.Currency = figures.Currency;
        row.LedgerAmount = TaxReturns.Money(figures.Ledger);
        row.BilAmount = TaxReturns.Money(figures.Bil);
        row.EntryCount = figures.Entries;
        row.NetPayable = TaxReturns.Net(row.LedgerAmount, row.AdjustmentsTotal);
        row.LegalStatus = legal.Status;
        row.Provisional = legal.Provisional;
    }

    private TaxReturnRow NewRow(LegalEntityId legalEntity, string period, string taxCode, int version, Guid? supersedes, Computed figures, Legal legal, Instant now)
    {
        var row = new TaxReturnRow
        {
            ReturnId = Guid.CreateVersion7(), LegalEntityId = legalEntity.Value, PeriodCode = period, TaxCode = taxCode, VersionNo = version, SupersedesReturnId = supersedes,
            Status = TaxReturns.Draft, CreatedBy = context.Actor.Id, CreatedAt = now,
        };
        Apply(row, figures, legal);
        return row;
    }

    private async Task<DomainError?> SubmitCheckAsync(TaxReturnRow row, CancellationToken cancellationToken)
    {
        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        if (await PeriodGateAsync(legalEntity, row.PeriodCode, cancellationToken).ConfigureAwait(false) is { } closed)
        {
            return closed;
        }

        return (await LegalAsync(cancellationToken).ConfigureAwait(false)).Error;
    }

    /// <summary>SUBMIT: the preparer freezes the content hash and asks for approval. Returns an error, or null when the row was changed.</summary>
    private async Task<Result<TaxReturnTransitionResponse>?> SubmitAsync(TaxReturnRow row, string? comment, CancellationToken cancellationToken)
    {
        if (row.Status != TaxReturns.Draft || row.ApprovalRequestId is not null || row.SupersededByReturnId is not null)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "Only a draft that is not yet submitted can be submitted.");
        }

        if (TaxReturns.ReconStatus(row.LedgerAmount, row.BilAmount, row.ReconExplanation) == TaxReturns.Unexplained)
        {
            return DomainError.Of(ModuleCode.FIN, "RECON-UNEXPLAINED", "The ledger and the BIL tax lines differ; add an adjustment that explains the difference before the return is submitted.");
        }

        if (await SubmitCheckAsync(row, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var adjustments = await AdjustmentsAsync(row.ReturnId, cancellationToken).ConfigureAwait(false);
        var hash = TaxReturns.Hash(row, adjustments);
        var approvals = services.GetRequiredService<IPlatformApprovalService>();
        try
        {
            var response = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = TaxReturnApproval.Type,
                    ObjectRef = TaxReturnApproval.Subject(row.ReturnId),
                    PayloadHash = hash,
                    Authority = new ApprovalAuthority { Type = TaxReturnApproval.Type, Amount = TaxReturnApproval.Total(row) },
                    ReferralRole = TaxReturnApproval.ReferralRole,
                    Reason = $"Tax return {row.TaxCode} {row.PeriodCode} v{row.VersionNo}" + (comment is null ? string.Empty : ": " + comment),
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            row.ApprovalRequestId = response.Request.RequestId;
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        row.ContentHash = hash.Value;
        row.SubmittedBy = context.Actor.Id;
        row.SubmittedAt = clock.Now;
        row.RecordVersion++;
        return null;
    }

    private async Task<Result<TaxReturnTransitionResponse>?> DecideAsync(TaxReturnRow row, bool approve, string? comment, CancellationToken cancellationToken)
    {
        if (row.Status != TaxReturns.Draft || row.ApprovalRequestId is not { } requestId || row.ContentHash is null)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "Only a submitted draft can be approved or rejected.");
        }

        var checker = context.Actor.Id;
        var adjustments = await AdjustmentsAsync(row.ReturnId, cancellationToken).ConfigureAwait(false);
        if (string.Equals(row.CreatedBy, checker, StringComparison.Ordinal) || string.Equals(row.SubmittedBy, checker, StringComparison.Ordinal)
            || adjustments.Any(a => string.Equals(a.CreatedBy, checker, StringComparison.Ordinal)))
        {
            return DomainError.Of(ModuleCode.FIN, "SOD", "The person who prepared, adjusted or submitted a tax return cannot decide it (maker is not checker).");
        }

        var hash = TaxReturns.Hash(row, adjustments);
        if (hash.Value != row.ContentHash)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The return changed after it was submitted.");
        }

        if (approve && await SubmitCheckAsync(row, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var approvals = services.GetRequiredService<IPlatformApprovalService>();
        try
        {
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId,
                    PayloadHash = hash,
                    Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                    Comment = comment,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            if (approve)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = TaxReturnApproval.Type, ObjectRef = TaxReturnApproval.Subject(row.ReturnId) },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != TaxReturnApproval.Type || verified.Authority.Amount != TaxReturnApproval.Total(row))
                {
                    return DomainError.Of(ModuleCode.FIN, "APPROVAL-REQUIRED", "The approval does not cover this return's content and amount.");
                }
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (approve)
        {
            row.Status = TaxReturns.Reviewed;
            row.ReviewedBy = checker;
            row.ReviewedAt = clock.Now;
        }
        else
        {
            // Back to an editable draft: the frozen hash and the request are dropped, the next submission asks again.
            row.ApprovalRequestId = null;
            row.ContentHash = null;
            row.SubmittedBy = null;
            row.SubmittedAt = null;
        }

        row.RecordVersion++;
        return null;
    }

    /// <summary>The state of the payment follows the BIL disbursement: Cleared makes the return PAID.</summary>
    private async Task RefreshPaymentAsync(TaxReturnRow row, CancellationToken cancellationToken)
    {
        if (row.DisbursementId is not { } disbursement || row.Status == TaxReturns.Paid || services.GetService<IBillingDisbursementService>() is not { } bil)
        {
            return;
        }

        try
        {
            var current = await bil.GetAsync(disbursement.ToString("D"), cancellationToken).ConfigureAwait(false);
            var status = current.Disbursement.Status.ToString();
            if (status != row.PaymentStatus)
            {
                row.PaymentStatus = status;
                row.RecordVersion++;
            }

            if (current.Disbursement.Status == DisbursementRequestResponse.StatusValue.Cleared && row.Status == TaxReturns.Filed)
            {
                row.Status = TaxReturns.Paid;
                row.RecordVersion++;
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException)
        {
            // BIL cannot tell right now: the last known payment status stays.
            // TODO(hardening): an event-driven update from BIL DisbursementCleared instead of a read-time refresh.
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(DomainError.Of(ModuleCode.FIN, "STATE", "The return changed meanwhile; load it and try again."));
        }
    }

    /// <summary>A return is computed only for a SOFT_CLOSED or LOCKED period (BO-FIN-04).</summary>
    private async Task<DomainError?> PeriodGateAsync(LegalEntityId legalEntity, string period, CancellationToken cancellationToken)
    {
        var status = await db.Periods.AsNoTracking().Where(p => p.LegalEntityId == legalEntity.Value && p.PeriodCode == period).Select(p => p.Status).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return status is PeriodClose.SoftClosed or PeriodClose.Locked
            ? null
            : DomainError.Of(ModuleCode.FIN, "GATES-FAILED", $"Period {period} is {status ?? PeriodClose.Open}; a tax return is computed once the period is soft-closed or locked.");
    }

    /// <summary>The IPT values of the country pack: refused in Production unless Settled (D-USR-19, OQ-011), recorded as provisional elsewhere.</summary>
    private async Task<(Legal Value, DomainError? Error)> LegalAsync(CancellationToken cancellationToken)
    {
        var unbound = DomainError.Of(ModuleCode.FIN, "SPI-UNBOUND", "The country pack holds no IPT configuration for this legal entity.");
        if (services.GetService<IMarketConfigurationService>() is not { } market || context.LegalEntity is not { } code || context.Jurisdiction is not { } jurisdiction)
        {
            return (default, unbound);
        }

        try
        {
            var resolved = await market.ResolveAsync(
                new ConfigurationResolveRequest { LegalEntity = code.Value, Jurisdiction = jurisdiction.Value, Keys = [TaxReturns.PackKey] },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (resolved.Values.FirstOrDefault(v => v.Key == TaxReturns.PackKey) is not { } value)
            {
                return (default, unbound);
            }

            var settled = value.LegalStatus is ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled or ConfigurationResolveResponse.ValueItem.LegalStatusValue.NotRegulatory;
            return !settled && environment.IsProduction()
                ? (default, DomainError.Of(ModuleCode.FIN, "SPI-UNBOUND", $"The IPT rules have legal status {value.LegalStatus}; Production refuses a tax return until they are Settled (D-USR-19)."))
                : (new Legal(value.LegalStatus.ToString(), !settled), null);
        }
        catch (DomainException ex)
        {
            return (default, ex.Error);
        }
    }

    private async Task<(Computed Value, DomainError? Error)> ComputeAsync(LegalEntityId legalEntity, Guid periodId, string taxCode, CancellationToken cancellationToken)
    {
        var lines = await db.JournalLines.AsNoTracking()
            .Where(l => l.LegalEntityId == legalEntity.Value && l.AccountCode == TaxReturns.PayableAccount && (l.GlKey ?? TaxReturns.DefaultTaxCode) == taxCode)
            .Join(db.Journals.Where(j => j.PeriodId == periodId), l => l.JournalId, j => j.JournalId, (l, j) => new { l.Side, l.Currency, l.Amount, j.SourceModule })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var currencies = lines.Select(l => l.Currency.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1)
        {
            return (default, DomainError.Of(ModuleCode.FIN, "VALIDATION", "The IPT postings of the period are in more than one currency; a return is made in one currency."));
        }

        var currency = currencies.Count == 1
            ? currencies[0]
            : (await db.BookProfiles.AsNoTracking().Where(b => b.LegalEntityCode == context.LegalEntity!.Value.Value).Select(b => b.FunctionalCurrency).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false))?.Trim() ?? "EUR";
        static decimal Liability(IEnumerable<(string Side, decimal Amount)> items) => items.Sum(i => i.Side == Sides.Credit ? i.Amount : -i.Amount);
        var ledger = Liability(lines.Select(l => (l.Side, l.Amount)));
        var bil = Liability(lines.Where(l => l.SourceModule == TaxReturns.BilSourceModule).Select(l => (l.Side, l.Amount)));
        return (new Computed(currency, TaxReturns.Money(ledger), TaxReturns.Money(bil), lines.Count), null);
    }

    private Task<TaxReturnRow?> FindAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        if (ResolveEntity() is not { } legalEntity)
        {
            return Task.FromResult<TaxReturnRow?>(null);
        }

        var query = tracked ? db.TaxReturns : db.TaxReturns.AsNoTracking();
        return query.SingleOrDefaultAsync(r => r.ReturnId == id && r.LegalEntityId == legalEntity.Value, cancellationToken);
    }

    private Task<List<TaxReturnAdjustmentRow>> AdjustmentsAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.TaxReturnAdjustments.AsNoTracking().Where(a => a.ReturnId == returnId).OrderBy(a => a.AdjustmentId).ToListAsync(cancellationToken);

    private LegalEntityId? ResolveEntity()
    {
        if (context.LegalEntity is not { } code)
        {
            return null;
        }

        try
        {
            return legalEntities.Resolve(code);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<TaxReturnGetResponse> ResponseAsync(TaxReturnRow row, string? content, CancellationToken cancellationToken) => new()
    {
        Return = await ViewAsync(row, cancellationToken).ConfigureAwait(false),
        File = row.FileHash is null ? null : new TaxReturnFileView { FileName = row.FileName!, FileHash = row.FileHash, ContentType = TaxReturns.ContentType, Content = content },
        PaymentStatus = row.PaymentStatus,
    };

    private async Task<TaxReturnView> ViewAsync(TaxReturnRow row, CancellationToken cancellationToken)
    {
        var adjustments = await AdjustmentsAsync(row.ReturnId, cancellationToken).ConfigureAwait(false);
        var recon = TaxReturns.ReconStatus(row.LedgerAmount, row.BilAmount, row.ReconExplanation);
        return new TaxReturnView
        {
            ReturnId = row.ReturnId,
            Period = row.PeriodCode,
            TaxCode = row.TaxCode,
            VersionNo = row.VersionNo,
            SupersedesReturnId = row.SupersedesReturnId,
            SupersededByReturnId = row.SupersededByReturnId,
            Status = StatusOf(row.Status),
            Currency = row.Currency.Trim(),
            LedgerAmount = TaxReturns.Text(row.LedgerAmount),
            BilAmount = TaxReturns.Text(row.BilAmount),
            AdjustmentsTotal = TaxReturns.Text(row.AdjustmentsTotal),
            NetPayable = TaxReturns.Text(row.NetPayable),
            EntryCount = row.EntryCount,
            Reconciliation = ReconOf(recon),
            ReconDifference = TaxReturns.Text(TaxReturns.Money(row.LedgerAmount) - TaxReturns.Money(row.BilAmount)),
            ReconExplanation = row.ReconExplanation,
            LegalStatus = row.LegalStatus,
            Provisional = row.Provisional,
            ContentHash = row.ContentHash,
            FileName = row.FileName,
            FileHash = row.FileHash,
            ApprovalRequestId = row.ApprovalRequestId is { } a ? new ApprovalRequestId(a) : null,
            SubmittedBy = row.SubmittedBy,
            SubmittedAt = row.SubmittedAt,
            ReviewedBy = row.ReviewedBy,
            ReviewedAt = row.ReviewedAt,
            FiledBy = row.FiledBy,
            FiledAt = row.FiledAt,
            FiledOn = row.FiledOn,
            FilingReference = row.FilingReference,
            DisbursementId = row.DisbursementId?.ToString("D"),
            PaymentStatus = row.PaymentStatus,
            CreatedBy = row.CreatedBy,
            CreatedAt = row.CreatedAt,
            Adjustments =
            [
                .. adjustments.Select(a => new TaxReturnAdjustmentView
                {
                    AdjustmentId = a.AdjustmentId, Amount = TaxReturns.Text(a.Amount), Reason = a.Reason, EvidenceRef = a.EvidenceRef,
                    ExplainsReconciliation = a.ExplainsReconciliation, CreatedBy = a.CreatedBy, CreatedAt = a.CreatedAt,
                }),
            ],
        };
    }

    private static TaxReturnListItem ToListItem(TaxReturnRow row) => new()
    {
        ReturnId = row.ReturnId,
        Period = row.PeriodCode,
        TaxCode = row.TaxCode,
        VersionNo = row.VersionNo,
        Status = ListStatusOf(row.Status),
        Currency = row.Currency.Trim(),
        NetPayable = TaxReturns.Text(row.NetPayable),
        Reconciliation = ListReconOf(TaxReturns.ReconStatus(row.LedgerAmount, row.BilAmount, row.ReconExplanation)),
        Provisional = row.Provisional,
        SupersededByReturnId = row.SupersededByReturnId,
        FileName = row.FileName,
        FilingReference = row.FilingReference,
        FiledAt = row.FiledAt,
        PaymentStatus = row.PaymentStatus,
    };

    private static TaxReturnView.StatusValue StatusOf(string status) => status switch
    {
        TaxReturns.Reviewed => TaxReturnView.StatusValue.Reviewed,
        TaxReturns.Filed => TaxReturnView.StatusValue.Filed,
        TaxReturns.Paid => TaxReturnView.StatusValue.Paid,
        _ => TaxReturnView.StatusValue.Draft,
    };

    private static TaxReturnListItem.StatusValue ListStatusOf(string status) => status switch
    {
        TaxReturns.Reviewed => TaxReturnListItem.StatusValue.Reviewed,
        TaxReturns.Filed => TaxReturnListItem.StatusValue.Filed,
        TaxReturns.Paid => TaxReturnListItem.StatusValue.Paid,
        _ => TaxReturnListItem.StatusValue.Draft,
    };

    private static TaxReturnView.ReconciliationValue ReconOf(string status) => status switch
    {
        TaxReturns.Matched => TaxReturnView.ReconciliationValue.Matched,
        TaxReturns.Explained => TaxReturnView.ReconciliationValue.Explained,
        _ => TaxReturnView.ReconciliationValue.Unexplained,
    };

    private static TaxReturnListItem.ReconciliationValue ListReconOf(string status) => status switch
    {
        TaxReturns.Matched => TaxReturnListItem.ReconciliationValue.Matched,
        TaxReturns.Explained => TaxReturnListItem.ReconciliationValue.Explained,
        _ => TaxReturnListItem.ReconciliationValue.Unexplained,
    };
}
