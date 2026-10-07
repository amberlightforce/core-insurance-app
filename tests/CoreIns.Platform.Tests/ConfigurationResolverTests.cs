using System.Text.Json.Nodes;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using FsCheck.Xunit;

namespace CoreIns.Platform.Tests;

/// <summary>Configuration resolve shape (REQ-MKT-001, 031/032, 041, 046) on the in-memory resolver.</summary>
public sealed class ConfigurationResolverTests
{
    private static readonly Instant T0 = Instant.Parse("2026-01-01T00:00:00Z");
    private static readonly ConfigKey DownPayment = ConfigKey.Parse("bil.down_payment.percent");
    private static readonly LegalEntityCode Entity = LegalEntityCode.Parse("GR-TEST");
    private static readonly Jurisdiction Greece = Jurisdiction.Parse("GR");
    private static readonly string[] Regions = ["a", "b", "c", "d", "e"];

    private static ConfigValueVersion Value(LayerNode node, string value, bool final = false, string? id = null, Instant? validTo = null) =>
        new(DownPayment, node, JsonValue.Create(value), final, new InstantRange(T0, validTo), InstantRange.Open(T0), id ?? node.Name);

    private static InMemoryConfigurationResolver Resolver(params ConfigValueVersion[] versions)
    {
        var resolver = new InMemoryConfigurationResolver(new ManualClock(T0.Plus(TimeSpan.FromDays(30))));
        foreach (var version in versions)
        {
            resolver.Add(version);
        }

        return resolver;
    }

    [Fact]
    public async Task Product_and_channel_beat_product_which_beats_channel()
    {
        // REQ-MKT-032 example: 20 at product, 10 at channel BANK_BRANCH, 15 at product+channel.
        var motor = ProductCode.Parse("MOTOR");
        var resolver = Resolver(
            Value(LayerNode.Core, "25"),
            Value(LayerNode.Product(motor), "20"),
            Value(LayerNode.Channel("BANK_BRANCH"), "10"),
            Value(LayerNode.ProductAndChannel(motor, "BANK_BRANCH"), "15"));
        var context = new ResolutionContext(Entity, Greece) { Product = motor, Channel = "BANK_BRANCH" };
        var ct = TestContext.Current.CancellationToken;

        var motorBank = await resolver.ResolveAsync([DownPayment], context, T0, null, ct);
        var homeBank = await resolver.ResolveAsync([DownPayment], context with { Product = ProductCode.Parse("HOME") }, T0, null, ct);

        motorBank.Values[DownPayment].Value!.GetValue<string>().ShouldBe("15");
        motorBank.Values[DownPayment].Layer.ShouldBe(ConfigLayer.ProductChannel);
        homeBank.Values[DownPayment].Value!.GetValue<string>().ShouldBe("10");
        homeBank.Values[DownPayment].Source.Name.ShouldBe("channel:BANK_BRANCH");
    }

    [Fact]
    public async Task A_final_value_blocks_more_specific_nodes()
    {
        var resolver = Resolver(
            Value(LayerNode.Country(Greece), "30", final: true),
            Value(LayerNode.Entity(Entity), "40"));

        var result = await resolver.ResolveAsync([DownPayment], new ResolutionContext(Entity, Greece), T0, null, TestContext.Current.CancellationToken);

        result.Values[DownPayment].Value!.GetValue<string>().ShouldBe("30");
        result.Values[DownPayment].Final.ShouldBeTrue();
        result.Values[DownPayment].Layer.ShouldBe(ConfigLayer.Country);
    }

    [Fact]
    public async Task Missing_keys_are_reported_never_defaulted()
    {
        var result = await Resolver().ResolveAsync([DownPayment], new ResolutionContext(Entity, Greece), T0, null, TestContext.Current.CancellationToken);

        result.Values.ShouldBeEmpty();
        result.Missing.ShouldBe([DownPayment]);
    }

    [Fact]
    public async Task Resolution_is_time_travel_aware()
    {
        var later = T0.Plus(TimeSpan.FromDays(10));
        var resolver = Resolver(Value(LayerNode.Core, "25", validTo: later));
        resolver.Add(new ConfigValueVersion(DownPayment, LayerNode.Core, JsonValue.Create("35"), false, InstantRange.Open(later), InstantRange.Open(later), "v2"));
        var context = new ResolutionContext(Entity, Greece);
        var ct = TestContext.Current.CancellationToken;

        (await resolver.ResolveAsync([DownPayment], context, later, null, ct)).Values[DownPayment].VersionId.ShouldBe("v2");
        (await resolver.ResolveAsync([DownPayment], context, T0, null, ct)).Values[DownPayment].VersionId.ShouldBe("core");
        (await resolver.ResolveAsync([DownPayment], context, later, T0, ct)).Missing.ShouldBe([DownPayment]);
    }

    [Property]
    public bool The_hash_does_not_depend_on_insertion_order(int seed)
    {
        var versions = Regions.Select(id => Value(LayerNode.Region(id), id, id: id)).ToArray();
        var shuffled = versions.ToArray();
#pragma warning disable CA5394 // Test shuffling, not security.
        new Random(seed).Shuffle(shuffled);
#pragma warning restore CA5394
        return InMemoryConfigurationResolver.Hash(versions) == InMemoryConfigurationResolver.Hash(shuffled);
    }

    [Fact]
    public void The_hash_changes_when_one_valid_to_changes()
    {
        var open = InMemoryConfigurationResolver.Hash([Value(LayerNode.Core, "25")]);
        var closed = InMemoryConfigurationResolver.Hash([Value(LayerNode.Core, "25", validTo: T0.Plus(TimeSpan.FromDays(1)))]);

        open.ShouldNotBe(closed);
        open.ToString().Length.ShouldBe(64);
    }
}
