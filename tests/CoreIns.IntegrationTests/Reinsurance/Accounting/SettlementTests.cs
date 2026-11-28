using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Accounting;

/// <summary>
/// BO-RI-16 manual settlement (D-CON-13): the net balance of a reinsurer is proposed (and equals the statement closing), approved under RI.SETTLEMENT by
/// someone other than the proposer within the amount authority, then released once or recorded off-system with evidence; both post the movement to the account.
/// </summary>
public sealed class SettlementTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static async Task<(RegistrySlice Slice, string ContractId, Guid Lead)> NewContractWithItemsAsync(PostgresFixture database)
    {
        var slice = new RegistrySlice(database);
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, _) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        (await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/deposit-schedules/reschedule",
            new JsonObject
            {
                ["contractId"] = id, ["layerNo"] = 1, ["minimumPremium"] = Money("20000.00"), ["depositPremium"] = Money("100000.01"), ["instalmentCount"] = 3,
                ["firstDueDate"] = "2026-03-31", ["intervalMonths"] = 3, ["reason"] = "Placement slip",
            },
            Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/generate", new { contractId = id, asOf = "2026-12-31" }, Accountant, "riacct")).Response.StatusCode
            .ShouldBe(HttpStatusCode.OK);
        return (slice, id, Guid.Parse(lead));
    }

    private static decimal Amount(JsonNode? money) => decimal.Parse(money!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(RegistrySlice slice, string action, object body, string roles = Accountant, string user = "riacct") =>
        slice.SendAsync(HttpMethod.Post, "/api/ri/v1/settlements/" + action, body, roles, user);

    private static async Task<JsonNode> ProposeAsync(RegistrySlice slice, Guid party, string period)
    {
        var (response, body) = await PostAsync(slice, "propose", new { reinsurerPartyId = party, currency = "EUR", period });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["settlement"]!;
    }

    private static async Task<JsonNode> ApproveAsync(RegistrySlice slice, string settlementId)
    {
        var (response, body) = await PostAsync(slice, "approve", new { settlementId }, Manager, "rimgr");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["settlement"]!;
    }

    /// <summary>Posts a technical account item for the reinsurer straight into the table (a large premium or recovery the engine would otherwise have to produce).</summary>
    private static Task AddItemAsync(RegistrySlice slice, string contractId, Guid party, string amount, string kind = "CEDED_PREMIUM") =>
        slice.ExecuteAsync(
            "INSERT INTO ri.account_item (item_id, legal_entity_id, contract_id, contract_year, layer_no, reinsurer_party_id, kind, source_kind, source_id, source_group, currency, amount, period, item_date, reason, created_at, created_by) " +
            $"SELECT gen_random_uuid(), legal_entity_id, contract_id, contract_year, NULL, '{party}', '{kind}', 'TEST', gen_random_uuid()::text, gen_random_uuid()::text, 'EUR', {amount}, '2026-05', DATE '2026-05-15', 'test', now(), 'test' FROM ri.contract WHERE contract_id = '{contractId}'");

    [Fact]
    public async Task The_net_equals_the_statement_balance_and_a_release_posts_it_once_after_the_checker_with_authority_approves()
    {
        var (slice, id, lead) = await NewContractWithItemsAsync(database);
        await using var _ = slice;

        // A statement of the period; the proposal nets the same items, so it equals the statement closing.
        var (gen, genBody) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/statements/generate", new { reinsurerPartyId = lead, period = "2026-06" }, Accountant, "riacct");
        gen.StatusCode.ShouldBe(HttpStatusCode.OK, genBody?.ToJsonString());
        var closing = Amount(genBody!["statement"]!["closing"]);
        closing.ShouldBeGreaterThan(0m);

        // Validation: the proposal needs a known currency and period shape; nothing to settle is refused; a reader cannot propose.
        (await PostAsync(slice, "propose", new { reinsurerPartyId = lead, currency = "USD", period = "2026-06" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "propose", new { reinsurerPartyId = lead, currency = "EUR", period = "2026-13" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "propose", new { reinsurerPartyId = lead, currency = "EUR", period = "2025-01" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "propose", new { reinsurerPartyId = lead, currency = "EUR", period = "2026-06" }, "Staff.RecoverySpecialist", "rispec")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var proposal = await ProposeAsync(slice, lead, "2026-06");
        var settlementId = proposal["settlementId"]!.GetValue<string>();
        proposal["status"]!.GetValue<string>().ShouldBe("PROPOSED");
        proposal["direction"]!.GetValue<string>().ShouldBe("PAY");
        Amount(proposal["net"]).ShouldBe(closing);
        Amount(proposal["amount"]).ShouldBe(closing);
        Amount(proposal["statementClosing"]).ShouldBe(closing);
        proposal["statementId"]!.GetValue<string>().ShouldBe(genBody["statement"]!["statementId"]!.GetValue<string>());

        // One live proposal per reinsurer.
        (await PostAsync(slice, "propose", new { reinsurerPartyId = lead, currency = "EUR", period = "2026-06" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // SoD: not before approval; the proposer cannot approve even with the checker's role; the accountant role has no approve permission.
        (await PostAsync(slice, "release", new { settlementId })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (sod, sodBody) = await PostAsync(slice, "approve", new { settlementId }, Manager, "riacct");
        sod.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        sodBody.Text("code").ShouldBe("RI-ERR-SOD");
        (await PostAsync(slice, "approve", new { settlementId })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(slice, "approve", new { settlementId = Guid.NewGuid() }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var approved = await ApproveAsync(slice, settlementId);
        approved["status"]!.GetValue<string>().ShouldBe("APPROVED");
        approved["approvedBy"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await PostAsync(slice, "approve", new { settlementId }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The release is once: it posts the movement that brings the balance to zero and the second release is refused.
        var (released, releasedBody) = await PostAsync(slice, "release", new { settlementId });
        released.StatusCode.ShouldBe(HttpStatusCode.OK, releasedBody?.ToJsonString());
        var settled = releasedBody!["settlement"]!;
        settled["status"]!.GetValue<string>().ShouldBe("RELEASED");
        settled["itemId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        settled["bilReference"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await PostAsync(slice, "release", new { settlementId })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.account_item WHERE source_kind = 'SETTLEMENT' AND source_id = '{settlementId}'")).ShouldBe(1L);
        (await slice.ScalarAsync<decimal>($"SELECT amount FROM ri.account_item WHERE source_kind = 'SETTLEMENT' AND source_id = '{settlementId}'")).ShouldBe(-closing);

        // The balance of the settled period is zero now: another proposal through it has nothing to settle; a later period settles only what follows.
        (await PostAsync(slice, "propose", new { reinsurerPartyId = lead, currency = "EUR", period = "2026-06" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var rest = await ProposeAsync(slice, lead, "2026-12");
        Amount(rest["net"]).ShouldBeGreaterThan(0m);
        Amount(rest["net"]).ShouldBeLessThan(closing);

        // The list shows both, newest first, and filters by status.
        var (_, listed) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/settlements?reinsurerPartyId={lead:D}", roles: Accountant);
        listed!["items"]!.AsArray().Count.ShouldBe(2);
        var (_, onlyReleased) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/settlements?status=RELEASED", roles: Accountant);
        onlyReleased!["items"]!.AsArray().ShouldAllBe(i => i!["status"]!.GetValue<string>() == "RELEASED");
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/settlements?status=NOPE", roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A settlement is sealed: its net never changes and it is never deleted.
        foreach (var sql in new[] { $"UPDATE ri.settlement SET net = 1, amount = 1 WHERE settlement_id = '{settlementId}'", $"DELETE FROM ri.settlement WHERE settlement_id = '{settlementId}'" })
        {
            (await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync(sql))).SqlState.ShouldBe("42501");
        }
    }

    [Fact]
    public async Task Approval_is_limited_by_the_amount_authority_and_a_moved_balance_makes_the_settlement_stale_until_it_is_proposed_again()
    {
        var (slice, id, lead) = await NewContractWithItemsAsync(database);
        await using var _ = slice;

        // Above the manager's illustrative limit (EUR 2,000,000) the approval is refused.
        await AddItemAsync(slice, id, lead, "3000000.00");
        var big = await ProposeAsync(slice, lead, "2026-12");
        var bigId = big["settlementId"]!.GetValue<string>();
        Amount(big["net"]).ShouldBeGreaterThan(2000000m);
        var (denied, deniedBody) = await PostAsync(slice, "approve", new { settlementId = bigId }, Manager, "rimgr");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        deniedBody.Text("code").ShouldBe("RI-ERR-AUTHORITY");
        (await slice.ScalarAsync<string>($"SELECT status FROM ri.settlement WHERE settlement_id = '{bigId}'")).ShouldBe("PROPOSED");

        // The balance moves (a recovery receivable is booked): the old proposal is superseded by the next one, which is within the limit.
        await AddItemAsync(slice, id, lead, "-2990000.00", "RECOVERY");
        var proposal = await ProposeAsync(slice, lead, "2026-12");
        var settlementId = proposal["settlementId"]!.GetValue<string>();
        settlementId.ShouldNotBe(bigId);
        (await slice.ScalarAsync<string>($"SELECT status FROM ri.settlement WHERE settlement_id = '{bigId}'")).ShouldBe("SUPERSEDED");
        await ApproveAsync(slice, settlementId);

        // An item booked after the approval moves the balance again: the release refuses the stale net, and a new proposal replaces the approved one.
        await AddItemAsync(slice, id, lead, "5.00");
        var (stale, staleBody) = await PostAsync(slice, "release", new { settlementId });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.account_item WHERE source_kind = 'SETTLEMENT' AND source_id = '{settlementId}'")).ShouldBe(0L);
        var again = await ProposeAsync(slice, lead, "2026-12");
        again["settlementId"]!.GetValue<string>().ShouldNotBe(settlementId);
        (await slice.ScalarAsync<string>($"SELECT status FROM ri.settlement WHERE settlement_id = '{settlementId}'")).ShouldBe("SUPERSEDED");
    }

    [Fact]
    public async Task A_manual_movement_is_recorded_with_evidence_once_per_bank_reference_and_a_receivable_is_collected()
    {
        var (slice, id, lead) = await NewContractWithItemsAsync(database);
        await using var _ = slice;

        var proposal = await ProposeAsync(slice, lead, "2026-06");
        var settlementId = proposal["settlementId"]!.GetValue<string>();
        var amount = Amount(proposal["amount"]).ToString("F2", CultureInfo.InvariantCulture);

        // Only an approved settlement is recorded; the evidence and the exact amount are mandatory.
        (await PostAsync(slice, "record-manual", new { settlementId, amount = Money(amount), bankReference = "BANK-A", evidenceRef = "SWIFT-1" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        await ApproveAsync(slice, settlementId);
        (await PostAsync(slice, "record-manual", new { settlementId, amount = Money(amount), bankReference = "BANK-A", evidenceRef = "" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "record-manual", new { settlementId, amount = Money(amount), bankReference = "", evidenceRef = "SWIFT-1" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "record-manual", new { settlementId, amount = Money("1.00"), bankReference = "BANK-A", evidenceRef = "SWIFT-1" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "record-manual", new { settlementId, amount = Money(amount, "USD"), bankReference = "BANK-A", evidenceRef = "SWIFT-1" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        var (recorded, recordedBody) = await PostAsync(slice, "record-manual", new { settlementId, amount = Money(amount), bankReference = "BANK-A", evidenceRef = "SWIFT-1" });
        recorded.StatusCode.ShouldBe(HttpStatusCode.OK, recordedBody?.ToJsonString());
        var settlement = recordedBody!["settlement"]!;
        settlement["status"]!.GetValue<string>().ShouldBe("RECORDED");
        settlement["evidenceRef"]!.GetValue<string>().ShouldBe("SWIFT-1");
        settlement["bankReference"]!.GetValue<string>().ShouldBe("BANK-A");
        settlement["valueDate"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await PostAsync(slice, "record-manual", new { settlementId, amount = Money(amount), bankReference = "BANK-A", evidenceRef = "SWIFT-1" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "release", new { settlementId })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The same bank reference cannot settle another movement of the reinsurer.
        var next = await ProposeAsync(slice, lead, "2026-12");
        var nextId = next["settlementId"]!.GetValue<string>();
        await ApproveAsync(slice, nextId);
        (await PostAsync(slice, "record-manual", new { settlementId = nextId, amount = next["amount"], bankReference = "BANK-A", evidenceRef = "SWIFT-2" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);

        // A reinsurer that owes the cedant (a recovery receivable larger than the premium) is settled in the other direction; the unsettled proposal is superseded.
        await AddItemAsync(slice, id, lead, "-500000.00", "RECOVERY");
        var receive = await ProposeAsync(slice, lead, "2026-12");
        receive["direction"]!.GetValue<string>().ShouldBe("RECEIVE");
        Amount(receive["net"]).ShouldBeLessThan(0m);
        Amount(receive["amount"]).ShouldBe(-Amount(receive["net"]));
        var receiveId = receive["settlementId"]!.GetValue<string>();
        await ApproveAsync(slice, receiveId);
        var (done, doneBody) = await PostAsync(slice, "release", new { settlementId = receiveId });
        done.StatusCode.ShouldBe(HttpStatusCode.OK, doneBody?.ToJsonString());
        (await slice.ScalarAsync<decimal>($"SELECT amount FROM ri.account_item WHERE source_kind = 'SETTLEMENT' AND source_id = '{receiveId}'")).ShouldBe(Amount(receive["amount"]));
    }
}
