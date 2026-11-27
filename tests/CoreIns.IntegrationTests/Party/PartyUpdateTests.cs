using System.Net;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-01: <c>pty.Party.update</c>, <c>pty.Party.validate</c> and <c>pty.Identifier.verify</c> over HTTP on a real PostgreSQL.</summary>
public sealed class PartyUpdateTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(HttpClient Client, string Id, int Version)> CreateAsync(string given, string family, string? afm)
    {
        var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person(given, family, afm));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (client, body.Text("party.partyId"), int.Parse(body.Text("party.recordVersion"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_name_change_bumps_the_version_refreshes_latin_form_and_search_keys_and_keeps_history()
    {
        var (client, id, version) = await CreateAsync("Μαρία", "Παπαδοπούλου", null);

        var (response, body) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", new
        {
            expectedRecordVersion = version,
            person = new { givenNames = "Ελένη", familyName = "Καραγιάννη" },
            reason = "MARRIAGE",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("party.recordVersion").ShouldBe((version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        body.Text("party.names.0.familyName").ShouldBe("Καραγιάννη");
        body.Text("party.names.1.form").ShouldBe("LATIN_GENERATED");
        body.Text("party.names.1.familyName").ShouldBe("Karagianni");
        body.Text("party.names.1.givenNames").ShouldBe("Eleni");
        body.Text("party.names.0.fatherName").ShouldBe("Γεώργιος");

        // Search keys follow the name: the new name (also in Latin) finds the party, the old one does not.
        var (_, byNew) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/search", new { name = "Karagianni" }, withKey: false);
        byNew.Text("items.0.partyId").ShouldBe(id);
        var (_, byOld) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/search", new { name = "Παπαδοπούλου" }, withKey: false);
        byOld!["items"]!.AsArray().Select(i => i!["partyId"]!.GetValue<string>()).ShouldNotContain(id);

        // Record-time history: as known before the change, the old name is still there (rows are closed, never deleted).
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM pty.party_name WHERE party_id = @p AND recorded_to IS NOT NULL");
        command.Parameters.AddWithValue("p", Guid.Parse(id));
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Birth_date_and_language_change_without_touching_the_name()
    {
        var (client, id, version) = await CreateAsync("Νίκος", "Αθανασίου", null);

        var (response, body) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", new
        {
            expectedRecordVersion = version,
            person = new { birthDate = "1975-02-03" },
            preferredLanguage = "en",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("party.preferredLanguage").ShouldBe("en");
        body.Text("party.names.0.familyName").ShouldBe("Αθανασίου");
        var (_, revealed) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}?revealPurpose=SERVICING");
        revealed.Text("party.birthDate").ShouldBe("1975-02-03");
    }

    [Fact]
    public async Task A_stale_version_is_refused_with_409_and_changes_nothing()
    {
        var (client, id, version) = await CreateAsync("Πέτρος", "Σταθόπουλος", null);
        var change = new { expectedRecordVersion = version, preferredLanguage = "en" };
        (await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", change)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", new { expectedRecordVersion = version, person = new { givenNames = "Άλλος", familyName = "Όνομα" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.Text("code").ShouldBe("PTY-ERR-STALE");
        var (_, current) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}");
        current.Text("party.names.0.familyName").ShouldBe("Σταθόπουλος");
    }

    [Fact]
    public async Task Update_refuses_bad_shape_unknown_party_and_a_restricted_party()
    {
        var (client, id, version) = await CreateAsync("Δήμητρα", "Κωστοπούλου", null);

        var (lonely, lonelyBody) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", new { expectedRecordVersion = version, person = new { givenNames = "Μόνο" } });
        ((int)lonely.StatusCode).ShouldBeInRange(400, 422, lonelyBody?.ToJsonString());

        var (noChange, _) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", new { expectedRecordVersion = version });
        ((int)noChange.StatusCode).ShouldBeInRange(400, 422);

        var (missing, missingBody) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{Guid.CreateVersion7()}", new { expectedRecordVersion = 1, preferredLanguage = "en" });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missingBody.Text("code").ShouldBe("PTY-ERR-NOT-FOUND");

        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var insert = dataSource.CreateCommand(
                "INSERT INTO pty.dsar_restriction (restriction_id, party_id, record_class, legal_basis, retention_end, reason, created_at, created_by) " +
                "VALUES (gen_random_uuid(), @p, 'PARTY_RECORD', 'test', 'UNSET_F404', 'test', now(), 'test')");
            insert.Parameters.AddWithValue("p", Guid.Parse(id));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (restricted, restrictedBody) = await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", new { expectedRecordVersion = version, preferredLanguage = "en" });
        restricted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        restrictedBody.Text("code").ShouldBe("PTY-ERR-RESTRICTED");

        (await SendAsync(client, HttpMethod.Patch, $"/api/pty/v1/parties/{id}", new { expectedRecordVersion = version, preferredLanguage = "en" }, roles: "Viewer"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Validate_reports_invalid_afm_and_duplicate_candidates_without_saving()
    {
        var (client, existing, _) = await CreateAsync("Θεόδωρος", "Βασιλείου", "111111114");

        var (bad, badBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/validate",
            new { partyData = Person("Άλλος", "Άνθρωπος", "123456780") }, withKey: false);
        bad.StatusCode.ShouldBe(HttpStatusCode.OK, badBody?.ToJsonString());
        badBody!["issues"]!.AsArray().Count.ShouldBeGreaterThan(0);

        var (dup, dupBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/validate",
            new { partyData = Person("Θεόδωρος", "Βασιλείου", "111111114") }, withKey: false);
        dup.StatusCode.ShouldBe(HttpStatusCode.OK, dupBody?.ToJsonString());
        dupBody!["issues"]!.AsArray().Count.ShouldBe(0);
        dupBody.Text("candidates.0.partyId").ShouldBe(existing);

        var (shape, shapeBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/validate",
            new { partyData = new { partyType = "PERSON", person = (object?)null } }, withKey: false);
        shape.StatusCode.ShouldBe(HttpStatusCode.OK, shapeBody?.ToJsonString());
        shapeBody!["issues"]!.AsArray().Count.ShouldBeGreaterThan(0);

        var (search, searchBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/search", new { name = "Άνθρωπος" }, withKey: false);
        search.StatusCode.ShouldBe(HttpStatusCode.OK);
        searchBody!["items"]!.AsArray().Count.ShouldBe(0);

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties/validate", new { partyData = Person("Α", "Β", null) }, roles: "Viewer", withKey: false))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Verify_rechecks_the_identifier_and_records_the_source()
    {
        var (client, id, _) = await CreateAsync("Γεωργία", "Μπακάλη", "123456783");

        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/identifiers/verify", new { partyId = id, scheme = "AFM" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("valid").ShouldBe("true");
        body.Text("status").ShouldBe("SELF_DECLARED");

        var (_, party) = await SendAsync(client, HttpMethod.Get, $"/api/pty/v1/parties/{id}");
        party.Text("party.identifiers.0.verificationSource").ShouldBe("ID_VALIDATOR");

        var (none, noneBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/identifiers/verify", new { partyId = id, scheme = "PASSPORT" });
        none.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        noneBody.Text("code").ShouldBe("PTY-ERR-NOT-FOUND");

        (await SendAsync(client, HttpMethod.Post, "/api/pty/v1/identifiers/verify", new { partyId = id, scheme = "AFM" }, roles: "Viewer"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
