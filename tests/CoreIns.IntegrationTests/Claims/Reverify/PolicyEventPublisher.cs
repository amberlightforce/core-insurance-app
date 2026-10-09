using System.Text.Json.Nodes;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Claims.Reverify;

/// <summary>Publishes a POL event through the real outbox, as SL-POL does (the producer of PolicyChanged is SL3-POL-CHANGE, not yet merged).</summary>
internal static class PolicyEventPublisher
{
    public static async Task<EventEnvelope> PublishAsync(IServiceProvider services, EventContract contract, Guid policyId, JsonObject payload, BusinessKeys keys)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("pol-test-producer");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('c', 64));
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var envelope = scope.ServiceProvider.GetRequiredService<IEventPublisher>()
            .Publish(new OutgoingEvent(EventDescriptor.From(contract), "Policy", policyId.ToString(), payload, keys));
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return envelope;
    }
}
