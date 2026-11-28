using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Cession;

/// <summary>
/// BO-RI-07: the cession preview (a dry run, writes nothing), the rerun of a policy (idempotent, only deltas) and the exception workflow
/// (assign, resolve with a reason, rerun with linked deltas, a resolved exception is final, reinsurance roles only). Synthetic treaty and amounts.
/// </summary>
public sealed class CessionRerunTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private sealed record Setup(RegistrySlice Slice, string LegalEntity, string Coverage, string TreatyId);

    private async Task<Setup> NewAsync(bool withTreaty = true)
    {
        var slice = new RegistrySlice(database);
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var (id, _) = await slice.ApprovedAsync(Body(NewProduct(), "FIRE", lead, follow));
        var legalEntity = (await slice.ScalarAsync<Guid>($"SELECT legal_entity_id FROM ri.contract WHERE contract_id = '{id}'")).ToString();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var setup = new Setup(slice, legalEntity, "COV_" + suffix, "QS-" + suffix);
        if (withTreaty)
        {
            await AddTreatyAsync(setup);
        }

        return setup;
    }

    private static Task AddTreatyAsync(Setup s) => s.Slice.ExecuteAsync(
        "INSERT INTO ri.cession_treaty (treaty_version_id, legal_entity_id, treaty_id, kind, contract_year, share_pct, cession_base, currency, coverage_codes, valid_from, valid_to, status, source, known_from) " +
        $"VALUES (gen_random_uuid(), '{s.LegalEntity}', '{s.TreatyId}', 'QUOTA_SHARE', 2026, 30, 'NET', 'EUR', ARRAY['{s.Coverage}'], '2026-01-01', '2027-01-01', 'APPROVED', 'REGISTRY', now());");

    private static Task AddLineAsync(Setup s, Guid policy, Guid term, string amount, string? coverage = null) => s.Slice.ExecuteAsync(
        "INSERT INTO ri.premium_delta (event_id, legal_entity_id, policy_id, term_id, coverage_code, charge_category, amount, valid_from, set_size, recorded_at) " +
        $"VALUES (gen_random_uuid(), '{s.LegalEntity}', '{policy}', '{term}', '{coverage ?? s.Coverage}', 'PREMIUM', {amount}, '2026-03-01', 1, now());");

    /// <summary>An exception that holds its premium line (as the intake now records it).</summary>
    private static async Task<Guid> AddExceptionAsync(Setup s, Guid policy, Guid term, string amount, string? coverage = null)
    {
        var id = Guid.CreateVersion7();
        await s.Slice.ExecuteAsync(
            "INSERT INTO ri.cession_exception (exception_id, legal_entity_id, term_id, source_event_id, code, detail, recorded_at, policy_id, coverage_code, charge_category, amount, currency, valid_from, set_size) " +
            $"VALUES ('{id}', '{s.LegalEntity}', '{term}', gen_random_uuid(), 'CESSION_NO_TREATY', 'No treaty.', now(), '{policy}', '{coverage ?? s.Coverage}', 'PREMIUM', {amount}, 'EUR', '2026-03-01', 1);");
        return id;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(Setup s, string path, object body, string roles = Accountant, string user = "riacct") =>
        s.Slice.SendAsync(HttpMethod.Post, "/api/ri/v1/" + path, body, roles, user);

    private static Task<long> CountAsync(Setup s, Guid term) => s.Slice.ScalarAsync<long>($"SELECT count(*) FROM ri.cession WHERE term_id = '{term}'");

    [Fact]
    public async Task Preview_shows_target_and_gaps_and_writes_nothing()
    {
        var s = await NewAsync();
        await using var _ = s.Slice;
        var policy = Guid.NewGuid();
        var term = Guid.NewGuid();
        await AddLineAsync(s, policy, term, "1000.00");

        var (response, body) = await PostAsync(s, "cessions/preview", new { riskDescription = new { policyId = policy } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var share = body!["shares"]!.AsArray().Single()!;
        share["treatyId"]!.GetValue<string>().ShouldBe(s.TreatyId);
        share["target"]!.GetValue<string>().ShouldBe("300.00");
        share["delta"]!.GetValue<string>().ShouldBe("300.00");
        body["capacity"]!["toBook"]!.GetValue<string>().ShouldBe("300.00");

        // A hypothetical extra line on the same term and one for a coverage no treaty knows (a gap).
        var (_, hypo) = await PostAsync(s, "cessions/preview", new
        {
            riskDescription = new { termId = term, lines = new object[] { new { coverageCode = s.Coverage, amount = "500.00", validFrom = "2026-04-01" }, new { coverageCode = "UNKNOWN_COV", amount = "10", validFrom = "2026-04-01" } } },
        });
        hypo!["shares"]!.AsArray().Single()!["target"]!.GetValue<string>().ShouldBe("450.00");
        hypo["gaps"]!.AsArray().Single()!["code"]!.GetValue<string>().ShouldBe("CESSION_NO_TREATY");

        (await CountAsync(s, term)).ShouldBe(0L);
        (await s.Slice.ScalarAsync<long>($"SELECT count(*) FROM ri.premium_delta WHERE term_id = '{term}'")).ShouldBe(1L);

        (await PostAsync(s, "cessions/preview", new { riskDescription = new { } })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(s, "cessions/preview", new { riskDescription = new { policyId = policy } }, ClaimsHandler, "clh")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rerun_writes_only_the_delta_once_and_a_second_run_changes_nothing()
    {
        var s = await NewAsync();
        await using var _ = s.Slice;
        var policy = Guid.NewGuid();
        var term = Guid.NewGuid();
        await AddLineAsync(s, policy, term, "1000.00");

        (await PostAsync(s, "cessions/rerun", new { scope = new { policyId = policy } })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // a reason is required
        (await PostAsync(s, "cessions/rerun", new { scope = new { policyId = Guid.NewGuid() }, reason = "missing" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(s, "cessions/rerun", new { scope = new { policyId = policy }, reason = "no" }, ClaimsHandler, "clh")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (first, firstBody) = await PostAsync(s, "cessions/rerun", new { scope = new { policyId = policy }, reason = "Treaty approved late." });
        first.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted);
        firstBody!["runId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await CountAsync(s, term)).ShouldBe(1L);
        (await s.Slice.ScalarAsync<decimal>($"SELECT sum(delta) FROM ri.cession WHERE term_id = '{term}'")).ShouldBe(300m);

        // The ledger grows by 200; the rerun books only the 60 that is missing, and the next one nothing.
        await AddLineAsync(s, policy, term, "200.00");
        await PostAsync(s, "cessions/rerun", new { scope = new { termId = term }, reason = "Late line." });
        await PostAsync(s, "cessions/rerun", new { scope = new { termId = term }, reason = "Again." });
        (await CountAsync(s, term)).ShouldBe(2L);
        (await s.Slice.ScalarAsync<decimal>($"SELECT sum(delta) FROM ri.cession WHERE term_id = '{term}'")).ShouldBe(360m);
        (await s.Slice.ScalarAsync<long>($"SELECT count(*) FROM ri.cession WHERE term_id = '{term}' AND run_id IS NOT NULL")).ShouldBe(2L);
    }

    [Fact]
    public async Task An_exception_rerun_replays_the_held_line_links_its_deltas_and_a_resolved_exception_is_final()
    {
        var s = await NewAsync(withTreaty: false);
        await using var _ = s.Slice;
        var policy = Guid.NewGuid();
        var term = Guid.NewGuid();
        var exception = await AddExceptionAsync(s, policy, term, "1000.00");

        // Still no treaty: the rerun reports the failure and the exception stays open, with nothing booked.
        var (stuck, stuckBody) = await PostAsync(s, "cession-exceptions/rerun", new { filters = new { exceptionId = exception } });
        stuck.StatusCode.ShouldBe(HttpStatusCode.OK, stuckBody?.ToJsonString());
        stuckBody!["exception"]!["status"]!.GetValue<string>().ShouldBe("OPEN");
        stuckBody["exception"]!["rerunOutcome"]!["code"]!.GetValue<string>().ShouldBe("CESSION_NO_TREATY");
        (await CountAsync(s, term)).ShouldBe(0L);

        // Assign, then register the treaty and rerun: the held line enters the ledger, the delta is booked and linked, and the exception resolves.
        var (assigned, assignedBody) = await PostAsync(s, "cession-exceptions/assign", new { filters = new { exceptionId = exception }, action = new { assignee = "riacct" } });
        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, assignedBody?.ToJsonString());
        assignedBody!["exception"]!["status"]!.GetValue<string>().ShouldBe("ASSIGNED");
        await AddTreatyAsync(s);
        var (done, doneBody) = await PostAsync(s, "cession-exceptions/rerun", new { filters = new { exceptionId = exception }, reason = "Treaty registered." });
        done.StatusCode.ShouldBe(HttpStatusCode.OK, doneBody?.ToJsonString());
        doneBody!["exception"]!["status"]!.GetValue<string>().ShouldBe("RESOLVED");
        doneBody["exception"]!["resolution"]!.GetValue<string>().ShouldBe("RERUN");
        doneBody["deltas"]!.AsArray().Single()!["delta"]!.GetValue<string>().ShouldBe("300.00");
        (await s.Slice.ScalarAsync<decimal>($"SELECT sum(delta) FROM ri.cession WHERE term_id = '{term}'")).ShouldBe(300m);

        // The list links the deltas; a resolved exception can be neither rerun, resolved nor assigned again.
        var (_, list) = await s.Slice.SendAsync(HttpMethod.Get, "/api/ri/v1/cession-exceptions?limit=200");
        var item = list!["items"]!.AsArray().Single(i => i!["exception"]!["exceptionId"]!.GetValue<string>() == exception.ToString())!;
        item["deltas"]!.AsArray().Count.ShouldBe(1);
        item["exception"]!["status"]!.GetValue<string>().ShouldBe("RESOLVED");
        foreach (var path in new[] { "rerun", "resolve", "assign" })
        {
            var (response, _) = await PostAsync(s, "cession-exceptions/" + path, new { filters = new { exceptionId = exception }, action = new { assignee = "x" }, reason = "again" });
            ((int)response.StatusCode).ShouldBeOneOf(409, 422);
        }

        (await CountAsync(s, term)).ShouldBe(1L);
    }

    [Fact]
    public async Task Resolve_needs_a_reason_assign_is_limited_to_reinsurance_roles_and_unknown_ids_are_not_found()
    {
        var s = await NewAsync();
        await using var _ = s.Slice;
        var exception = await AddExceptionAsync(s, Guid.NewGuid(), Guid.NewGuid(), "10.00", "OTHER_COV");
        var filters = new { exceptionId = exception };

        (await PostAsync(s, "cession-exceptions/resolve", new { filters })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(s, "cession-exceptions/resolve", new { filters, reason = "  " })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(s, "cession-exceptions/assign", new { filters, action = new { } })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        foreach (var role in new[] { ClaimsHandler, "Staff.RecoverySpecialist", "Staff.Underwriter" })
        {
            (await PostAsync(s, "cession-exceptions/assign", new { filters, action = new { assignee = "riacct" } }, role, "other")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, role);
        }

        (await PostAsync(s, "cession-exceptions/assign", new { filters = new { exceptionId = Guid.NewGuid() }, action = new { assignee = "riacct" } })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (ok, body) = await PostAsync(s, "cession-exceptions/resolve", new { filters, reason = "Not a premium line of ours." }, Manager, "rimgr");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["exception"]!["status"]!.GetValue<string>().ShouldBe("RESOLVED");
        body["exception"]!["resolution"]!.GetValue<string>().ShouldBe("MANUAL");
    }
}
