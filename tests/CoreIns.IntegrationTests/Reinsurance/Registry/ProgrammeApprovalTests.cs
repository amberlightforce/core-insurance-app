using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>BO-RI-02: programme approval (submit, approve, return) through PLT maker-checker, activation guard, supersession. Synthetic data.</summary>
public sealed class ProgrammeApprovalTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static string Url => "/api/ri/v1/programmes";

    private static JsonObject Layer(int no, string attachment, string limit, string contractId) =>
        new() { ["layerNo"] = no, ["attachment"] = Money(attachment), ["limit"] = Money(limit), ["contractId"] = contractId };

    /// <summary>Two treaties of 2026 (500k xs 500k and 1m xs 1m) of one product; approved through their own maker-checker when <paramref name="approve"/>.</summary>
    private static async Task<(string A, string B)> TreatiesAsync(RegistrySlice slice, string product, bool approve)
    {
        var lead = await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        var first = RegistrySlice.Body(product, "OD", lead, follow);
        var second = RegistrySlice.Body(product, "OD", lead, follow, change: body =>
            body["layers"] = new JsonArray(new JsonObject
            {
                ["layerNo"] = 1, ["attachment"] = Money("1000000.00"), ["limit"] = Money("1000000.00"), ["aad"] = Money("0.00"), ["aal"] = null,
            }));
        if (approve)
        {
            return ((await slice.ApprovedAsync(first)).Id, (await slice.ApprovedAsync(second)).Id);
        }

        return ((await slice.CreateAsync(first)).Id, (await slice.CreateAsync(second)).Id);
    }

    private static JsonObject ProgrammeBody(string line, string a, string b) => new()
    {
        ["code"] = "PRG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        ["name"] = "Synthetic motor XoL programme",
        ["lineCode"] = line,
        ["period"] = new JsonObject { ["from"] = "2026-01-01", ["to"] = "2027-01-01" },
        ["layers"] = new JsonArray(Layer(1, "500000.00", "500000.00", a), Layer(2, "1000000.00", "1000000.00", b)),
    };

    private static async Task<(string Id, int Version, string A, string B, string Line)> DraftAsync(RegistrySlice slice, bool approveContracts = true)
    {
        var line = NewProduct();
        var (a, b) = await TreatiesAsync(slice, line, approveContracts);
        var (response, created) = await slice.SendAsync(HttpMethod.Post, Url, ProgrammeBody(line, a, b));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, created?.ToJsonString());
        var programme = created!["programme"]!;
        return (programme["programmeId"]!.GetValue<string>(), programme["recordVersion"]!.GetValue<int>(), a, b, line);
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> SubmitAsync(RegistrySlice slice, string id, int version, string user = "riacct", string roles = Accountant) =>
        slice.SendAsync(HttpMethod.Post, $"{Url}/submit", new { programmeId = id, expectedRecordVersion = version }, roles, user);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> ApproveAsync(RegistrySlice slice, string id, int version, string user = "rimgr", string roles = Manager) =>
        slice.SendAsync(HttpMethod.Post, $"{Url}/approve", new { programmeId = id, expectedRecordVersion = version }, roles, user);

    private static async Task<int> SubmittedAsync(RegistrySlice slice, string id, int version)
    {
        var (response, body) = await SubmitAsync(slice, id, version);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["programme"]!["recordVersion"]!.GetValue<int>();
    }

    [Fact]
    public async Task Submit_binds_a_hash_and_approval_activates_and_a_later_version_supersedes_it()
    {
        await using var slice = new RegistrySlice(database);
        var (id, version, a, b, _) = await DraftAsync(slice);

        var (response, submitted) = await SubmitAsync(slice, id, version);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, submitted?.ToJsonString());
        submitted!["programme"]!["status"]!.GetValue<string>().ShouldBe("PENDING_APPROVAL");
        var requestId = submitted["approvalRequestId"]!.GetValue<string>();
        submitted["programme"]!["approvalRequestId"]!.GetValue<string>().ShouldBe(requestId);
        (await slice.ScalarAsync<string>($"SELECT length(content_hash)::text FROM ri.programme_version WHERE programme_id = '{id}'")).ShouldBe("64");
        (await slice.ScalarAsync<string>($"SELECT approval_request_id::text FROM ri.programme_version WHERE programme_id = '{id}'")).ShouldBe(requestId);

        // A pending version is not edited and not submitted twice.
        var edit = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = submitted["programme"]!["recordVersion"]!.GetValue<int>(), name = "No" });
        edit.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var twice = await SubmitAsync(slice, id, submitted["programme"]!["recordVersion"]!.GetValue<int>());
        twice.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, twice.Body?.ToJsonString());

        var (ok, approved) = await ApproveAsync(slice, id, submitted["programme"]!["recordVersion"]!.GetValue<int>());
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
        approved!["programme"]!["status"]!.GetValue<string>().ShouldBe("ACTIVE");
        approved["supersededVersionNo"].ShouldBeNull();
        (await slice.ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("Approved");

        // An update of the active programme starts version 2; its approval supersedes version 1 in one step.
        var current = approved["programme"]!["recordVersion"]!.GetValue<int>();
        var (_, second) = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = current, name = "Second version", layers = new[] { Layer(1, "500000.00", "500000.00", a), Layer(2, "1000000.00", "1000000.00", b) } });
        second!["programme"]!["versionNo"]!.GetValue<int>().ShouldBe(2);
        var pending = await SubmittedAsync(slice, id, second["programme"]!["recordVersion"]!.GetValue<int>());
        var (ok2, activated) = await ApproveAsync(slice, id, pending);
        ok2.StatusCode.ShouldBe(HttpStatusCode.OK, activated?.ToJsonString());
        activated!["supersededVersionNo"]!.GetValue<int>().ShouldBe(1);
        activated["programme"]!["versions"]!.AsArray().Select(v => v!["status"]!.GetValue<string>()).ShouldBe(["SUPERSEDED", "ACTIVE"]);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.programme_version WHERE programme_id = '{id}' AND status = 'ACTIVE'")).ShouldBe(1);

        // An active version is decided only once.
        var again = await ApproveAsync(slice, id, activated["programme"]!["recordVersion"]!.GetValue<int>());
        again.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_maker_the_editor_and_the_submitter_cannot_approve_and_the_checker_needs_the_role()
    {
        await using var slice = new RegistrySlice(database);
        var (id, version, _, _, _) = await DraftAsync(slice);

        // A second person edits the Draft: they become a participant, and then cannot approve it either.
        var (edit, edited) = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = version, name = "Edited by another" }, Accountant, "rieditor");
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, edited?.ToJsonString());
        var pending = await SubmittedAsync(slice, id, edited!["programme"]!["recordVersion"]!.GetValue<int>());

        foreach (var user in new[] { "riacct", "rieditor" })
        {
            var (response, body) = await ApproveAsync(slice, id, pending, user);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body?.ToJsonString());
            body!.ToJsonString().ShouldContain("RI-ERR-SOD");
        }

        (await ApproveAsync(slice, id, pending, "riacct", Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.ScalarAsync<string>($"SELECT status FROM ri.programme_version WHERE programme_id = '{id}'")).ShouldBe("PENDING_APPROVAL");

        var (ok, approved) = await ApproveAsync(slice, id, pending);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
    }

    [Fact]
    public async Task A_return_needs_a_reason_sends_the_version_back_to_draft_and_it_can_be_edited_and_submitted_again()
    {
        await using var slice = new RegistrySlice(database);
        var (id, version, _, _, _) = await DraftAsync(slice);
        var pending = await SubmittedAsync(slice, id, version);

        (await slice.SendAsync(HttpMethod.Post, $"{Url}/return", new { programmeId = id, expectedRecordVersion = pending, reason = "" }, Manager, "rimgr"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await slice.SendAsync(HttpMethod.Post, $"{Url}/return", new { programmeId = id, expectedRecordVersion = pending, reason = "Wrong ladder" }, Manager, "riacct"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.SendAsync(HttpMethod.Post, $"{Url}/return", new { programmeId = id, expectedRecordVersion = pending, reason = "Wrong ladder" }, Accountant, "rimgr"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (response, returned) = await slice.SendAsync(HttpMethod.Post, $"{Url}/return", new { programmeId = id, expectedRecordVersion = pending, reason = "Wrong ladder" }, Manager, "rimgr");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, returned?.ToJsonString());
        returned!["programme"]!["status"]!.GetValue<string>().ShouldBe("DRAFT");
        returned["programme"]!["returnReason"]!.GetValue<string>().ShouldBe("Wrong ladder");
        var requestId = await slice.ScalarAsync<string>($"SELECT approval_request_id::text FROM ri.programme_version WHERE programme_id = '{id}'");
        (await slice.ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("Rejected");

        // Not pending any more: neither a second return nor an approval is possible.
        var current = returned["programme"]!["recordVersion"]!.GetValue<int>();
        (await ApproveAsync(slice, id, current)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (edit, edited) = await slice.SendAsync(HttpMethod.Patch, $"{Url}/{id}", new { expectedRecordVersion = current, name = "Corrected programme" });
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, edited?.ToJsonString());
        var again = await SubmittedAsync(slice, id, edited!["programme"]!["recordVersion"]!.GetValue<int>());
        var (ok, approved) = await ApproveAsync(slice, id, again);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
        approved!["programme"]!["returnReason"].ShouldBeNull();
    }

    [Fact]
    public async Task A_programme_is_not_activated_while_a_contract_is_not_approved_and_a_stale_record_version_loses()
    {
        await using var slice = new RegistrySlice(database);
        var (id, version, a, _, _) = await DraftAsync(slice, approveContracts: false);
        var pending = await SubmittedAsync(slice, id, version);

        var (response, refused) = await ApproveAsync(slice, id, pending);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refused?.ToJsonString());
        refused!.ToJsonString().ShouldContain("RI-ERR-STATE");
        (await slice.ScalarAsync<string>($"SELECT status FROM ri.programme_version WHERE programme_id = '{id}'")).ShouldBe("PENDING_APPROVAL");

        (await ApproveAsync(slice, id, pending + 5)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Approving the treaties (their own maker-checker) lets the programme through.
        foreach (var contract in new[] { a, await slice.ScalarAsync<string>($"SELECT contract_id::text FROM ri.programme_layer l JOIN ri.programme_version v ON v.version_id = l.version_id WHERE v.programme_id = '{id}' AND l.layer_no = 2") })
        {
            var contractVersion = int.Parse(await slice.ScalarAsync<string>($"SELECT record_version::text FROM ri.contract WHERE contract_id = '{contract}'"), System.Globalization.CultureInfo.InvariantCulture);
            var submitted = await slice.SubmitAsync(contract, contractVersion);
            (await slice.ApproveAsync(contract, submitted)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var (ok, approved) = await ApproveAsync(slice, id, pending);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
        approved!["programme"]!["status"]!.GetValue<string>().ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task A_contract_of_another_programme_and_a_contract_outside_the_line_are_refused()
    {
        await using var slice = new RegistrySlice(database);
        var (id, _, a, b, line) = await DraftAsync(slice, approveContracts: false);

        // The same treaties under a second programme of another line: refused, they already sit in a programme of the period.
        var other = ProgrammeBody(NewProduct(), a, b);
        var (response, problem) = await slice.SendAsync(HttpMethod.Post, Url, other);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem?.ToJsonString());
        problem!.ToJsonString().ShouldContain("CONTRACT_IN_OTHER_PROGRAMME");

        // Treaties whose scope is another product than the programme line are refused.
        var (c, d) = await TreatiesAsync(slice, NewProduct(), approve: false);
        var outside = await slice.SendAsync(HttpMethod.Post, Url, ProgrammeBody(line + "_X", c, d));
        outside.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, outside.Body?.ToJsonString());
        outside.Body!.ToJsonString().ShouldContain("CONTRACT_SCOPE_EXCLUDES_LINE");

        // A superseded or other period is not a clash: the same programme may keep using its own treaties.
        (await slice.SendAsync(HttpMethod.Get, $"{Url}/{id}")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Submit_is_refused_for_unknown_stale_or_foreign_programmes_and_by_the_wrong_role()
    {
        await using var slice = new RegistrySlice(database);
        var (id, version, _, _, _) = await DraftAsync(slice);

        (await SubmitAsync(slice, Guid.NewGuid().ToString(), 1)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SubmitAsync(slice, id, version + 3)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SubmitAsync(slice, id, version, "rimgr", Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApproveAsync(slice, Guid.NewGuid().ToString(), 1)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ApproveAsync(slice, id, version)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
