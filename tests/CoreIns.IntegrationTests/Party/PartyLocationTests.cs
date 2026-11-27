using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-02: <c>pty.Location.add/update/remove</c> (addresses and contact points) over HTTP on a real PostgreSQL.</summary>
public sealed class PartyLocationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private static readonly string[] LegalMailing = ["LEGAL", "MAILING"];
    private static readonly string[] Legal = ["LEGAL"];
    private static readonly string[] Billing = ["BILLING"];
    private static readonly string[] Mailing = ["MAILING"];
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(HttpClient Client, string Id, int Version)> CreateAsync(string given, string family)
    {
        var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person(given, family, null));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (client, body.Text("party.partyId"), int.Parse(body.Text("party.recordVersion"), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<JsonNode> PartyAsync(HttpClient client, string id)
    {
        var (_, body) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}");
        return body!["party"]!;
    }

    private static IEnumerable<JsonNode> Emails(JsonNode party) =>
        party["contactPoints"]!.AsArray().Select(c => c!).Where(c => c["type"]!.GetValue<string>() == "EMAIL");

    private async Task<long> CountAsync(string sql, string partyId)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", Guid.Parse(partyId));
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Adding_a_primary_email_demotes_the_old_primary_and_keeps_it_in_the_history()
    {
        var (client, id, version) = await CreateAsync("Μαρία", "Παπαδοπούλου");

        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new
        {
            partyId = id, expectedRecordVersion = version, contactPoint = new { type = "EMAIL", value = " New.Mail@Example.ORG ", primary = true },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("kind").ShouldBe("CONTACT_POINT");
        body.Text("contactPoint.value").ShouldBe("new.mail@example.org");
        body.Text("recordVersion").ShouldBe((version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var emails = Emails(await PartyAsync(client, id)).ToList();
        emails.Count.ShouldBe(2);
        emails.Count(e => e["primary"]!.GetValue<bool>()).ShouldBe(1);
        emails.Single(e => e["primary"]!.GetValue<bool>())["value"]!.GetValue<string>().ShouldBe("new.mail@example.org");

        // The demotion is a new record version: the former primary row is closed, not rewritten.
        (await CountAsync("SELECT count(*) FROM pty.party_contact_point WHERE party_id = @p AND type = 'EMAIL' AND recorded_to IS NOT NULL AND is_primary", id)).ShouldBe(1);
    }

    [Fact]
    public async Task The_first_contact_of_a_type_becomes_primary_and_a_secondary_one_does_not()
    {
        var (client, id, _) = await CreateAsync("Νίκος", "Αθανασίου");

        var (_, landline) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "LANDLINE", value = "210 123 4567" } });
        landline.Text("contactPoint.primary").ShouldBe("true");
        landline.Text("contactPoint.value").ShouldBe("+302101234567");

        var (_, second) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "LANDLINE", value = "+30 211 765 4321" } });
        second.Text("contactPoint.primary").ShouldBe("false");
    }

    [Fact]
    public async Task Invalid_phone_email_duplicate_and_postcode_are_refused()
    {
        var (client, id, _) = await CreateAsync("Πέτρος", "Σταθόπουλος");

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "MOBILE", value = "12ab" } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "EMAIL", value = "not-an-email" } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "EMAIL", value = "TEST.person@example.org" } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (bad, badBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new
        {
            partyId = id, address = new { types = Billing, country = "GR", street = "Σταδίου", number = "1", postcode = "1234", locality = "Αθήνα" },
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badBody.Text("code").ShouldBe("PTY-ERR-POSTCODE-FORMAT");

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id }))
            .Response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Updating_an_address_creates_a_new_version_and_keeps_the_old_one_in_the_history()
    {
        var (client, id, version) = await CreateAsync("Δήμητρα", "Κωστοπούλου");
        var address = (await PartyAsync(client, id))["addresses"]![0]!;
        var addressId = address["addressId"]!.GetValue<string>();

        var (response, body) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/locations/{addressId}", new
        {
            expectedRecordVersion = version,
            address = new { types = LegalMailing, country = "GR", street = "Πανεπιστημίου", number = "10", postcode = "10679", locality = "Αθήνα" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("address.native.street").ShouldBe("Πανεπιστημίου");
        body.Text("address.latin.street").ShouldNotBe("Πανεπιστημίου");
        body.Text("address.primary").ShouldBe("true");
        body.Text("locationId").ShouldNotBe(addressId);

        var addresses = (await PartyAsync(client, id))["addresses"]!.AsArray();
        addresses.Count.ShouldBe(1);
        addresses[0]!["native"]!["postcode"]!.GetValue<string>().ShouldBe("10679");

        // The old id is no longer current; a stale party version is refused too.
        (await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/locations/{addressId}", new { address = new { types = Legal, country = "GR", postcode = "10679" } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (stale, staleBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new
        {
            partyId = id, expectedRecordVersion = version, contactPoint = new { type = "MOBILE", value = "697 000 1111" },
        });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("PTY-ERR-STALE");

        // Bitemporal history: the first version stays in the table, closed in record time.
        (await CountAsync("SELECT count(*) FROM pty.party_address WHERE party_id = @p AND recorded_to IS NOT NULL", id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_new_primary_address_replaces_the_primary_of_the_same_type()
    {
        var (client, id, _) = await CreateAsync("Γιάννης", "Οικονόμου");

        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new
        {
            partyId = id, address = new { types = Mailing, primary = true, country = "GR", street = "Σταδίου", number = "5", postcode = "10562", locality = "Αθήνα" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var addresses = (await PartyAsync(client, id))["addresses"]!.AsArray().Select(a => a!).ToList();
        addresses.Count.ShouldBe(2);
        addresses.Single(a => a["primary"]!.GetValue<bool>())["addressId"]!.GetValue<string>().ShouldBe(body.Text("locationId"));
    }

    [Fact]
    public async Task Removing_a_location_ends_it_keeps_the_history_and_promotes_the_next_one()
    {
        var (client, id, _) = await CreateAsync("Ελένη", "Καραγιάννη");
        var (_, second) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "EMAIL", value = "second@example.org" } });
        second.Text("contactPoint.primary").ShouldBe("false");
        var primary = Emails(await PartyAsync(client, id)).Single(e => e["primary"]!.GetValue<bool>());

        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/remove", new { locationId = primary["contactPointId"]!.GetValue<string>() });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var remaining = Emails(await PartyAsync(client, id)).ToList();
        remaining.Count.ShouldBe(1);
        remaining[0]["value"]!.GetValue<string>().ShouldBe("second@example.org");
        remaining[0]["primary"]!.GetValue<bool>().ShouldBeTrue();

        // The ended email (it never started before today, so it is voided) is still stored, closed in record time.
        (await CountAsync("SELECT count(*) FROM pty.party_contact_point WHERE party_id = @p AND value = 'test.person@example.org' AND recorded_to IS NOT NULL", id)).ShouldBeGreaterThanOrEqualTo(1);

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/remove", new { locationId = Guid.CreateVersion7() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_migrated_contact_point_stays_readable_and_can_be_changed()
    {
        var (client, id, _) = await CreateAsync("Θεόδωρος", "Βασιλείου");
        var migrated = Guid.CreateVersion7();
        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var insert = dataSource.CreateCommand(
                "INSERT INTO pty.party_contact_point (contact_point_id, party_id, legal_entity_id, type, value, purpose, is_primary, verification_status, source, valid_from, recorded_from, created_by) " +
                "SELECT @id, party_id, legal_entity_id, 'FAX', '2101112222', 'WORK', true, 'UNVERIFIED', 'MIGRATION:batch-1', valid_from, now(), 'migration' FROM pty.party_contact_point WHERE party_id = @p LIMIT 1");
            insert.Parameters.AddWithValue("id", migrated);
            insert.Parameters.AddWithValue("p", Guid.Parse(id));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await PartyAsync(client, id))["contactPoints"]!.AsArray().Select(c => c!["contactPointId"]!.GetValue<string>()).ShouldContain(migrated.ToString());

        var (response, body) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/locations/{migrated}", new { contactPoint = new { type = "FAX", value = "+30 210 111 3333", primary = true } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("contactPoint.value").ShouldBe("+302101113333");
    }

    [Fact]
    public async Task A_restricted_party_and_a_role_without_the_permission_are_refused()
    {
        var (client, id, _) = await CreateAsync("Άννα", "Μαυροκέφαλου");
        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "MOBILE", value = "697 000 2222" } }, roles: "Viewer"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var insert = dataSource.CreateCommand(
                "INSERT INTO pty.dsar_restriction (restriction_id, party_id, record_class, legal_basis, retention_end, reason, created_at, created_by) " +
                "VALUES (gen_random_uuid(), @p, 'PARTY_RECORD', 'test', 'UNSET_F404', 'test', now(), 'test')");
            insert.Parameters.AddWithValue("p", Guid.Parse(id));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (restricted, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/locations/add", new { partyId = id, contactPoint = new { type = "MOBILE", value = "697 000 2222" } });
        restricted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.Text("code").ShouldBe("PTY-ERR-RESTRICTED");
    }
}
