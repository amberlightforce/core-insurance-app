using CoreIns.Modules.Billing.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// Verification of payee (REQ-BIL-203, Regulation (EU) 2024/886 Art. 5c): name against IBAN at the payee's bank. The
/// MKT SPI <c>PayeeVerification</c> (REQ-MKT-102) does not exist yet, so BIL owns this adapter seam until it does.
/// When no adapter is bound (Production in the slice) the outcome is NotAvailable and disbursements fail closed.
/// </summary>
internal interface IPayeeVerifier
{
    /// <summary>Verifies the holder name against the IBAN. The IBAN never leaves the adapter (no log, no exception text).</summary>
    Task<VopCheck> VerifyAsync(string iban, string holderName, CancellationToken cancellationToken);
}

/// <summary>One verification outcome with the bank's suggested name on CloseMatch.</summary>
internal sealed record VopCheck(VopOutcome Outcome, string? SuggestedName);

/// <summary>
/// The bank channel of released disbursements (REQ-BIL-206/207: pain.001 out, pain.002 acknowledgement, camt.053/054
/// debit). The MKT SPI <c>BankFileFormat</c> and the batch release are later packages; the slice hands each released
/// disbursement to the channel and records its acknowledgement and statement debit.
/// </summary>
internal interface IBankChannel
{
    /// <summary>Submits one released payment; returns the bank's acknowledgement and, when known, the statement debit.</summary>
    Task<BankOutcome> SubmitAsync(BankInstruction instruction, CancellationToken cancellationToken);
}

/// <summary>What the channel is asked to pay (the payee account is a reference; no IBAN or name in this record).</summary>
internal sealed record BankInstruction(DisbursementId DisbursementId, string EndToEndId, Money Amount, BusinessDate ValueDate, string Method, Guid PayeeAccountId);

/// <summary>The channel's answer: accepted (pain.002 ACCP → Issued) and debited (camt → Cleared), with the bank reference.</summary>
internal sealed record BankOutcome(bool Accepted, bool Debited, BusinessDate ValueDate, string BankReference);

/// <summary>VoP stub (D-SL2-05): every payee matches. Never bound in Production.</summary>
internal sealed class StubPayeeVerifier : IPayeeVerifier
{
    public Task<VopCheck> VerifyAsync(string iban, string holderName, CancellationToken cancellationToken) =>
        Task.FromResult(new VopCheck(VopOutcome.Match, null));
}

/// <summary>
/// Bank-channel stub (D-SL2-05): acknowledges every released payment and reports its statement debit at once, with no
/// file and no external call, so Released → Issued → Cleared happens deterministically in the releasing transaction.
/// Never bound in Production.
/// </summary>
internal sealed class StubBankChannel : IBankChannel
{
    public const string ReferencePrefix = "STUB-";

    public Task<BankOutcome> SubmitAsync(BankInstruction instruction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return Task.FromResult(new BankOutcome(true, true, instruction.ValueDate, ReferencePrefix + instruction.EndToEndId));
    }
}

/// <summary>Binding of BIL's payment adapters (the Host calls it next to the country packs).</summary>
public static class BillingPaymentAdapters
{
    /// <summary>
    /// Binds the slice's VoP and bank-channel stubs outside Production only (D-SL2-05, as the myDATA fiscal stub). In
    /// Production nothing is bound: payee accounts stay VoPNotAvailable and disbursements are refused or stay Approved
    /// (fail closed) until real adapters exist.
    /// </summary>
    public static IServiceCollection AddBillingPaymentAdapters(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsProduction())
        {
            services.AddSingleton<IPayeeVerifier, StubPayeeVerifier>();
            services.AddSingleton<IBankChannel, StubBankChannel>();
        }

        return services;
    }
}
