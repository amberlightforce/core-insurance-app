using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Accounting;

/// <summary>
/// BO-RI-14 reinsurer statements: built from the technical account items (the closing equals the balance report), sealed with a content hash,
/// maker-checker, answered by agree or dispute with a reason, and reissued as a new version after a reversal.
/// </summary>
public sealed class StatementTests(PostgresFixture database) : IClassFixture<PostgresFixture>
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
        slice.SendAsync(HttpMethod.Post, "/api/ri/v1/statements/" + action, body, roles, user);

    private static async Task<JsonNode> GenerateAsync(RegistrySlice slice, Guid party, string period)
    {
        var (response, body) = await PostAsync(slice, "generate", new { reinsurerPartyId = party, period });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["statement"]!;
    }

    private static async Task<decimal> BalanceClosingAsync(RegistrySlice slice, string contractId, Guid party, string period)
    {
        var (_, report) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/balances/report", new { contractId, reinsurerPartyId = party, periodTo = period }, Accountant, "riacct");
        return Amount(report!["balances"]!.AsArray().Last(l => l!["period"]!.GetValue<string>() == period)!["closing"]);
    }

    [Fact]
    public async Task A_statement_lists_the_period_items_opens_with_the_earlier_balance_and_agrees_with_the_balance_report()
    {
        var (slice, id, lead) = await NewContractWithItemsAsync(database);
        await using var _ = slice;

        var statement = await GenerateAsync(slice, lead, "2026-06");
        statement["status"]!.GetValue<string>().ShouldBe("DRAFT");
        statement["version"]!.GetValue<int>().ShouldBe(1);
        statement["contentHash"]!.GetValue<string>().Length.ShouldBe(64);
        var lines = statement["lines"]!.AsArray();
        lines.Count.ShouldBe(1); // the second instalment, due 2026-06-30
        var opening = Amount(statement["opening"]);
        opening.ShouldBeGreaterThan(0m); // the first instalment of the 60% line
        Amount(statement["movement"]).ShouldBe(Amount(lines[0]!["amount"]));
        Amount(statement["closing"]).ShouldBe(opening + Amount(statement["movement"]));
        Amount(statement["closing"]).ShouldBe(await BalanceClosingAsync(slice, id, lead, "2026-06"));

        // One live statement per reinsurer and period; a period without items has nothing to state.
        (await PostAsync(slice, "generate", new { reinsurerPartyId = lead, period = "2026-06" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "generate", new { reinsurerPartyId = lead, period = "2025-01" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "generate", new { reinsurerPartyId = lead, period = "2026-13" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(slice, "generate", new { reinsurerPartyId = lead, period = "2026-06" }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Maker_checker_then_issue_and_the_reinsurer_agrees_with_a_reason_once()
    {
        var (slice, _, lead) = await NewContractWithItemsAsync(database);
        await using var _ = slice;
        var statementId = (await GenerateAsync(slice, lead, "2026-03"))["statementId"]!.GetValue<string>();

        // The order is fixed: submit, approve, issue, answer.
        (await PostAsync(slice, "approve", new { statementId }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "issue", new { statementId })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "submit", new { statementId })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The maker cannot approve, even with the checker's role; an unknown statement is not found.
        var (sod, sodBody) = await PostAsync(slice, "approve", new { statementId }, Manager, "riacct");
        sod.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        sodBody.Text("code").ShouldBe("RI-ERR-SOD");
        (await PostAsync(slice, "approve", new { statementId = Guid.NewGuid() }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (approved, approvedBody) = await PostAsync(slice, "approve", new { statementId }, Manager, "rimgr");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody.Text("statement.status").ShouldBe("APPROVED");
        approvedBody.Text("statement.approvedBy").ShouldNotBeNullOrEmpty();

        var (issued, issuedBody) = await PostAsync(slice, "issue", new { statementId }, Manager, "rimgr");
        issued.StatusCode.ShouldBe(HttpStatusCode.OK, issuedBody?.ToJsonString());
        issuedBody.Text("statement.status").ShouldBe("ISSUED");
        (await PostAsync(slice, "issue", new { statementId }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The answer needs a reason and is recorded once.
        (await PostAsync(slice, "record-response", new { statementId, response = "AGREED", reason = "" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (agreed, agreedBody) = await PostAsync(slice, "record-response", new { statementId, response = "AGREED", reason = "Reconciled with the reinsurer's ledger" });
        agreed.StatusCode.ShouldBe(HttpStatusCode.OK, agreedBody?.ToJsonString());
        agreedBody.Text("statement.status").ShouldBe("AGREED");
        agreedBody.Text("statement.responseReason").ShouldBe("Reconciled with the reinsurer's ledger");
        (await PostAsync(slice, "record-response", new { statementId, response = "DISPUTED", reason = "Changed our mind" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // An agreed statement is final: no reissue.
        (await PostAsync(slice, "reissue", new { statementId, reason = "Again" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_dispute_is_answered_by_a_reversal_and_a_reissue_that_supersedes_the_statement_and_the_content_is_sealed()
    {
        var (slice, id, lead) = await NewContractWithItemsAsync(database);
        await using var _ = slice;

        // A recovery booked now lands in the current period, where a later reversal lands too.
        var batch = Guid.NewGuid();
        await slice.ExecuteAsync(
            "INSERT INTO ri.recovery_batch (batch_id, legal_entity_id, contract_id, contract_year, claim_id, source_event_id, recorded_at, calc_hash, engine_version, contract_version, trace) " +
            $"SELECT '{batch}', legal_entity_id, contract_id, contract_year, gen_random_uuid(), gen_random_uuid(), now(), 'h', 'e', '1', '[]' FROM ri.contract WHERE contract_id = '{id}'");
        await slice.ExecuteAsync(
            "INSERT INTO ri.recovery (recovery_id, batch_id, legal_entity_id, contract_id, contract_year, claim_id, layer_id, layer_no, participant_party_id, incurred_delta, paid_delta, outstanding_delta, state, sii_lob, recorded_at) " +
            $"SELECT gen_random_uuid(), '{batch}', legal_entity_id, contract_id, contract_year, gen_random_uuid(), gen_random_uuid(), 1, '{lead}', 7000.00, 7000.00, 0, 'CALCULATED', 'UNMAPPED', now() FROM ri.contract WHERE contract_id = '{id}'");
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/generate", new { contractId = id }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, recoveries) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/items?contractId={id}&kind=RECOVERY", roles: Accountant);
        var item = recoveries!["items"]![0]!["item"]!;
        var period = item["period"]!.GetValue<string>();

        var first = await GenerateAsync(slice, lead, period);
        var firstId = first["statementId"]!.GetValue<string>();
        Amount(first["closing"]).ShouldBe(await BalanceClosingAsync(slice, id, lead, period));
        (await PostAsync(slice, "submit", new { statementId = firstId })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(slice, "approve", new { statementId = firstId }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(slice, "issue", new { statementId = firstId }, Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Nothing to reissue until the items change; a statement not yet issued cannot be reissued either.
        (await PostAsync(slice, "reissue", new { statementId = firstId, reason = "Check" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (disputed, disputedBody) = await PostAsync(slice, "record-response", new { statementId = firstId, response = "DISPUTED", reason = "The recovery belongs to another claim" });
        disputed.StatusCode.ShouldBe(HttpStatusCode.OK, disputedBody?.ToJsonString());
        disputedBody.Text("statement.status").ShouldBe("DISPUTED");

        // The dispute is answered in the account: a reversal of the item, then a reissue.
        var itemId = item["itemId"]!.GetValue<string>();
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/items/reverse", new { itemId, reason = "Wrong claim" }, Accountant, "riacct")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(slice, "reissue", new { statementId = firstId, reason = "" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (reissued, reissuedBody) = await PostAsync(slice, "reissue", new { statementId = firstId, reason = "Recovery reversed after the dispute" });
        reissued.StatusCode.ShouldBe(HttpStatusCode.OK, reissuedBody?.ToJsonString());
        var second = reissuedBody!["statement"]!;
        second["version"]!.GetValue<int>().ShouldBe(2);
        second["previousStatementId"]!.GetValue<string>().ShouldBe(firstId);
        second["status"]!.GetValue<string>().ShouldBe("DRAFT");
        second["contentHash"]!.GetValue<string>().ShouldNotBe(first["contentHash"]!.GetValue<string>());
        // The reversal is posted in the current period: a line of this statement, or (when the clock is earlier) part of its opening balance.
        second["lines"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(first["lines"]!.AsArray().Count);
        Amount(second["closing"]).ShouldBe(await BalanceClosingAsync(slice, id, lead, period));
        Amount(second["closing"]).ShouldBe(Amount(first["closing"]) + 7000m); // the recovery payable to the cedant is netted away

        // The first version stays as issued, superseded by the second; it cannot be reissued again.
        (await slice.ScalarAsync<string>($"SELECT status FROM ri.statement WHERE statement_id = '{firstId}'")).ShouldBe("SUPERSEDED");
        (await slice.ScalarAsync<string>($"SELECT superseded_by::text FROM ri.statement WHERE statement_id = '{firstId}'")).ShouldBe(second["statementId"]!.GetValue<string>());
        (await PostAsync(slice, "reissue", new { statementId = firstId, reason = "Again" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(slice, "generate", new { reinsurerPartyId = lead, period })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The content is sealed: figures and lines cannot change, nor can a statement be deleted.
        foreach (var sql in new[]
        {
            $"UPDATE ri.statement SET closing = 1 WHERE statement_id = '{firstId}'",
            $"UPDATE ri.statement SET content_hash = repeat('a', 64) WHERE statement_id = '{firstId}'",
            $"DELETE FROM ri.statement WHERE statement_id = '{firstId}'",
            $"UPDATE ri.statement_line SET amount = 1 WHERE statement_id = '{firstId}'",
            $"DELETE FROM ri.statement_line WHERE statement_id = '{firstId}'",
            "TRUNCATE ri.statement_line",
        })
        {
            (await Should.ThrowAsync<PostgresException>(() => slice.ExecuteAsync(sql))).SqlState.ShouldBe("42501");
        }
    }
}
