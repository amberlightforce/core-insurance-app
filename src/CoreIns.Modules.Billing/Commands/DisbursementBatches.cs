using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.DisbursementBatch.prepare</c> (BO-BIL-10): group Approved non-claim disbursements into one bank file.</summary>
internal sealed record PrepareDisbursementBatch(DisbursementBatchPrepareRequest Request) : ICommand<DisbursementBatchPrepareResponse>;

/// <summary><c>bil.DisbursementBatch.release</c> (BO-BIL-10): the first person asks for the release; the file is rebuilt without stopped items.</summary>
internal sealed record RequestDisbursementBatchRelease(DisbursementBatchReleaseRequest Request) : ICommand<DisbursementBatchReleaseResponse>;

/// <summary><c>bil.DisbursementBatch.approveRelease</c> (BO-BIL-10): the second person releases the batch to the bank.</summary>
internal sealed record ApproveDisbursementBatchRelease(DisbursementBatchApproveReleaseRequest Request) : ICommand<DisbursementBatchApproveReleaseResponse>;

internal sealed class PrepareDisbursementBatchValidator : AbstractValidator<PrepareDisbursementBatch>
{
    public PrepareDisbursementBatchValidator()
    {
        RuleFor(c => c.Request.BankAccount).NotEmpty().MaximumLength(60).Must(a => Iban.TryParse(a, out _)).WithErrorCode("IBAN_INVALID").WithMessage("The bank account is not a valid IBAN.");
        RuleFor(c => c.Request.DisbursementIds).Must(ids => ids is null || (ids.Count is > 0 and <= BatchRules.MaxItems && ids.Distinct().Count() == ids.Count))
            .WithErrorCode("IDS").WithMessage("disbursementIds must be 1 to 1000 distinct ids.");
    }
}

/// <summary>Constants and the pure eligibility rule of a batch.</summary>
internal static class BatchRules
{
    public const int MaxItems = 1000;

    /// <summary>Why an Approved or not-Approved disbursement cannot enter a batch; null when it can.</summary>
    public static string? Hold(DisbursementRow row, PayeeAccountRow? account, bool inBatch, BusinessDate today)
    {
        if (!DisbursementCodes.IsGeneric(row.SourceType))
        {
            return "SOURCE_NOT_BATCHED";
        }

        if (row.State != Codes.Of(DisbursementState.Approved))
        {
            return "NOT_APPROVED";
        }

        if (inBatch)
        {
            return "IN_BATCH";
        }

        if (row.Currency != Currency.EUR.Code)
        {
            return "CURRENCY";
        }

        if (account is null || account.Status != Codes.Of(PayeeAccountStatus.Active) || account.ValidTo is not null)
        {
            return "PAYEE_ACCOUNT";
        }

        if (Codes.Parse<PayeeVerification>(account.VerificationStatus) is not (PayeeVerification.VopMatched or PayeeVerification.Confirmed))
        {
            return "VOP";
        }

        return account.IsChange && today < account.CoolingOffUntil ? "COOLING_OFF" : null;
    }
}

/// <summary>Builds the bank file of a batch and its hash, and the API view; shared by the three commands.</summary>
internal sealed class DisbursementBatchFiles(BillingDbContext db, PayeeProtection protection, RequestContext context)
{
    public sealed record Built(string Xml, Sha256Hash Hash, string FileRef, IReadOnlyList<DisbursementRow> Rows, decimal Total);

    /// <summary>The file of the given disbursements (decrypts each payee IBAN for the file only).</summary>
    public async Task<Built> BuildAsync(DisbursementBatchRow batch, IReadOnlyList<DisbursementRow> rows, CancellationToken cancellationToken)
    {
        var accountIds = rows.Select(r => r.PayeeAccountId).Distinct().ToList();
        var accounts = await db.PayeeAccounts.AsNoTracking().Where(a => accountIds.Contains(a.PayeeAccountId)).ToDictionaryAsync(a => a.PayeeAccountId, cancellationToken).ConfigureAwait(false);
        var transfers = new List<Pain001Transfer>(rows.Count);
        foreach (var row in rows)
        {
            var account = accounts[row.PayeeAccountId];
            var iban = await protection.DecryptAsync(account.PayeeAccountId, account.IbanEncrypted, cancellationToken).ConfigureAwait(false);
            transfers.Add(new Pain001Transfer(row.DisbursementNumber, row.Amount, account.HolderName, iban, row.PurposeText ?? row.StatementRef));
        }

        var total = rows.Sum(r => r.Amount);
        var xml = Pain001.Build(batch.BatchId, batch.PreparedAt, context.LegalEntity?.ToString() ?? "CoreIns", batch.BankAccount, batch.ValueDate, transfers);
        var hash = Sha256Hash.ComputeUtf8(xml);
        return new Built(xml, hash, $"bil/disbursement-batches/{batch.BatchId:N}/pain001-{hash.Value[..12]}.xml", rows, total);
    }

