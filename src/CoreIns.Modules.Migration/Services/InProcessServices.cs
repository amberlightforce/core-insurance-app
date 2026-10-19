using System.Text.Json;
using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Contracts;
using CoreIns.Modules.Migration.Contracts.Api;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Services;

/// <summary>In-process <see cref="IMigrationXrefService"/> (D-ARC-16): module importers register links through it.</summary>
internal sealed class MigrationXrefService(RequestContext context, ICommandHandler<RegisterXref, XrefView> handler) : IMigrationXrefService
{
    public async Task<XrefRegisterResponse> RegisterAsync(XrefRegisterRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var body = new XrefRegisterBody(
            Text(request.Source, "source"), Text(request.ObjectType, "objectType"), Text(request.LegacyKey, "legacyKey"),
            request.CoreIds is { Count: > 0 } ids ? ids[0] : throw new DomainException(DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "coreIds is required.")),
            request.Batch is { ValueKind: JsonValueKind.String } batch && Guid.TryParse(batch.GetString(), out var batchId) ? batchId : null);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await handler.HandleAsync(new RegisterXref(body), cancellationToken).ConfigureAwait(false);
            return new XrefRegisterResponse { XrefId = (result.IsSuccess ? result.Value : throw new DomainException(result.Error!)).XrefId };
        }
    }

    private static string Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.String } e && !string.IsNullOrWhiteSpace(e.GetString())
            ? e.GetString()!
            : throw new DomainException(DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"{name} is required."));
}

/// <summary>
/// In-process <see cref="IMigrationRoutingService"/>: the authoritative master of a policy on a date. NONE reads as LegacyMaster
/// (the legacy system still owns it), TRANSITIONING as Transitioning, CORE as CoreMaster. The key is the legacy or core policy key
/// the routing was set with; the date is the valid-at date, today when absent.
/// </summary>
internal sealed class MigrationRoutingService(MigrationReader reader, IClock clock) : IMigrationRoutingService
{
    public async Task<RoutingResolveResponse> ResolveAsync(RoutingResolveRequest request, ValidAt? validAt = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var date = validAt?.Date ?? new BusinessDate(DateOnly.FromDateTime(clock.Now.ToDateTimeOffset().UtcDateTime));
        var result = await reader.ResolveRouteAsync(request.ObjectType, request.Key, date, cancellationToken).ConfigureAwait(false);
        var view = result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
        return new RoutingResolveResponse
        {
            MasterState = view.MasterState switch
            {
                RouteState.Core => RoutingResolveResponse.MasterStateValue.CoreMaster,
                RouteState.Transitioning => RoutingResolveResponse.MasterStateValue.Transitioning,
                _ => RoutingResolveResponse.MasterStateValue.LegacyMaster,
            },
            RouteId = view.RouteId,
            LegacyKey = request.Key,
        };
    }
}
