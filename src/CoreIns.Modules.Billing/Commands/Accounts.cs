using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Party.Contracts;
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
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.BillingAccount.update</c>: suspends or resumes an account (BO-BIL-01).</summary>
internal sealed record UpdateBillingAccount(BillingAccountId BillingAccountId, BillingAccountUpdateRequest Request) : ICommand<BillingAccountUpdateResponse>;

/// <summary><c>bil.BillingAccount.close</c>: ACTIVE or SUSPENDED to CLOSED; refused while anything is open on the account (BO-BIL-01).</summary>
internal sealed record CloseBillingAccount(BillingAccountCloseRequest Request) : ICommand<BillingAccountCloseResponse>;

/// <summary><c>bil.BillingAccount.reopen</c>: CLOSED to ACTIVE (BO-BIL-01).</summary>
internal sealed record ReopenBillingAccount(BillingAccountReopenRequest Request) : ICommand<BillingAccountReopenResponse>;

/// <summary><c>bil.BillingAccount.changePayer</c>: points the account at another PTY party; the ledger is untouched (BO-BIL-01).</summary>
internal sealed record ChangeBillingAccountPayer(BillingAccountChangePayerRequest Request) : ICommand<BillingAccountChangePayerResponse>;

internal sealed class UpdateBillingAccountValidator : AbstractValidator<UpdateBillingAccount>
{
    public UpdateBillingAccountValidator()
    {
        RuleFor(c => c.BillingAccountId.Value).NotEmpty();
        RuleFor(c => c.Request.Reason).MaximumLength(200);
    }
}

internal sealed class CloseBillingAccountValidator : AbstractValidator<CloseBillingAccount>
{
    public CloseBillingAccountValidator()
    {
        RuleFor(c => c.Request.BillingAccountId.Value).NotEmpty();
        RuleFor(c => c.Request.Reason).MaximumLength(200);
    }
}

internal sealed class ReopenBillingAccountValidator : AbstractValidator<ReopenBillingAccount>
{
    public ReopenBillingAccountValidator()
    {
        RuleFor(c => c.Request.BillingAccountId.Value).NotEmpty();
        RuleFor(c => c.Request.Reason).MaximumLength(200);
    }
}

internal sealed class ChangeBillingAccountPayerValidator : AbstractValidator<ChangeBillingAccountPayer>
{
    public ChangeBillingAccountPayerValidator()
    {
        RuleFor(c => c.Request.BillingAccountId.Value).NotEmpty();
        RuleFor(c => c.Request.NewPayerPartyId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(200);
    }
}

/// <summary>Shared lookups, refusals and saving of the account lifecycle commands (BO-BIL-01).</summary>
internal static class AccountRules
{
    /// <summary>Invoice states that need nothing more from the payer.</summary>
    private static readonly string[] SettledInvoices = [Codes.Of(InvoiceState.Paid), Codes.Of(InvoiceState.WrittenOff), Codes.Of(InvoiceState.Reversed)];

    /// <summary>Ledger accounts that must net to zero before an account is closed.</summary>
    private static readonly string[] ClosingLedger =
        [LedgerAccounts.WrittenUnbilled, LedgerAccounts.BilledReceivable, LedgerAccounts.Suspense, LedgerAccounts.RefundsPayable];

    public static DomainError NotEligible(string message) => DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", message);

    public static async Task<Result<BillingAccountRow>> LoadAsync(BillingDbContext db, LegalEntityId legalEntity, BillingAccountId id, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.SingleOrDefaultAsync(a => a.BillingAccountId == id && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        return row is null ? BillingErrors.NotFound("billing account") : row;
    }

    /// <summary>Saves the changed account, turning a lost update into <c>BIL-ERR-STALE</c> and a second ACTIVE account of the payer into the refusal.</summary>
    public static async Task<DomainError?> SaveAsync(BillingDbContext db, string conflictMessage, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.BIL, "STALE", "The billing account changed meanwhile; load it and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_billing_account_active_payer" })
        {
            return NotEligible(conflictMessage);
        }
    }