    /// <summary>The disbursements of a batch that are still in it, in file order.</summary>
    public async Task<(List<DisbursementBatchItemRow> Items, List<DisbursementRow> Rows)> LoadAsync(Guid batchId, bool includeDropped, CancellationToken cancellationToken)
    {
        var items = await db.DisbursementBatchItems.Where(i => i.BatchId == batchId && (includeDropped || !i.Dropped)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = items.Select(i => i.DisbursementId).ToList();
        var rows = await db.Disbursements.Where(d => ids.Contains(d.DisbursementId)).OrderBy(d => d.DisbursementNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (items, rows);
    }

    public static DisbursementBatchView View(DisbursementBatchRow batch, IReadOnlyList<DisbursementRow> rows) => new()
    {
        BatchId = batch.BatchId,
        Status = batch.State switch
        {
            BatchStates.Prepared => DisbursementBatchView.StatusValue.Prepared,
            BatchStates.ReleaseRequested => DisbursementBatchView.StatusValue.ReleaseRequested,
            _ => DisbursementBatchView.StatusValue.Released,
        },
        MaskedBankAccount = IbanMask.Mask(batch.BankAccount[^4..]),
        ValueDate = batch.ValueDate,
        ItemCount = batch.ItemCount,
        TotalAmount = new Money(batch.TotalAmount, Currency.EUR),
        FileHash = Sha256Hash.Parse(batch.FileHash),
        FileRef = batch.FileRef,
        PreparedBy = batch.PreparedBy,
        PreparedAt = batch.PreparedAt,
        ReleaseRequestedBy = batch.ReleaseRequestedBy,
        ReleaseRequestedAt = batch.ReleaseRequestedAt,
        ReleasedBy = batch.ReleasedBy,
        ReleasedAt = batch.ReleasedAt,
        Items =
        [
            .. rows.Select(r => new DisbursementBatchItem
            {
                DisbursementId = r.DisbursementId,
                DisbursementNumber = DisbursementNumber.Parse(r.DisbursementNumber),
                SourceType = r.SourceType,
                SourceId = r.SourceId,
                Amount = new Money(r.Amount, Currency.EUR),
                EndToEndId = r.DisbursementNumber,
                Status = Codes.Api<DisbursementRequestResponse.StatusValue>(Codes.Parse<DisbursementState>(r.State)).ToString(),
            }),
        ],
    };
}

/// <summary>
/// <c>bil.DisbursementBatch.prepare</c>: takes the Approved non-claim disbursements (commission, levy, tax remittance, other) that are
/// not in a batch yet and whose payee is still VoP-verified, into one batch for one own bank account and value date, and fixes the hash of
/// its SEPA pain.001 file. A disbursement is in at most one batch (primary key of the item table). Needs the bank channel and the bank-file
/// store: with neither bound (Production before a real adapter) nothing is prepared (fail closed). Posts nothing and sends nothing.
/// </summary>
internal sealed class PrepareDisbursementBatchHandler(
    BillingDbContext db, RequestContext context, IClock clock, LedgerWriter ledger, DisbursementBatchFiles files, IServiceProvider services)
    : ICommandHandler<PrepareDisbursementBatch, DisbursementBatchPrepareResponse>
{
    public async Task<Result<DisbursementBatchPrepareResponse>> HandleAsync(PrepareDisbursementBatch command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (services.GetService<IBankChannel>() is null || services.GetService<IBankFileStore>() is null)
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-UNAVAILABLE", "No bank channel or bank-file store is bound; a batch cannot be prepared (fail closed).");
        }

        var today = ledger.Today;
        if (request.ValueDate < today)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "The value date is in the past.");
        }

