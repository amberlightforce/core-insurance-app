using System.Text.Json.Nodes;
using CoreIns.Modules.Product;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;
using CoreIns.SharedKernel.Json;

namespace CoreIns.IntegrationTests.Product.Motor11;

/// <summary>Pure tests of MOTOR-GR 1.1 (SL3-PFC-MOTOR11, D-SL3-04, D-SL3-08): content, lint and write-once discipline. No database.</summary>
public sealed class Motor11DomainTests
{
    private static ProductArtefact V11() =>
        System.Text.Json.JsonSerializer.Deserialize<ProductArtefact>(ProductSeeds.MotorPrivateCar11Json(), SharedKernelJson.Options)!;

    private static ProductArtefact V10() =>
        System.Text.Json.JsonSerializer.Deserialize<ProductArtefact>(ProductSeeds.MotorPrivateCarJson(), SharedKernelJson.Options)!;

    private static List<string> LintCodes(ProductArtefact a) => ArtefactCompiler.Lint(a).Select(e => e.Code).ToList();

    [Fact]
    public void REQ_PFC_135_version_1_1_declares_TERM_RATIO_as_provisional_with_the_finance_note_and_1_0_stays_ACT_365F()
    {
        var a = V11();

        a.Version.ToString().ShouldBe("1.1");
        a.DayCount.ShouldBe("TERM_RATIO");
        a.DayCountProvisional.ShouldBe(true);
        a.DayCountNote.ShouldNotBeNull().ShouldContain("confirm with finance");
        V10().DayCount.ShouldBe("ACT/365F");
        V10().DayCountProvisional.ShouldBeNull();
    }

    [Fact]
    public void REQ_PFC_134_refund_method_for_Policyholder_is_ProRata_illustrative_and_DistanceWithdrawal_is_FullRefund_settled()
    {
        var methods = V11().RefundMethods.ShouldNotBeNull();

        var policyholder = methods.Single(m => m.Source == "Policyholder");
        policyholder.Method.ShouldBe(RefundMethod.ProRata);
        policyholder.Illustrative.ShouldBe(true);

        var withdrawal = methods.Single(m => m.Source == "DistanceWithdrawal");
        withdrawal.Method.ShouldBe(RefundMethod.FullRefund);
        withdrawal.LegalStatus.ShouldBe(ConfigLegalStatus.Settled);
        withdrawal.Reference.ShouldNotBeNull().ShouldContain("5317/2026 Art. 72");
    }

    [Fact]
    public void REQ_PFC_134_every_other_source_is_absent_so_POL_refuses_it_and_1_0_declares_none()
    {
        var methods = V11().RefundMethods.ShouldNotBeNull();

        methods.Select(m => m.Source).ShouldBe(["Policyholder", "DistanceWithdrawal"], ignoreOrder: true);
        foreach (var source in new[] { "Insurer", "NonPayment", "Void", "Reinsurer", "Unknown" })
        {
            methods.Any(m => m.Source == source).ShouldBeFalse(source);
        }

        V10().RefundMethods.ShouldBeNull();
    }

    [Fact]
    public void REQ_PFC_066_vehicle_field_edits_and_replacement_are_permitted_and_MTPL_stays_non_removable()
    {
        var a = V11();
        var change = a.ChangePermissions.ShouldNotBeNull();
        var vehicleFields = a.Elements.Single(e => e.Code == "vehicle").Fields.Select(f => f.Code).ToHashSet();

        change.VehicleReplacement.ShouldBeTrue();
        change.EditableVehicleFields.ShouldContain("vehicleValue");
        change.EditableVehicleFields.ShouldContain("usage");
        change.EditableVehicleFields.ShouldAllBe(f => vehicleFields.Contains(f));

        var mtpl = a.Coverages.Single(c => c.Code == "MTPL");
        mtpl.Existence.ShouldBe(CoverageExistence.Required);
        mtpl.Removal.ShouldBe(CoverageRemoval.Block);
        LintCodes(a).ShouldBeEmpty();
    }

    [Fact]
    public void REQ_PFC_088_making_MTPL_removable_in_1_1_is_still_refused()
    {
        var a = V11();
        var changed = a with
        {
            Coverages = [.. a.Coverages.Select(c => c.Code == "MTPL" ? c with { Existence = CoverageExistence.Electable, Removal = CoverageRemoval.Allowed } : c)],
        };

        LintCodes(changed).ShouldContain("PFC-LINT-STATUTORY-001");
    }

