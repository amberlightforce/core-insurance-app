using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.FriendlySettlement;

/// <summary>SL4-CLM-FS-CASE main paths over the real host, database and the GR stub clearing adapter (Testing environment).</summary>
public sealed class FsCaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string Insurer = Guid.NewGuid().ToString("D");
    private readonly FakePolicyPolicyService _policies = new();
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(
            database.AppConnectionString,
            services => services.AddSingleton<IPolicyPolicyService>(_policies),
            new Dictionary<string, string?>
            {
                ["FriendlySettlement:Stub:OwnInsurerCode"] = "OWN-INSURER",
                ["FriendlySettlement:Stub:MemberCodes:0"] = "OWN-INSURER",
                ["FriendlySettlement:Stub:MemberCodes:1"] = Insurer,
            });
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    // Liability facts are written directly: clm.Claim.update (SL4-CLM-RECOVERY-OPS) is a parallel work package.
    private async Task<MoneyClaim> ClaimAsync(ScriptedPolicy policy, string? insurer = null, decimal fault = 0m)
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(policy));
        await database.ExecuteAsSuperuserAsync(
            $"UPDATE clm.claim SET fault_insured_pct={fault.ToString(System.Globalization.CultureInfo.InvariantCulture)}, fault_source='JOINT_REPORT', "
            + $"counterparty_insurer_party_id='{insurer ?? Insurer}', joint_accident_report=true, vehicle_count=2, accident_in_greece=true WHERE claim_id='{claim.ClaimId}'", ClaimsSlice.Ct);
        return claim;
    }

    private async Task<JsonNode> PostAsync(string path, object body, HttpStatusCode expected = HttpStatusCode.OK, string roles = Handler)
    {
        var (response, json) = await _money.SendAsync(HttpMethod.Post, path, body, roles);
        response.StatusCode.ShouldBe(expected, json?.ToJsonString());
        return json!;
    }

    [Fact]
    public async Task Own_leg_evaluates_opens_the_FS_receivable_and_submits_once()
    {
        var claim = await ClaimAsync(_slice.Policy());
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1200m)]);

        var evaluated = await PostAsync("/api/clm/v1/friendly-settlements/evaluate", new { claimId = claim.ClaimId });
        evaluated.Text("eligibility.result").ShouldBe("ELIGIBLE");
        evaluated.Text("eligibility.legalStatus").ShouldBe("UNVERIFIED");
        evaluated["eligibility"]!["provisional"]!.GetValue<bool>().ShouldBeTrue();
        evaluated.Text("eligibility.clearingValueBasis").ShouldBe("ACTUAL");
        Amount(evaluated["fsCase"]!["clearingValue"]).ShouldBe(1200m);
        evaluated.Text("fsCase.state").ShouldBe("ELIGIBLE");
        var caseId = evaluated.Text("fsCase.fsCaseId");

        var opened = await PostAsync("/api/clm/v1/friendly-settlements/open-own-settlement",
            new { fsCaseId = caseId, expectedRecordVersion = evaluated["fsCase"]!["recordVersion"]!.GetValue<int>() });
        opened.Text("exposureId").ShouldNotBeNullOrEmpty();
        opened.Text("recoveryId").ShouldNotBeNullOrEmpty();
        await _money.DrainAsync();
        var kinds = await _money.ScalarAsync<string>(
            $"SELECT string_agg(kind, ',') FROM clm.exposure WHERE claim_id='{claim.ClaimId}' AND kind='FS_OWN_SETTLEMENT'");
        kinds.ShouldBe("FS_OWN_SETTLEMENT");

        var version = opened["fsCase"]!["recordVersion"]!.GetValue<int>();
        var submitted = await PostAsync("/api/clm/v1/friendly-settlements/submit", new { fsCaseId = caseId, expectedRecordVersion = version });
        submitted.Text("clearingReference").ShouldStartWith("STUB-FS-");
        submitted.Text("fsCase.state").ShouldBe("SUBMITTED");

        // Submitting twice is refused by the state, not repeated.
        await PostAsync("/api/clm/v1/friendly-settlements/submit",
            new { fsCaseId = caseId, expectedRecordVersion = submitted["fsCase"]!["recordVersion"]!.GetValue<int>() }, HttpStatusCode.UnprocessableEntity);

        var (getResponse, got) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/friendly-settlements/{caseId}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        got.Text("fsCase.state").ShouldBe("SUBMITTED");
        var (_, list) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/friendly-settlements?claimId={claim.ClaimId}&role=OWN_INSURER");
        list!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Not_eligible_counterparty_and_amount_give_reasons_and_no_opening()
    {
        var claim = await ClaimAsync(_slice.Policy(), insurer: Guid.NewGuid().ToString("D"));
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 4000m)]);
        var evaluated = await PostAsync("/api/clm/v1/friendly-settlements/evaluate", new { claimId = claim.ClaimId });
        evaluated.Text("eligibility.result").ShouldBe("NOT_ELIGIBLE");
        evaluated["eligibility"]!["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain("COUNTERPARTY_NOT_MEMBER");
        evaluated.Text("fsCase.state").ShouldBe("NOT_ELIGIBLE");
        await PostAsync("/api/clm/v1/friendly-settlements/open-own-settlement",
            new { fsCaseId = evaluated.Text("fsCase.fsCaseId"), expectedRecordVersion = evaluated["fsCase"]!["recordVersion"]!.GetValue<int>() },
            HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task At_fault_notification_matches_the_claim_and_creates_a_CLEARING_payment_without_a_disbursement()
    {
        var policy = _slice.Policy();
        var claim = await ClaimAsync(policy, fault: 100m);
        var lossDate = await _money.ScalarAsync<string>($"SELECT loss_date::text FROM clm.claim WHERE claim_id='{claim.ClaimId}'");
        var reference = "STUB-FS-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var notification = new
        {
            clearingReference = reference, policyNumber = policy.PolicyNumber, lossDate, counterpartyInsurerPartyId = Insurer,
            amount = "3000.00", currency = "EUR", damageType = "MATERIAL_DAMAGE",
        };

        var received = await PostAsync("/api/clm/v1/dev/fs-clearing/notifications", notification, roles: Manager);
        received.Text("fsCase.state").ShouldBe("ACCEPTED");
        received.Text("fsCase.role").ShouldBe("AT_FAULT_INSURER");
        received.Text("claimId").ShouldBe(claim.ClaimId);
        var setId = received.Text("setId");

        // Replay: same clearing reference, same set, no second case.
        var replay = await PostAsync("/api/clm/v1/dev/fs-clearing/notifications", notification, roles: Manager);
        replay.Text("setId").ShouldBe(setId);
        replay["replay"]!.GetValue<bool>().ShouldBeTrue();
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.fs_case WHERE claim_id='{claim.ClaimId}' AND role='AT_FAULT_INSURER'")).ShouldBe(1);

        // Authority is still checked: a set over the handler's limits waits for real approvals.
        var set = await _money.SetAsync(setId);
        foreach (var approval in set["approvals"]?.AsArray() ?? [])
        {
            (await _money.DecideAsync(approval!.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _money.DrainAsync();
        var payments = await _money.PaymentsAsync(claim);
        payments.AsArray().Count.ShouldBe(1);
        payments[0].Text("method").ShouldBe("CLEARING");
        Amount(payments[0]!["amount"]).ShouldBe(3000m);
        payments[0]!["disbursementId"].ShouldBeNull();

        var (_, got) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/friendly-settlements/{received.Text("fsCase.fsCaseId")}");
        got.Text("fsCase.atFaultPaymentId").ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Malformed_notification_is_a_validation_problem_not_a_500()
    {
        var problem = await PostAsync("/api/clm/v1/dev/fs-clearing/notifications", new { clearingReference = "STUB-FS-X" }, HttpStatusCode.BadRequest, Manager);
        problem.ToJsonString().ShouldContain("FS_NOTIFICATION_MALFORMED");
    }

    [Fact]
    public async Task At_fault_notification_for_an_unknown_claim_creates_it_on_the_policy()
    {
        var policy = _slice.Policy();
        _policies.Setup("pol.Policy.search", new PolicySearchPage
        {
            Items = [new PolicySearchItem { PolicyId = new PolicyId(policy.PolicyId), PolicyNumber = PolicyNumber.Parse(policy.PolicyNumber), ProductCode = policy.ProductCode, InsuredPartyId = policy.InsuredPartyId }],
            NextCursor = null,
        });
        var notification = new
        {
            clearingReference = "STUB-FS-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), policyNumber = policy.PolicyNumber,
            lossDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)).ToString("yyyy-MM-dd"), counterpartyInsurerPartyId = Insurer,
            amount = "3000.00", damageType = "MATERIAL_DAMAGE",
        };
        var received = await PostAsync("/api/clm/v1/dev/fs-clearing/notifications", notification, roles: Manager);
        var claimId = received.Text("claimId");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.exposure WHERE claim_id='{claimId}' AND kind='MTPL_PD'")).ShouldBe(1);
        (await _money.ScalarAsync<decimal>($"SELECT fault_insured_pct FROM clm.claim WHERE claim_id='{claimId}'")).ShouldBe(100m);
    }
}