    /// <summary>The refusal when an invoice is still open, a ledger balance is not zero or a mandate is live; null when the account may close.</summary>
    public static async Task<DomainError?> CloseBlockerAsync(BillingDbContext db, BillingAccountRow account, CancellationToken cancellationToken)
    {
        var id = account.BillingAccountId;
        if (await db.Invoices.AsNoTracking().AnyAsync(i => i.BillingAccountId == id && !SettledInvoices.Contains(i.State), cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.BIL, "BALANCE-NOT-ZERO", "The account has open invoices; settle or write them off before closing it.");
        }

        var lines = await db.LedgerLines.AsNoTracking()
            .Where(l => l.BillingAccountId == id && l.Currency == account.Currency && ClosingLedger.Contains(l.AccountCode))
            .GroupBy(l => new { l.AccountCode, l.Side })
            .Select(g => new { g.Key.AccountCode, g.Key.Side, Sum = g.Sum(l => l.Amount) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var debit = Codes.Of(LedgerSide.Debit);
        if (lines.GroupBy(l => l.AccountCode).Any(g => g.Sum(l => l.Side == debit ? l.Sum : -l.Sum) != 0m))
        {
            return DomainError.Of(ModuleCode.BIL, "BALANCE-NOT-ZERO", "The account's ledger balance is not zero (unbilled, billed, unapplied or refund amounts remain).");
        }

        return await LiveMandateAsync(db, id, "close the account", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The refusal when a draft or active mandate sits on the account (one live mandate per account, BO-BIL-06a).</summary>
    public static async Task<DomainError?> LiveMandateAsync(BillingDbContext db, BillingAccountId id, string action, CancellationToken cancellationToken) =>
        await db.Mandates.AsNoTracking().AnyAsync(m => m.BillingAccountId == id && MandateRules.Live.Contains(m.Status), cancellationToken).ConfigureAwait(false)
            ? MandateRules.State($"A draft or active mandate is on the account; cancel it before you {action}.")
            : null;

    public static async Task<Result<BillingAccountGetResponse>> ViewAsync(BillingReader reader, LegalEntityId legalEntity, BillingAccountId id, CancellationToken cancellationToken) =>
        await reader.AccountAsync(legalEntity, id, cancellationToken).ConfigureAwait(false) is { } view ? view : BillingErrors.NotFound("billing account");
}

internal sealed class UpdateBillingAccountHandler(BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, BillingReader reader)
    : ICommandHandler<UpdateBillingAccount, BillingAccountUpdateResponse>
{
    public async Task<Result<BillingAccountUpdateResponse>> HandleAsync(UpdateBillingAccount command, CancellationToken cancellationToken)
    {
        var legalEntity = MandateRules.Current(context, legalEntities);
        var loaded = await AccountRules.LoadAsync(db, legalEntity, command.BillingAccountId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var account = loaded.Value;
        var target = command.Request.Status == BillingAccountUpdateRequest.StatusValue.Suspended ? BillingAccountStatus.Suspended : BillingAccountStatus.Active;
        var current = Codes.Parse<BillingAccountStatus>(account.Status);
        if (current is BillingAccountStatus.Closed or BillingAccountStatus.Closing)
        {
            return AccountRules.NotEligible($"The account is {account.Status}; only an Active or Suspended account can be changed (reopen a closed one).");
        }

        if (current != target)
        {
            account.Status = Codes.Of(target);
            account.RecordVersion++;
            if (await AccountRules.SaveAsync(db, "The payer already has another Active account in this currency.", cancellationToken).ConfigureAwait(false) is { } failure)
            {
                return failure;
            }
        }

        var view = await AccountRules.ViewAsync(reader, legalEntity, account.BillingAccountId, cancellationToken).ConfigureAwait(false);
        return view.IsFailure ? view.Error! : new BillingAccountUpdateResponse { Account = view.Value.Account, BalancesByState = view.Value.BalancesByState };
    }
}

internal sealed class CloseBillingAccountHandler(BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, BillingReader reader)
    : ICommandHandler<CloseBillingAccount, BillingAccountCloseResponse>
{
    public async Task<Result<BillingAccountCloseResponse>> HandleAsync(CloseBillingAccount command, CancellationToken cancellationToken)
    {
        var legalEntity = MandateRules.Current(context, legalEntities);
        var loaded = await AccountRules.LoadAsync(db, legalEntity, command.Request.BillingAccountId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var account = loaded.Value;
        if (account.Status == Codes.Of(BillingAccountStatus.Closed))
        {
            return AccountRules.NotEligible("The account is already closed.");
        }

        if (await AccountRules.CloseBlockerAsync(db, account, cancellationToken).ConfigureAwait(false) is { } blocker)
        {
            return blocker;
        }

        account.Status = Codes.Of(BillingAccountStatus.Closed);
        account.RecordVersion++;
        if (await AccountRules.SaveAsync(db, "The account cannot be closed.", cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        var view = await AccountRules.ViewAsync(reader, legalEntity, account.BillingAccountId, cancellationToken).ConfigureAwait(false);
        return view.IsFailure ? view.Error! : new BillingAccountCloseResponse { Account = view.Value.Account, BalancesByState = view.Value.BalancesByState };
    }
}

internal sealed class ReopenBillingAccountHandler(BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, BillingReader reader)
    : ICommandHandler<ReopenBillingAccount, BillingAccountReopenResponse>
{
    public async Task<Result<BillingAccountReopenResponse>> HandleAsync(ReopenBillingAccount command, CancellationToken cancellationToken)
    {
        var legalEntity = MandateRules.Current(context, legalEntities);
        var loaded = await AccountRules.LoadAsync(db, legalEntity, command.Request.BillingAccountId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var account = loaded.Value;
        if (account.Status != Codes.Of(BillingAccountStatus.Closed))
        {
            return AccountRules.NotEligible($"The account is {account.Status}; only a closed account can be reopened.");
        }

        account.Status = Codes.Of(BillingAccountStatus.Active);
        account.RecordVersion++;
        if (await AccountRules.SaveAsync(db, "The payer already has another Active account in this currency.", cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        var view = await AccountRules.ViewAsync(reader, legalEntity, account.BillingAccountId, cancellationToken).ConfigureAwait(false);
        return view.IsFailure ? view.Error! : new BillingAccountReopenResponse { Account = view.Value.Account, BalancesByState = view.Value.BalancesByState };
    }
}

/// <summary>
/// Moves the account to a new payer after a PTY check (the party exists and is ACTIVE or PROSPECT; PTY missing or failing refuses, fail closed). Only the
/// payer column changes: invoices, charges and ledger lines stay where they are, so the account's balances are the same before and after.
/// </summary>
internal sealed class ChangeBillingAccountPayerHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, BillingReader reader, IClock clock, IOptions<BillingOptions> options, IServiceProvider services)
    : ICommandHandler<ChangeBillingAccountPayer, BillingAccountChangePayerResponse>
{
    public async Task<Result<BillingAccountChangePayerResponse>> HandleAsync(ChangeBillingAccountPayer command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = MandateRules.Current(context, legalEntities);
        var loaded = await AccountRules.LoadAsync(db, legalEntity, request.BillingAccountId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var account = loaded.Value;
        if (account.Status is var s && (s == Codes.Of(BillingAccountStatus.Closed) || s == Codes.Of(BillingAccountStatus.Closing)))
        {
            return AccountRules.NotEligible($"The account is {account.Status}; reopen it before changing its payer.");
        }

        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        if (request.EffectiveDate is { } effective && effective > today)
        {
            // TODO(hardening): future-dated payer change (effective-dated payer history); only an immediate change is supported.
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "A future-dated payer change is not supported yet.");
        }

        var newPayer = new PartyId(request.NewPayerPartyId);
        if (newPayer == account.PayerPartyId)
        {
            return AccountRules.NotEligible("The account already has this payer.");
        }

        if (await CheckPayerAsync(newPayer, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        if (await AccountRules.LiveMandateAsync(db, account.BillingAccountId, "change the payer", cancellationToken).ConfigureAwait(false) is { } mandate)
        {
            return mandate;
        }

        var previous = account.PayerPartyId;
        account.PayerPartyId = newPayer;
        account.RecordVersion++;
        if (await AccountRules.SaveAsync(db, "The new payer already has an Active account in this currency.", cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        var view = await AccountRules.ViewAsync(reader, legalEntity, account.BillingAccountId, cancellationToken).ConfigureAwait(false);
        return view.IsFailure
            ? view.Error!
            : new BillingAccountChangePayerResponse { Account = view.Value.Account, BalancesByState = view.Value.BalancesByState, PreviousPayerPartyId = previous.Value };
    }

    /// <summary>PTY read (masked): the payer must exist and be ACTIVE or PROSPECT. Never throws; any failure refuses the change.</summary>
    private async Task<DomainError?> CheckPayerAsync(PartyId party, CancellationToken cancellationToken)
    {
        var parties = services.GetService<IPartyPartyService>();
        if (parties is null)
        {
            return DomainError.Of(ModuleCode.BIL, "PAYER-ROLE", "The new payer cannot be verified: the party service is not available.");
        }

        try
        {
            var view = (await parties.GetAsync(party.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Party;
            return view.Status.ToUpperInvariant() is "ACTIVE" or "PROSPECT"
                ? null
                : DomainError.Of(ModuleCode.BIL, "PAYER-ROLE", $"The new payer is {view.Status}; only a live party can pay.");
        }
        catch (DomainException ex) when (ex.Error.Code.Name == "NOT-FOUND")
        {
            return DomainError.Of(ModuleCode.BIL, "PAYER-ROLE", "The new payer does not exist.");
        }
#pragma warning disable CA1031 // A failing PTY call refuses the change (fail closed).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return DomainError.Of(ModuleCode.BIL, "PAYER-ROLE", "The new payer cannot be verified right now.");
        }
    }
}

/// <summary>Audit facts of the account lifecycle commands: ids, states and the reason; no personal data.</summary>
internal sealed class BillingAccountAuditor<TCommand, TResponse> : ICommandAuditor<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResponse>? result)
    {
        var (id, reason) = command switch
        {
            UpdateBillingAccount c => (c.BillingAccountId.Value, c.Request.Reason),
            CloseBillingAccount c => (c.Request.BillingAccountId.Value, c.Request.Reason),
            ReopenBillingAccount c => (c.Request.BillingAccountId.Value, c.Request.Reason),
            ChangeBillingAccountPayer c => (c.Request.BillingAccountId.Value, c.Request.Reason),
            _ => (Guid.Empty, null),
        };
        object? after = null;
        if (result is { IsSuccess: true } ok)
        {
            after = ok.Value switch
            {
                BillingAccountUpdateResponse r => Facts(r.Account, null),
                BillingAccountCloseResponse r => Facts(r.Account, null),
                BillingAccountReopenResponse r => Facts(r.Account, null),
                BillingAccountChangePayerResponse r => Facts(r.Account, r.PreviousPayerPartyId),
                _ => null,
            };
        }

        var key = id.ToString("D");
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "BillingAccount", key),
            BusinessKeys = BusinessKeys.Empty.With("billingAccountId", key),
            Changes = after is null ? [] : AuditDiff.Compute(null, new { operation = typeof(TCommand).Name, reason, state = after }),
        };
    }

    private static object Facts(BillingAccountView account, Guid? previousPayer) => new
    {
        account.AccountNumber,
        status = account.Status.ToString(),
        payerPartyId = account.PayerPartyId,
        previousPayerPartyId = previousPayer,
    };
}