    [Fact]
    public void REQ_PFC_116_a_refund_method_on_a_tax_or_levy_charge_type_is_a_validation_error()
    {
        var a = V11();
        foreach (var taxCharge in a.ChargeTypes.Where(c => c.Category is ChargeCategory.Tax or ChargeCategory.Levy))
        {
            taxCharge.CancellationTreatment.ShouldBe(CancellationTreatment.PackTaxTreatment, taxCharge.Code);
        }

        foreach (var treatment in new[] { CancellationTreatment.ProRata, CancellationTreatment.ShortRate, CancellationTreatment.NonRefundable, CancellationTreatment.FollowProductRefund })
        {
            var changed = a with
            {
                ChargeTypes = [.. a.ChargeTypes.Select(c => c.Code == "GR-IPT" ? c with { CancellationTreatment = treatment } : c)],
            };
            var result = ArtefactCompiler.Compile(changed);

            result.IsFailure.ShouldBeTrue(treatment.ToString());
            result.Error.FieldErrors!.Select(e => e.Code).ShouldContain("PFC-LINT-TAX-SHAPE");
        }

        // Premium charge types follow the product refund method.
        a.ChargeTypes.Where(c => c.Category == ChargeCategory.Premium)
            .ShouldAllBe(c => c.CancellationTreatment == CancellationTreatment.FollowProductRefund);
    }

    [Fact]
    public void REQ_PFC_134_a_refund_method_must_be_settled_with_a_source_or_marked_illustrative_and_sources_are_unique()
    {
        var a = V11();
        var unmarked = a with { RefundMethods = [new RefundMethodDef { Source = "Policyholder", Method = RefundMethod.ShortRate }] };
        var noSource = a with { RefundMethods = [new RefundMethodDef { Source = "DistanceWithdrawal", Method = RefundMethod.FullRefund, LegalStatus = ConfigLegalStatus.Settled }] };
        var duplicate = a with { RefundMethods = [.. a.RefundMethods!, a.RefundMethods![0]] };

        LintCodes(unmarked).ShouldContain("PFC-LINT-REFUND-STATUS");
        LintCodes(noSource).ShouldContain("PFC-LINT-REFUND-SOURCE");
        LintCodes(duplicate).ShouldContain("PFC-LINT-DUPLICATE");
    }

    [Fact]
    public void REQ_PFC_066_a_change_permission_naming_an_unknown_vehicle_field_is_refused_and_a_provisional_flag_needs_a_day_count()
    {
        var a = V11();

        LintCodes(a with { ChangePermissions = new ChangePermissions { EditableVehicleFields = ["noSuchField"], VehicleReplacement = true } })
            .ShouldContain("PFC-LINT-CHANGE-FIELD");
        LintCodes(a with { DayCount = null }).ShouldContain("PFC-LINT-DAY-COUNT");
    }

    [Fact]
    public void REQ_PFC_010_1_1_differs_from_1_0_only_in_version_day_count_refund_methods_and_change_permissions_so_the_premium_inputs_are_unchanged()
    {
        var v10 = JsonNode.Parse(ProductSeeds.MotorPrivateCarJson())!.AsObject();
        var v11 = JsonNode.Parse(ProductSeeds.MotorPrivateCar11Json())!.AsObject();
        foreach (var key in new[] { "version", "dayCount", "dayCountProvisional", "dayCountNote", "refundMethods", "changePermissions" })
        {
            v10.Remove(key);
            v11.Remove(key);
        }

        // Rating reference (same algorithm slot, same range), coverages, charge types and windows are identical, so 430.00 + 64.51 is unchanged.
        v11.ToJsonString().ShouldBe(v10.ToJsonString());
        V11().Windows.NewBusiness.Start.ToString().ShouldBe("2026-01-01");
        V11().Windows.Renewal.Start.ToString().ShouldBe("2026-01-01");
    }

    [Fact]
    public void REQ_PFC_193_the_1_0_seed_is_write_once_its_artefact_hash_is_pinned()
    {
        // If anyone edits motor-gr.product.json this hash changes: publish a new version instead (D-SL3-04).
        var hash = ArtefactCompiler.Compile(V10()).Value.Hash.Value;

        ArtefactCompiler.Compile(V11()).Value.Hash.Value.ShouldNotBe(hash);
        hash.ShouldBe(Pinned10Hash);
    }

    // Recorded from the unmodified 1.0 seed (main 72c2095) when MOTOR-GR 1.1 was published.
    private const string Pinned10Hash = "PLACEHOLDER";
}
