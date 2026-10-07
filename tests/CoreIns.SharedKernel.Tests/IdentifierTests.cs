using System.Text.Json;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.SharedKernel.Tests;

public sealed class IdentifierTests
{
    [Fact]
    public void Entity_ids_are_uuid_v7_and_typed()
    {
        var policy = PolicyId.New();
        var claim = ClaimId.New();

        policy.Value.Version.ShouldBe(7);
        claim.Value.ShouldNotBe(policy.Value);
        Should.Throw<ArgumentException>(() => PolicyId.From(Guid.Empty));
        EntityIds.Parse<PolicyId>(policy.ToString()).ShouldBe(policy);
        Should.Throw<FormatException>(() => EntityIds.Parse<PolicyId>("not-a-guid"));
    }

    [Fact]
    public void Entity_ids_serialise_as_uuid_strings_and_dictionary_keys()
    {
        var id = InvoiceId.New();
        var json = JsonSerializer.Serialize(new Dictionary<InvoiceId, int> { [id] = 1 }, SharedKernelJson.Options);

        JsonSerializer.Serialize(id, SharedKernelJson.Options).ShouldBe($"\"{id.Value:D}\"");
        json.ShouldBe($"{{\"{id.Value:D}\":1}}");
        JsonSerializer.Deserialize<Dictionary<InvoiceId, int>>(json, SharedKernelJson.Options)!.ShouldContainKey(id);
    }

    [Theory]
    [InlineData("BIL_NONPAY_NOTICE", true)]
    [InlineData("UW_NATCAT_RESPONSE", true)]
    [InlineData("bil_nonpay", false)]
    [InlineData("BILLING_NOTICE", false)]
    [InlineData("BIL_NONPAY_NOTICE\n", false)]
    public void Clock_codes_follow_the_contract(string code, bool valid) => ClockCode.TryParse(code, out _).ShouldBe(valid);

    [Theory]
    [InlineData("pol.Job.bind", true)]
    [InlineData("mkt.Configuration.resolve", true)]
    [InlineData("pol.job.bind", false)]
    [InlineData("POL.Job.bind", false)]
    [InlineData("pol.Job", false)]
    public void Operation_names_follow_the_contract(string name, bool valid) => OperationName.TryParse(name, out _).ShouldBe(valid);

    [Theory]
    [InlineData("GR-TEST", true)]
    [InlineData("GR01", true)]
    [InlineData("gr-test", false)]
    [InlineData("", false)]
    public void Legal_entity_codes_are_upper_case(string code, bool valid) => LegalEntityId.TryParse(code, out _).ShouldBe(valid);

