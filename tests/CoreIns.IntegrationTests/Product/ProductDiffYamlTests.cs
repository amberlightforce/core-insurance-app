using System.Text.Json.Nodes;
using CoreIns.Modules.Product;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;

namespace CoreIns.IntegrationTests.Product;

/// <summary>BO-PFC-03: the pure item-level diff and the YAML reader and writer (no database).</summary>
public sealed class ProductDiffYamlTests
{
    private static JsonNode V10() => JsonNode.Parse(ProductSeeds.MotorPrivateCarJson())!;

    private static JsonNode V11() => JsonNode.Parse(ProductSeeds.MotorPrivateCar11Json())!;

    private static IReadOnlyList<ProductDiffEntry> Entries(IReadOnlyList<ProductDiffGroup> groups) => [.. groups.SelectMany(g => g.Entries)];

    [Fact]
    public void diff_of_1_0_and_1_1_lists_term_ratio_refund_methods_and_change_permissions()
    {
        var groups = ProductDiff.Compute(V10(), V11());
        var entries = Entries(groups);

        var dayCount = entries.Single(e => e.Path == "/dayCount");
        dayCount.Class.ShouldBe(ProductDiffEntry.ClassValue.Changed);
        dayCount.After.ShouldBe("\"TERM_RATIO\"");
        entries.Single(e => e.Path == "/refundMethods").Class.ShouldBe(ProductDiffEntry.ClassValue.Added);
        entries.Single(e => e.Path == "/changePermissions").Class.ShouldBe(ProductDiffEntry.ClassValue.Added);
        groups.Select(g => g.Area).ShouldContain("refundMethods");
        entries.Select(e => e.Path).ShouldContain("/version");
    }

    [Fact]
    public void diff_works_per_code_for_added_removed_and_changed_items_and_is_empty_for_equal_documents()
    {
        var from = JsonNode.Parse("""{"coverages":[{"code":"A","limit":1},{"code":"B","limit":2}],"tags":["x"]}""")!;
        var to = JsonNode.Parse("""{"coverages":[{"code":"B","limit":3},{"code":"C","limit":4}],"tags":["y"]}""")!;
        var entries = Entries(ProductDiff.Compute(from, to));

        entries.Single(e => e.Path == "/coverages[A]").Class.ShouldBe(ProductDiffEntry.ClassValue.Removed);
        entries.Single(e => e.Path == "/coverages[C]").Class.ShouldBe(ProductDiffEntry.ClassValue.Added);
        var changed = entries.Single(e => e.Path == "/coverages[B]/limit");
        (changed.Class, changed.Code, changed.Before, changed.After).ShouldBe((ProductDiffEntry.ClassValue.Changed, "B", "2", "3"));
        entries.Single(e => e.Path == "/tags").Class.ShouldBe(ProductDiffEntry.ClassValue.Changed);
        ProductDiff.Compute(from, from.DeepClone()).ShouldBeEmpty();
    }

    [Fact]
    public void yaml_round_trip_keeps_the_artefact_hash_for_both_seed_versions()
    {
        foreach (var json in new[] { ProductSeeds.MotorPrivateCarJson(), ProductSeeds.MotorPrivateCar11Json() })
        {
            var original = ArtefactCompiler.Compile(ArtefactCompiler.Parse(json)).Value;
            var yaml = ProductYaml.Write(JsonNode.Parse(original.CanonicalJson)!);
            yaml.ShouldContain("channels:");
            var again = ArtefactCompiler.Compile(ArtefactCompiler.Parse(ProductYaml.Read(yaml)!.ToJsonString())).Value;
            again.Hash.ShouldBe(original.Hash);
            again.CanonicalJson.ShouldBe(original.CanonicalJson);
        }
    }

    [Fact]
    public void yaml_writer_quotes_text_that_would_read_back_as_another_type()
    {
        var tree = JsonNode.Parse("""{"version":"1.0","flag":"true","none":"null","n":7,"d":1.5,"b":false,"date":"2026-01-01","text":"Hello world","empty":"","list":["1","x"]}""")!;
        var back = ProductYaml.Read(ProductYaml.Write(tree))!;
        JsonNode.DeepEquals(back, tree).ShouldBeTrue(back.ToJsonString());
    }

    [Theory]
    [InlineData("a: &x [1]\nb: *x")]
    [InlineData("a: 1\n---\nb: 2")]
    [InlineData("a: [unclosed")]
    public void yaml_reader_refuses_anchors_multiple_documents_and_bad_syntax(string yaml) =>
        Should.Throw<FormatException>(() => ProductYaml.Read(yaml));
}
