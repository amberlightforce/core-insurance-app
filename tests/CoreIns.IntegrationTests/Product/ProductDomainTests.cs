using CoreIns.Modules.Product;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;
using CoreIns.SharedKernel.Json;

namespace CoreIns.IntegrationTests.Product;

/// <summary>Pure tests of the product compiler and the question evaluator (no database).</summary>
public sealed class ProductDomainTests
{
    private static ProductArtefact Seed() =>
        System.Text.Json.JsonSerializer.Deserialize<ProductArtefact>(ProductSeeds.MotorPrivateCarJson(), SharedKernelJson.Options)!;

    [Fact]
    public void REQ_PFC_193_194_the_compiled_artefact_is_deterministic_canonical_json_with_decimals_as_strings()
    {
        var first = ArtefactCompiler.Compile(Seed());
        var second = ArtefactCompiler.Compile(Seed());

        first.IsSuccess.ShouldBeTrue(string.Join("; ", first.IsFailure ? first.Error.FieldErrors.Select(e => e.Message) : []));
        second.Value.CanonicalJson.ShouldBe(first.Value.CanonicalJson);
        second.Value.Hash.ShouldBe(first.Value.Hash);

        // Sorted keys, no insignificant whitespace, decimals as fixed strings (never JSON numbers).
        first.Value.CanonicalJson.ShouldContain("\"value\":\"1300000\"");
        first.Value.CanonicalJson.ShouldNotContain(": ");
        var parsed = ArtefactCompiler.Parse(first.Value.CanonicalJson);
        parsed.Product.Code.ShouldBe("MOTOR-GR");
        ArtefactCompiler.Compile(parsed).Value.Hash.ShouldBe(first.Value.Hash);

        // A one-character change gives another hash.
        var changed = Seed() with { Product = Seed().Product with { Name = new LocalizedText { El = "Ιδιωτικό Αυτοκίνητο", En = "Motor Private Car." } } };
        ArtefactCompiler.Compile(changed).Value.Hash.ShouldNotBe(first.Value.Hash);
    }

    [Fact]
    public void The_seed_defines_the_motor_private_car_product_for_GR_TEST()
    {
        var a = Seed();
        a.Product.Name.En.ShouldBe("Motor Private Car");
        a.LegalEntity.ShouldBe("GR-TEST");
        a.Coverages.Select(c => c.Code).ShouldBe(["MTPL", "OWN-DAMAGE", "WINDSCREEN"]);
        a.Elements.Select(e => e.Code).ShouldBe(["policyLine", "vehicle", "driver"]);
        a.Elements.Single(e => e.Code == "policyLine").Fields.Single().Code.ShouldBe("greenCardNumber");
        a.Terms.Allowed.ShouldBe(["P12M"]);

        // No statutory value is held beyond what PRD-02 states: the MTPL options are 1,300,000 (stated) and one marked illustrative.
        var bi = a.Coverages[0].Terms.Single(t => t.Code == "BI_PER_PERSON");
        bi.Options!.Single(o => o.Illustrative != true).Value.ShouldBe(1_300_000m);
        bi.Options!.Where(o => o.Illustrative == true).Select(o => o.Code).ShouldBe(["BI-2000K"]);
    }

    [Theory]
    [InlineData("PRIVATE", "NO", 0, 0, true)]
    [InlineData("BUSINESS", "NO", 1, 0, false)]
    [InlineData("PRIVATE", "YES", 0, 1, true)]
    public void REQ_PFC_006_the_evaluator_reports_referrals_and_knock_outs(string usage, string hire, int referrals, int knockOuts, bool complete)
    {
        var set = Seed().QuestionSets.Single();
        var result = QuestionEvaluator.Evaluate(set, new Dictionary<string, string> { ["Q-USAGE"] = usage, ["Q-HIRE-REWARD"] = hire });

        result.IsSuccess.ShouldBeTrue();
        result.Value.Referrals.Count.ShouldBe(referrals);
        result.Value.KnockOuts.Count.ShouldBe(knockOuts);
        result.Value.Complete.ShouldBe(complete);
    }

    [Fact]
    public void REQ_PFC_061_answers_to_hidden_questions_are_ignored()
    {
        var set = Seed().QuestionSets.Single();
        var result = QuestionEvaluator.Evaluate(set, new Dictionary<string, string> { ["Q-USAGE"] = "PRIVATE", ["Q-HIRE-REWARD"] = "NO", ["Q-BUSINESS-USE"] = "courier" });

        result.Value.Questions.Single(q => q.Question == "Q-BUSINESS-USE").Visible.ShouldBeFalse();
        result.Value.Questions.Single(q => q.Question == "Q-BUSINESS-USE").Answered.ShouldBeFalse();
    }

    [Fact]
    public void REQ_PFC_086_lint_reports_every_finding_not_just_the_first()
    {
        var a = Seed();
        var broken = a with
        {
            Coverages = [a.Coverages[0] with { Existence = CoverageExistence.Electable, Removal = CoverageRemoval.Allowed }, .. a.Coverages.Skip(1)],
            ChargeTypes = [.. a.ChargeTypes.Select(c => c.Code == "GR-IPT" ? c with { WrittenPremium = true } : c)],
        };

        var result = ArtefactCompiler.Compile(broken);

        result.IsFailure.ShouldBeTrue();
        result.Error.FieldErrors.Select(e => e.Code).ShouldContain("PFC-LINT-STATUTORY-001");
        result.Error.FieldErrors.Select(e => e.Code).ShouldContain("PFC-LINT-WRITTEN-PREMIUM");
    }
}
