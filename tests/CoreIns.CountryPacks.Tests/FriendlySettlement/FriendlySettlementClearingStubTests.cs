using System.Text;
using CoreIns.CountryPacks.CY.Claims;
using CoreIns.CountryPacks.GR.Claims;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.Tests.FriendlySettlement;

/// <summary>Greek Friendly Settlement stub and Cyprus NotApplicable (SL4-MKT-FS, REQ-MKT-109/-313, D-SL4-03).</summary>
public sealed class FriendlySettlementClearingStubTests
{
    private const string Own = "OWN-INSURER";
    private const string Alpha = "ALPHA-INS";

    private static FriendlySettlementClearingStub Stub()
    {
        var options = new FriendlySettlementStubOptions { OwnInsurerCode = Own };
        options.MemberCodes.Add(Own);
        options.MemberCodes.Add(Alpha);
        return new FriendlySettlementClearingStub(options, TimeProvider.System);
    }

    private static FsClaimFacts Facts(
        decimal amount = 4000.00m, string? counterparty = Alpha, bool inGreece = true, int vehicles = 2, decimal fault = 0m, string[]? damage = null) => new()
    {
        LegalEntityId = Guid.CreateVersion7(),
        ClaimId = Guid.CreateVersion7(),
        AccidentDate = new DateOnly(2026, 10, 1),
        ProductLine = "MOTOR",
        CounterpartyInsurerCode = counterparty,
        AccidentInCountry = inGreece,
        VehicleCount = vehicles,
        InsuredFaultPercent = fault,
        DamageTypes = damage ?? ["MATERIAL_DAMAGE"],
        Amount = new SpiMoney(amount, "EUR"),
        JointReport = true,
    };

    [Fact]
    public async Task An_eligible_case_carries_the_value_actual_basis_and_the_unverified_provisional_flags()
    {
        var d = await Stub().EvaluateEligibilityAsync(Facts(), TestContext.Current.CancellationToken);
        d.Result.ShouldBe(FsEligibilityResult.Eligible);
        d.Reasons.ShouldBeEmpty();
        d.ClearingValue.ShouldBe(new SpiMoney(4000.00m, "EUR"));
        d.ClearingValueBasis.ShouldBe(ClearingValueBasis.Actual);
        d.RuleId.ShouldBe(FriendlySettlementClearingStub.RuleId);
        d.LegalStatus.ShouldBe(LegalStatus.Unverified);
        d.Provisional.ShouldBeTrue();
    }

    public static TheoryData<string, FsClaimFacts> Failing => new()
    {
        { "AMOUNT_ABOVE_LIMIT", Facts(amount: 7000.00m) },
        { "VEHICLE_COUNT", Facts(vehicles: 3) },
        { "DAMAGE_TYPE_NOT_MATERIAL_ONLY", Facts(damage: ["MATERIAL_DAMAGE", "BODILY_INJURY"]) },
        { "COUNTERPARTY_NOT_MEMBER", Facts(counterparty: "OUTSIDER") },
        { "NOT_IN_GREECE", Facts(inGreece: false) },
        { "LIABILITY_NOT_CLEAR", Facts(fault: 50m) },
    };

    [Theory]
    [MemberData(nameof(Failing))]
    public async Task Each_failing_reason_is_listed_and_the_result_stays_provisional(string reason, FsClaimFacts facts)
    {
        var d = await Stub().EvaluateEligibilityAsync(facts, TestContext.Current.CancellationToken);
        d.Result.ShouldBe(FsEligibilityResult.NotEligible);
        d.Reasons.ShouldBe([reason]);
        d.ClearingValue.ShouldBeNull();
        d.LegalStatus.ShouldBe(LegalStatus.Unverified);
        d.Provisional.ShouldBeTrue();
    }

