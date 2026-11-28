using System.Text.Json.Nodes;
using CoreIns.Modules.Product.Domain;

namespace CoreIns.IntegrationTests.Product;

/// <summary>BO-PFC-02: the pure three-way merge of product definitions (no database).</summary>
public sealed class ThreeWayMergeTests
{
    private const string Base = """
        {"version":"1.0","dayCount":"ACT/365F","elements":[{"code":"a","max":1},{"code":"b","max":2}],
         "coverages":[{"code":"MTPL","limit":100}],"chargeTypes":[{"code":"PREMIUM","rank":1}],"tags":["x"]}
        """;

    private static JsonNode Doc(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void changes_on_different_paths_merge_and_the_result_contains_both()
    {
        var ours = Doc(Base);
        ours["elements"]![0]!["max"] = 5;
        var theirs = Doc(Base);
        theirs["dayCount"] = "TERM_RATIO";
        var result = ThreeWayMerge.Merge(Doc(Base), ours, theirs);
        result.HasConflicts.ShouldBeFalse();
        result.Merged["dayCount"]!.GetValue<string>().ShouldBe("TERM_RATIO");
        result.Merged["elements"]![0]!["max"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public void the_same_change_on_both_sides_is_not_a_conflict()
    {
        var ours = Doc(Base);
        ours["dayCount"] = "TERM_RATIO";
        var theirs = Doc(Base);
        theirs["dayCount"] = "TERM_RATIO";
        var result = ThreeWayMerge.Merge(Doc(Base), ours, theirs);
        result.HasConflicts.ShouldBeFalse();
        result.Merged["dayCount"]!.GetValue<string>().ShouldBe("TERM_RATIO");
    }

    [Fact]
    public void different_changes_to_the_same_path_conflict_and_report_the_path_with_all_three_values()
    {
        var ours = Doc(Base);
        ours["dayCount"] = "ACT/360";
        var theirs = Doc(Base);
        theirs["dayCount"] = "TERM_RATIO";
        var result = ThreeWayMerge.Merge(Doc(Base), ours, theirs);
        var conflict = result.Conflicts.ShouldHaveSingleItem();
        conflict.Path.ShouldBe("/dayCount");
        conflict.Base.ShouldBe("\"ACT/365F\"");
        conflict.Ours.ShouldBe("\"ACT/360\"");
        conflict.Theirs.ShouldBe("\"TERM_RATIO\"");
    }

    [Fact]
    public void coded_lists_merge_per_code_for_added_removed_and_modified_items()
    {
        var ours = Doc(Base);
        ((JsonArray)ours["elements"]!).Add(Doc("""{"code":"c","max":3}"""));
        ours["elements"]![0]!["max"] = 9;
        var theirs = Doc(Base);
        ((JsonArray)theirs["elements"]!).RemoveAt(1);
        ((JsonArray)theirs["coverages"]!).Add(Doc("""{"code":"OD","limit":50}"""));
        theirs["chargeTypes"]![0]!["rank"] = 7;

        var result = ThreeWayMerge.Merge(Doc(Base), ours, theirs);
        result.HasConflicts.ShouldBeFalse();
        ((JsonArray)result.Merged["elements"]!).Select(e => (string)e!["code"]!).ToArray().ShouldBe(["a", "c"]);
        result.Merged["elements"]![0]!["max"]!.GetValue<int>().ShouldBe(9);
        ((JsonArray)result.Merged["coverages"]!).Select(e => (string)e!["code"]!).ToArray().ShouldBe(["MTPL", "OD"]);
        result.Merged["chargeTypes"]![0]!["rank"]!.GetValue<int>().ShouldBe(7);
    }

    [Fact]
    public void modify_versus_delete_and_two_different_adds_of_one_code_conflict_per_property()
    {
        var ours = Doc(Base);
        ours["elements"]![1]!["max"] = 8;
        ours["coverages"]![0]!["limit"] = 150;
        ((JsonArray)ours["chargeTypes"]!).Add(Doc("""{"code":"FEE","rank":2}"""));
        var theirs = Doc(Base);
        ((JsonArray)theirs["elements"]!).RemoveAt(1);
        ((JsonArray)theirs["coverages"]!).Clear();
        ((JsonArray)theirs["chargeTypes"]!).Add(Doc("""{"code":"FEE","rank":3}"""));

        var result = ThreeWayMerge.Merge(Doc(Base), ours, theirs);
        result.Conflicts.Select(c => c.Path).Order().ToArray().ShouldBe(["/chargeTypes[FEE]/rank", "/coverages[MTPL]", "/elements[b]"]);
    }

    [Fact]
    public void properties_inside_one_item_merge_while_an_atomic_array_changed_on_both_sides_conflicts()
    {
        var ours = Doc(Base);
        ours["coverages"]![0]!["limit"] = 150;
        ours["tags"] = Doc("""["x","y"]""");
        var theirs = Doc(Base);
        theirs["coverages"]![0]!["name"] = "Motor TPL";
        theirs["tags"] = Doc("""["x","z"]""");
        var result = ThreeWayMerge.Merge(Doc(Base), ours, theirs);
        result.Conflicts.ShouldHaveSingleItem().Path.ShouldBe("/tags");
        result.Merged["coverages"]![0]!["limit"]!.GetValue<int>().ShouldBe(150);
        result.Merged["coverages"]![0]!["name"]!.GetValue<string>().ShouldBe("Motor TPL");
    }

    [Fact]
    public void ours_wins_paths_keep_the_draft_version_without_a_conflict()
    {
        var ours = Doc(Base);
        ours["version"] = "1.2";
        var theirs = Doc(Base);
        theirs["version"] = "1.1";
        var result = ThreeWayMerge.Merge(Doc(Base), ours, theirs, ["/version"]);
        result.HasConflicts.ShouldBeFalse();
        result.Merged["version"]!.GetValue<string>().ShouldBe("1.2");
    }
}
