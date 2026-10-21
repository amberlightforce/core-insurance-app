using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Context;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Billing.MigrationImport;

/// <summary>Object types the BIL importer accepts.</summary>
internal static class BillingImportTypes
{
    public const string Receivable = "Receivable";
    public const string Mandate = "Mandate";
}

/// <summary>One open item of a legacy invoice; the items must add up to the legacy outstanding.</summary>
internal sealed record ImportOpenItem(decimal Amount, string? Description);

/// <summary>Legacy invoice with an outstanding balance (SL6-MIG-BIL-IMPORT).</summary>
internal sealed record ImportReceivablePayload(
    string LegacyInvoiceNumber,
    PartyId PayerPartyId,
    string? PolicyLegacyNumber,
    DateOnly DueDate,
    string? Currency,
    decimal? OriginalAmount,
    decimal Outstanding,
    List<ImportOpenItem>? OpenItems);

/// <summary>Legacy SEPA mandate: the original reference and signature date are kept as they are.</summary>
internal sealed record ImportMandatePayload(string MandateReference, PartyId PayerPartyId, DateOnly SignatureDate, string? IbanMasked);

/// <summary>
/// <c>IMigrationImporter</c> for module BIL (SL6-MIG-BIL-IMPORT): opening receivables (one per legacy invoice, the legacy outstanding to
/// the cent, checked against the sum of the payload's open items) and SEPA mandates, all origin=MIGRATION. Nothing is invoiced or
/// fiscalised: these are balances brought over, not new billing. Idempotency is the framework's xref; the unique indexes catch a repeat.
/// </summary>
internal sealed class BillingMigrationImporter(
    BillingDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    LedgerWriter ledger) : IMigrationImporter
{
    private const string Validation = "MIG-ERR-VALIDATION";

    public string ModuleName => "BIL";

    public async Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(importContext);
        try
        {
            return item.ObjectType switch
            {
                BillingImportTypes.Receivable => await ImportReceivableAsync(item, importContext, cancellationToken).ConfigureAwait(false),
                BillingImportTypes.Mandate => await ImportMandateAsync(item, importContext, cancellationToken).ConfigureAwait(false),
                _ => Failed(Validation, $"BIL imports object types Receivable and Mandate, not '{item.ObjectType}'."),
            };
        }
        catch (JsonException ex)
        {
            return Failed(Validation, "The payload is not valid: " + ex.Message);
        }
    }

    private async Task<ImportOutcome> ImportReceivableAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        var payload = item.Payload.Deserialize<ImportReceivablePayload>(SharedKernelJson.Options);
        if (payload is null || string.IsNullOrWhiteSpace(payload.LegacyInvoiceNumber) || payload.PayerPartyId.Value == Guid.Empty)
        {
            return Failed(Validation, "A receivable needs legacyInvoiceNumber, payerPartyId, dueDate and outstanding.");
        }

        var currency = Currency.FromCode(payload.Currency ?? "EUR");
        var outstanding = new Money(payload.Outstanding, currency);
        if (!outstanding.IsPositive || !outstanding.IsRoundedToMinorUnits)
        {
            return Failed(Validation, "The outstanding must be positive with at most two decimals.");
        }

        if (payload.OpenItems is not { Count: > 0 })
        {
            return Failed(Validation, "A receivable needs its open items so the outstanding can be checked.");
        }

        var itemsTotal = 0m;
        foreach (var open in payload.OpenItems)
        {
            if (open.Amount <= 0 || !new Money(open.Amount, currency).IsRoundedToMinorUnits)
            {
                return Failed(Validation, "Every open item needs a positive amount with at most two decimals.");
            }

            itemsTotal += open.Amount;
        }

        if (itemsTotal != payload.Outstanding)
        {
            return Failed(
                "MIG-ERR-SUM-MISMATCH",
                $"The open items add up to {itemsTotal.ToString("0.00", CultureInfo.InvariantCulture)}, not the legacy outstanding {payload.Outstanding.ToString("0.00", CultureInfo.InvariantCulture)}.");
        }

        var original = payload.OriginalAmount ?? payload.Outstanding;
        if (original < payload.Outstanding || !new Money(original, currency).IsRoundedToMinorUnits)
        {
            return Failed(Validation, "originalAmount must not be below the outstanding.");
        }

        // TODO(hardening): other currencies; the sub-ledger and receipts of the slice are EUR only.
        if (currency.Code != "EUR")
        {
            return Failed(Validation, "Only EUR receivables are imported.");
        }

        var legalEntity = LegalEntity();
        if (await db.MigrationReceivables.AnyAsync(r => r.LegalEntityId == legalEntity && r.LegacyInvoiceNumber == payload.LegacyInvoiceNumber, cancellationToken).ConfigureAwait(false))
        {
            return Failed("MIG-ERR-DUPLICATE", $"Legacy invoice {payload.LegacyInvoiceNumber} is already imported.");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would import opening receivable {payload.LegacyInvoiceNumber}, outstanding {outstanding}.");
        }

        var account = await AccountAsync(legalEntity, payload.PayerPartyId, cancellationToken).ConfigureAwait(false);
        var row = new MigrationReceivableRow
        {
            ReceivableId = Guid.CreateVersion7(), LegalEntityId = legalEntity, BatchId = importContext.BatchId,
            LegacyInvoiceNumber = payload.LegacyInvoiceNumber, BillingAccountId = account, PayerPartyId = payload.PayerPartyId,
            PolicyLegacyNumber = payload.PolicyLegacyNumber, OriginalAmount = original, OutstandingAmount = payload.Outstanding,
            Currency = currency.Code, DueDate = payload.DueDate, ImportedAt = clock.Now, ImportedBy = context.Actor.ToString(),
        };

        // The opening receivable is the BIL sub-ledger of the legacy balance: bil.migration_receivable (per account and currency, summed by
        // the reconciliation against the FIN opening-balance receivable accounts). It posts no ledger entry on purpose: a BillingEntryPosted
        // would make FIN journal the receivable a second time next to the FIN opening-balance journal (REQ-FIN-072 double count).
        db.MigrationReceivables.Add(row);
        return await SaveAsync(row.ReceivableId, $"Opening receivable {payload.LegacyInvoiceNumber}: {outstanding}.", payload.LegacyInvoiceNumber, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportOutcome> ImportMandateAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        var payload = item.Payload.Deserialize<ImportMandatePayload>(SharedKernelJson.Options);
        if (payload is null || string.IsNullOrWhiteSpace(payload.MandateReference) || payload.PayerPartyId.Value == Guid.Empty)
        {
            return Failed(Validation, "A mandate needs mandateReference, payerPartyId and signatureDate.");
        }

        // SEPA unique mandate reference: at most 35 characters.
        if (payload.MandateReference.Length > 35)
        {
            return Failed(Validation, "The mandate reference is longer than 35 characters.");
        }

        var today = DateOnly.FromDateTime(clock.Now.ToUtcDateTime());
        if (payload.SignatureDate > today)
        {
            return Failed(Validation, "The signature date is in the future.");
        }

        var legalEntity = LegalEntity();
        if (await db.MigrationMandates.AnyAsync(m => m.LegalEntityId == legalEntity && m.MandateReference == payload.MandateReference, cancellationToken).ConfigureAwait(false))
        {
            return Failed("MIG-ERR-DUPLICATE", $"Mandate {payload.MandateReference} is already imported.");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would import mandate {payload.MandateReference} signed {payload.SignatureDate:yyyy-MM-dd}.");
        }

        var account = await AccountAsync(legalEntity, payload.PayerPartyId, cancellationToken).ConfigureAwait(false);
        var row = new MigrationMandateRow
        {
            MandateId = Guid.CreateVersion7(), LegalEntityId = legalEntity, BatchId = importContext.BatchId, BillingAccountId = account,
            PayerPartyId = payload.PayerPartyId, MandateReference = payload.MandateReference, SignatureDate = payload.SignatureDate,
            IbanMasked = payload.IbanMasked, ImportedAt = clock.Now, ImportedBy = context.Actor.ToString(),
        };

        // TODO(hardening): the IBAN itself (P2, field encryption) and the mandate scheme / sequence type; only the masked IBAN is carried.
        db.MigrationMandates.Add(row);
        return await SaveAsync(row.MandateId, $"Mandate {payload.MandateReference}.", payload.MandateReference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The payer's active PREMIUM account in EUR, created when the payer has none yet.</summary>
    private async Task<BillingAccountId> AccountAsync(LegalEntityId legalEntity, PartyId payer, CancellationToken cancellationToken)
    {
        var lockKey = legalEntity.Value.ToString("D") + ":" + payer.Value.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken).ConfigureAwait(false);
        var existing = await db.Accounts.Where(a => a.LegalEntityId == legalEntity && a.PayerPartyId == payer && a.Currency == "EUR" && a.AccountType == "PREMIUM" && a.Status == "ACTIVE")
            .Select(a => (BillingAccountId?)a.BillingAccountId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { } id)
        {
            return id;
        }

        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.BillingAccount, ledger.Today), cancellationToken).ConfigureAwait(false);
        var account = new BillingAccountRow
        {
            BillingAccountId = BillingAccountId.New(), LegalEntityId = legalEntity, Jurisdiction = ledger.Jurisdiction.Value, PayerPartyId = payer,
            AccountNumber = number.Value, AccountType = "PREMIUM", Currency = "EUR", Status = "ACTIVE", CreatedAt = clock.Now,
            CreatedBy = context.Actor.ToString(), RecordVersion = 1,
        };
        db.Accounts.Add(account);
        return account.BillingAccountId;
    }

    private async Task<ImportOutcome> SaveAsync(Guid coreId, string message, string legacyKey, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Failed("MIG-ERR-DUPLICATE", $"{legacyKey} conflicts with an existing record.");
        }

        return new ImportOutcome(ImportStatus.Imported, coreId, null, message);
    }

    private LegalEntityId LegalEntity() =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private static ImportOutcome Failed(string code, string message) => new(ImportStatus.Failed, null, code, message);
}
