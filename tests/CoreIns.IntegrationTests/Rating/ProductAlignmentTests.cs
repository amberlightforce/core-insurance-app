using System.Text.Json.Nodes;
using CoreIns.Modules.Product;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Modules.Underwriting.Domain;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>
/// The rating artefact and the UW rule sets are bound to what the PFC product MOTOR-GR 1.0 declares: product code and version,
/// the rating slot, the UW rule-set codes, the coverages, the premium charge types and the MKT keys its charge types name.
/// </summary>
public sealed class ProductAlignmentTests
{
    private static readonly JsonNode Product = JsonNode.Parse(ProductSeeds.MotorPrivateCarJson())!;

    [Fact]
    public void The_rating_artefact_is_bound_to_the_product_version_and_its_rating_slot()
    {
        var definition = BuiltInArtefacts.Definition(BuiltInArtefacts.DefaultProductCode, BuiltInArtefacts.DefaultProductVersion, new Dictionary<string, string>());

        definition.ProductCode.ShouldBe(Product["product"]!["code"]!.GetValue<string>());
        definition.ProductVersion.ShouldBe(Product["version"]!.GetValue<string>());
        definition.Code.ShouldBe(Product["references"]!["rating"]!["algorithmCode"]!.GetValue<string>());
        definition.Label.ShouldStartWith("1."); // satisfies the slot's compatibleArtefactRange ^1
        Product["references"]!["rating"]!["compatibleArtefactRange"]!.GetValue<string>().ShouldBe("^1");
        definition.AnnualTermsOnly.ShouldBeTrue();
        Product["terms"]!["allowed"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["P12M"]);
    }

    [Fact]
    public void Every_rated_coverage_and_premium_charge_type_is_one_the_product_declares()
    {
        var definition = BuiltInArtefacts.Definition(BuiltInArtefacts.DefaultProductCode, BuiltInArtefacts.DefaultProductVersion, new Dictionary<string, string>());
        var coverages = Product["coverages"]!.AsArray().Select(c => c!["code"]!.GetValue<string>()).ToList();
        var premiumTypes = Product["chargeTypes"]!.AsArray()
            .Where(c => c!["computedBy"]?.GetValue<string>() == "RATING")
            .ToDictionary(c => c!["coverage"]!.GetValue<string>(), c => c!["code"]!.GetValue<string>());

        definition.ChargeTypes.ShouldBe(premiumTypes, ignoreOrder: true);
        coverages.ShouldBe(definition.ChargeTypes.Keys, ignoreOrder: true);
        var taxTypes = Product["chargeTypes"]!.AsArray().Where(c => c!["computedBy"]?.GetValue<string>() == "TAX_CALCULATOR").Select(c => c!["code"]!.GetValue<string>());
        definition.TaxPlan.Select(p => p.ChargeType).ShouldAllBe(code => taxTypes.Contains(code));
    }

    [Fact]
    public void The_underwriting_rule_sets_carry_the_codes_the_product_references()
    {
        var uw = Product["references"]!["uwRuleSets"]!;

        BuiltInRuleSets.Quote("MOTOR-GR").Code.ShouldBe(uw["PRE_QUOTE"]!["code"]!.GetValue<string>());
        BuiltInRuleSets.Bind("MOTOR-GR").Code.ShouldBe(uw["PRE_BIND"]!["code"]!.GetValue<string>());
        BuiltInRuleSets.Bind("MOTOR-GR").Version.ShouldStartWith("1."); // ^1
        uw["PRE_BIND"]!["range"]!.GetValue<string>().ShouldBe("^1");
    }

    [Fact]
    public void The_risk_tree_fields_pricing_reads_are_fields_the_product_declares()
    {
        var vehicle = Product["elements"]!.AsArray().Single(e => e!["code"]!.GetValue<string>() == "vehicle")!["fields"]!.AsArray().Select(f => f!["code"]!.GetValue<string>()).ToList();
        var driver = Product["elements"]!.AsArray().Single(e => e!["code"]!.GetValue<string>() == "driver")!["fields"]!.AsArray().Select(f => f!["code"]!.GetValue<string>()).ToList();

        vehicle.ShouldContain("firstRegistrationYear");
        vehicle.ShouldContain("engineCapacityCc");
        vehicle.ShouldContain("vehicleValue");
        vehicle.ShouldContain("usage");
        driver.ShouldContain("dateOfBirth");
        driver.ShouldContain("claimsLast5Years");
    }
}
