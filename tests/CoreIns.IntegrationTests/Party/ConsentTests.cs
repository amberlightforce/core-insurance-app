using System.Net;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-06: <c>pty.Consent.*</c> and <c>pty.CommunicationPreference.*</c> over HTTP on a real PostgreSQL.</summary>
public sealed class ConsentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private static readonly string[] Rules = ["CALL_FIRST"];
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(HttpClient Client, string Id)> CreateAsync(string given, string family)
    {
        var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person(given, family, null));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (client, body.Text("party.partyId"));
    }

    private static object Give(string id, string purpose, string channel, string? proof = "doc-1") =>
        new { partyId = id, purpose, channel, kind = "GIVEN", source = "PORTAL", wordingVersion = "v3", proofRef = proof };

    [Fact]
    public async Task A_withdrawal_is_immediate_audited_and_keeps_the_history()
    {
        var (client, id) = await CreateAsync("Μαρία", "Κανέλλου");
        var (given, givenBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/record", Give(id, "MARKETING", "EMAIL"));
        given.StatusCode.ShouldBe(HttpStatusCode.OK, givenBody?.ToJsonString());
        givenBody.Text("consent.state").ShouldBe("GRANTED");

        var (_, before) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "MARKETING" });
        before.Text("channel").ShouldBe("EMAIL");

        var (withdrawn, withdrawnBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/withdraw", new { partyId = id, purpose = "MARKETING", channel = "EMAIL" });
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, withdrawnBody?.ToJsonString());
        withdrawnBody.Text("consent.state").ShouldBe("WITHDRAWN");

        // Immediate: the very next resolve finds no consent.
        var (after, afterBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "MARKETING" });
        after.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        afterBody.Text("code").ShouldBe("PTY-ERR-NO-CONSENT");

        var (_, current) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/consents/query?partyId={id}");
        current!["items"]!.AsArray().Count.ShouldBe(1);
        current.Text("items.0.state").ShouldBe("WITHDRAWN");
        var (_, history) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/consents/query?partyId={id}&includeHistory=true");
        history!["items"]!.AsArray().Count.ShouldBe(2);
        history.Text("items.0.state").ShouldBe("WITHDRAWN");
        history.Text("items.1.state").ShouldBe("GRANTED");

        // Withdrawing again changes nothing; an unknown consent is not found.
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/withdraw", new { partyId = id, purpose = "MARKETING", channel = "EMAIL" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/consents/query?partyId={id}&includeHistory=true")).Body!["items"]!.AsArray().Count.ShouldBe(2);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/withdraw", new { partyId = id, purpose = "PROFILING", channel = "SMS" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var audit = dataSource.CreateCommand("SELECT count(*) FROM plt.audit_event WHERE object_type = 'Consent' AND business_keys ->> 'partyId' = @p");
        audit.Parameters.AddWithValue("p", id);
        Convert.ToInt32(await audit.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
        await using var outbox = dataSource.CreateCommand("SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ConsentChanged' AND aggregate_id = @id");
        outbox.Parameters.AddWithValue("id", id);
        Convert.ToInt32(await outbox.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task A_consent_needs_proof_and_an_objection_blocks_the_channel()
    {
        var (client, id) = await CreateAsync("Νίκος", "Ράλλης");
        var noProof = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/record", Give(id, "MARKETING", "SMS", proof: ""));
        noProof.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noProof.Body.Text("code").ShouldBe("PTY-ERR-PROOF-REQUIRED");
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/record", Give(id, "NOPE", "SMS"))).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A service purpose needs no consent, but an objection on its channel is honoured.
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/set", new { partyId = id, purpose = "DEFAULT", channel = "SMS" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, open) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "CLAIMS_UPDATES" });
        open.Text("channel").ShouldBe("SMS");
        var (objected, objectedBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/record",
            new { partyId = id, purpose = "CLAIMS_UPDATES", channel = "SMS", kind = "OBJECTION", source = "PHONE", proofRef = "call-77" });
        objected.StatusCode.ShouldBe(HttpStatusCode.OK, objectedBody?.ToJsonString());
        var (_, next) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "CLAIMS_UPDATES" });
        next.Text("channel").ShouldBe("EMAIL");
    }

    [Fact]
    public async Task Resolve_prefers_the_purpose_then_the_default_then_the_fallback_and_keeps_history()
    {
        var (client, id) = await CreateAsync("Ελένη", "Βασιλείου");

        // No preference: the party has an email contact point.
        var (_, fallback) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "RENEWAL_NOTICES" });
        fallback.Text("channel").ShouldBe("EMAIL");
        fallback.Text("proof").ShouldBe("FALLBACK:EMAIL");

        var (_, def) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/set",
            new { partyId = id, purpose = "DEFAULT", channel = "SMS", validFrom = "2026-01-01" });
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "RENEWAL_NOTICES" }))
            .Body.Text("channel").ShouldBe("SMS");

        var (_, specific) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/set",
            new { partyId = id, purpose = "RENEWAL_NOTICES", channel = "POST", validFrom = "2026-01-01" });
        specific.Text("medium").ShouldBe("PAPER");
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "RENEWAL_NOTICES" }))
            .Body.Text("channel").ShouldBe("POST");
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "CLAIMS_UPDATES" }))
            .Body.Text("channel").ShouldBe("SMS");

        // A later change keeps the earlier preference for the earlier dates.
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/set",
            new { partyId = id, purpose = "RENEWAL_NOTICES", channel = "PORTAL", validFrom = "2026-09-01" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve?validAt=2026-10-01", new { partyId = id, purpose = "RENEWAL_NOTICES" }))
            .Body.Text("channel").ShouldBe("PORTAL");
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve?validAt=2026-05-01", new { partyId = id, purpose = "RENEWAL_NOTICES" }))
            .Body.Text("channel").ShouldBe("POST");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var rows = dataSource.CreateCommand("SELECT count(*) FROM pty.communication_preference WHERE party_id = @id AND purpose = 'RENEWAL_NOTICES'");
        rows.Parameters.AddWithValue("id", Guid.Parse(id));
        Convert.ToInt32(await rows.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
    }

    [Fact]
    public async Task Marketing_needs_a_granted_consent_and_a_vulnerable_party_gets_paper_and_no_marketing()
    {
        var (client, id) = await CreateAsync("Γιώργος", "Αλεξίου");
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/set", new { partyId = id, purpose = "MARKETING", channel = "SMS" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The preferred channel has no consent: refused, until another channel is consented.
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "MARKETING" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/consents/record", Give(id, "MARKETING", "PHONE"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "MARKETING" }))
            .Body.Text("channel").ShouldBe("PHONE");

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/vulnerabilities/record", new { partyId = id, ruleCodes = Rules })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (marketing, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "MARKETING" });
        marketing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (_, service) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/communication-preferences/resolve", new { partyId = id, purpose = "RENEWAL_NOTICES" });
        service.Text("medium").ShouldBe("PAPER");
        service.Text("proof").ShouldContain("VULNERABLE");
    }
}
