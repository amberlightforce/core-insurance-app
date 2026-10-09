using System.Net;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>
/// D-SL2-13 regression tests (the SL2-CLM-MONEY deep review's probes, inverted): CLM.RESERVE on the resulting exposure
/// total, CLM.PAYMENT per payment and on the claim's cumulative paid, one PLT request per referred (type, cost type) with
/// the set executing only when all are approved, and no separate check for the release a FINAL payment proposes.
/// </summary>
public sealed class ClaimsAuthorityTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Task<MoneyClaim> ClaimAsync() => _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));

    [Fact]
    public async Task D_SL2_13_D1_A_reserve_split_into_many_transactions_is_checked_on_the_exposure_total()
    {
        var claim = await ClaimAsync();
        var (built, build) = await _money.BuildAsync(claim, [.. Enumerable.Range(0, 20).Select(_ => Reserve(claim, 50000.01m))]);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, build?.ToJsonString());

        // 1,000,000.20 total: above the manager's 1,000,000.00 as well → DENY, even for the manager.
        var (handler, handlerBody) = await _money.SubmitAsync(build.Text("setId"));
        handler.StatusCode.ShouldBe(HttpStatusCode.Forbidden, handlerBody?.ToJsonString());
        handlerBody.Text("code").ShouldBe("CLM-ERR-AUTHORITY");
        var (manager, managerBody) = await _money.SubmitAsync(build.Text("setId"), Manager, ManagerUser);
        manager.StatusCode.ShouldBe(HttpStatusCode.Forbidden, managerBody?.ToJsonString());
        Amount((await _money.FinancialsAsync(claim))["totals"]!["reserved"]).ShouldBe(0m);

        // Two sets of 3,000.00 each: the second makes the exposure total 6,000.00 → REFER.
        (await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 3000m)])).Text("status").ShouldBe("APPROVED");
        var second = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1000m), Reserve(claim, 2000m)]);
        second.Text("status").ShouldBe("PENDING_APPROVAL");
        Amount(second["authorityChecks"]![0]!["amount"]).ShouldBe(6000m);
    }

    [Fact]
    public async Task D_SL2_13_D2_Payments_are_checked_on_the_claims_cumulative_paid()
    {
        var claim = await ClaimAsync();
        (await _money.BuildAndSubmitAsync(claim, [.. Enumerable.Range(0, 4).Select(_ => Reserve(claim, 5000m))], Manager, ManagerUser)).Text("status").ShouldBe("APPROVED");
        var (account, _) = await _money.CaptureAsync(claim);

        (await _money.BuildAndSubmitAsync(claim, [Payment(claim, 5000.00m, account, paymentType: "PARTIAL")])).Text("status").ShouldBe("APPROVED");
        var second = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 4999.99m, account, paymentType: "PARTIAL")]);
        second.Text("status").ShouldBe("PENDING_APPROVAL");
        var cumulative = second["authorityChecks"]!.AsArray().Single(c => c!.Text("basis") == "CLAIM_CUMULATIVE_PAID")!;
        cumulative.Text("decision").ShouldBe("REFER");
        Amount(cumulative["amount"]).ShouldBe(9999.99m);
        second["authorityChecks"]!.AsArray().Single(c => c!.Text("basis") == "PAYMENT")!.Text("decision").ShouldBe("ALLOW");

        await _money.DrainAsync();
        Amount((await _money.FinancialsAsync(claim))["totals"]!["paid"]).ShouldBe(5000m);
    }

    [Fact]
    public async Task D_SL2_13_D3_A_mixed_set_needs_one_approval_per_referred_type_and_cost_type()
    {
        var claim = await ClaimAsync();
        var (account, _) = await _money.CaptureAsync(claim);

        // Reserve 40,000 on ASSESSOR_FEE (EXPENSE_ALLOCATED) + a payment of 6,000 on VEHICLE_REPAIR with its 6,000 top-up (INDEMNITY).
        var submit = await _money.BuildAndSubmitAsync(claim,
        [
            Reserve(claim, 40000m, category: "ASSESSOR_FEE", costType: "EXPENSE_ALLOCATED"),
            Payment(claim, 6000m, account, paymentType: "PARTIAL"),
        ]);
        submit.Text("status").ShouldBe("PENDING_APPROVAL");
        var approvals = submit["set"]!["approvals"]!.AsArray();
        approvals.Select(a => $"{a!.Text("authorityType")}/{a.Text("costType")}/{a!["amount"]!["amount"]!.GetValue<string>()}").Order(StringComparer.Ordinal)
            .ShouldBe(["CLM.PAYMENT/INDEMNITY/6000.00", "CLM.RESERVE/EXPENSE_ALLOCATED/46000.00", "CLM.RESERVE/INDEMNITY/46000.00"]);
        approvals.Single(a => a!.Text("authorityType") == "CLM.PAYMENT")!.Text("approvalType").ShouldBe("CLM.CLAIM_PAYMENT");

        // Approving two of the three leaves the set pending; nothing is applied or paid.
        var ids = approvals.Select(a => a!.Text("approvalRequestId")).ToList();
        foreach (var id in ids.Take(2))
        {
            (await _money.DecideAsync(id)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _money.DrainAsync();
        var pending = await _money.SetAsync(submit.Text("setId"));
        pending.Text("status").ShouldBe("PENDING_APPROVAL");
        pending["approvals"]!.AsArray().Count(a => a!.Text("status") == "APPROVED").ShouldBe(2);
        Amount((await _money.FinancialsAsync(claim))["totals"]!["reserved"]).ShouldBe(0m);

        // The third approval executes the set and the payment.
        (await _money.DecideAsync(ids[2])).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        (await _money.SetAsync(submit.Text("setId"))).Text("status").ShouldBe("APPROVED");
        (await _money.PaymentsAsync(claim))[0].Text("status").ShouldBe("CLEARED");

        // A rejection of any one of a set's requests rejects the set.
        var claim2 = await ClaimAsync();
        var (account2, _) = await _money.CaptureAsync(claim2);
        var mixed = await _money.BuildAndSubmitAsync(claim2,
        [
            Reserve(claim2, 40000m, category: "ASSESSOR_FEE", costType: "EXPENSE_ALLOCATED"),
            Payment(claim2, 6000m, account2, paymentType: "PARTIAL"),
        ]);
        var first = mixed["set"]!["approvals"]![0]!.Text("approvalRequestId");
        (await _money.DecideAsync(first, "Reject")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        (await _money.SetAsync(mixed.Text("setId"))).Text("status").ShouldBe("REJECTED");
    }

    [Fact]
    public async Task D_SL2_13_A_final_payments_release_needs_no_separate_check_but_a_manual_decrease_does()
    {
        var claim = await ClaimAsync();
        (await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 40000m)], Manager, ManagerUser)).Text("status").ShouldBe("APPROVED");
        var (account, _) = await _money.CaptureAsync(claim);

        // A 100.00 final payment releases 39,900.00 in the same set: within the handler's authority.
        var submit = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 100m, account)]);
        submit.Text("status").ShouldBe("APPROVED", submit.ToJsonString());
        Amount(submit["set"]!["transactions"]![1]!["amount"]).ShouldBe(-39900m);

        // A manual decrease of 6,000.00 is checked on its absolute amount → REFER for the handler.
        var other = await ClaimAsync();
        (await _money.BuildAndSubmitAsync(other, [Reserve(other, 9000m)], Manager, ManagerUser)).Text("status").ShouldBe("APPROVED");
        var decrease = await _money.BuildAndSubmitAsync(other, [Reserve(other, -6000m, reason: "RELEASE")]);
        decrease.Text("status").ShouldBe("PENDING_APPROVAL");
        decrease.Text("authorityChecks.0.basis").ShouldBe("RESERVE_DECREASE");
    }
}
