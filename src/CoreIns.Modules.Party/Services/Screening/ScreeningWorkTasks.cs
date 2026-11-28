using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Work.Contracts;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Party.Services.Screening;

/// <summary>
/// Raises the review task of a screening case in the Work queue through <c>wrk.Activity.create</c> (BO-PTY-07). Idempotent on (pattern, case): an
/// already-open activity counts as raised. The case row is the system of record: a missing or failing Work service is logged and never undoes the
/// case or the payment block. TODO(hardening): retry a failed raise from a sweeper instead of only logging it.
/// </summary>
internal sealed partial class ScreeningWorkTasks(IServiceProvider services, ILogger<ScreeningWorkTasks> logger)
{
    /// <summary>The WRK pattern (owned by the Work catalogue).</summary>
    public const string Pattern = "WRK-PTY-SCREENING-HIT";

    public async Task RaiseAsync(Guid caseId, string caseNumber, CancellationToken cancellationToken)
    {
        var work = services.GetService<IWorkActivityService>();
        if (work is null)
        {
            LogUnavailable(logger, caseNumber);
            return;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Pattern}|{caseId:D}"));
        var key = IdempotencyKey.From(new Guid(hash.AsSpan(0, 16)));
        try
        {
            await work.CreateAsync(
                new ActivityCreateRequest
                {
                    PatternCode = Pattern, PrimaryObject = new ObjectRef(ModuleCode.PTY, "ScreeningCase", caseId.ToString("D")), RaisingModule = ModuleCode.PTY,
                    CauseRef = caseNumber,
                },
                new CommandOptions(key),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.ToString().EndsWith("DUPLICATE", StringComparison.Ordinal))
        {
            // already open: nothing to add
        }
#pragma warning disable CA1031 // The case and the payment block are already stored; staff still see the case in the queue.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, caseNumber);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Work service is not available; the review task of screening case {CaseNumber} was not raised.")]
    private static partial void LogUnavailable(ILogger logger, string caseNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raising the review task of screening case {CaseNumber} failed; the case stands.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string caseNumber);
}
