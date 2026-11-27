using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Accounting;

/// <summary>
/// BO-RI-10 premium accounting: deposit schedules (instalments that sum exactly), the adjustment premium and the sliding-scale commission,
/// both decided once through PLT by a checker other than the maker; the rows are append-only.
/// </summary>
public sealed class PremiumAccountingTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static async Task<(RegistrySlice Slice, string ContractId)> NewContractAsync(PostgresFixture database)
    {
        var slice = new RegistrySlice(database);
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, _) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        return (slice, id);
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> RescheduleAsync(
        RegistrySlice slice, string id, string minimum = "20000.00", string deposit = "100000.01", int count = 3, int months = 3, int? expectedRevision = null, int layer = 1) =>
        slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/deposit-schedules/reschedule",
            new JsonObject
            {
                ["contractId"] = id, ["layerNo"] = layer, ["minimumPremium"] = Money(minimum), ["depositPremium"] = Money(deposit), ["instalmentCount"] = count,
                ["firstDueDate"] = "2026-03-31", ["intervalMonths"] = months, ["expectedRevision"] = expectedRevision, ["reason"] = "Placement slip",
            },
            Accountant, "riacct");

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> CalculateAsync(RegistrySlice slice, string id, string subject = "1200000.00", string rate = "2.5") =>
        slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/adjustments/calculate",
            new JsonObject { ["contractId"] = id, ["layerNo"] = 1, ["subjectPremium"] = Money(subject), ["ratePct"] = rate, ["reason"] = "Final subject premium" },
            Accountant, "riacct");

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAdjustmentAsync(
        RegistrySlice slice, string adjustmentId, string decision = "APPROVE", string user = "rimgr", string roles = Manager, string? reason = null) =>
        slice.SendAsync(HttpMethod.Post, "/api/ri/v1/adjustments/approve", new { adjustmentId, decision, reason }, roles, user);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> CommissionAsync(
        RegistrySlice slice, string id, string ceded = "100000.00", string incurred = "65000.00") =>
        slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/commission/adjust",
            new JsonObject
            {
                ["contractId"] = id, ["cededPremium"] = Money(ceded), ["incurredLoss"] = Money(incurred), ["provisionalRatePct"] = "25",
                ["scale"] = new JsonArray(
                    new JsonObject { ["lossRatioFromPct"] = "0", ["commissionRatePct"] = "25" },
                    new JsonObject { ["lossRatioFromPct"] = "60", ["commissionRatePct"] = "20" },
                    new JsonObject { ["lossRatioFromPct"] = "80", ["commissionRatePct"] = "15" }),
                ["reason"] = "Year-end loss ratio",
            },
            Accountant, "riacct");

    private static decimal Amount(JsonNode? body, string path) => decimal.Parse(body.Text(path), CultureInfo.InvariantCulture);

    [Fact]
    public async Task Instalments_sum_exactly_to_the_deposit_and_a_reschedule_is_the_next_revision()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;
        var (ok, first) = await RescheduleAsync(slice, id);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, first?.ToJsonString());
        var instalments = first!["schedule"]!["instalments"]!.AsArray();
        instalments.Count.ShouldBe(3);
        instalments.Sum(i => decimal.Parse(i!["amount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture)).ShouldBe(100000.01m);
        first.Text("schedule.revision").ShouldBe("1");
        instalments[0]!["dueDate"]!.GetValue<string>().ShouldBe("2026-03-31");
        instalments[2]!["dueDate"]!.GetValue<string>().ShouldBe("2026-09-30");
        var firstId = first.Text("schedule.scheduleId");

        // A stale expectation is refused; the next revision supersedes the first, which stays readable.
        (await RescheduleAsync(slice, id, expectedRevision: 0)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var (second, next) = await RescheduleAsync(slice, id, deposit: "90000.00", count: 4, months: 1, expectedRevision: 1);
        second.StatusCode.ShouldBe(HttpStatusCode.OK, next?.ToJsonString());
        next.Text("schedule.revision").ShouldBe("2");
        var (_, old) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/deposit-schedules/" + firstId, roles: Accountant);
        old.Text("schedule.current").ShouldBe("false");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.deposit_schedule WHERE contract_id = '{id}'")).ShouldBe(2);

        // Validation: minimum above deposit, unknown layer, unusual interval.
        (await RescheduleAsync(slice, id, minimum: "95000.00", deposit: "90000.00")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RescheduleAsync(slice, id, layer: 9)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RescheduleAsync(slice, id, months: 5)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/deposit-schedules/" + Guid.NewGuid(), roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_adjustment_is_calculated_from_the_schedule_and_approved_exactly_once_by_another_person()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;

        // No schedule yet: nothing to adjust against.
        (await CalculateAsync(slice, id)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RescheduleAsync(slice, id, minimum: "20000.00", deposit: "25000.00", count: 2, months: 6)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (calculated, adjustment) = await CalculateAsync(slice, id);
        calculated.StatusCode.ShouldBe(HttpStatusCode.OK, adjustment?.ToJsonString());
        Amount(adjustment, "adjustment.calculatedPremium.amount").ShouldBe(30000m); // 1,200,000 at 2.5 %
        Amount(adjustment, "adjustment.adjustmentAmount.amount").ShouldBe(5000m); // less the 25,000 deposit
        adjustment.Text("adjustment.status").ShouldBe("PENDING");
        var adjustmentId = adjustment.Text("adjustment.adjustmentId");

        // Another calculation is refused while one is pending; the maker (even as manager) and a non-manager cannot decide.
        (await CalculateAsync(slice, id)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await DecideAdjustmentAsync(slice, adjustmentId, user: "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DecideAdjustmentAsync(slice, adjustmentId, user: "other", roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DecideAdjustmentAsync(slice, adjustmentId, "REJECT")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // a rejection needs its reason

        var (approved, decided) = await DecideAdjustmentAsync(slice, adjustmentId);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, decided?.ToJsonString());
        decided.Text("adjustment.status").ShouldBe("APPROVED");
        decided.Text("adjustment.decidedBy").ShouldNotBeNullOrEmpty();

        // Once: a second decision and a new calculation are both refused; the schedule is frozen too.
        (await DecideAdjustmentAsync(slice, adjustmentId)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CalculateAsync(slice, id)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RescheduleAsync(slice, id)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.accounting_decision WHERE subject_id = '{adjustmentId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_rejected_adjustment_can_be_calculated_again_and_a_return_premium_is_negative()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;
        (await RescheduleAsync(slice, id, minimum: "20000.00", deposit: "25000.00", count: 1, months: 12)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, first) = await CalculateAsync(slice, id);
        var (rejected, body) = await DecideAdjustmentAsync(slice, first.Text("adjustment.adjustmentId"), "REJECT", reason: "Subject premium not final");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("adjustment.status").ShouldBe("REJECTED");

        // Subject premium 400,000 at 2.5 % is 10,000: below the minimum, so the minimum is earned and 5,000 comes back.
        var (again, second) = await CalculateAsync(slice, id, "400000.00");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, second?.ToJsonString());
        Amount(second, "adjustment.adjustmentAmount.amount").ShouldBe(-5000m);
    }

    [Fact]
    public async Task Commission_follows_the_sliding_scale_is_decided_once_and_the_history_is_append_only()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;

        var (ok, calculated) = await CommissionAsync(slice, id);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, calculated?.ToJsonString());
        Amount(calculated, "commission.lossRatioPct").ShouldBe(65m);
        Amount(calculated, "commission.earnedRatePct").ShouldBe(20m);
        Amount(calculated, "commission.earnedCommission.amount").ShouldBe(20000m);
        Amount(calculated, "commission.provisionalCommission.amount").ShouldBe(25000m);
        Amount(calculated, "commission.adjustmentAmount.amount").ShouldBe(-5000m);
        var commissionId = calculated.Text("commission.commissionId");

        (await CommissionAsync(slice, id)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // one pending at a time
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/commission/approve", new { commissionId, decision = "APPROVE" }, Manager, "riacct")).Response.StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        var (approved, decided) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/commission/approve", new { commissionId, decision = "APPROVE" }, Manager, "rimgr");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, decided?.ToJsonString());
        decided.Text("commission.status").ShouldBe("APPROVED");
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/commission/approve", new { commissionId, decision = "APPROVE" }, Manager, "rimgr")).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);

        // A later calculation of the year (loss ratio 85 % → 15 %) adds to the history; nothing earlier changes.
        var (_, second) = await CommissionAsync(slice, id, incurred: "85000.00");
        Amount(second, "commission.earnedRatePct").ShouldBe(15m);
        var (listed, history) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/commission/history?contractId={id}", roles: Accountant);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, history?.ToJsonString());
        var items = history!["items"]!.AsArray();
        items.Count.ShouldBe(2);
        items[0]!["commission"]!["status"]!.GetValue<string>().ShouldBe("PENDING");
        items[1]!["commission"]!["status"]!.GetValue<string>().ShouldBe("APPROVED");

        // Append-only for everyone, the owner included.
        var update = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync($"UPDATE ri.commission SET earned_rate_pct = 1 WHERE commission_id = '{commissionId}'"));
        update.SqlState.ShouldBe("42501");
        var delete = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync($"DELETE FROM ri.accounting_decision WHERE subject_id = '{commissionId}'"));
        delete.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task A_scale_that_does_not_cover_the_loss_ratio_is_refused()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;
        var (response, body) = await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/commission/adjust",
            new JsonObject
            {
                ["contractId"] = id, ["cededPremium"] = Money("100000.00"), ["incurredLoss"] = Money("10000.00"), ["provisionalRatePct"] = "25",
                ["scale"] = new JsonArray(new JsonObject { ["lossRatioFromPct"] = "50", ["commissionRatePct"] = "20" }),
            },
            Accountant, "riacct");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body?.ToJsonString());
    }
}
