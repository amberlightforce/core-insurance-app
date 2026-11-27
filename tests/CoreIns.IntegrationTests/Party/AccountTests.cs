using System.Net;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-04: <c>pty.Account.*</c> over HTTP on a real PostgreSQL.</summary>
public sealed class AccountTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly ApiHostFactory _factory = new(
        database.AppConnectionString, settings: new Dictionary<string, string?> { ["Party:AccountMemberLimit"] = "3" });

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<string> PartyAsync(HttpClient client, string given, string family, string? afm = null)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person(given, family, afm));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("party.partyId");
    }

    private static async Task<string> CreateAccountAsync(HttpClient client, string holder, string type = "INDIVIDUAL")
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts", new { holderPartyId = holder, accountType = type });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body.Text("account.accountId");
    }

    [Fact]
    public async Task An_account_is_pending_until_the_holder_has_a_verified_identifier_and_the_holder_role_is_recorded()
    {
        var client = _factory.CreateClient();
        var unverified = await PartyAsync(client, "Μαρία", "Ιωάννου");
        var verified = await PartyAsync(client, "Νίκος", "Βλάχος", "094259216");

        var (_, pending) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts", new { holderPartyId = unverified, accountType = "INDIVIDUAL", name = "Ιωάννου" });
        pending.Text("account.status").ShouldBe("PENDING");
        pending.Text("account.holderPartyId").ShouldBe(unverified);
        pending.Text("account.accountNumber").ShouldStartWith("A");

        var accountId = await CreateAccountAsync(client, verified);
        var (get, got) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/accounts/{accountId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        got.Text("account.status").ShouldBe("ACTIVE");

        var (_, roles) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/party-roles/query?partyId={verified}&roleType=POLICYHOLDER");
        roles.Text("items.0.contextRef.type").ShouldBe("Account");
        roles.Text("items.0.contextRef.id").ShouldBe(accountId);

        (await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/accounts/{Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task There_is_one_holder_at_a_time_and_search_finds_accounts_by_holder_and_member()
    {
        var client = _factory.CreateClient();
        var first = await PartyAsync(client, "Α", "Πρώτος");
        var second = await PartyAsync(client, "Β", "Δεύτερος");
        var accountId = await CreateAccountAsync(client, first);
        await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/add-member", new { accountId, partyId = second, memberRole = "SPOUSE" });

        var (_, asMember) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/accounts/search?partyId={second}");
        asMember!["items"]!.AsArray().Count.ShouldBe(1);
        asMember.Text("items.0.memberCount").ShouldBe("1");

        var (changed, changedBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/set-holder", new { accountId, holderPartyId = second });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, changedBody?.ToJsonString());
        changedBody.Text("account.holderPartyId").ShouldBe(second);
        changedBody!["account"]!["members"]!.AsArray().Count.ShouldBe(0); // the new holder left the member list

        // Exactly one POLICYHOLDER role of this account is current today; the old holder's role is ended.
        var (_, oldRoles) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/party-roles/query?partyId={first}&roleType=POLICYHOLDER");
        oldRoles!["items"]!.AsArray().Count.ShouldBe(0);
        var (_, newRoles) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/party-roles/query?partyId={second}&roleType=POLICYHOLDER");
        newRoles!["items"]!.AsArray().Count.ShouldBe(1);

        var (_, byHolder) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/accounts/search?partyId={first}");
        byHolder!["items"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Members_are_limited_unique_and_a_household_member_belongs_to_one_household()
    {
        var client = _factory.CreateClient();
        var holder = await PartyAsync(client, "Ο", "Κάτοχος");
        var accountId = await CreateAccountAsync(client, holder, "HOUSEHOLD");
        var members = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            members.Add(await PartyAsync(client, "Μ" + i, "Μέλος"));
        }

        (await Add(accountId, holder)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // the holder is not a member
        for (var i = 0; i < 3; i++)
        {
            var (ok, body) = await Add(accountId, members[i]);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        }

        (await Add(accountId, members[0])).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict); // duplicate
        (await Add(accountId, members[3])).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // limit 3

        // One household only.
        var otherHolder = await PartyAsync(client, "Ο", "Άλλος");
        var other = await CreateAccountAsync(client, otherHolder, "HOUSEHOLD");
        (await Add(other, members[1])).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Removing frees the slot and the party.
        var (removed, removedBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/remove-member", new { accountId, partyId = members[1] });
        removed.StatusCode.ShouldBe(HttpStatusCode.OK, removedBody?.ToJsonString());
        removedBody!["account"]!["members"]!.AsArray().Count.ShouldBe(2);
        (await Add(other, members[1])).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/remove-member", new { accountId, partyId = members[1] })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        Task<(HttpResponseMessage Response, System.Text.Json.Nodes.JsonNode? Body)> Add(string account, string party) =>
            SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/add-member", new { accountId = account, partyId = party, memberRole = "CHILD" });
    }

    [Fact]
    public async Task Withdraw_is_terminal_ends_the_holder_role_and_removes_members()
    {
        var client = _factory.CreateClient();
        var holder = await PartyAsync(client, "Γ", "Αποχωρών");
        var member = await PartyAsync(client, "Δ", "Μέλος");
        var accountId = await CreateAccountAsync(client, holder);
        await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/add-member", new { accountId, partyId = member, memberRole = "OTHER" });

        var (withdrawn, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/withdraw", new { accountId, reason = "Customer left" });
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("account.status").ShouldBe("WITHDRAWN");
        body!["account"]!["members"]!.AsArray().Count.ShouldBe(0);

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/withdraw", new { accountId, reason = "again" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/accounts/add-member", new { accountId, partyId = member, memberRole = "OTHER" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (_, roles) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/party-roles/query?partyId={holder}&roleType=POLICYHOLDER");
        roles!["items"]!.AsArray().Count.ShouldBe(0);
    }
}
