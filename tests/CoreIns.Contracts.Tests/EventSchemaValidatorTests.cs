using System.Text.Json.Nodes;
using CoreIns.Testing.Contracts;

namespace CoreIns.Contracts.Tests;

/// <summary>NX-TST-05: the schema validator behind the integration-test drift check accepts real shapes and catches drift.</summary>
public sealed class EventSchemaValidatorTests
{
    private static readonly EventSchemaValidator Validator = EventSchemaValidator.ForRepository;

    private static JsonObject ClaimClosed() => JsonNode.Parse("""
        {
          "eventId": "01900000-0000-7000-8000-000000000001", "eventType": "ClaimClosed", "schemaVersion": "1.0", "producer": "CLM",
          "aggregateType": "Claim", "aggregateId": "c-1", "aggregateSequence": 4,
          "occurredAt": "2026-10-07T09:00:00Z", "recordedAt": "2026-10-07T09:00:00Z",
          "legalEntity": "GR-TEST", "jurisdiction": "GR", "configurationHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "businessKeys": { "claimId": "c-1" }, "correlationId": "0123456789abcdef0123456789abcdef", "causationId": null,
          "actor": { "kind": "SERVICE", "id": "test" }, "aiInteractionId": null, "origin": "LIVE", "dataClassification": "P0",
          "payload": { "outcome": "COMPLETED", "totals": [] }
        }
        """)!.AsObject();

    [Fact]
    public void Finds_every_catalogued_event_schema() => Validator.SchemaCount.ShouldBe(304);

    [Fact]
    public void A_conforming_event_is_valid() => Validator.Validate(ClaimClosed()).ShouldBeEmpty();

    [Fact]
    public void An_unknown_payload_member_is_reported()
    {
        var wire = ClaimClosed();
        wire["payload"]!["surprise"] = 1;
        Validator.Validate(wire).Select(v => v.Key).ShouldBe(["$.payload: additionalProperties surprise"]);
    }

    [Fact]
    public void A_missing_required_and_a_mistyped_member_are_reported()
    {
        var wire = ClaimClosed();
        wire["payload"]!.AsObject().Remove("outcome");
        wire["payload"]!["totals"] = "none";
        Validator.Validate(wire).Select(v => v.Key).ShouldBe(["$.payload: required outcome", "$.payload.totals: type array"], ignoreOrder: true);
    }

    [Fact]
    public void An_envelope_violation_and_a_pinned_value_are_reported()
    {
        var wire = ClaimClosed();
        wire["jurisdiction"] = "gr";
        wire["producer"] = "POL";
        var keys = Validator.Validate(wire).Select(v => v.Key).ToList();
        keys.ShouldContain("$.jurisdiction: pattern");
        keys.ShouldContain("$.producer: const");
    }

    [Fact]
    public void An_event_without_a_schema_is_reported()
    {
        var wire = ClaimClosed();
        wire["eventType"] = "NoSuchThing";
        Validator.Validate(wire).Select(v => v.Key).ShouldBe(["$: no event schema"]);
    }
}
