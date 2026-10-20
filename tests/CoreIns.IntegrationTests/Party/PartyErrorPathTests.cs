using System.Net;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>NX-TST-07: Party error paths, idempotency and authorisation beyond the happy-path tests.</summary>
public sealed class PartyErrorPathTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_body_is_refused_and_creates_nothing_new()
    {
        using var client = _factory.CreateClient();
        var key = Guid.NewGuid();
        var (first, firstBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Κώστας", "Βλάχος", null), key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, firstBody?.ToJsonString());

        var (second, secondBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Ιωάννα", "Λάμπρου", null), key: key);
        ((int)second.StatusCode).ShouldBeInRange(400, 499);
        secondBody?["party"].ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_party_id_or_number_and_a_malformed_id_are_not_found()
    {
        using var client = _factory.CreateClient();
        foreach (var id in new[] { "P999999999", "not-a-party", Guid.CreateVersion7().ToString() })
        {
            var (response, body) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, id);
            body.Text("code").ShouldBe("PTY-ERR-NOT-FOUND");
        }
    }

    [Fact]
    public async Task Party_operations_need_their_permissions()
    {
        using var client = _factory.CreateClient();
        var (_, created) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Ειρήνη", "Μαυρίδου", null));
        var id = created.Text("party.partyId");

        (await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}", roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/search", new { name = "Μαυρίδου" }, roles: "Viewer", withKey: false)).Response
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/vulnerabilities/record", new { partyId = id }, roles: "Viewer")).Response
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/intermediaries", new { }, roles: "Viewer")).Response
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_search_without_any_criteria_is_refused()
    {
        using var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/search", new { }, withKey: false);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        body.Text("code").ShouldStartWith("PTY-ERR-");
    }

    [Fact]
    public async Task A_duplicate_identifier_is_refused_even_with_a_new_idempotency_key_and_different_spacing()
    {
        using var client = _factory.CreateClient();
        var (first, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Θεόδωρος", "Κοντός", "090000045"));
        // The AFM may already exist from another test class sharing the database: 201 or 409 are both a valid first answer.
        first.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);

        var (again, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Θεόδωρος", "Κοντός", "090 000 045"));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.Text("code").ShouldBe("PTY-ERR-DUPLICATE-IDENTIFIER");
        body!["existingPartyId"].ShouldNotBeNull();
    }

    [Fact]
    public async Task A_masked_identifier_never_shows_the_full_value_to_a_role_without_reveal()
    {
        using var client = _factory.CreateClient();
        var (_, created) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Ηλίας", "Βασιλείου", "800000014", "1990-03-04"));
        var id = created.Text("party.partyId");

        var (response, body) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}", roles: Billing);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!.ToJsonString().ShouldNotContain("800000014");
        body.ToJsonString().ShouldNotContain("1990-03-04");
    }
}
