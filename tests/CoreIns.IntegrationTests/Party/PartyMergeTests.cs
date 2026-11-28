using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-05: duplicate candidates, maker-checker merge, the pointer of a merged party, unmerge and the never-merge rules over HTTP on a real PostgreSQL.</summary>
public sealed class PartyMergeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Manager = "Staff.UnderwritingManager";
    private static int _afmSeed = 61_000_000;
    private static int _nameSeed;

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static string NextAfm()
    {
        var digits = Interlocked.Increment(ref _afmSeed).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += (digits[i] - '0') << (8 - i);
        }

        return digits + (sum % 11 % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A Latin family name no other test uses (letters only, so the search key keeps all of it).</summary>
    private static string UniqueName()
    {
        var n = Interlocked.Increment(ref _nameSeed) + 1000;
        return "Mrg" + string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => (char)('a' + (c - '0'))));
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> AsAsync(string user, string roles, HttpMethod method, string path, object? body = null)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<string> CreatePartyAsync(string family, string? afm, string birthDate = "1980-05-17", string given = "Nikos")
    {
        var (response, body) = await SendAsync(_factory.CreateClient(), HttpMethod.Post, "/api/pty/v1/parties", Person(given, family, afm, birthDate));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("party.partyId");
    }

    private async Task<string> PartyStatusAsync(string id)
    {
        var (response, body) = await AsAsync("reader", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("party.status");
    }

    private async Task<string> RequestMergeAsync(string survivor, string merged, string maker = "maker")
    {
        var (response, body) = await AsAsync(maker, Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge", new { survivorPartyId = survivor, mergedPartyId = merged, reason = "Same customer" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("merge.status").ShouldBe("PENDING_APPROVAL");
        return body.Text("merge.mergeId");
    }

    [Fact]
    public async Task Candidates_list_the_pair_with_score_and_reasons_and_flag_a_conflict_of_afms()
    {
        var family = UniqueName();
        var a = await CreatePartyAsync(family, NextAfm());
        var b = await CreatePartyAsync(family, null);
        var c = await CreatePartyAsync(family, NextAfm(), "1991-01-01");

        var (response, body) = await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/duplicates?partyId={b}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var items = body!["items"]!.AsArray();
        items.Count.ShouldBe(2); // b with a, b with c
        var withA = items.Single(i => i.Text("partyA.partyId") == a || i.Text("partyB.partyId") == a);
        withA.Text("score").ShouldBe("90");
        withA!["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["NAME", "NAME_AND_BIRTH_DATE"], ignoreOrder: true);
        withA!["mergeBlockedReason"].ShouldBeNull();
        var withC = items.Single(i => i.Text("partyA.partyId") == c || i.Text("partyB.partyId") == c);
        withC.Text("score").ShouldBe("30");
        withC!["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain("BIRTH_DATE_DIFFERS");

        // a and c hold different AFMs: listed (same name) but flagged as never mergeable.
        var (_, pair) = await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/duplicates?partyId={c}&minScore=0");
        pair!["items"]!.AsArray().Single(i => i.Text("partyA.partyId") == a || i.Text("partyB.partyId") == a).Text("mergeBlockedReason").ShouldBe("IDENTIFIER_CONFLICT");

        (await AsAsync("steward", Underwriter, HttpMethod.Get, "/api/pty/v1/parties/duplicates?minScore=101")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsAsync("nobody", "Viewer", HttpMethod.Get, "/api/pty/v1/parties/duplicates")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_merge_needs_a_different_checker_then_the_merged_party_is_refused_everywhere_and_resolves_to_the_survivor()
    {
        var family = UniqueName();
        var survivor = await CreatePartyAsync(family, NextAfm());
        var merged = await CreatePartyAsync(family, null);
        var mergeId = await RequestMergeAsync(survivor, merged);

        // Nothing changed yet; the maker cannot decide even holding the checker role.
        (await PartyStatusAsync(merged)).ShouldBe("PROSPECT");
        var (self, selfBody) = await AsAsync("maker", Manager, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId, decision = "APPROVE" });
        self.IsSuccessStatusCode.ShouldBeFalse();
        selfBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await AsAsync("checker", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId, decision = "APPROVE" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A second open request on the same party is refused while the first waits.
        (await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge", new { survivorPartyId = survivor, mergedPartyId = merged, reason = "again" }))
            .Body.Text("code").ShouldBe("PTY-ERR-STATE");

        var (approved, approvedBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId, decision = "APPROVE", comment = "ok" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody.Text("merge.status").ShouldBe("EXECUTED");
        approvedBody.Text("merge.decidedBy").ShouldNotBe(approvedBody.Text("merge.requestedBy"));
        approvedBody!["merge"]!["unmergeWindowEnd"].ShouldNotBeNull();

        (await PartyStatusAsync(merged)).ShouldBe("MERGED");
        (await PartyStatusAsync(survivor)).ShouldBe("PROSPECT");

        // The merged party is refused by the existing guards; lookup by its old id resolves to the survivor.
        var (update, updateBody) = await AsAsync("steward", Underwriter, HttpMethod.Patch, $"/api/pty/v1/parties/{merged}", new { expectedRecordVersion = 2, preferredLanguage = "en" });
        update.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        updateBody.Text("code").ShouldBe("PTY-ERR-MERGED");
        var (role, roleBody) = await AsAsync("steward", Underwriter, HttpMethod.Post, "/api/pty/v1/party-roles/assign", new { partyId = merged, roleType = "POLICYHOLDER" });
        role.StatusCode.ShouldBe(HttpStatusCode.Conflict, roleBody?.ToJsonString());
        roleBody.Text("code").ShouldBe("PTY-ERR-MERGED");

        var (resolved, resolvedBody) = await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/{merged}/survivor");
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, resolvedBody?.ToJsonString());
        resolvedBody.Text("survivorPartyId").ShouldBe(survivor);
        resolvedBody.Text("merged").ShouldBe("true");
        resolvedBody.Text("mergeId").ShouldBe(mergeId);
        (await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/{survivor}/survivor")).Body.Text("merged").ShouldBe("false");
        (await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/{Guid.NewGuid()}/survivor")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The merged party is gone from the candidate queue, and the merge is listed.
        var (_, queue) = await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/duplicates?partyId={survivor}");
        queue!["items"]!.AsArray().Count.ShouldBe(0);
        var (_, list) = await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/merges?partyId={merged}&status=EXECUTED");
        list!["items"]!.AsArray().Single().Text("mergeId").ShouldBe(mergeId);

        // A decided merge cannot be decided again.
        (await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId, decision = "APPROVE" })).Body.Text("code").ShouldBe("PTY-ERR-STATE");
    }

    [Fact]
    public async Task Unmerge_inside_the_window_restores_the_absorbed_party_exactly_and_a_closed_window_refuses()
    {
        var family = UniqueName();
        var survivor = await CreatePartyAsync(family, NextAfm());
        var merged = await CreatePartyAsync(family, null);
        var (_, before) = await AsAsync("reader", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/{merged}");
        var mergeId = await RequestMergeAsync(survivor, merged);
        (await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId, decision = "APPROVE" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AsAsync("steward", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/unmerge", new { mergeId, reason = "Not the same" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (unmerged, body) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/unmerge", new { mergeId, reason = "Not the same" });
        unmerged.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("merge.status").ShouldBe("UNMERGED");
        body.Text("restoredPartyId").ShouldBe(merged);

        var (_, after) = await AsAsync("reader", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/{merged}");
        after.Text("party.status").ShouldBe("PROSPECT");
        after!["party"]!["names"]!.ToJsonString().ShouldBe(before!["party"]!["names"]!.ToJsonString());
        after["party"]!["addresses"]!.ToJsonString().ShouldBe(before["party"]!["addresses"]!.ToJsonString());
        after["party"]!["identifiers"]!.ToJsonString().ShouldBe(before["party"]!["identifiers"]!.ToJsonString());
        (await AsAsync("steward", Underwriter, HttpMethod.Get, $"/api/pty/v1/parties/{merged}/survivor")).Body.Text("merged").ShouldBe("false");

        // The restored party works again and can be merged anew; an unmerged merge cannot be unmerged twice.
        (await AsAsync("steward", Underwriter, HttpMethod.Post, "/api/pty/v1/party-roles/assign", new { partyId = merged, roleType = "POLICYHOLDER" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/unmerge", new { mergeId, reason = "again" })).Body.Text("code").ShouldBe("PTY-ERR-STATE");

        // Window: a second merge whose window has run out.
        var other = await CreatePartyAsync(UniqueName(), null);
        var secondId = await RequestMergeAsync(survivor, other);
        (await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId = secondId, decision = "APPROVE" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var close = dataSource.CreateCommand("UPDATE pty.party_merge SET unmerge_window_end = now() - interval '1 day' WHERE merge_id = @m");
            close.Parameters.AddWithValue("m", Guid.Parse(secondId));
            await close.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (late, lateBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/unmerge", new { mergeId = secondId, reason = "late" });
        late.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, lateBody?.ToJsonString());
        lateBody.Text("code").ShouldBe("PTY-ERR-WINDOW-CLOSED");
        (await PartyStatusAsync(other)).ShouldBe("MERGED");
    }

    [Fact]
    public async Task A_rejected_merge_changes_nothing_and_never_merge_rules_hold()
    {
        var family = UniqueName();
        var a = await CreatePartyAsync(family, NextAfm());
        var b = await CreatePartyAsync(family, NextAfm());

        // Different valid AFMs: refused when requested.
        var (conflict, conflictBody) = await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge", new { survivorPartyId = a, mergedPartyId = b, reason = "x" });
        conflict.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, conflictBody?.ToJsonString());
        conflictBody.Text("code").ShouldBe("PTY-ERR-MERGE-IDENTIFIER-CONFLICT");

        // Itself, an unknown party and a person with an organisation.
        (await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge", new { survivorPartyId = a, mergedPartyId = a, reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge", new { survivorPartyId = a, mergedPartyId = Guid.NewGuid(), reason = "x" })).Body.Text("code").ShouldBe("PTY-ERR-NOT-FOUND");
        var (_, organisation) = await SendAsync(_factory.CreateClient(), HttpMethod.Post, "/api/pty/v1/parties", new { partyType = "ORGANISATION", organisation = new { legalName = "Mrg Org " + family } });
        (await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge", new { survivorPartyId = a, mergedPartyId = organisation.Text("party.partyId"), reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A DSAR-restricted party cannot be merged.
        var restricted = await CreatePartyAsync(family, null);
        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var insert = dataSource.CreateCommand(
                "INSERT INTO pty.dsar_restriction (restriction_id, party_id, record_class, legal_basis, retention_end, reason, created_at, created_by) " +
                "VALUES (gen_random_uuid(), @p, 'PARTY_RECORD', 'test', 'UNSET_F404', 'test', now(), 'test')");
            insert.Parameters.AddWithValue("p", Guid.Parse(restricted));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (blocked, blockedBody) = await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/parties/merge", new { survivorPartyId = a, mergedPartyId = restricted, reason = "x" });
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("PTY-ERR-RESTRICTED");

        // A rejection needs a comment and leaves both parties as they were.
        var plain = await CreatePartyAsync(family, null);
        var mergeId = await RequestMergeAsync(a, plain);
        (await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId, decision = "REJECT" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (rejected, rejectedBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/merge/approve", new { mergeId, decision = "REJECT", comment = "Different people" });
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody.Text("merge.status").ShouldBe("REJECTED");
        (await PartyStatusAsync(plain)).ShouldBe("PROSPECT");
        (await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/parties/unmerge", new { mergeId, reason = "x" })).Body.Text("code").ShouldBe("PTY-ERR-STATE");
    }
}