    [Fact]
    public async Task Several_failing_reasons_are_all_listed()
    {
        var d = await Stub().EvaluateEligibilityAsync(Facts(amount: 9000m, vehicles: 3, inGreece: false), TestContext.Current.CancellationToken);
        d.Reasons.ShouldBe(["NOT_IN_GREECE", "VEHICLE_COUNT", "AMOUNT_ABOVE_LIMIT"], ignoreOrder: true);
    }

    [Fact]
    public async Task The_limit_5000_00_is_eligible_and_5000_01_is_not()
    {
        var stub = Stub();
        (await stub.EvaluateEligibilityAsync(Facts(5000.00m), TestContext.Current.CancellationToken)).Result.ShouldBe(FsEligibilityResult.Eligible);
        var over = await stub.EvaluateEligibilityAsync(Facts(5000.01m), TestContext.Current.CancellationToken);
        over.Result.ShouldBe(FsEligibilityResult.NotEligible);
        over.Reasons.ShouldBe(["AMOUNT_ABOVE_LIMIT"]);
    }

    [Fact]
    public async Task Submit_is_idempotent_on_the_case_and_returns_a_STUB_FS_reference()
    {
        var stub = Stub();
        var submission = new FsSubmission
        {
            LegalEntityId = Guid.CreateVersion7(), FsCaseId = Guid.CreateVersion7(), ClaimId = Guid.CreateVersion7(),
            CounterpartyInsurerCode = Alpha, AccidentDate = new DateOnly(2026, 10, 1),
            ClearingValue = new SpiMoney(1200.00m, "EUR"), ClearingValueBasis = ClearingValueBasis.Actual,
        };
        var first = await stub.SubmitAsync(submission, TestContext.Current.CancellationToken);
        var second = await stub.SubmitAsync(submission, TestContext.Current.CancellationToken);
        first.ClearingReference.ShouldStartWith("STUB-FS-");
        second.ClearingReference.ShouldBe(first.ClearingReference);
    }

