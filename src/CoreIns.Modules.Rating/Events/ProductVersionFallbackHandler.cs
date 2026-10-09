using CoreIns.Modules.Product.Contracts.Events;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Rating.Events;

/// <summary>Activates the exact source tariff for a server-published fallback; the outbox owns event-id idempotency.</summary>
internal sealed partial class ProductVersionFallbackHandler(
    RatingStore store, IAuditWriter audit, RequestContext context, IClock clock, ILogger<ProductVersionFallbackHandler> logger)
    : IEventHandler<ProductVersionPublishedV1>
{
    public async Task HandleAsync(EventEnvelope envelope, ProductVersionPublishedV1 payload, CancellationToken cancellationToken)
    {
        if (payload.FallbackOf is not { } source)
        {
            return;
        }

        await store.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        var at = payload.NewBusinessWindow.Start.ToBusinessDate(TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens"));
        var result = await store.ActivateFallbackAsync(
            payload.ProductCode, source.ToString(), payload.Version.ToString(), at.Value, envelope.EventId.Value, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is { } refusal)
        {
            LogRefused(logger, payload.ProductCode, payload.Version.ToString(), refusal);
        }

        audit.Append(new AuditRecord
        {
            Actor = context.Actor, OnBehalfOf = context.OnBehalfOf, Roles = [.. context.Roles],
            Operation = OperationName.Parse("rat.RateActivation.activateFallback"),
            ObjectRef = new ObjectRef(ModuleCode.RAT, "RateActivation", payload.ProductCode + "/" + payload.Version),
            Outcome = result.Refusal is null ? AuditOutcome.Succeeded : AuditOutcome.Rejected,
            ErrorCode = result.Refusal is null ? null : "RAT-ERR-NO-ACTIVE-ARTEFACT",
            Reason = result.Refusal ?? "Activate the fallback product under its source's exact tariff hash.",
            Changes = AuditDiff.Compute(null, new
            {
                sourceVersion = source.ToString(), productVersion = payload.Version.ToString(),
                sourceEventId = envelope.EventId.Value, result.ArtefactHash, result.Created,
            }),
            LegalEntity = envelope.LegalEntity, Jurisdiction = envelope.Jurisdiction,
            CorrelationId = envelope.CorrelationId, CausationId = envelope.EventId.Value,
            Origin = envelope.Origin, BusinessKeys = envelope.BusinessKeys, OccurredAt = clock.Now,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "RAT fallback {ProductCode} {Version} refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string productCode, string version, string reason);
}
