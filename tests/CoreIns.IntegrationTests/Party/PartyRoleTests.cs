using System.Net;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-03: <c>pty.PartyRole.*</c> and <c>pty.Relationship.*</c> over HTTP on a real PostgreSQL.</summary>
public sealed class PartyRoleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(HttpClient Client, string Id)> CreateAsync(string given, string family, string? afm = null)
    {
        var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person(given, family, afm));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (client, body.Text("party.partyId"));
    }

    [Fact]
    public async Task A_duplicate_role_is_refused_and_the_first_role_activates_the_party()
    {
        var (client, id) = await CreateAsync("Μαρία", "Ιωάννου");
        var body = new { partyId = id, roleType = "POLICYHOLDER", contextRef = new { module = "POL", type = "Policy", id = "p-1" }, validFrom = "2026-01-01" };

        var (first, firstBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/assign", body);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());

        var (dup, dupBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/assign", body);
        dup.StatusCode.ShouldBe(HttpStatusCode.Conflict, dupBody?.ToJsonString());

        // Another context is fine.
        var (other, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/assign",
            new { partyId = id, roleType = "POLICYHOLDER", contextRef = new { module = "POL", type = "Policy", id = "p-2" }, validFrom = "2026-01-01" });
        other.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, party) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}");
        party.Text("party.status").ShouldBe("ACTIVE");

        var (unknown, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/assign", new { partyId = id, roleType = "NOPE" });
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Ending_a_role_keeps_history_and_the_query_honours_the_as_of_date()
    {
        var (client, id) = await CreateAsync("Νίκος", "Βλάχος");
        var (_, assigned) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/assign",
            new { partyId = id, roleType = "INSURED", validFrom = "2026-01-01" });
        var roleId = assigned.Text("role.roleId");

        var (ended, endedBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/end", new { roleId, validTo = "2026-06-01" });
        ended.StatusCode.ShouldBe(HttpStatusCode.OK, endedBody?.ToJsonString());

        var (_, inside) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/party-roles/query?partyId={id}&validAt=2026-03-01");
        inside!["items"]!.AsArray().Count.ShouldBe(1);
        inside.Text("items.0.validPeriod.to").ShouldBe("2026-06-01");
        var (_, after) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/party-roles/query?partyId={id}&validAt=2026-07-01");
        after!["items"]!.AsArray().Count.ShouldBe(0);

        // Ending outside the period is refused; the earlier record is closed, not deleted.
        var (bad, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/end", new { roleId, validTo = "2026-09-01" });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM pty.party_role WHERE role_id = @r");
        command.Parameters.AddWithValue("r", Guid.Parse(roleId));
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(2);
    }

    [Fact]
    public async Task A_vendor_role_needs_an_afm()
    {
        var (client, noAfm) = await CreateAsync("Ελένη", "Δημου");
        var (refused, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/assign", new { partyId = noAfm, roleType = "VENDOR" });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (_, withAfm) = await CreateAsync("Γιώργος", "Παππάς", "094259216");
        var (ok, okBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/party-roles/assign", new { partyId = withAfm, roleType = "VENDOR" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());
    }

    [Fact]
    public async Task Relationships_refuse_overlap_and_cycles_and_can_be_ended()
    {
        var (client, a) = await CreateAsync("Α", "Πρώτος");
        var (_, b) = await CreateAsync("Β", "Δεύτερος");
        var (_, c) = await CreateAsync("Γ", "Τρίτος");

        var (added, addedBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/relationships/add",
            new { fromPartyId = a, toPartyId = b, relationshipType = "SUBSIDIARY_OF", validFrom = "2026-01-01" });
        added.StatusCode.ShouldBe(HttpStatusCode.OK, addedBody?.ToJsonString());
        var relationshipId = addedBody.Text("relationship.relationshipId");

        var (overlap, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/relationships/add",
            new { fromPartyId = a, toPartyId = b, relationshipType = "SUBSIDIARY_OF", validFrom = "2026-02-01" });
        overlap.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/relationships/add",
            new { fromPartyId = b, toPartyId = c, relationshipType = "SUBSIDIARY_OF", validFrom = "2026-01-01" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // c -> a would close a -> b -> c -> a.
        var (cycle, cycleBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/relationships/add",
            new { fromPartyId = c, toPartyId = a, relationshipType = "SUBSIDIARY_OF", validFrom = "2026-01-01" });
        cycle.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, cycleBody?.ToJsonString());

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/relationships/end", new { relationshipId, validTo = "2026-06-01" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, early) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/relationships/query?partyId={a}&validAt=2026-03-01");
        early!["items"]!.AsArray().Count.ShouldBe(1);
        var (_, late) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/relationships/query?partyId={a}&validAt=2026-07-01");
        late!["items"]!.AsArray().Count.ShouldBe(0);
    }
}
