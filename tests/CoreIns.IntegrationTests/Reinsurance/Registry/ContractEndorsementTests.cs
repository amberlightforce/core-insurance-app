using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>
/// BO-RI-03 contract versions: endorse (maker), approve the endorsement (checker, another person), and the bitemporal
/// <c>versionAt</c>. An endorsement is a further version row; approved versions are never changed (REQ-RI-057, -065).
/// </summary>
public sealed class ContractEndorsementTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static async Task<(RegistrySlice Slice, string Lead, string Follow)> NewSliceAsync(PostgresFixture database)
    {
        var slice = new RegistrySlice(database);
        return (slice, await slice.OrganisationAsync("Synthetic Lead Re " + Guid.NewGuid().ToString("N")[..6]), await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]));
    }

    private static JsonArray Layer(string attachment) =>
        new(new JsonObject { ["layerNo"] = 1, ["attachment"] = Money(attachment), ["limit"] = Money("500000.00"), ["aad"] = Money("0.00"), ["aal"] = Money("1500000.00") });

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> EndorseAsync(
        RegistrySlice slice, string id, int recordVersion, string effectiveFrom, string attachment = "600000.00", string user = "riacct") =>
        slice.SendAsync(
            HttpMethod.Post, "/api/ri/v1/contracts/endorse",
            new JsonObject { ["contractId"] = id, ["expectedRecordVersion"] = recordVersion, ["effectiveFrom"] = effectiveFrom, ["reason"] = "Attachment raised at renewal of the retention", ["layers"] = Layer(attachment) },
            Accountant, user);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(
        RegistrySlice slice, string id, int versionNo, string decision = "APPROVE", string user = "rimgr", string? reason = null) =>
        slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/approve-endorsement", new { contractId = id, versionNo, decision, reason }, Manager, user);

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> VersionAtAsync(RegistrySlice slice, string id, string validAt, string? knownAt = null) =>
        slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/version-at?contractId={id}&validAt={validAt}" + (knownAt is null ? string.Empty : "&knownAt=" + Uri.EscapeDataString(knownAt)), roles: Accountant);

    private static decimal Attachment(JsonNode? body) => decimal.Parse(body.Text("contract.layers.0.attachment.amount"), CultureInfo.InvariantCulture);

    [Fact]
    public async Task Endorsement_mid_year_is_a_new_version_from_its_date_and_the_original_stays_as_it_was()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);
        var baseHash = await slice.ScalarAsync<string>($"SELECT content_hash FROM ri.contract_version WHERE contract_id = '{id}' AND version_no = 1");

        slice.Clock.Now = slice.Clock.Now.Plus(TimeSpan.FromDays(1));
        var endorsedAt = slice.Clock.Now;
        var (endorsed, pending) = await EndorseAsync(slice, id, recordVersion, "2026-07-01");
        endorsed.StatusCode.ShouldBe(HttpStatusCode.OK, pending?.ToJsonString());
        pending.Text("contract.versionNo").ShouldBe("2");
        Attachment(pending).ShouldBe(600000m);

        // Pending: nothing changes for anyone yet.
        var (_, whilePending) = await VersionAtAsync(slice, id, "2026-08-01");
        whilePending.Text("contract.versionNo").ShouldBe("1");

        slice.Clock.Now = slice.Clock.Now.Plus(TimeSpan.FromDays(1));
        var approvedAt = slice.Clock.Now;
        var (decided, result) = await DecideAsync(slice, id, 2);
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, result?.ToJsonString());
        result.Text("decision").ShouldBe("APPROVE");

        // Valid time: before the effective date the original, from it the endorsement.
        var (_, before) = await VersionAtAsync(slice, id, "2026-06-30");
        before.Text("contract.versionNo").ShouldBe("1");
        Attachment(before).ShouldBe(500000m);
        var (_, from) = await VersionAtAsync(slice, id, "2026-07-01");
        from.Text("contract.versionNo").ShouldBe("2");
        Attachment(from).ShouldBe(600000m);

        // Record time: as known before the approval the endorsement does not exist.
        var (_, knownBefore) = await VersionAtAsync(slice, id, "2026-08-01", endorsedAt.Plus(TimeSpan.FromHours(1)).ToString());
        knownBefore.Text("contract.versionNo").ShouldBe("1");
        var (_, knownAfter) = await VersionAtAsync(slice, id, "2026-08-01", approvedAt.ToString());
        knownAfter.Text("contract.versionNo").ShouldBe("2");

        // The approved original is untouched; the contract view and the applicable lookup follow the endorsement.
        (await slice.ScalarAsync<string>($"SELECT content_hash FROM ri.contract_version WHERE contract_id = '{id}' AND version_no = 1")).ShouldBe(baseHash);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.contract_version WHERE contract_id = '{id}'")).ShouldBe(2);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{id}'")).ShouldBe(0); // nothing booked is restated
        Attachment(await slice.GetAsync(id)).ShouldBe(500000m); // today (February) the original is still the one in force
        slice.Clock.Now = AthensMidnight(2026, 7, 2);
        Attachment(await slice.GetAsync(id)).ShouldBe(600000m);
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("ACTIVE");

        // Outside the treaty period there is no version.
        (await VersionAtAsync(slice, id, "2027-01-01")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_endorser_and_the_enterer_cannot_decide_and_only_one_endorsement_is_pending()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);

        (await EndorseAsync(slice, id, recordVersion, "2026-07-01")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EndorseAsync(slice, id, recordVersion, "2026-08-01", "700000.00")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The endorser (riacct, a maker) is refused even with the manager role; so is the person who endorsed under another role.
        var (self, selfBody) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/approve-endorsement", new { contractId = id, versionNo = 2, decision = "APPROVE" }, Manager, "riacct");
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfBody?.ToJsonString());
        (await slice.ScalarAsync<string>($"SELECT endorsement_status FROM ri.contract_version WHERE contract_id = '{id}' AND version_no = 2")).ShouldBe("PENDING");

        // An accountant cannot decide at all.
        var (noRole, _) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/approve-endorsement", new { contractId = id, versionNo = 2, decision = "APPROVE" }, Accountant, "other");
        noRole.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await DecideAsync(slice, id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Decided once.
        (await DecideAsync(slice, id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_rejected_endorsement_never_applies_and_the_contract_can_be_endorsed_again()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);
        (await EndorseAsync(slice, id, recordVersion, "2026-07-01")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await DecideAsync(slice, id, 2, "REJECT")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // a rejection needs its reason
        var (rejected, body) = await DecideAsync(slice, id, 2, "REJECT", reason: "Retention not agreed");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (await slice.ScalarAsync<string>($"SELECT endorsement_status FROM ri.contract_version WHERE contract_id = '{id}' AND version_no = 2")).ShouldBe("REJECTED");
        Attachment(await slice.GetAsync(id)).ShouldBe(500000m);
        (await VersionAtAsync(slice, id, "2026-08-01")).Body.Text("contract.versionNo").ShouldBe("1");

        var (again, second) = await EndorseAsync(slice, id, recordVersion, "2026-07-01", "650000.00");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, second?.ToJsonString());
        second.Text("contract.versionNo").ShouldBe("3");
        (await DecideAsync(slice, id, 3)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Attachment((await VersionAtAsync(slice, id, "2026-08-01")).Body).ShouldBe(650000m);
    }

    [Fact]
    public async Task Only_an_Active_contract_with_a_real_change_inside_its_period_can_be_endorsed()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        var (draft, draftVersion, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        (await EndorseAsync(slice, draft, draftVersion, "2026-07-01")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));
        var recordVersion = int.Parse(approved.Text("contract.recordVersion"), CultureInfo.InvariantCulture);
        (await EndorseAsync(slice, id, recordVersion, "2026-01-01")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // not after the start
        (await EndorseAsync(slice, id, recordVersion, "2027-01-01")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // not inside the period
        (await EndorseAsync(slice, id, recordVersion, "2026-07-01", "500000.00")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // same terms
        (await EndorseAsync(slice, id, recordVersion + 5, "2026-07-01")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict); // stale
        (await VersionAtAsync(slice, Guid.NewGuid().ToString(), "2026-07-01")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
