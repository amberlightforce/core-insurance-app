using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>
/// BO-RI-05 participations (add, replace as effective-dated endorsements), security rules, the exposure report and line tables.
/// Security rule sets are per legal entity and shared by the tests of this class, so the tests that set rules take effect from
/// September 2026 and the others change their panels in February.
/// </summary>
public sealed class ParticipationTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Early = "2026-02-20";

    private static async Task<(RegistrySlice Slice, string Lead, string Follow, string Third)> NewSliceAsync(PostgresFixture database)
    {
        var slice = new RegistrySlice(database);
        string Name(string label) => $"Synthetic {label} Re " + Guid.NewGuid().ToString("N")[..6];
        return (slice, await slice.OrganisationAsync(Name("Lead")), await slice.OrganisationAsync(Name("Follow")), await slice.OrganisationAsync(Name("Third")));
    }

    private static decimal Pct(JsonNode? body, int index) => decimal.Parse(body.Text($"contract.participations.{index}.signedLinePct"), CultureInfo.InvariantCulture);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> AddAsync(
        RegistrySlice slice, string id, int recordVersion, string effectiveFrom, JsonArray lines, bool? rebalance = null) =>
        slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/participations/add",
            new JsonObject { ["contractId"] = id, ["expectedRecordVersion"] = recordVersion, ["effectiveFrom"] = effectiveFrom, ["reason"] = "Capacity added", ["lines"] = lines, ["rebalance"] = rebalance },
            Accountant);

    private static JsonObject Line(string reinsurer, string pct, bool lead = false) => new() { ["reinsurerPartyId"] = reinsurer, ["signedLinePct"] = pct, ["lead"] = lead };

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(RegistrySlice slice, string id, int versionNo) =>
        slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/approve-endorsement", new { contractId = id, versionNo, decision = "APPROVE" }, Manager, "rimgr");

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> VersionAtAsync(RegistrySlice slice, string id, string validAt) =>
        slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/version-at?contractId={id}&validAt={validAt}", roles: Accountant);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> SetRulesAsync(
        RegistrySlice slice, string effectiveFrom, string? floor, string? maxShare, params (string Reinsurer, string Rating)[] ratings) =>
        slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/security-rules/set",
            new JsonObject
            {
                ["effectiveFrom"] = effectiveFrom, ["ratingFloor"] = floor, ["maxSharePct"] = maxShare,
                ["ratings"] = new JsonArray([.. ratings.Select(r => (JsonNode?)new JsonObject { ["reinsurerPartyId"] = r.Reinsurer, ["rating"] = r.Rating })]),
            },
            Manager, "rimgr");

    [Fact]
    public async Task Add_rebalances_the_existing_lines_to_the_placed_share_with_largest_remainder_and_keeps_the_history()
    {
        var (slice, lead, follow, third) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);

        var (added, pending) = await AddAsync(slice, id, recordVersion, Early, new JsonArray(Line(third, "33.333333")));
        added.StatusCode.ShouldBe(HttpStatusCode.OK, pending?.ToJsonString());
        pending.Text("contract.versionNo").ShouldBe("2");

        // 100 - 33.333333 = 66.666667 shared 60:40 -> 40.000000 and 26.666667 (the odd unit to the larger remainder): the sum is exact.
        var lines = ((JsonArray)pending!["contract"]!["participations"]!).Select(l => decimal.Parse((string)l!["signedLinePct"]!, CultureInfo.InvariantCulture)).ToList();
        lines.Count.ShouldBe(3);
        lines.Sum().ShouldBe(100m);
        Pct(pending, 0).ShouldBe(40m);
        pending.Text("contract.participations.0.lead").ShouldBe("true");

        // Pending changes nothing; once decided the panel counts from its date and the earlier version keeps its panel.
        (await VersionAtAsync(slice, id, "2026-03-01")).Body.Text("contract.versionNo").ShouldBe("1");
        (await DecideAsync(slice, id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, before) = await VersionAtAsync(slice, id, "2026-02-19");
        before.Text("contract.versionNo").ShouldBe("1");
        ((JsonArray)before!["contract"]!["participations"]!).Count.ShouldBe(2);
        var (_, after) = await VersionAtAsync(slice, id, Early);
        after.Text("contract.versionNo").ShouldBe("2");
        ((JsonArray)after!["contract"]!["participations"]!).Count.ShouldBe(3);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.participation p JOIN ri.contract_version v ON v.version_id = p.version_id WHERE v.contract_id = '{id}'")).ShouldBe(5);
    }

    [Fact]
    public async Task Add_without_rebalance_must_complete_the_panel_and_a_repeated_reinsurer_is_refused()
    {
        var (slice, lead, follow, third) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);

        // 60 + 40 + 10 is 110: the shares must sum to the placed share.
        (await AddAsync(slice, id, recordVersion, Early, new JsonArray(Line(third, "10")), rebalance: false)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await AddAsync(slice, id, recordVersion, Early, new JsonArray(Line(follow, "10")))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        // A line as large as the whole placement leaves nothing for the others.
        (await AddAsync(slice, id, recordVersion, Early, new JsonArray(Line(third, "100")))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.contract_version WHERE contract_id = '{id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Replace_is_effective_dated_and_the_incoming_reinsurer_takes_the_line_of_the_outgoing_one()
    {
        var (slice, lead, follow, third) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);

        var (replaced, pending) = await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/participations/replace",
            new { contractId = id, expectedRecordVersion = recordVersion, effectiveFrom = "2026-04-01", reason = "Follow reinsurer withdrew", outgoingReinsurerPartyId = follow, incomingReinsurerPartyId = third }, Accountant);
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK, pending?.ToJsonString());
        var ids = ((JsonArray)pending!["contract"]!["participations"]!).Select(l => (string)l!["reinsurerPartyId"]!).ToList();
        ids.ShouldContain(third);
        ids.ShouldNotContain(follow);
        ids.ShouldContain(lead);
        (await DecideAsync(slice, id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Before the date the old panel, from it the new one; the same shares, and the old rows are still stored.
        var (_, old) = await VersionAtAsync(slice, id, "2026-03-31");
        ((JsonArray)old!["contract"]!["participations"]!).Select(l => (string)l!["reinsurerPartyId"]!).ShouldContain(follow);
        var (_, current) = await VersionAtAsync(slice, id, "2026-04-01");
        var panel = (JsonArray)current!["contract"]!["participations"]!;
        panel.Select(l => decimal.Parse((string)l!["signedLinePct"]!, CultureInfo.InvariantCulture)).Sum().ShouldBe(100m);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.participation p JOIN ri.contract_version v ON v.version_id = p.version_id WHERE v.contract_id = '{id}' AND p.reinsurer_party_id = '{follow}'")).ShouldBe(1);

        // Not on the panel / already on the panel.
        var (again, _) = await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/participations/replace",
            new { contractId = id, expectedRecordVersion = recordVersion, effectiveFrom = "2026-06-01", reason = "x", outgoingReinsurerPartyId = follow, incomingReinsurerPartyId = lead }, Accountant);
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_rating_floor_and_a_share_cap_refuse_a_breaching_panel_and_the_accountant_cannot_set_rules()
    {
        var (slice, lead, follow, third) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);

        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/security-rules/set", new { effectiveFrom = "2026-09-01", ratingFloor = "A-" }, Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SetRulesAsync(slice, "2026-09-01", "NOPE", null)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Floor A-: an unrated reinsurer and a BBB one are refused, an AA one is accepted; the existing panel is not re-judged.
        var (set, rules) = await SetRulesAsync(slice, "2026-09-01", "A-", null, (third, "BBB"));
        set.StatusCode.ShouldBe(HttpStatusCode.OK, rules?.ToJsonString());
        rules.Text("ruleSet.ratingFloor").ShouldBe("A-");
        var (breach, problem) = await AddAsync(slice, id, recordVersion, "2026-09-15", new JsonArray(Line(third, "10")));
        breach.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("RI-ERR-SECURITY");
        (await SetRulesAsync(slice, "2026-09-10", "A-", null, (third, "AA"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (ok, accepted) = await AddAsync(slice, id, recordVersion, "2026-09-15", new JsonArray(Line(third, "10")));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, accepted?.ToJsonString());
        accepted.Text("checks.0.ok").ShouldBe("true");
        (await DecideAsync(slice, id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A cap on one line: from October no line may exceed 30%, so the 54% lead cannot be handed to a new reinsurer.
        (await SetRulesAsync(slice, "2026-10-01", null, "30")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fourth = await slice.OrganisationAsync("Synthetic Fourth Re " + Guid.NewGuid().ToString("N")[..6]);
        var (capped, cappedBody) = await slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/participations/replace",
            new { contractId = id, expectedRecordVersion = recordVersion, effectiveFrom = "2026-10-15", reason = "Swap", outgoingReinsurerPartyId = lead, incomingReinsurerPartyId = fourth }, Accountant);
        capped.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, cappedBody?.ToJsonString());
        cappedBody!.ToJsonString().ShouldContain("MAX_SHARE");
    }

    [Fact]
    public async Task The_report_lists_the_exposure_per_reinsurer_and_flags_breaches_of_the_rules_in_force()
    {
        var (slice, lead, follow, _) = await NewSliceAsync(database);
        await using var _ = slice;
        await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));

        var (reported, report) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/securities/report?validAt=2026-02-15", new { }, Accountant);
        reported.StatusCode.ShouldBe(HttpStatusCode.OK, report?.ToJsonString());
        var rows = ((JsonArray)report!["rows"]!).Where(r => (string?)r!["reinsurerPartyId"] == lead || (string?)r!["reinsurerPartyId"] == follow).ToList();
        rows.Count.ShouldBe(2);
        // 500,000 limit: 60% and 40% lines.
        decimal.Parse((string)rows.Single(r => (string)r!["reinsurerPartyId"]! == lead)!["limitExposure"]!["amount"]!, CultureInfo.InvariantCulture).ShouldBe(300000m);
        decimal.Parse((string)rows.Single(r => (string)r!["reinsurerPartyId"]! == follow)!["limitExposure"]!["amount"]!, CultureInfo.InvariantCulture).ShouldBe(200000m);

        // With a rule set whose cap is 50%, the unrated 60% lead is flagged twice and the 40% AAA reinsurer not (rules effective in 2026-12, report at that date).
        var (set, rules) = await SetRulesAsync(slice, "2026-12-01", "AA", "50", (follow, "AAA"));
        set.StatusCode.ShouldBe(HttpStatusCode.OK, rules?.ToJsonString());
        var (_, flagged) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/securities/report?validAt=2026-12-05", new { }, Manager, "rimgr");
        var leadRow = ((JsonArray)flagged!["rows"]!).Single(r => (string)r!["reinsurerPartyId"]! == lead)!;
        ((JsonArray)leadRow["breaches"]!).Select(b => (string)b!).ShouldBe(["RATING_FLOOR", "MAX_SHARE"], ignoreOrder: true);
        var followRow = ((JsonArray)flagged["rows"]!).Single(r => (string)r!["reinsurerPartyId"]! == follow)!;
        ((JsonArray)followRow["breaches"]!).Count.ShouldBe(0); // AAA and 40% is under the 50% cap
    }

    [Fact]
    public async Task Rule_sets_are_read_by_id_and_stay_as_history()
    {
        var (slice, lead, _, _) = await NewSliceAsync(database);
        await using var _ = slice;
        var (_, first) = await SetRulesAsync(slice, "2027-03-01", "BBB", "40", (lead, "A"));
        var (_, second) = await SetRulesAsync(slice, "2027-06-01", "A", "35", (lead, "A+"));
        second.Text("ruleSet.versionNo").ShouldBe((int.Parse(first.Text("ruleSet.versionNo"), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));

        var (_, old) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/security-rules/" + first.Text("ruleSet.ruleSetId"), roles: Accountant);
        old.Text("ruleSet.ratingFloor").ShouldBe("BBB");
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/security-rules/" + Guid.NewGuid(), roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // The rule sets cannot be changed or deleted.
        var error = await Should.ThrowAsync<Exception>(() => slice.ExecuteAsync("DELETE FROM ri.security_rule"));
        error.Message.ShouldContain("append");
    }

    [Fact]
    public async Task A_line_table_overlap_is_refused_and_each_set_is_a_new_version()
    {
        var (slice, _, _, _) = await NewSliceAsync(database);
        await using var _ = slice;
        static JsonObject Band(string from, string? to, string retention, int lines) => new() { ["sumInsuredFrom"] = from, ["sumInsuredTo"] = to, ["retention"] = retention, ["maxLines"] = lines };
        Task<(HttpResponseMessage Response, JsonNode? Body)> Set(params JsonObject[] rows) =>
            slice.SendAsync(HttpMethod.Post, "/api/ri/v1/line-tables/set", new JsonObject { ["rows"] = new JsonArray([.. rows]) }, Manager, "rimgr");

        (await Set(Band("0", "100000", "50000", 4), Band("90000", null, "80000", 5))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // overlap
        (await Set(Band("0", null, "50000", 4), Band("100000", null, "80000", 5))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // open band not last
        (await Set(Band("0", "0", "50000", 4))).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // empty band

        var (ok, one) = await Set(Band("100000", null, "80000", 5), Band("0", "100000", "50000", 4));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        var (_, two) = await Set(Band("0", null, "60000", 3));
        (int.Parse(two.Text("tableVersion.versionNo"), CultureInfo.InvariantCulture) - int.Parse(one.Text("tableVersion.versionNo"), CultureInfo.InvariantCulture)).ShouldBe(1);

        var (_, byId) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/line-tables/" + one.Text("tableVersion.tableId"), roles: Accountant);
        ((JsonArray)byId!["tableVersion"]!["rows"]!).Count.ShouldBe(2);
        decimal.Parse(byId.Text("tableVersion.rows.0.sumInsuredFrom"), CultureInfo.InvariantCulture).ShouldBe(0m); // stored in band order
        var (_, current) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/line-tables/current", roles: Accountant);
        current.Text("tableVersion.tableId").ShouldBe(two.Text("tableVersion.tableId"));
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/line-tables/" + Guid.NewGuid(), roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