        var legalEntity = ledger.LegalEntityId;
        var generic = new[] { DisbursementCodes.CommissionPayment, DisbursementCodes.LevyPayment, DisbursementCodes.TaxRemittance, DisbursementCodes.OtherPayment };
        var approved = Codes.Of(DisbursementState.Approved);
        List<DisbursementRow> candidates;
        if (request.DisbursementIds is { Count: > 0 } wanted)
        {
            candidates = await db.Disbursements.Where(d => d.LegalEntityId == legalEntity && wanted.Contains(d.DisbursementId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count != wanted.Count)
            {
                return BillingErrors.NotFound("disbursement");
            }
        }
        else
        {
            candidates = await db.Disbursements
                .Where(d => d.LegalEntityId == legalEntity && d.State == approved && generic.Contains(d.SourceType)
                            && (d.RequestedValueDate == null || d.RequestedValueDate <= request.ValueDate)
                            && !db.DisbursementBatchItems.Any(i => i.DisbursementId == d.DisbursementId))
                .OrderBy(d => d.DisbursementNumber).Take(BatchRules.MaxItems + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var candidateIds = candidates.Select(c => c.DisbursementId).ToList();
        var inBatch = (await db.DisbursementBatchItems.AsNoTracking().Where(i => candidateIds.Contains(i.DisbursementId)).Select(i => i.DisbursementId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var accountIds = candidates.Select(c => c.PayeeAccountId).Distinct().ToList();
        var accounts = await db.PayeeAccounts.AsNoTracking().Where(a => accountIds.Contains(a.PayeeAccountId)).ToDictionaryAsync(a => a.PayeeAccountId, cancellationToken).ConfigureAwait(false);
        var rows = new List<DisbursementRow>();
        var held = new List<string>();
        foreach (var row in candidates.OrderBy(c => c.DisbursementNumber, StringComparer.Ordinal))
        {
            accounts.TryGetValue(row.PayeeAccountId, out var account);
            var hold = BatchRules.Hold(row, account, inBatch.Contains(row.DisbursementId), today);
            if (hold is null)
            {
                rows.Add(row);
            }
            else if (request.DisbursementIds is not null)
            {
                held.Add($"{row.DisbursementNumber}:{hold}");
            }
        }

        if (held.Count > 0)
        {
            return DomainError.Of(ModuleCode.BIL, "HELD-ITEMS", "Some disbursements cannot enter a batch: " + string.Join(", ", held) + ".") with
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["held"] = string.Join(",", held) },
            };
        }

        if (rows.Count == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NOTHING-PAYABLE", "There is no Approved disbursement to batch for this value date.");
        }

        if (rows.Count > BatchRules.MaxItems)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", $"A batch holds at most {BatchRules.MaxItems} disbursements; name the ones to batch.");
        }

        var now = clock.Now;
        var batch = new DisbursementBatchRow
        {
            BatchId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            State = BatchStates.Prepared,
            BankAccount = Iban.Parse(request.BankAccount).Value,
            ValueDate = request.ValueDate,
            Currency = Currency.EUR.Code,
            PreparedAt = now,
            PreparedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        var built = await files.BuildAsync(batch, rows, cancellationToken).ConfigureAwait(false);
        batch.ItemCount = rows.Count;
        batch.TotalAmount = built.Total;
        batch.FileHash = built.Hash.Value;
        batch.FileRef = built.FileRef;
        db.DisbursementBatches.Add(batch);
        db.DisbursementBatchItems.AddRange(rows.Select(r => new DisbursementBatchItemRow { DisbursementId = r.DisbursementId, BatchId = batch.BatchId, Amount = r.Amount }));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent prepare took one of the items (primary key of the item table) or produced the same file (EC-08).
            return DomainError.Of(ModuleCode.BIL, "DUPLICATE", "One of the disbursements is already in a batch, or an identical batch exists.");
        }

        return new DisbursementBatchPrepareResponse { Batch = DisbursementBatchFiles.View(batch, rows), FileRef = batch.FileRef };
    }
}

/// <summary>
/// <c>bil.DisbursementBatch.release</c>: the first person asks for the release. Items that are no longer Approved (stopped since the
/// preparation) are dropped, the file and its hash are rebuilt, and the batch waits in ReleaseRequested for the second person.
/// </summary>
internal sealed class RequestDisbursementBatchReleaseHandler(BillingDbContext db, RequestContext context, IClock clock, LedgerWriter ledger, DisbursementBatchFiles files)
    : ICommandHandler<RequestDisbursementBatchRelease, DisbursementBatchReleaseResponse>
{
    public async Task<Result<DisbursementBatchReleaseResponse>> HandleAsync(RequestDisbursementBatchRelease command, CancellationToken cancellationToken)
    {
        var legalEntity = ledger.LegalEntityId;
        var id = command.Request.BatchId;
        var batch = await db.DisbursementBatches.SingleOrDefaultAsync(b => b.BatchId == id && b.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return BillingErrors.NotFound("disbursement batch");
        }

        if (batch.State == BatchStates.Released)
        {
            return DomainError.Of(ModuleCode.BIL, "APPROVAL-REQUIRED", "The batch is already released.");
        }

        var (items, rows) = await files.LoadAsync(id, includeDropped: false, cancellationToken).ConfigureAwait(false);
        var approved = Codes.Of(DisbursementState.Approved);
        var live = new List<DisbursementRow>();
        foreach (var row in rows)
        {
            if (row.State == approved)
            {
                live.Add(row);
            }
            else
            {
                items.Single(i => i.DisbursementId == row.DisbursementId).Dropped = true; // stopped or otherwise moved since the preparation
            }
        }

        if (live.Count == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "NOTHING-PAYABLE", "Every disbursement of the batch was stopped; nothing is left to release.");
        }

        var built = await files.BuildAsync(batch, live, cancellationToken).ConfigureAwait(false);
        batch.ItemCount = live.Count;
        batch.TotalAmount = built.Total;
        batch.FileHash = built.Hash.Value;
        batch.FileRef = built.FileRef;
        batch.State = BatchStates.ReleaseRequested;
        batch.ReleaseRequestedAt = clock.Now;
        batch.ReleaseRequestedBy = context.Actor.ToString();
        batch.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DisbursementBatchReleaseResponse { Batch = DisbursementBatchFiles.View(batch, live), FileRef = batch.FileRef };
    }
}

