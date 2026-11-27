using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Accounting;

/// <summary>
/// BO-RI-11 technical account: items are produced from recoveries, cession premium deltas, deposit instalments and approved adjustments and
/// commission, split over the participants without losing a cent, once per source; a correction is a reversal; the table is append-only.
/// </summary>
public sealed class AccountItemTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static async Task<(RegistrySlice Slice, string ContractId)> NewContractAsync(PostgresFixture database)
    {
        var slice = new RegistrySlice(database);
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, _) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        return (slice, id);
    }

    private static decimal Sum(JsonNode? list, Func<JsonNode, bool>? where = null) =>
        list!["items"]!.AsArray().Select(i => i!["item"]!).Where(i => where is null || where(i))
            .Sum(i => decimal.Parse(i["amount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture));

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> GenerateAsync(RegistrySlice slice, string id, string asOf = "2026-12-31") =>
        slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/generate", new { contractId = id, asOf }, Accountant, "riacct");

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> ListAsync(RegistrySlice slice, string id, string query = "") =>
        slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/items?contractId={id}&limit=200{query}", roles: Accountant);

    private static async Task<(JsonNode? Body, Guid Lead, Guid Follow)> SeedAllSourcesAsync(RegistrySlice slice, string id)
    {
        // Deposit schedule: three instalments of an amount that does not divide by 3 or by the 60/40 lines.
        (await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/deposit-schedules/reschedule",
            new JsonObject
            {
                ["contractId"] = id, ["layerNo"] = 1, ["minimumPremium"] = Money("20000.00"), ["depositPremium"] = Money("100000.01"), ["instalmentCount"] = 3,
                ["firstDueDate"] = "2026-03-31", ["intervalMonths"] = 3, ["reason"] = "Placement slip",
            },
            Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lead = await slice.ScalarAsync<Guid>(
            $"SELECT p.reinsurer_party_id FROM ri.participation p JOIN ri.contract_version v ON v.version_id = p.version_id WHERE v.contract_id = '{id}' AND p.lead");
        var follow = await slice.ScalarAsync<Guid>(
            $"SELECT p.reinsurer_party_id FROM ri.participation p JOIN ri.contract_version v ON v.version_id = p.version_id WHERE v.contract_id = '{id}' AND NOT p.lead");

        // A cession premium delta of the treaty and year (written by the cession intake in production).
        await slice.ExecuteAsync(
            "INSERT INTO ri.cession (cession_id, legal_entity_id, treaty_id, contract_year, term_id, set_key, written_premium, target, booked_before, delta, mode, recorded_at) " +
            $"SELECT gen_random_uuid(), legal_entity_id, stable_treaty_id, contract_year, gen_random_uuid(), gen_random_uuid(), 1000.01, 1000.01, 0, 1000.01, 'NET', '2026-02-10T10:00:00Z' FROM ri.contract WHERE contract_id = '{id}'");

        // Booked recoveries (written by the recovery booker in production): 30,000.00 from the lead, 10,000.50 from the follower.
        var batch = Guid.NewGuid();
        await slice.ExecuteAsync(
            "INSERT INTO ri.recovery_batch (batch_id, legal_entity_id, contract_id, contract_year, claim_id, source_event_id, recorded_at, calc_hash, engine_version, contract_version, trace) " +
            $"SELECT '{batch}', legal_entity_id, contract_id, contract_year, gen_random_uuid(), gen_random_uuid(), '2026-02-10T11:00:00Z', 'h', 'e', '1', '[]' FROM ri.contract WHERE contract_id = '{id}'");
        foreach (var (party, paid) in new[] { (lead, "30000.00"), (follow, "10000.50") })
        {
            await slice.ExecuteAsync(
                "INSERT INTO ri.recovery (recovery_id, batch_id, legal_entity_id, contract_id, contract_year, claim_id, layer_id, layer_no, participant_party_id, incurred_delta, paid_delta, outstanding_delta, state, sii_lob, recorded_at) " +
                $"SELECT gen_random_uuid(), '{batch}', legal_entity_id, contract_id, contract_year, gen_random_uuid(), gen_random_uuid(), 1, '{party}', {paid}, {paid}, 0, 'CALCULATED', 'UNMAPPED', '2026-02-10T11:00:00Z' FROM ri.contract WHERE contract_id = '{id}'");
        }

        // An approved adjustment (return premium) and an approved commission adjustment.
        var (_, adjustment) = await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/adjustments/calculate",
            new JsonObject { ["contractId"] = id, ["layerNo"] = 1, ["subjectPremium"] = Money("1200000.00"), ["ratePct"] = "2.5" }, Accountant, "riacct");
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/adjustments/approve", new { adjustmentId = adjustment.Text("adjustment.adjustmentId"), decision = "APPROVE" }, Manager, "rimgr"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, commission) = await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/commission/adjust",
            new JsonObject
            {
                ["contractId"] = id, ["cededPremium"] = Money("100000.00"), ["incurredLoss"] = Money("65000.00"), ["provisionalRatePct"] = "25",
                ["scale"] = new JsonArray(
                    new JsonObject { ["lossRatioFromPct"] = "0", ["commissionRatePct"] = "25" },
                    new JsonObject { ["lossRatioFromPct"] = "60", ["commissionRatePct"] = "20" }),
            },
            Accountant, "riacct");
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/commission/approve", new { commissionId = commission.Text("commission.commissionId"), decision = "APPROVE" }, Manager, "rimgr"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (adjustment, lead, follow);
    }

    [Fact]
    public async Task Items_from_every_source_sum_to_the_booked_amounts_and_a_second_run_adds_nothing()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;
        var (adjustment, lead, follow) = await SeedAllSourcesAsync(slice, id);

        var (ok, generated) = await GenerateAsync(slice, id);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, generated?.ToJsonString());
        generated.Text("skipped").ShouldBe("0");
        generated.Text("created").ShouldBe("14"); // 3 instalments, 1 cession, 2 recoveries, 1 adjustment, 1 commission, each per reinsurer except the 2 recoveries

        var (_, all) = await ListAsync(slice, id);
        Sum(all, i => i["kind"]!.GetValue<string>() == "DEPOSIT_INSTALMENT").ShouldBe(100000.01m);
        Sum(all, i => i["kind"]!.GetValue<string>() == "CEDED_PREMIUM").ShouldBe(1000.01m);
        Sum(all, i => i["kind"]!.GetValue<string>() == "RECOVERY").ShouldBe(-40000.50m);
        Sum(all, i => i["kind"]!.GetValue<string>() == "PREMIUM_ADJUSTMENT").ShouldBe(decimal.Parse(adjustment.Text("adjustment.adjustmentAmount.amount"), CultureInfo.InvariantCulture));
        Sum(all, i => i["kind"]!.GetValue<string>() == "COMMISSION").ShouldBe(5000m); // earned 20,000 against 25,000 provisional: 5,000 more is payable

        // The recoveries stay with the participant that booked them; the split is 60 / 40 and exact.
        var leadRecoveries = Sum(all, i => i["kind"]!.GetValue<string>() == "RECOVERY" && i["reinsurerPartyId"]!.GetValue<string>() == lead.ToString("D"));
        leadRecoveries.ShouldBe(-30000m);
        Sum(all, i => i["kind"]!.GetValue<string>() == "DEPOSIT_INSTALMENT" && i["reinsurerPartyId"]!.GetValue<string>() == follow.ToString("D")).ShouldBeInRange(40000.00m, 40000.02m);

        // Once per source: running again, or for every contract, creates nothing.
        var (_, again) = await GenerateAsync(slice, id);
        again.Text("created").ShouldBe("0");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.account_item WHERE contract_id = '{id}'")).ShouldBe(14);

        // The balance report agrees with the items: per reinsurer the last closing is the sum of that reinsurer's items.
        var (reported, report) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/balances/report", new { contractId = id }, Accountant, "riacct");
        reported.StatusCode.ShouldBe(HttpStatusCode.OK, report?.ToJsonString());
        var lines = report!["balances"]!.AsArray();
        foreach (var party in new[] { lead, follow })
        {
            var mine = lines.Where(l => l!["reinsurerPartyId"]!.GetValue<string>() == party.ToString("D")).ToList();
            mine.Count.ShouldBeGreaterThan(1);
            var last = decimal.Parse(mine[^1]!["closing"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            last.ShouldBe(Sum(all, i => i["reinsurerPartyId"]!.GetValue<string>() == party.ToString("D")));
        }

        // A period filter keeps the running balance of the earlier periods.
        var (_, later) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/balances/report", new { contractId = id, periodFrom = "2026-09" }, Accountant, "riacct");
        later!["balances"]!.AsArray().Count.ShouldBeLessThan(lines.Count);
        later["balances"]!.AsArray().Select(l => l!["period"]!.GetValue<string>()).ShouldAllBe(p => string.CompareOrdinal(p, "2026-09") >= 0);
    }

    [Fact]
    public async Task A_pending_adjustment_produces_nothing_and_instalments_not_yet_due_wait()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;
        (await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/deposit-schedules/reschedule",
            new JsonObject
            {
                ["contractId"] = id, ["layerNo"] = 1, ["minimumPremium"] = Money("20000.00"), ["depositPremium"] = Money("90000.00"), ["instalmentCount"] = 3,
                ["firstDueDate"] = "2026-03-31", ["intervalMonths"] = 3, ["reason"] = "Placement slip",
            },
            Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/adjustments/calculate",
            new JsonObject { ["contractId"] = id, ["layerNo"] = 1, ["subjectPremium"] = Money("1200000.00"), ["ratePct"] = "2.5" }, Accountant, "riacct")).Response.StatusCode
            .ShouldBe(HttpStatusCode.OK);

        // Only the first instalment is due on 2026-04-30; the pending adjustment is not an account item.
        var (_, first) = await GenerateAsync(slice, id, "2026-04-30");
        first.Text("created").ShouldBe("2");
        first!["byKind"]!.AsObject().Select(p => p.Key).ShouldBe(["DEPOSIT_INSTALMENT"]);

        // Later instalments come with a later as-of date; the earlier ones are not repeated.
        var (_, next) = await GenerateAsync(slice, id, "2026-12-31");
        next.Text("created").ShouldBe("4");
        Sum((await ListAsync(slice, id)).Body).ShouldBe(90000m);
    }

    [Fact]
    public async Task A_reversal_nets_the_item_once_is_not_regenerated_and_the_table_is_append_only()
    {
        var (slice, id) = await NewContractAsync(database);
        await using var _ = slice;
        await SeedAllSourcesAsync(slice, id);
        (await GenerateAsync(slice, id)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, recoveries) = await ListAsync(slice, id, "&kind=RECOVERY");
        var item = recoveries!["items"]![0]!["item"]!;
        var itemId = item["itemId"]!.GetValue<string>();
        var amount = decimal.Parse(item["amount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        // A reason is required; the maker role only; then the opposite item is appended.
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/reverse", new { itemId, reason = "" }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/reverse", new { itemId, reason = "Wrong claim" }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (reversed, body) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/reverse", new { itemId, reason = "Wrong claim" }, Accountant, "riacct");
        reversed.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        decimal.Parse(body.Text("reversal.amount.amount"), CultureInfo.InvariantCulture).ShouldBe(-amount);
        body.Text("reversal.reversalOf").ShouldBe(itemId);

        // Once, and a reversal is not reversed.
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/reverse", new { itemId, reason = "Again" }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/reverse", new { itemId = body.Text("reversal.itemId"), reason = "Undo" }, Accountant, "riacct")).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/reverse", new { itemId = Guid.NewGuid(), reason = "Unknown" }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The source is not produced again, and the recoveries of the contract net by the reversal.
        (await GenerateAsync(slice, id)).Body.Text("created").ShouldBe("0");
        Sum((await ListAsync(slice, id, "&kind=RECOVERY")).Body).ShouldBe(-40000.50m - amount);

        // Append-only for everyone, the owner included.
        var update = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync($"UPDATE ri.account_item SET amount = 1 WHERE item_id = '{itemId}'"));
        update.SqlState.ShouldBe("42501");
        var delete = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync($"DELETE FROM ri.account_item WHERE item_id = '{itemId}'"));
        delete.SqlState.ShouldBe("42501");
        var truncate = await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync("TRUNCATE ri.account_item"));
        truncate.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Bad_filters_are_refused_and_an_unknown_contract_has_no_items()
    {
        var (slice, _) = await NewContractAsync(database);
        await using var _ = slice;
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/items?kind=NOPE", roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/items?period=2026-13", roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/items?contractId=not-a-guid", roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/generate", new { contractId = Guid.NewGuid() }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/balances/report", new { periodFrom = "bad" }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (empty, body) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/items?contractId={Guid.NewGuid()}", roles: Accountant);
        empty.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!["items"]!.AsArray().Count.ShouldBe(0);
    }
}
