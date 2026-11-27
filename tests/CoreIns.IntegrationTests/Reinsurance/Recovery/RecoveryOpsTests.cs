using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using Npgsql;

namespace CoreIns.IntegrationTests.Reinsurance.Recovery;

/// <summary>
/// BO-RI-08: <c>ri.Recovery.recalculate</c>, <c>ri.Recovery.adjust</c>, <c>ri.Occurrence.link / unlink</c> and
/// <c>ri.AggregateTracker.get</c> (REQ-RI-003, -115, -135, -152). Treaty: 500,000 xs 500,000, AAL 1,500,000, 60% / 40%, placed 100%.
/// Everything is synthetic.
/// </summary>
public sealed class RecoveryOpsTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static decimal Total(JsonNode? page)
    {
        var sum = 0m;
        foreach (var item in page!["items"]!.AsArray())
        {
            sum += decimal.Parse(item!["recoverableIncurred"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        }

        return sum;
    }

    private static async Task<(Guid Claim, Guid Exposure)> ClaimAsync(RecoverySlice slice, string product, string reserve)
    {
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, reserve);
        return (claim, exposure);
    }

    private static async Task<JsonNode> TrackerRowAsync(RecoverySlice slice, string contract)
    {
        var year = await slice.ScalarAsync<int>($"SELECT contract_year FROM ri.contract WHERE contract_id = '{contract}'");
        var (response, body) = await slice.Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/aggregate-tracker/get?year={year}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["schedule"]!.AsArray().Single(r => r!["contractId"]!.GetValue<string>() == contract)!;
    }

    private static decimal Money(JsonNode? node) => decimal.Parse(node!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    [Fact]
    public async Task Recalculate_restates_the_year_to_the_same_result_as_the_incremental_intake()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var first = await ClaimAsync(slice, product, "800000.00");
        var second = await ClaimAsync(slice, product, "650000.00");
        await slice.PaidAsync(first.Claim, first.Exposure, "300000.00");
        var before = Total((await slice.ByContractAsync(contract)).Body);
        var rows = await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'");
        before.ShouldBe(450000m); // 300,000 + 150,000

        var year = await slice.ScalarAsync<int>($"SELECT contract_year FROM ri.contract WHERE contract_id = '{contract}'");
        var (response, body) = await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/recalculate", new { contractId = contract, contractYear = year });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body?.ToJsonString());
        body!["rowsBooked"]!.GetValue<int>().ShouldBe(0); // the full recalculation finds nothing to change
        Total((await slice.ByContractAsync(contract)).Body).ShouldBe(before);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'")).ShouldBe(rows);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery_batch WHERE batch_id = '{body["batchId"]!.GetValue<string>()}'")).ShouldBe(1);
        (await slice.Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/recoveries/trace?batchId={body["batchId"]!.GetValue<string>()}")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Claim.ShouldNotBe(Guid.Empty);

        // A wrong year, an unknown contract and a cat event are refused; a claims role may not restate.
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/recalculate", new { contractId = contract, contractYear = year + 1 })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/recalculate", new { contractId = Guid.NewGuid(), contractYear = year })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/recalculate", new { contractId = contract, catEventCode = "STORM" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/recalculate", new { contractId = contract, contractYear = year }, RegistrySlice.ClaimsHandler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Recalculate_repairs_a_missing_delta_and_books_only_the_difference()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        await ClaimAsync(slice, product, "800000.00");
        await slice.Registry.ExecuteAsync($"UPDATE ri.claim_amount SET open_reserve = 900000 WHERE claim_id IN (SELECT claim_id FROM ri.claim_exposure WHERE contract_id = '{contract}')");
        var year = await slice.ScalarAsync<int>($"SELECT contract_year FROM ri.contract WHERE contract_id = '{contract}'");

        var (response, body) = await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/recalculate", new { contractId = contract, contractYear = year });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body?.ToJsonString());
        body!["rowsBooked"]!.GetValue<int>().ShouldBe(2); // one delta per participant: 300,000 -> 400,000
        Total((await slice.ByContractAsync(contract)).Body).ShouldBe(400000m);
    }

    [Fact]
    public async Task Linking_two_claims_measures_them_as_one_occurrence_and_unlinking_restores_the_recoveries()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var a = await ClaimAsync(slice, product, "800000.00");
        var b = await ClaimAsync(slice, product, "800000.00");
        Total((await slice.ByContractAsync(contract)).Body).ShouldBe(600000m); // 300,000 each, separately

        // One occurrence: UNL 1,600,000 -> layer loss capped at the 500,000 limit.
        var (linked, body) = await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/link", new { claimIds = new[] { a.Claim, b.Claim }, reason = "Same storm" });
        linked.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["occurrence"]!["status"]!.GetValue<string>().ShouldBe("ACTIVE");
        body["occurrence"]!["claimIds"]!.AsArray().Count.ShouldBe(2);
        Total((await slice.ByContractAsync(contract)).Body).ShouldBe(500000m);

        // A claim already linked cannot join another occurrence.
        var c = await ClaimAsync(slice, product, "100000.00");
        var (again, _) = await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/link", new { claimIds = new[] { a.Claim, c.Claim }, reason = "Again" });
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);

        // A later movement of a linked claim is measured on the occurrence too.
        await slice.ReserveAsync(b.Claim, b.Exposure, "900000.00");
        Total((await slice.ByContractAsync(contract)).Body).ShouldBe(500000m);

        var (unlinked, unlinkBody) = await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/unlink", new { claimIds = new[] { b.Claim }, reason = "Different event" });
        unlinked.StatusCode.ShouldBe(HttpStatusCode.OK, unlinkBody?.ToJsonString());
        unlinkBody!["occurrence"]!["status"]!.GetValue<string>().ShouldBe("DISSOLVED"); // one claim left: the occurrence is gone
        Total((await slice.ByContractAsync(contract)).Body).ShouldBe(700000m); // 300,000 + 400,000 again on their own
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.occurrence_claim WHERE unlinked_at IS NULL AND claim_id IN ('{a.Claim}', '{b.Claim}')")).ShouldBe(0);

        // Not linked any more; a second unlink is refused. Rows are only ever added: the restatement is a delta, not an update.
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/unlink", new { claimIds = new[] { b.Claim }, reason = "Twice" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}' AND incurred_delta < 0")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Claims_under_different_contracts_cannot_be_linked_and_a_reason_is_required()
    {
        await using var slice = new RecoverySlice(database);
        var (product, _, _, _) = await slice.TreatyAsync();
        var (otherProduct, _, _, _) = await slice.TreatyAsync();
        var a = await ClaimAsync(slice, product, "800000.00");
        var b = await ClaimAsync(slice, otherProduct, "800000.00");

        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/link", new { claimIds = new[] { a.Claim, b.Claim }, reason = "Mixed" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/link", new { claimIds = new[] { a.Claim, Guid.NewGuid() }, reason = "Unknown" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/link", new { claimIds = new[] { a.Claim, b.Claim }, reason = "" })).Response.IsSuccessStatusCode.ShouldBeFalse();
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/occurrences/link", new { claimIds = new[] { a.Claim, b.Claim }, reason = "x" }, RegistrySlice.ClaimsHandler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_adjustment_changes_the_tracker_once_splits_by_largest_remainder_and_is_sealed()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, lead, follow) = await slice.TreatyAsync();
        var claim = await ClaimAsync(slice, product, "800000.00");
        var before = await TrackerRowAsync(slice, contract);
        Money(before["recoverableIncurred"]).ShouldBe(300000m);
        Money(before["adjustments"]).ShouldBe(0m);
        var layer = before["layerId"]!.GetValue<string>();

        var key = Guid.NewGuid();
        var body = new { contractId = contract, claimId = claim.Claim, layerId = layer, amount = new { amount = "1000.01", currency = "EUR" }, reason = "Reinstatement premium agreed with the leader" };
        var (response, item) = await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", body, RegistrySlice.Manager, "rimgr", key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, item?.ToJsonString());
        var parts = item!["item"]!["parts"]!.AsArray().ToDictionary(p => p!["participantPartyId"]!.GetValue<string>(), p => Money(p!["amount"]));
        parts.Values.Sum().ShouldBe(1000.01m); // exact, no cent lost
        parts[lead].ShouldBe(600.01m); // the lead takes the odd cent
        parts[follow].ShouldBe(400.00m);

        // The same request replayed (same key) books nothing more; the tracker moved once.
        var (replay, _) = await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", body, RegistrySlice.Manager, "rimgr", key);
        replay.IsSuccessStatusCode.ShouldBeTrue();
        var after = await TrackerRowAsync(slice, contract);
        Money(after["recoverableIncurred"]).ShouldBe(301000.01m);
        Money(after["adjustments"]).ShouldBe(1000.01m);
        Money(after["recoverableOutstanding"]).ShouldBe(301000.01m);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery_adjustment WHERE contract_id = '{contract}'")).ShouldBe(2);

        // A restatement does not undo it, and the engine's own recoveries are unchanged.
        var year = await slice.ScalarAsync<int>($"SELECT contract_year FROM ri.contract WHERE contract_id = '{contract}'");
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/recalculate", new { contractId = contract, contractYear = year })).Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Money((await TrackerRowAsync(slice, contract))["recoverableIncurred"]).ShouldBe(301000.01m);
        Total((await slice.ByContractAsync(contract)).Body).ShouldBe(300000m);

        // Sealed: no update or delete, even for the owner role (PITFALLS 8).
        var update = await Should.ThrowAsync<PostgresException>(() => slice.Registry.ExecuteAsync($"UPDATE ri.recovery_adjustment SET incurred_delta = 0 WHERE contract_id = '{contract}'"));
        update.SqlState.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_adjustment_needs_a_reason_authority_for_the_amount_and_a_layer_of_the_contract()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var claim = await ClaimAsync(slice, product, "800000.00");
        var layer = (await TrackerRowAsync(slice, contract))["layerId"]!.GetValue<string>();
        object Body(string amount, string reason, string? layerId = null) => new { contractId = contract, claimId = claim.Claim, layerId = layerId ?? layer, amount = new { amount, currency = "EUR" }, reason };

        // The accountant may adjust up to 5,000 (illustrative grant); 5,000.01 is above the limit.
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("5000.00", "ok"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("5000.01", "too much"))).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("-250.00", "negative is fine"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("10.00", ""))).Response.IsSuccessStatusCode.ShouldBeFalse();
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("0.00", "zero"))).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("10.005", "mills"))).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("10.00", "layer", Guid.NewGuid().ToString()))).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.Registry.SendAsync(HttpMethod.Post, "/api/ri/v1/recoveries/adjust", Body("10.00", "claims role"), RegistrySlice.ClaimsHandler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Money((await TrackerRowAsync(slice, contract))["adjustments"]).ShouldBe(4750.00m);
    }

    [Fact]
    public async Task The_tracker_rejects_a_missing_year_and_filters_by_layer_number()
    {
        await using var slice = new RecoverySlice(database);
        var (_, contract, _, _) = await slice.TreatyAsync();
        var year = await slice.ScalarAsync<int>($"SELECT contract_year FROM ri.contract WHERE contract_id = '{contract}'");

        (await slice.Registry.SendAsync(HttpMethod.Get, "/api/ri/v1/aggregate-tracker/get")).Response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableContent);
        var (response, body) = await slice.Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/aggregate-tracker/get?year={year}&layer=1");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!["schedule"]!.AsArray().Any(r => r!["contractId"]!.GetValue<string>() == contract && r["layerNo"]!.GetValue<int>() == 1).ShouldBeTrue();
        (await slice.Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/aggregate-tracker/get?year={year}&layer=99")).Body!["schedule"]!.AsArray().Count.ShouldBe(0);
    }
}