    [Fact]
    public void Business_numbers_are_opaque_but_never_blank_or_padded()
    {
        PolicyNumber.Parse("ΑΣ-2026/000123").Value.ShouldBe("ΑΣ-2026/000123");
        PolicyNumber.TryParse(" 123", out _).ShouldBeFalse();
        PolicyNumber.TryParse(new string('9', 65), out _).ShouldBeFalse();
        JsonSerializer.Serialize(ClaimNumber.Parse("CL-1"), SharedKernelJson.Options).ShouldBe("\"CL-1\"");
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Jurisdiction>("\"Greece\"", SharedKernelJson.Options));
    }

    [Fact]
    public void Product_versions_compare_numerically()
    {
        (ProductVersionNumber.Parse("1.10") > ProductVersionNumber.Parse("1.9")).ShouldBeTrue();
        ProductVersionNumber.TryParse("01.2", out _).ShouldBeFalse();
        ProductVersionNumber.TryParse("1.2.3", out _).ShouldBeFalse();
        new TermNumber(1).Next().ShouldBe(new TermNumber(2));
        Should.Throw<ArgumentOutOfRangeException>(() => new TermNumber(0));
    }

    [Fact]
    public void Idempotency_keys_and_trace_ids_are_validated()
    {
        IdempotencyKey.TryParse("3f2c3a0e-8a7b-4c55-9f0e-1d2b3c4d5e6f", out var key).ShouldBeTrue();
        key.ToString().ShouldBe("3f2c3a0e-8a7b-4c55-9f0e-1d2b3c4d5e6f");
        IdempotencyKey.TryParse("3F2C3A0E8A7B4C559F0E1D2B3C4D5E6F", out _).ShouldBeFalse();
        IdempotencyKey.TryParse("00000000-0000-0000-0000-000000000000", out _).ShouldBeFalse();

        CorrelationId.TryParse("4BF92F3577B34DA6A3CE929D0E0E4736", out var trace).ShouldBeTrue();
        trace.Value.ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        CorrelationId.TryParse(new string('0', 32), out _).ShouldBeFalse();
        CorrelationId.New().Value.Length.ShouldBe(32);
    }

    [Fact]
    public void Business_keys_are_validated_ordered_and_merged_without_conflicts()
    {
        var policy = PolicyId.New();
        var keys = BusinessKeys.Empty.With(BusinessKeyNames.QuoteId, "q-1").With(BusinessKeyNames.PolicyId, policy);

        keys.Keys.ShouldBe(["policyId", "quoteId"]);
        Should.Throw<ArgumentException>(() => keys.With("Policy-Id", "x"));
        Should.Throw<ArgumentException>(() => keys.With("jobId", string.Empty));
        Should.Throw<ArgumentException>(() => keys.Merge(BusinessKeys.Empty.With("quoteId", "q-2")));
        keys.Merge(BusinessKeys.Empty.With("quoteId", "q-1").With("jobId", "j-1")).Count.ShouldBe(3);

        var json = JsonSerializer.Serialize(keys, SharedKernelJson.Options);
        JsonSerializer.Deserialize<BusinessKeys>(json, SharedKernelJson.Options).ShouldBe(keys);
    }

    [Fact]
    public void Hashes_are_lower_case_hex()
    {
        Sha256Hash.ComputeUtf8("abc").Value.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        ConfigurationHash.Parse("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD").ToString()
            .ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        ResolutionHash.TryParse("abc", out _).ShouldBeFalse();
    }

    [Fact]
    public void Error_codes_follow_the_contract()
    {
        var code = ErrorCode.For(ModuleCode.POL, "IDEMPOTENCY-MISMATCH");

        code.Value.ShouldBe("POL-ERR-IDEMPOTENCY-MISMATCH");
        code.Module.ShouldBe(ModuleCode.POL);
        code.Name.ShouldBe("IDEMPOTENCY-MISMATCH");
        ErrorCode.TryParse("XX-ERR-001", out _).ShouldBeFalse();
        ErrorCode.TryParse("POL-001", out _).ShouldBeFalse();
        ErrorCode.TryParse("UW-ERR-042", out _).ShouldBeTrue();
    }

    [Fact]
    public void Results_carry_a_value_or_an_error()
    {
        Result<int> ok = 5;
        Result<int> failed = DomainError.Of(ModuleCode.PLT, "NOT-FOUND");

        ok.Map(v => v * 2).Value.ShouldBe(10);
        failed.Map(v => v * 2).IsFailure.ShouldBeTrue();
        ok.Bind(v => Result.Failure<int>(DomainError.Of(ModuleCode.PLT, "X"))).IsFailure.ShouldBeTrue();
        failed.Match(v => "ok", e => e.Code.Value).ShouldBe("PLT-ERR-NOT-FOUND");
        Should.Throw<InvalidOperationException>(() => failed.Value);
    }
}

public sealed class TextAndBankingTests
{
    [Theory]
    [InlineData("GR16 0110 1250 0000 0001 2300 695")]
    [InlineData("gr1601101250000000012300695")]
    [InlineData("DE89 3704 0044 0532 0130 00")]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    [InlineData("CY17 0020 0128 0000 0012 0052 7600")]
    public void Valid_ibans_are_accepted_and_normalised(string text)
    {
        Iban.TryParse(text, out var iban).ShouldBeTrue();
        iban.Value.ShouldNotContain(" ");
        iban.Value.ShouldBe(iban.Value.ToUpperInvariant());
    }

    [Theory]
    [InlineData("GR17 0110 1250 0000 0001 2300 695")]
    [InlineData("GR16 0110 1250 0000 0001 2300 69")]
    [InlineData("XX16 0110 1250 0000 0001 2300 695")]
    [InlineData("GR16-0110-1250-0000-0001-2300-695")]
    [InlineData("")]
    public void Invalid_ibans_are_rejected(string text) => Iban.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Ibans_and_tax_ids_are_masked_in_text()
    {
        var iban = Iban.Parse("GR1601101250000000012300695");
        iban.ToString().ShouldBe("GR16…0695");
        iban.ToPrintFormat().ShouldBe("GR16 0110 1250 0000 0001 2300 695");
        new TaxIdentifier(Jurisdiction.Parse("GR"), " 094014201 ").ToString().ShouldBe("GR:…01");
    }

    [Fact]
    public void Localized_text_needs_both_languages()
    {
        var text = new LocalizedText("Ασφάλιστρο", "Premium");

        text.In(Language.El).ShouldBe("Ασφάλιστρο");
        text.In(Language.En).ShouldBe("Premium");
        Should.Throw<ArgumentException>(() => new LocalizedText("Ασφάλιστρο", " "));
        Languages.TryParse("el-GR", out var el).ShouldBeTrue();
        el.ShouldBe(Language.El);
        Languages.TryParse("de", out _).ShouldBeFalse();
        JsonSerializer.Deserialize<LocalizedText>(JsonSerializer.Serialize(text, SharedKernelJson.Options), SharedKernelJson.Options).ShouldBe(text);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<LocalizedText>("{\"el\":\"Α\"}", SharedKernelJson.Options));
    }
}
