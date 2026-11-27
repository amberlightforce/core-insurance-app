using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Work.Contracts;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Policy.Services;

/// <summary>
/// Raises the PSR (policy service representative) review task in the Work queue through <c>wrk.Activity.create</c> (NX-HD-11, patterns of NX-HD-05).
/// Idempotent on (pattern, object, cause): the key is derived from them, and an already-open activity (WRK-ERR-DUPLICATE) counts as raised.
/// The module's own exception record stays the system of record; a missing or failing Work service is logged and never blocks the business
/// flow (the exception record is still there for staff). TODO(hardening): retry a failed raise from a sweeper instead of only logging it.
/// </summary>
internal sealed partial class PsrWorkTasks(IServiceProvider services, ILogger<PsrWorkTasks> logger)
{
    public async Task RaiseAsync(string patternCode, ObjectRef primary, string cause, CancellationToken cancellationToken)
    {
        var work = services.GetService<IWorkActivityService>();
        if (work is null)
        {
            LogUnavailable(logger, patternCode);
            return;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{patternCode}|{primary}|{cause}"));
        var key = IdempotencyKey.From(new Guid(hash.AsSpan(0, 16)));
        try
        {
            await work.CreateAsync(
                new ActivityCreateRequest { PatternCode = patternCode, PrimaryObject = primary, RaisingModule = ModuleCode.POL, CauseRef = cause },
                new CommandOptions(key),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.ToString().EndsWith("DUPLICATE", StringComparison.Ordinal))
        {
            // already open: nothing to add
        }
#pragma warning disable CA1031 // The business flow is already decided; the module's exception record remains for staff.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, patternCode);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Work service is not available; PSR task {Pattern} was not raised.")]
    private static partial void LogUnavailable(ILogger logger, string pattern);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raising the PSR task {Pattern} failed; the module's exception record stands.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string pattern);
}
