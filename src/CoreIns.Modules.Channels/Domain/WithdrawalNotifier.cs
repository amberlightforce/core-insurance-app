using CoreIns.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Channels.Domain;

/// <summary>
/// The seam to the notification channel: delivers the confirmation token of a withdrawal declaration to the declarant by e-mail
/// (REQ-CHN-007). No notification module exists yet; the default only records that a token was issued.
/// </summary>
internal interface IWithdrawalNotifier
{
    /// <summary>Sends the token to the declarant. The token is a secret: implementations never log it.</summary>
    Task SendConfirmationTokenAsync(string requestNumber, string email, string token, Instant expiresAt, CancellationToken cancellationToken);
}

/// <summary>Default notifier: logs that no channel is wired (never the token or the address).</summary>
internal sealed partial class LoggingWithdrawalNotifier(ILogger<LoggingWithdrawalNotifier> logger) : IWithdrawalNotifier
{
    public Task SendConfirmationTokenAsync(string requestNumber, string email, string token, Instant expiresAt, CancellationToken cancellationToken)
    {
        // TODO(hardening): replace by the real e-mail channel; until then set Channels:ReturnWithdrawalToken=true where the token must reach the customer.
        LogNotWired(logger, requestNumber);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No notification channel is wired; the confirmation token of withdrawal {RequestNumber} was not sent.")]
    private static partial void LogNotWired(ILogger logger, string requestNumber);
}
