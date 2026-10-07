using System.Text.Json.Nodes;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Platform.Tests;

/// <summary>The C# envelope guard (contracts/README: rules JSON Schema cannot express; D-CON-26/28; contract D4).</summary>
public sealed class EnvelopeTests
{
    internal static readonly EventDescriptor ChargeDeltaEmitted = new(
        ModuleCode.POL, EventTypeName.Parse("ChargeDeltaEmitted"), "1.0", ["Policy"], DataClassification.P0)
    {
        RequiredBusinessKeys = ["policyId", "chargeId", "policyTermId", "transactionId"],
        SetCompleteness = true,
    };

    internal static readonly EventDescriptor PartyEvent = new(
        ModuleCode.PTY, EventTypeName.Parse("PartiesMerged"), "1.0", ["Party", "Account"], DataClassification.P1)
    {
        RequiredBusinessKeys = ["partyId|accountId"],
    };

    internal static EventEnvelope Valid(EventDescriptor descriptor, long sequence = 1) => new()
    {
        EventId = EventId.New(),
        EventType = descriptor.EventType,
        SchemaVersion = descriptor.SchemaVersion,
        Producer = descriptor.Producer,
        AggregateType = descriptor.AggregateTypes[0],
        AggregateId = Guid.CreateVersion7().ToString(),
        AggregateSequence = sequence,
        OccurredAt = Instant.Parse("2026-10-07T08:00:00Z"),
        RecordedAt = Instant.Parse("2026-10-07T08:00:01Z"),
        LegalEntity = LegalEntityId.Parse("GR-TEST"),
        Jurisdiction = Jurisdiction.Parse("GR"),
        ConfigurationHash = ConfigurationHash.Parse(new string('a', 64)),
        BusinessKeys = BusinessKeys.Empty
            .With("policyId", "p").With("chargeId", "c").With("policyTermId", "t").With("transactionId", "x").With("partyId", "y"),
        CorrelationId = CorrelationId.New(),
        Actor = ActorRef.User("user-1"),
        Origin = EventOrigin.Live,
        DataClassification = descriptor.DataClassification,
        Set = descriptor.SetCompleteness ? new EventSet(Guid.NewGuid(), 3, 2) : null,
        Payload = new JsonObject { ["amount"] = new JsonObject { ["amount"] = "12.50", ["currency"] = "EUR" } },
    };

    [Fact]
    public void A_complete_envelope_is_valid()
    {
        Valid(ChargeDeltaEmitted).Validate(ChargeDeltaEmitted).ShouldBeEmpty();
        Valid(PartyEvent).Validate(PartyEvent).ShouldBeEmpty();
        Valid(PartyEvent).RoutingKey.ShouldBe("pty.PartiesMerged.v1");
        ChargeDeltaEmitted.RegistryName.ShouldBe("pol.ChargeDeltaEmitted");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 3)]
    [InlineData(-1, 3)]
    [InlineData(1, 0)]
    public void Set_index_outside_1_to_set_size_is_rejected_at_construction(int index, int size) =>
        Should.Throw<ArgumentException>(() => new EventSet(Guid.NewGuid(), size, index));

    [Fact]
    public void Set_events_need_their_set_fields() =>
        (Valid(ChargeDeltaEmitted) with { Set = null }).Validate(ChargeDeltaEmitted).ShouldContain(p => p.Contains("contract D4"));

    [Fact]
    public void Required_business_keys_and_alternatives_are_enforced()
    {
        (Valid(ChargeDeltaEmitted) with { BusinessKeys = BusinessKeys.Empty.With("policyId", "p") })
            .Validate(ChargeDeltaEmitted).ShouldContain(p => p.Contains("chargeId"));
        (Valid(PartyEvent) with { BusinessKeys = BusinessKeys.Empty.With("accountId", "a") }).Validate(PartyEvent).ShouldBeEmpty();
        (Valid(PartyEvent) with { BusinessKeys = BusinessKeys.Empty.With("jobId", "j") })
            .Validate(PartyEvent).ShouldContain(p => p.Contains("partyId|accountId"));
        (Valid(PartyEvent) with { BusinessKeys = BusinessKeys.Empty }).Validate().ShouldContain(p => p.Contains("D-CON-28"));
    }

    [Fact]
    public void Pinned_classification_aggregate_and_version_are_enforced()
    {
        (Valid(ChargeDeltaEmitted) with { DataClassification = DataClassification.P1 }).Validate(ChargeDeltaEmitted)
            .ShouldContain(p => p.Contains("dataClassification"));
        (Valid(ChargeDeltaEmitted) with { AggregateType = "Claim" }).Validate(ChargeDeltaEmitted).ShouldContain(p => p.Contains("aggregateType"));
        (Valid(ChargeDeltaEmitted) with { SchemaVersion = "2.0" }).Validate(ChargeDeltaEmitted).ShouldContain(p => p.Contains("schemaVersion"));
        (Valid(ChargeDeltaEmitted) with { SchemaVersion = "1" }).Validate().ShouldContain(p => p.Contains("major.minor"));
    }

    [Fact]
    public void Required_context_fields_are_enforced()
    {
        (Valid(PartyEvent) with { Actor = null! }).Validate().ShouldContain(p => p.Contains("actor"));
        (Valid(PartyEvent) with { LegalEntity = default }).Validate().ShouldContain(p => p.Contains("D-CON-26"));
        (Valid(PartyEvent) with { ConfigurationHash = default }).Validate().ShouldContain(p => p.Contains("configurationHash"));
        (Valid(PartyEvent) with { EventId = EventId.From(Guid.NewGuid()) }).Validate().ShouldContain(p => p.Contains("UUIDv7"));
        (Valid(PartyEvent) with { AggregateSequence = 0 }).Validate().ShouldContain(p => p.Contains("aggregateSequence"));
        (Valid(PartyEvent) with { AggregateSequence = 0 }).Validate(requireSequence: false).ShouldBeEmpty();
        Should.Throw<EnvelopeValidationException>(() => (Valid(PartyEvent) with { AggregateId = string.Empty }).EnsureValid());
    }

    [Fact]
    public void Payloads_over_256_kb_are_rejected()
    {
        var big = Valid(PartyEvent) with { Payload = new JsonObject { ["text"] = new string('x', EventEnvelope.MaxPayloadBytes) } };
        big.Validate().ShouldContain(p => p.Contains("256 KB"));
    }

    [Fact]
    public void Retry_backoff_doubles_from_one_second_up_to_five_minutes()
    {
        var options = new OutboxOptions();
        options.Backoff(1).ShouldBe(TimeSpan.FromSeconds(1));
        options.Backoff(2).ShouldBe(TimeSpan.FromSeconds(2));
        options.Backoff(5).ShouldBe(TimeSpan.FromSeconds(16));
        options.Backoff(9).ShouldBe(TimeSpan.FromSeconds(256));
        options.Backoff(10).ShouldBe(TimeSpan.FromMinutes(5));
        options.Backoff(60).ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Handler_names_are_unique()
    {
        var registration = new EventHandlerRegistration("A.handler", ModuleCode.BIL, "pol.PolicyBound.v1", (_, _, _) => Task.CompletedTask);
        Should.Throw<InvalidOperationException>(() => new EventHandlerRegistry([registration, registration with { RoutingKey = "x" }]));
        new EventHandlerRegistry([registration]).For("pol.PolicyBound.v1").ShouldHaveSingleItem();
    }
}
