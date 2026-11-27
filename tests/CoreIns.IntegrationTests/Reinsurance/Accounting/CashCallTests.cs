using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Accounting;

/// <summary>
/// BO-RI-15 cash calls: raised when the paid recoveries of a claim exceed the threshold (split per reinsurer, idempotent on the claim), sent, settled by
/// receipts that are idempotent on the bank reference (a partial receipt keeps the call open), and cancelled under maker-checker before any cash arrived.
/// </summary>
public sealed class CashCallTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static async Task<(RegistrySlice Slice, string ContractId, Guid Lead, Guid Follow)> NewContractAsync(PostgresFixture database)
    {
        var slice = new RegistrySlice(database);
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, _) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        return (slice, id, Guid.Parse(lead), Guid.Parse(follow));
    }

    /// <summary>Books paid recoveries of one claim (lead and follow) and generates their account items; returns the claim id.</summary>
    private static async Task<Guid> RecoveredClaimAsync(RegistrySlice slice, string contractId, Guid lead, Guid follow, decimal leadPaid, decimal followPaid)
    {
        var claim = Guid.NewGuid();
        var batch = Guid.NewGuid();
        await slice.ExecuteAsync(
            "INSERT INTO ri.recovery_batch (batch_id, legal_entity_id, contract_id, contract_year, claim_id, source_event_id, recorded_at, calc_hash, engine_version, contract_version, trace) " +
            $"SELECT '{batch}', legal_entity_id, contract_id, contract_year, '{claim}', gen_random_uuid(), now(), 'h', 'e', '1', '[]' FROM ri.contract WHERE contract_id = '{contractId}'");
        foreach (var (party, paid) in new[] { (lead, leadPaid), (follow, followPaid) })
        {
            await slice.ExecuteAsync(
                "INSERT INTO ri.recovery (recovery_id, batch_id, legal_entity_id, contract_id, contract_year, claim_id, layer_id, layer_no, participant_party_id, incurred_delta, paid_delta, outstanding_delta, state, sii_lob, recorded_at) " +
                $"SELECT gen_random_uuid(), '{batch}', legal_entity_id, contract_id, contract_year, '{claim}', gen_random_uuid(), 1, '{party}', {paid.ToString(CultureInfo.InvariantCulture)}, {paid.ToString(CultureInfo.InvariantCulture)}, 0, 'CALCULATED', 'UNMAPPED', now() FROM ri.contract WHERE contract_id = '{contractId}'");
        }

        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/generate", new { contractId }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return claim;
    }

    private static decimal Amount(JsonNode? money) => decimal.Parse(money!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(RegistrySlice slice, string action, object body, string roles = Accountant, string user = "riacct") =>
        slice.SendAsync(HttpMethod.Post, "/api/ri/v1/cash-calls/" + action, body, roles, user);

    private static async Task<JsonNode> RaiseAsync(RegistrySlice slice, string contractId, Guid claim)
    {
        var (response, body) = await PostAsync(slice, "raise", new { contractId, occurrence = claim.ToString("D") });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    [Fact]
    public async Task A_call_is_raised_above_the_threshold_split_per_reinsurer_and_raising_it_again_returns_the_same_call()
    {
        var (slice, id, lead, follow) = await NewContractAsync(database);
        await using var _ = slice;

        // Below the threshold (default 50 000.00) nothing is called; an occurrence that is not a claim id is refused.
        var small = await RecoveredClaimAsync(slice, id, lead, follow, 6000m, 4000m);
        (await PostAsync(slice, "raise", new { contractId = id, occurrence = small.ToString("D") })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "raise", new { contractId = id, occurrence = "not-a-claim" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "raise", new { contractId = id, occurrence = Guid.NewGuid().ToString("D") })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "raise", new { contractId = Guid.NewGuid(), occurrence = small.ToString("D") })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var claim = await RecoveredClaimAsync(slice, id, lead, follow, 60000.01m, 40000.00m);
        var raised = await RaiseAsync(slice, id, claim);
        raised["replayed"]!.GetValue<bool>().ShouldBeFalse();
        var call = raised["cashCall"]!;
        call["status"]!.GetValue<string>().ShouldBe("RAISED");
        Amount(call["total"]).ShouldBe(100000.01m);
        Amount(call["threshold"]).ShouldBe(50000m);
        Amount(call["outstanding"]).ShouldBe(100000.01m);
        var lines = call["lines"]!.AsArray();
        lines.Count.ShouldBe(2);
        lines.Sum(l => Amount(l!["amount"])).ShouldBe(Amount(call["total"]));
        lines.Single(l => l!["reinsurerPartyId"]!.GetValue<string>() == lead.ToString("D"))!["amount"]!["amount"]!.GetValue<string>().ShouldBe("60000.01");

        // Same claim again: the live call, once.
        var again = await RaiseAsync(slice, id, claim);
        again["replayed"]!.GetValue<bool>().ShouldBeTrue();
        again["cashCall"]!["cashCallId"]!.GetValue<string>().ShouldBe(call["cashCallId"]!.GetValue<string>());
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.cash_call WHERE contract_id = '{id}'")).ShouldBe(1L);

        // The accountant and manager may raise; a reader may not.
        (await PostAsync(slice, "raise", new { contractId = id, occurrence = claim.ToString("D") }, "Staff.RecoverySpecialist", "rispec")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Receipts_are_idempotent_on_the_bank_reference_a_partial_receipt_keeps_the_call_open_and_cash_posts_to_the_account()
    {
        var (slice, id, lead, follow) = await NewContractAsync(database);
        await using var _ = slice;
        var claim = await RecoveredClaimAsync(slice, id, lead, follow, 60000m, 40000m);
        var callId = (await RaiseAsync(slice, id, claim))["cashCall"]!["cashCallId"]!.GetValue<string>();

        // Cash is received only on a sent call.
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("1000.00"), bankReference = "BANK-0" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (sent, sentBody) = await PostAsync(slice, "issue", new { cashCallId = callId });
        sent.StatusCode.ShouldBe(HttpStatusCode.OK, sentBody?.ToJsonString());
        sentBody.Text("cashCall.status").ShouldBe("SENT");
        (await PostAsync(slice, "issue", new { cashCallId = callId })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // A partial receipt: the call stays open, PARTIALLY_SETTLED, the reinsurer still owes the rest.
        var (partial, partialBody) = await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("25000.00"), bankReference = "BANK-1" });
        partial.StatusCode.ShouldBe(HttpStatusCode.OK, partialBody?.ToJsonString());
        partialBody.Text("cashCall.status").ShouldBe("PARTIALLY_SETTLED");
        Amount(partialBody!["cashCall"]!["received"]).ShouldBe(25000m);
        Amount(partialBody["cashCall"]!["outstanding"]).ShouldBe(75000m);
        partialBody["replayed"]!.GetValue<bool>().ShouldBeFalse();

        // The same bank reference again books nothing; the same reference with another amount is refused.
        var (replay, replayBody) = await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("25000.00"), bankReference = "BANK-1" });
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, replayBody?.ToJsonString());
        replayBody!["replayed"]!.GetValue<bool>().ShouldBeTrue();
        Amount(replayBody["cashCall"]!["received"]).ShouldBe(25000m);
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("100.00"), bankReference = "BANK-1" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.cash_call_receipt WHERE cash_call_id = '{callId}'")).ShouldBe(1L);

        // More than the reinsurer owes, a reinsurer outside the call, a wrong currency and a zero amount are refused.
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("35000.01"), bankReference = "BANK-2" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = Guid.NewGuid(), amount = Money("10.00"), bankReference = "BANK-3" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("10.00", "USD"), bankReference = "BANK-4" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("0.00"), bankReference = "BANK-5" })).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        // Cash arrived: the call cannot be cancelled any more.
        (await PostAsync(slice, "cancel", new { cashCallId = callId, reason = "No longer needed" }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The rest settles the call.
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = lead, amount = Money("35000.00"), bankReference = "BANK-6" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (last, lastBody) = await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = follow, amount = Money("40000.00"), bankReference = "BANK-1" });
        last.StatusCode.ShouldBe(HttpStatusCode.OK, lastBody?.ToJsonString()); // the same reference of another reinsurer is another payment
        lastBody.Text("cashCall.status").ShouldBe("SETTLED");
        Amount(lastBody!["cashCall"]!["outstanding"]).ShouldBe(0m);
        lastBody["cashCall"]!["agingBucket"]!.GetValue<string>().ShouldBe("CLOSED");
        (await PostAsync(slice, "record-receipt", new { cashCallId = callId, reinsurerPartyId = follow, amount = Money("1.00"), bankReference = "BANK-7" })).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The cash is in the technical account: the positive items net the recovery receivable of the reinsurer.
        var (_, items) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/items?contractId={id}&reinsurerPartyId={lead:D}&kind=RECOVERY", roles: Accountant);
        var amounts = items!["items"]!.AsArray().Select(i => Amount(i!["item"]!["amount"])).ToList();
        amounts.Sum().ShouldBe(0m); // -60 000 recovery receivable + 25 000 + 35 000 received
        items["items"]!.AsArray().Count(i => i!["item"]!["sourceKind"]!.GetValue<string>() == "CASH_CALL_RECEIPT").ShouldBe(2);

        // The receipts are sealed.
        foreach (var sql in new[]
        {
            $"UPDATE ri.cash_call_receipt SET amount = 1 WHERE cash_call_id = '{callId}'",
            $"DELETE FROM ri.cash_call_receipt WHERE cash_call_id = '{callId}'",
            $"UPDATE ri.cash_call SET total = 1 WHERE cash_call_id = '{callId}'",
            $"DELETE FROM ri.cash_call WHERE cash_call_id = '{callId}'",
        })
        {
            (await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync(sql))).SqlState.ShouldBe("42501");
        }
    }

    [Fact]
    public async Task Cancel_is_maker_checker_needs_a_reason_and_frees_the_claim_for_a_new_call_and_the_list_shows_the_aging()
    {
        var (slice, id, lead, follow) = await NewContractAsync(database);
        await using var _ = slice;
        var claim = await RecoveredClaimAsync(slice, id, lead, follow, 70000m, 30000m);
        var callId = (await RaiseAsync(slice, id, claim))["cashCall"]!["cashCallId"]!.GetValue<string>();

        // The raiser cannot cancel, even with the checker's role; the checker needs a reason.
        var (sod, sodBody) = await PostAsync(slice, "cancel", new { cashCallId = callId, reason = "Raised in error" }, Manager, "riacct");
        sod.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        sodBody.Text("code").ShouldBe("RI-ERR-SOD");
        (await PostAsync(slice, "cancel", new { cashCallId = callId, reason = "" }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "cancel", new { cashCallId = Guid.NewGuid(), reason = "Unknown" }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(slice, "cancel", new { cashCallId = callId, reason = "Raised in error" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // the accountant has no cancel permission

        // A call that is open and overdue shows in the aging (the default due date is 30 days out; move it 75 days back: 45 days overdue).
        await slice.ExecuteAsync($"ALTER TABLE ri.cash_call DISABLE TRIGGER tr_cash_call_guard; UPDATE ri.cash_call SET due_date = due_date - 75 WHERE cash_call_id = '{callId}'; ALTER TABLE ri.cash_call ENABLE TRIGGER tr_cash_call_guard");
        var (_, listed) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cash-calls?contractId={id}&openOnly=true", roles: Accountant);
        var item = listed!["items"]!.AsArray().Single()!;
        item["agingBucket"]!.GetValue<string>().ShouldBe("D31_60");
        item["daysOverdue"]!.GetValue<int>().ShouldBe(45);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/cash-calls?status=NOPE", roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (cancelled, cancelledBody) = await PostAsync(slice, "cancel", new { cashCallId = callId, reason = "Raised in error" }, Manager, "rimgr");
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, cancelledBody?.ToJsonString());
        cancelledBody.Text("cashCall.status").ShouldBe("CANCELLED");
        cancelledBody.Text("cashCall.cancelReason").ShouldBe("Raised in error");
        (await PostAsync(slice, "cancel", new { cashCallId = callId, reason = "Again" }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "issue", new { cashCallId = callId })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The claim can be called again; the cancelled call stays, no longer open.
        var second = await RaiseAsync(slice, id, claim);
        second["replayed"]!.GetValue<bool>().ShouldBeFalse();
        second["cashCall"]!["cashCallId"]!.GetValue<string>().ShouldNotBe(callId);
        var (_, open) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cash-calls?contractId={id}&openOnly=true", roles: Accountant);
        open!["items"]!.AsArray().Count.ShouldBe(1);
        var (_, all) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/cash-calls?contractId={id}", roles: Accountant);
        all!["items"]!.AsArray().Count.ShouldBe(2);
    }
}
