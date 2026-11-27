using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// Sanctions screening of a disbursement payee (REQ-BIL-200, -214), fail closed: shared by the request (<c>bil.Disbursement.request</c>) and
/// the bank release of a batch (BO-BIL-10), which screens every payee again because lists change between the request and the payment.
/// </summary>
internal sealed partial class PayeeScreener(IServiceProvider services, ILogger<PayeeScreener> logger)
{
    /// <summary>The list versions on Clear, else the fail-closed error.</summary>
    public async Task<Result<string>> ScreenAsync(PartyId payee, DisbursementId disbursementId, CancellationToken cancellationToken)
    {
        if (services.GetService<IPartyScreeningService>() is not { } screening)
        {
            return DomainError.Of(ModuleCode.BIL, "SCREENING-UNAVAILABLE", "Sanctions screening is not available; the payment is not made (REQ-BIL-214).");
        }

        ScreeningScreenResponse response;
        try
        {
            response = await screening.ScreenAsync(
                new ScreeningScreenRequest { PartyId = payee, CallerRef = ObjectRef.For(ModuleCode.BIL, "Disbursement", disbursementId) },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScreeningFailed(logger, ex.GetType().Name);
            return DomainError.Of(ModuleCode.BIL, "SCREENING-UNAVAILABLE", "Sanctions screening failed; the payment is not made (REQ-BIL-214).");
        }

        if (response.Result != ScreeningScreenResponse.ResultValue.Clear || response.PaymentBlock)
        {
            return DomainError.Of(ModuleCode.BIL, "PAYEE-BLOCKED", $"Sanctions screening of the payee returned {response.Result}{(response.PaymentBlock ? " with a payment block" : string.Empty)}; the payment is not made (REQ-BIL-200).");
        }

        return string.Join(",", response.ListVersions);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sanctions screening failed ({ExceptionType}); the disbursement is refused (fail closed).")]
    private static partial void LogScreeningFailed(ILogger logger, string exceptionType);
}