/// <summary>
/// <c>bil.DisbursementBatch.approveRelease</c> (REQ-BIL-206, BO-BIL-10): the second person releases the batch. The approver is neither the
/// person who requested the release nor the requester of any item (BIL-ERR-SOD), confirms the exact file hash (BIL-ERR-STALE when an item
/// changed), every payee is screened again (fail closed) and must still be VoP-matched, and the bank channel and file store must be bound
/// (BIL-ERR-METHOD-UNAVAILABLE otherwise, so Production fails closed). The file is written to the store, and each disbursement is released
/// through the shared <see cref="DisbursementReleaser"/>: DISBURSEMENT_RELEASED posts (source payable to disbursements in transit) and, with
/// the auto stub bank, the payment issues and clears. A second call finds the batch Released and refuses (the items are Released already).
/// </summary>
internal sealed class ApproveDisbursementBatchReleaseHandler(
    BillingDbContext db, RequestContext context, IClock clock, LedgerWriter ledger, DisbursementBatchFiles files, DisbursementReleaser releaser,
    PayeeScreener screener, IServiceProvider services) : ICommandHandler<ApproveDisbursementBatchRelease, DisbursementBatchApproveReleaseResponse>
{
    public async Task<Result<DisbursementBatchApproveReleaseResponse>> HandleAsync(ApproveDisbursementBatchRelease command, CancellationToken cancellationToken)
    {
        if (services.GetService<IBankChannel>() is not { } channel || services.GetService<IBankFileStore>() is not { } store)
        {
            return DomainError.Of(ModuleCode.BIL, "METHOD-UNAVAILABLE", "No bank channel or bank-file store is bound; the batch is not released (fail closed).");
        }

        var legalEntity = ledger.LegalEntityId;
        var request = command.Request;
        var batch = await db.DisbursementBatches.SingleOrDefaultAsync(b => b.BatchId == request.BatchId && b.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return BillingErrors.NotFound("disbursement batch");
        }

        if (batch.State != BatchStates.ReleaseRequested)
        {
            return DomainError.Of(ModuleCode.BIL, "APPROVAL-REQUIRED", batch.State == BatchStates.Released ? "The batch is already released." : "The release of the batch has not been requested.");
        }

        var actor = context.Actor.ToString();
        var (_, rows) = await files.LoadAsync(batch.BatchId, includeDropped: false, cancellationToken).ConfigureAwait(false);
        if (string.Equals(actor, batch.ReleaseRequestedBy, StringComparison.Ordinal) || rows.Any(r => string.Equals(actor, r.CreatedBy, StringComparison.Ordinal)))
        {
            return DomainError.Of(ModuleCode.BIL, "SOD", "The release of a batch is approved by a second person: neither the one who requested the release nor the requester of an item (REQ-BIL-357).");
        }

        // The batch must still be exactly what the first person asked to release: every item Approved, the file unchanged.
        var approved = Codes.Of(DisbursementState.Approved);
        if (rows.Count != batch.ItemCount || rows.Any(r => r.State != approved))
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "A disbursement of the batch changed since the release was requested; request the release again.");
        }

        var built = await files.BuildAsync(batch, rows, cancellationToken).ConfigureAwait(false);
        if (built.Hash.Value != batch.FileHash || request.FileHash.Value != batch.FileHash)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The file hash differs from the current file; request the release again and confirm the new hash.");
        }

        // Payees: still VoP-matched and screened again at release (lists change between the request and the payment), fail closed.
        var today = ledger.Today;
        var accountIds = rows.Select(r => r.PayeeAccountId).Distinct().ToList();
        var accounts = await db.PayeeAccounts.AsNoTracking().Where(a => accountIds.Contains(a.PayeeAccountId)).ToDictionaryAsync(a => a.PayeeAccountId, cancellationToken).ConfigureAwait(false);
        var screened = new HashSet<PartyId>();
        foreach (var row in rows)
        {
            var hold = BatchRules.Hold(row, accounts[row.PayeeAccountId], inBatch: false, today);
            if (hold is not null)
            {
                return DomainError.Of(ModuleCode.BIL, "VOP-HOLD", $"Disbursement {row.DisbursementNumber} cannot be released ({hold}); stop it and request the release again.");
            }

            if (screened.Add(row.PayeePartyId))
            {
                var screening = await screener.ScreenAsync(row.PayeePartyId, row.DisbursementId, cancellationToken).ConfigureAwait(false);
                if (screening.IsFailure)
                {
                    return screening.Error!;
                }
            }
        }

        var now = clock.Now;
        await store.StoreAsync(batch.FileRef, System.Text.Encoding.UTF8.GetBytes(built.Xml), cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.RecordVersion++;
            await releaser.ReleaseAsync(row, channel, new Money(row.Amount, Currency.EUR), today, now, null, cancellationToken, batch.ValueDate).ConfigureAwait(false);
        }

        batch.State = BatchStates.Released;
        batch.ReleasedAt = now;
        batch.ReleasedBy = actor;
        batch.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DisbursementBatchApproveReleaseResponse { Batch = DisbursementBatchFiles.View(batch, rows), FileRef = batch.FileRef };
    }
}

