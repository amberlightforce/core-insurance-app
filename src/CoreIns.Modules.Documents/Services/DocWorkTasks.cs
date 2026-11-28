using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Work.Contracts;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Documents.Services;

/// <summary>
/// Raises the delivery-conflict review task in the DOC-OPS queue through <c>wrk.Activity.create</c> (pattern <c>WRK-DOC-DELIVERY-CONFLICT</c>, BO-WRK-04).
/// Idempotent per delivery: the key is derived from pattern, document and delivery, and an already-open activity (a duplicate) counts as raised.
/// The conflicting provider event stays in the evidence; a missing or failing Work service is logged and never blocks the callback.
/// TODO(hardening): retry a failed raise from a sweeper instead of only logging it.
/// </summary>
internal sealed partial class DocWorkTasks(IServiceProvider services, ILogger<DocWorkTasks> logger)
{
    public const string DeliveryConflictPattern = "WRK-DOC-DELIVERY-CONFLICT";

    public async Task RaiseDeliveryConflictAsync(DocumentId documentId, DeliveryId deliveryId, CancellationToken cancellationToken)
    {
        var work = services.GetService<IWorkActivityService>();
        if (work is null)
        {
            LogUnavailable(logger, DeliveryConflictPattern);
            return;
        }

        var primary = new ObjectRef(ModuleCode.DOC, "Document", documentId.Value.ToString("D"));
        var cause = $"delivery:{deliveryId.Value:D}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{DeliveryConflictPattern}|{primary}|{cause}"));
        try
        {
            await work.CreateAsync(
                new ActivityCreateRequest { PatternCode = DeliveryConflictPattern, PrimaryObject = primary, RaisingModule = ModuleCode.DOC, CauseRef = cause },
                new CommandOptions(IdempotencyKey.From(new Guid(hash.AsSpan(0, 16)))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.ToString().EndsWith("DUPLICATE", StringComparison.Ordinal))
        {
            // already open: nothing to add
        }
#pragma warning disable CA1031 // The callback is already recorded as evidence; the flagged conflict remains for staff.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, DeliveryConflictPattern);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Work service is not available; task {Pattern} was not raised.")]
    private static partial void LogUnavailable(ILogger logger, string pattern);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raising the task {Pattern} failed; the conflicting event stays in the delivery evidence.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string pattern);
}
