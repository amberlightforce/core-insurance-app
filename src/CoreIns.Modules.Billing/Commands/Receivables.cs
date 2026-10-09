using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Billing.Commands;

// Caller is captured by the owner service before the nested BIL pipeline replaces the current module.
internal sealed record RegisterReceivable(ReceivableRegisterRequest Request, ModuleCode? Caller) : ICommand<ReceivableRegisterResponse>;

internal sealed class RegisterReceivableValidator : AbstractValidator<RegisterReceivable>
{
    public RegisterReceivableValidator()
    {
        RuleFor(c => c.Request.SourceId).NotEmpty().MaximumLength(140);
        RuleFor(c => c.Request.Amount).Must(m => m.IsPositive && m.IsRoundedToMinorUnits && m.Currency.Code == "EUR");
        RuleFor(c => c.Request.Currency).Must((c, currency) => currency is null || currency == c.Request.Amount.Currency);
        RuleFor(c => c.Request.SourceType).IsInEnum();
        RuleFor(c => c.Request.Purpose).IsInEnum();
        RuleFor(c => c.Request.StatementRef).MaximumLength(140);
    }
}

internal sealed class RegisterReceivableHandler(BillingDbContext db, RequestContext context, ILegalEntityDirectory entities, IClock clock, INumberingService numbering, LedgerWriter ledger, IHostEnvironment environment, IOptions<BillingOptions> options)
    : ICommandHandler<RegisterReceivable, ReceivableRegisterResponse>
{
    public async Task<Result<ReceivableRegisterResponse>> HandleAsync(RegisterReceivable command, CancellationToken ct)
    {
        if (command.Caller != ModuleCode.CLM) return DomainError.Of(ModuleCode.BIL, "NOT-PERMITTED", "Only a registered CLM command may register receivables.");
        var r = command.Request;
        var isFs = r.SourceType == ReceivableSourceType.FsClearing;
        if ((isFs && (r.Purpose != ReceivablePurpose.FsNet || string.IsNullOrWhiteSpace(r.StatementRef) || r.ClaimId is not null || r.RecoveryId is not null))
            || (!isFs && (r.Purpose is not (ReceivablePurpose.Salvage or ReceivablePurpose.Subrogation) || r.ClaimId is null || r.RecoveryId is null || r.StatementRef is not null)))
            return DomainError.Of(ModuleCode.BIL, "SOURCE", "The receivable purpose and evidence do not match its source.");
        if (environment.IsProduction()) return DomainError.Of(ModuleCode.BIL, isFs ? "SOURCE" : "FISCAL-TREATMENT-OPEN", "Claim fiscal treatment and FS stub clearing are unavailable in Production.");
        var entity = entities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var source = isFs ? "FS_CLEARING" : "CLM_CLAIM_PAYMENT";
        if (!options.Value.Sources.TryGetValue(source, out var registered) || registered.CallingModule != "CLM" || registered.Direction is not ("in" or "both")) return DomainError.Of(ModuleCode.BIL, "SOURCE", "Incoming source is absent from the source register.");
        var purpose = isFs ? "FS_NET" : r.Purpose == ReceivablePurpose.Salvage ? "SALVAGE" : "SUBROGATION";
        // Serialize account creation and duplicate detection for this payer, within the ambient transaction.
        var lockKey = entity.Value.ToString("D") + ":" + r.CounterpartyPartyId.Value.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct).ConfigureAwait(false);
        if (await db.Receivables.AnyAsync(x => x.LegalEntityId == entity && x.SourceType == source && x.SourceId == r.SourceId && x.Purpose == purpose && x.CounterpartyPartyId == r.CounterpartyPartyId && x.Amount == r.Amount.Amount, ct).ConfigureAwait(false))
            return DomainError.Of(ModuleCode.BIL, "DUPLICATE", "This receivable business key is already registered.");
        var type = isFs ? "CLEARING" : "CLAIM_RECOVERY";
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.LegalEntityId == entity && a.PayerPartyId == r.CounterpartyPartyId && a.Currency == "EUR" && a.AccountType == type && a.Status == "ACTIVE", ct).ConfigureAwait(false);
        if (account is null)
        {
            var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.BillingAccount, ledger.Today), ct).ConfigureAwait(false);
            account = new BillingAccountRow { BillingAccountId = BillingAccountId.New(), LegalEntityId = entity, Jurisdiction = ledger.Jurisdiction.Value, PayerPartyId = r.CounterpartyPartyId, AccountNumber = number.Value, AccountType = type, Currency = "EUR", Status = "ACTIVE", CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(), RecordVersion = 1 };
            db.Accounts.Add(account);
        }
        var row = new ReceivableRow
        {
            ReceivableId = Guid.CreateVersion7(), LegalEntityId = entity, BillingAccountId = account.BillingAccountId,
            SourceType = source, SourceId = r.SourceId, Purpose = purpose, CounterpartyPartyId = r.CounterpartyPartyId,
            ClaimId = r.ClaimId, RecoveryId = r.RecoveryId, StatementRef = r.StatementRef, Amount = r.Amount.Amount,
            Currency = "EUR", DueDate = r.DueDate, RegisteredAt = clock.Now, RegisteredBy = context.Actor.ToString(), RecordVersion = 1,
        };
        row.PaymentReference = ReceivableReference.Create(row.ReceivableId);
        var rule = await ledger.RuleAsync(ledger.Key("RECEIVABLE_REGISTERED", RuleQualifiers.Any, source), ct).ConfigureAwait(false);
        if (rule.IsFailure) return rule.Error!;
        db.Receivables.Add(row);
        ledger.Post(new EntrySpec("RECEIVABLE_REGISTERED", account.BillingAccountId,
            [new PostingLeg(rule.Value, r.Amount, new LineDimensions { BillingAccountId = account.BillingAccountId, SourceType = source, SourceId = row.SourceId, ClaimId = row.ClaimId, RecoveryId = row.RecoveryId, StatementRef = row.StatementRef, CounterpartyPartyId = row.CounterpartyPartyId })],
            BusinessKeys.Empty.With("receivableId", row.ReceivableId.ToString("D")), "bil.Receivable.register"));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return DomainError.Of(ModuleCode.BIL, "DUPLICATE", "The receivable or reference was registered concurrently."); }
        return new ReceivableRegisterResponse { ReceivableId = row.ReceivableId, BillingAccountId = row.BillingAccountId, PaymentReference = row.PaymentReference, Receivable = ReceivableReader.View(row, 0) };
    }
}

internal sealed class RegisterReceivableAuditor : ICommandAuditor<RegisterReceivable, ReceivableRegisterResponse>
{
    public CommandAuditFacts Describe(RegisterReceivable command, Result<ReceivableRegisterResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.BIL, "Receivable", result is { IsSuccess: true } r ? r.Value.ReceivableId.ToString("D") : command.Request.SourceId),
        BusinessKeys = BusinessKeys.Empty.With("sourceId", command.Request.SourceId),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(null, new { receivableId = ok.Value.ReceivableId, amount = command.Request.Amount.ToString() }) : [],
    };
}