internal sealed class PrepareDisbursementBatchAuditor : ICommandAuditor<PrepareDisbursementBatch, DisbursementBatchPrepareResponse>
{
    public CommandAuditFacts Describe(PrepareDisbursementBatch command, Result<DisbursementBatchPrepareResponse>? result) => new()
    {
        ObjectRef = result is { IsSuccess: true } ok ? new ObjectRef(ModuleCode.BIL, "DisbursementBatch", ok.Value.Batch.BatchId.ToString()) : new ObjectRef(ModuleCode.BIL, "DisbursementBatch", "prepare"),
        BusinessKeys = result is { IsSuccess: true } done ? BusinessKeys.Empty.With("batchId", done.Value.Batch.BatchId.ToString()) : BusinessKeys.Empty,
        Changes = AuditDiff.Compute(null, new
        {
            valueDate = command.Request.ValueDate.ToString(),
            items = result is { IsSuccess: true } r ? r.Value.Batch.ItemCount : (int?)null,
            total = result is { IsSuccess: true } t ? t.Value.Batch.TotalAmount.ToString() : null,
            fileHash = result is { IsSuccess: true } h ? h.Value.Batch.FileHash.Value : null,
        }),
    };
}

internal sealed class RequestDisbursementBatchReleaseAuditor : ICommandAuditor<RequestDisbursementBatchRelease, DisbursementBatchReleaseResponse>
{
    public CommandAuditFacts Describe(RequestDisbursementBatchRelease command, Result<DisbursementBatchReleaseResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.BIL, "DisbursementBatch", command.Request.BatchId.ToString()),
        BusinessKeys = BusinessKeys.Empty.With("batchId", command.Request.BatchId.ToString()),
        Changes = AuditDiff.Compute(null, new { fileHash = result is { IsSuccess: true } ok ? ok.Value.Batch.FileHash.Value : null, items = result is { IsSuccess: true } r ? r.Value.Batch.ItemCount : (int?)null }),
    };
}

internal sealed class ApproveDisbursementBatchReleaseAuditor : ICommandAuditor<ApproveDisbursementBatchRelease, DisbursementBatchApproveReleaseResponse>
{
    public CommandAuditFacts Describe(ApproveDisbursementBatchRelease command, Result<DisbursementBatchApproveReleaseResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.BIL, "DisbursementBatch", command.Request.BatchId.ToString()),
        BusinessKeys = BusinessKeys.Empty.With("batchId", command.Request.BatchId.ToString()),
        Changes = AuditDiff.Compute(null, new { fileHash = command.Request.FileHash.Value, status = result is { IsSuccess: true } ok ? ok.Value.Batch.Status.ToString() : null }),
    };
}