    private static FsNotificationMessage Message(string json) =>
        new(FriendlySettlementClearingStub.NotificationFormat, Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task A_notification_is_parsed_into_the_typed_at_fault_notification()
    {
        var n = await Stub().ReceiveNotificationAsync(Message("""
            {"clearingReference":"STUB-FS-N1","policyNumber":"POL-1","lossDate":"2026-10-02","counterpartyInsurerPartyId":"ALPHA-INS","amount":"3000.00","damageType":"MATERIAL_DAMAGE"}
            """), TestContext.Current.CancellationToken);
        n.ClearingReference.ShouldBe("STUB-FS-N1");
        n.PolicyNumber.ShouldBe("POL-1");
        n.AccidentDate.ShouldBe(new DateOnly(2026, 10, 2));
        n.ClearingValue.ShouldBe(new SpiMoney(3000.00m, "EUR"));
        n.Role.ShouldBe(FsRole.AtFaultInsurer);
        n.DamageTypes.ShouldBe(["MATERIAL_DAMAGE"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"clearingReference":"X"}""")]
    [InlineData("""{"clearingReference":"X","policyNumber":"P","lossDate":"2026-13-45","counterpartyInsurerPartyId":"A","amount":"1.00","damageType":"MATERIAL_DAMAGE"}""")]
    [InlineData("""{"clearingReference":"X","policyNumber":"P","lossDate":"2026-10-02","counterpartyInsurerPartyId":"A","amount":"abc","damageType":"MATERIAL_DAMAGE"}""")]
    [InlineData("""{"clearingReference":"X","policyNumber":"P","lossDate":"2026-10-02","counterpartyInsurerPartyId":"A","amount":"-5","damageType":"MATERIAL_DAMAGE"}""")]
    public async Task A_malformed_notification_is_a_typed_validation_error_not_a_raw_exception(string json)
    {
        var ex = await Should.ThrowAsync<SpiException>(async () => await Stub().ReceiveNotificationAsync(Message(json), TestContext.Current.CancellationToken));
        ex.Error.Category.ShouldBe(SpiErrorCategory.Validation);
        ex.Error.Code.ShouldBe("FS_NOTIFICATION_MALFORMED");
    }

    [Fact]
    public async Task An_unknown_notification_format_is_a_typed_error()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () =>
            await Stub().ReceiveNotificationAsync(new FsNotificationMessage("other", "{}"u8.ToArray()), TestContext.Current.CancellationToken));
        ex.Error.Code.ShouldBe("FS_NOTIFICATION_MALFORMED");
    }

    [Fact]
    public async Task The_statement_is_deterministic_and_nets_per_counterparty_payable_3000_receivable_1200_gives_payable_1800()
    {
        var stub = Stub();
        var ct = TestContext.Current.CancellationToken;
        await stub.SubmitAsync(new FsSubmission
        {
            LegalEntityId = Guid.CreateVersion7(), FsCaseId = Guid.Parse("00000000-0000-0000-0000-000000000111"), ClaimId = Guid.CreateVersion7(),
            CounterpartyInsurerCode = Alpha, AccidentDate = new DateOnly(2026, 10, 1),
            ClearingValue = new SpiMoney(1200.00m, "EUR"), ClearingValueBasis = ClearingValueBasis.Actual,
        }, ct);
        await stub.ReceiveNotificationAsync(Message("""
            {"clearingReference":"STUB-FS-N1","policyNumber":"POL-1","lossDate":"2026-10-02","counterpartyInsurerPartyId":"ALPHA-INS","amount":"3000.00","damageType":"MATERIAL_DAMAGE"}
            """), ct);
        var period = new FsStatementPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1));

        var a = await stub.SettlementStatementAsync(period, cancellationToken: ct);
        var b = await stub.SettlementStatementAsync(period, cancellationToken: ct);

        a.StatementId.ShouldBe(b.StatementId);
        a.Lines.Select(l => l.LineRef).ShouldBe(b.Lines.Select(l => l.LineRef));
        a.Lines.Count.ShouldBe(2);
        a.Lines.Single(l => l.Direction == FsDirection.Receivable).Amount.ShouldBe(new SpiMoney(1200.00m, "EUR"));
        a.Lines.Single(l => l.Direction == FsDirection.Payable).Amount.ShouldBe(new SpiMoney(3000.00m, "EUR"));
        a.Nets.ShouldBe([new FsCounterpartyNet(Alpha, FsDirection.Payable, new SpiMoney(1800.00m, "EUR"))]);

        (await stub.SettlementStatementAsync(period, "OTHER", ct)).Lines.ShouldBeEmpty();
        (await stub.SettlementStatementAsync(new FsStatementPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)), cancellationToken: ct)).Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disputes_are_not_implemented()
    {
        var ex = await Should.ThrowAsync<SpiException>(async () => await Stub().SubmitDisputeAsync(new FsDisputeSubmission("R", "X"), TestContext.Current.CancellationToken));
        ex.Error.Category.ShouldBe(SpiErrorCategory.NotApplicable);
        ex.Error.Code.ShouldBe("FS_DISPUTE_NOT_IMPLEMENTED");
    }

    [Fact]
    public async Task Cyprus_is_NotApplicable_everywhere()
    {
        var cy = new CyFriendlySettlementClearing();
        var ct = TestContext.Current.CancellationToken;
        var d = await cy.EvaluateEligibilityAsync(Facts(), ct);
        d.Result.ShouldBe(FsEligibilityResult.NotApplicable);
        d.RuleId.ShouldNotBeNullOrEmpty();
        (await Should.ThrowAsync<SpiException>(async () => await cy.SettlementStatementAsync(new FsStatementPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)), null, ct)))
            .Error.Category.ShouldBe(SpiErrorCategory.NotApplicable);
        (await Should.ThrowAsync<SpiException>(async () => await cy.ReceiveNotificationAsync(Message("{}"), ct)))
            .Error.Code.ShouldBe(CyFriendlySettlementClearing.NotApplicableCode);
    }
}
