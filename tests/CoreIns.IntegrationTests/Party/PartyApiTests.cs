using System.Net;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>
/// The Party reference vertical over HTTP on a real PostgreSQL 17 (W2-PTY-01 subset): create, get, search, masking and
/// reveal, encryption at rest, outbox and audit in the command's transaction, idempotency, permissions.
/// </summary>
public sealed class PartyApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_person_is_created_with_number_latin_forms_masked_identifier_event_and_audit()
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Αγγελική", "Ευθυμίου", "123456783"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var partyId = body.Text("party.partyId");
        response.Headers.Location!.ToString().ShouldBe($"/api/pty/v1/parties/{partyId}");
        body.Text("party.partyNumber").ShouldMatch("^P[0-9]{9}$");
        body.Text("party.status").ShouldBe("PROSPECT");
        body.Text("party.preferredLanguage").ShouldBe("el");
        body.Text("party.legalEntity").ShouldBe("GR-TEST");
        body.Text("party.names.0.form").ShouldBe("NATIVE");
        body.Text("party.names.0.script").ShouldBe("Grek");
        body.Text("party.names.1.form").ShouldBe("LATIN_GENERATED");
        body.Text("party.names.1.familyName").ShouldBe("Efthymiou");
        body.Text("party.names.1.givenNames").ShouldBe("Angeliki");
        body.Text("party.names.1.transliteratorVersion").ShouldBe("GR-ELOT743-T2/1");
        body.Text("party.identifiers.0.value").ShouldBe("******783");
        body.Text("party.identifiers.0.masked").ShouldBe("true");
        body.Text("party.identifiers.0.verificationStatus").ShouldBe("SELF_DECLARED");
        body.Text("party.identifiers.0.validatorVersion").ShouldBe("GR-ID/1");
        body!["party"]!["birthDate"].ShouldBeNull();
        body.Text("party.p2Revealed").ShouldBe("false");
        body.Text("party.addresses.0.latin.street").ShouldBe("Leof. Kifisias");
        body.Text("party.addresses.0.latin.locality").ShouldBe("Athina");
        body.Text("party.addresses.0.validationState").ShouldBe("VALIDATED");
        body.Text("party.contactPoints.0.value").ShouldBe("test.person@example.org");
        body.Text("party.contactPoints.1.value").ShouldBe("+306912345678");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PartyCreated' AND aggregate_id = '{partyId}' AND business_keys->>'partyId' = '{partyId}'")).ShouldBe(1);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.audit_event WHERE operation = 'pty.Party.create' AND object_id = '{partyId}' AND outcome = 'Succeeded'")).ShouldBe(1);

        // REQ-PTY-060: ciphertext at rest, blind index for search, no plain value anywhere in the row or the event.
        var stored = await ScalarAsync<byte[]>(dataSource, $"SELECT value_encrypted FROM pty.party_identifier WHERE party_id = '{partyId}'");
        System.Text.Encoding.UTF8.GetString(stored).ShouldNotContain("123456783");
        (await ScalarAsync<string>(dataSource, $"SELECT value_blind_index FROM pty.party_identifier WHERE party_id = '{partyId}'")).ShouldStartWith("v1:");
        (await ScalarAsync<string>(dataSource, $"SELECT payload::text FROM plt.outbox_message WHERE aggregate_id = '{partyId}'")).ShouldNotContain("123456783");
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pty.party WHERE party_id = '{partyId}' AND birth_date_encrypted IS NOT NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task Search_is_accent_case_and_script_insensitive_and_finds_identifiers_through_the_blind_index()
    {
        var (created, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Ελένη", "Σωτηροπούλου", "111111114"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var number = body.Text("party.partyNumber");
        var id = body.Text("party.partyId");
        var (dokos, _) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Νίκος", "Ντόκος", null));
        dokos.StatusCode.ShouldBe(HttpStatusCode.Created);

        // GET carries only the party number (D-SLC-05).
        var (byNumber, numberPage) = await SendAsync(_client, HttpMethod.Get, $"/api/pty/v1/parties/search?partyNumber={number}");
        byNumber.StatusCode.ShouldBe(HttpStatusCode.OK);
        numberPage!["items"]!.AsArray().Select(item => item!["partyId"]!.GetValue<string>()).ShouldContain(id);

        // D-SLC-05: names, identifiers and the single search box travel in a POST body, never in a URL (a read: no Idempotency-Key).
        foreach (var criteria in new object[]
                 {
                     new { name = "ΣΩΤΗΡΟΠΟΥΛΟΥ" }, new { name = "σωτηροπουλου" }, new { name = "Sotiropoulou" }, new { name = "sotiropoulou eleni" },
                     new { name = "Ελενη Σωτηρ" }, new { name = "Sotiro%" },
                     new { identifierScheme = "AFM", identifierValue = "111111114" }, new { criteria = "111111114" }, new { criteria = number },
                     new { criteria = "Σωτηροπούλου" },
                 })
        {
            var (response, page) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties/search", criteria, withKey: false);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, criteria.ToString());
            page!["items"]!.AsArray().Select(item => item!["partyId"]!.GetValue<string>()).ShouldContain(id, criteria.ToString());
        }

        // The GET form ignores names and identifiers: they are never search criteria in a URL.
        var (ignored, ignoredPage) = await SendAsync(_client, HttpMethod.Get, "/api/pty/v1/parties/search?identifierScheme=AFM&identifierValue=111111114&name=Sotiropoulou");
        ignored.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, ignoredPage?.ToJsonString());

        // REQ-PTY-067: the reverse digraph alternative finds the Greek-only "Ντόκος" from Latin "Dokos".
        var (byDokos, dokosPage) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties/search", new { name = "Dokos" }, withKey: false);
        byDokos.StatusCode.ShouldBe(HttpStatusCode.OK);
        dokosPage!["items"]!.AsArray().Select(item => item!["displayName"]!.GetValue<string>()).ShouldContain("Νίκος Ντόκος");

        var (exact, exactPage) = await SendAsync(
            _client, HttpMethod.Post, "/api/pty/v1/parties/search", new { identifierScheme = "AFM", identifierValue = "111111114" }, withKey: false);
        exact.StatusCode.ShouldBe(HttpStatusCode.OK);
        exactPage.Text("items.0.matchQuality").ShouldBe("EXACT");
        exactPage.Text("items.0.maskedIdentifier.maskedValue").ShouldBe("******114");
        exactPage.Text("items.0.primaryPostcode").ShouldBe("11526");
        exactPage.Text("items.0.displayName").ShouldBe("Ελένη Σωτηροπούλου");
        exactPage.Text("items.0.displayNameLatin").ShouldBe("Eleni Sotiropoulou");

        var (tooShort, problem) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties/search", new { name = "Σ" }, withKey: false);
        tooShort.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        problem.Text("code").ShouldBe("PTY-ERR-QUERY-TOO-SHORT");
    }

    [Fact]
    public async Task Get_masks_P2_and_an_audited_reveal_with_permission_shows_it()
    {
        var (_, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Γιώργος", "Αγγελόπουλος", "800000002", "1975-01-31"));
        var id = body.Text("party.partyId");

        var (masked, maskedBody) = await SendAsync(_client, HttpMethod.Get, $"/api/pty/v1/parties/{id}", roles: Billing);
        masked.StatusCode.ShouldBe(HttpStatusCode.OK);
        maskedBody.Text("party.identifiers.0.value").ShouldBe("******002");

        var (denied, deniedBody) = await SendAsync(_client, HttpMethod.Get, $"/api/pty/v1/parties/{id}?revealPurpose=RATING", roles: Billing);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        deniedBody.Text("code").ShouldBe("PTY-ERR-AUTHORITY-DENIED");

        var (revealed, revealedBody) = await SendAsync(_client, HttpMethod.Get, $"/api/pty/v1/parties/{id}?revealPurpose=RATING");
        revealed.StatusCode.ShouldBe(HttpStatusCode.OK, revealedBody?.ToJsonString());
        revealedBody.Text("party.birthDate").ShouldBe("1975-01-31");
        revealedBody.Text("party.identifiers.0.value").ShouldBe("800000002");
        revealedBody.Text("party.p2Revealed").ShouldBe("true");

        var (byNumber, byNumberBody) = await SendAsync(_client, HttpMethod.Get, $"/api/pty/v1/parties/{body.Text("party.partyNumber")}");
        byNumber.StatusCode.ShouldBe(HttpStatusCode.OK);
        byNumberBody.Text("party.partyId").ShouldBe(id);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.audit_event WHERE operation = 'pty.Party.revealP2' AND object_id = '{id}' AND reason = 'RATING' AND outcome = 'Succeeded'"))
            .ShouldBe(1);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM plt.audit_event WHERE operation = 'pty.Party.revealP2' AND outcome = 'Rejected'")).ShouldBeGreaterThanOrEqualTo(1);

        var (missing, missingBody) = await SendAsync(_client, HttpMethod.Get, $"/api/pty/v1/parties/{Guid.CreateVersion7()}");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missingBody.Text("code").ShouldBe("PTY-ERR-NOT-FOUND");
    }

    [Fact]
    public async Task A_reveal_sent_with_an_idempotency_key_never_stores_its_result()
    {
        var (_, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Σοφία", "Καμπάνη", "094014298", "1969-07-20"));
        var id = body.Text("party.partyId");

        // HTTP: a GET carrying an Idempotency-Key header (the context picks it up for every request).
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/pty/v1/parties/{id}?revealPurpose=RATING", UriKind.Relative)))
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, Underwriter);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // In process: the caller's unit of work has an idempotency key (e.g. a POL command reading a birth date for rating).
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.User("rating-caller");
            context.Roles = [Underwriter];
            context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
            context.Jurisdiction = Jurisdiction.Parse("GR");
            context.IdempotencyKey = IdempotencyKey.New();
            var parties = scope.ServiceProvider.GetRequiredService<IPartyPartyService>();
            for (var i = 0; i < 2; i++)
            {
                (await parties.GetAsync(id, revealPurpose: "RATING", cancellationToken: TestContext.Current.CancellationToken)).Party.BirthDate
                    .ShouldBe(new CoreIns.SharedKernel.BusinessDate(1969, 7, 20));
            }
        }

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM plt.idempotency_record WHERE scope LIKE '%revealP2%'")).ShouldBe(0);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM plt.idempotency_record WHERE convert_from(response_body, 'UTF8') LIKE '%1969-07-20%'")).ShouldBe(0);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.audit_event WHERE operation = 'pty.Party.revealP2' AND object_id = '{id}'")).ShouldBe(3);
    }

    [Fact]
    public async Task Invalid_and_duplicate_identifiers_and_bad_postcodes_are_refused_with_PTY_codes()
    {
        var (checkDigit, checkBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Άννα", "Παπαδάκη", "123456789"));
        checkDigit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        checkBody.Text("code").ShouldBe("PTY-ERR-ID-CHECKDIGIT");

        var (postcode, postcodeBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Άννα", "Παπαδάκη", null, postcode: "1152"));
        postcode.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        postcodeBody.Text("code").ShouldBe("PTY-ERR-POSTCODE-FORMAT");

        var (first, firstBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Μαρία", "Χατζηδάκη", "100000090"));
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (duplicate, duplicateBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Μαρία", "Χατζηδάκη", "100 000 090"));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        duplicateBody.Text("code").ShouldBe("PTY-ERR-DUPLICATE-IDENTIFIER");
        duplicateBody.Text("existingPartyId").ShouldBe(firstBody.Text("party.partyId"));

        var (shape, shapeBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", new { partyType = "PERSON" });
        shape.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        shapeBody.Text("code").ShouldBe("PTY-ERR-VALIDATION");

        var (unreadable, unreadableBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", new { partyType = "PERSON", person = new { familyName = 42 } });
        unreadable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unreadableBody.Text("code").ShouldBe("PTY-ERR-VALIDATION");
    }

    [Fact]
    public async Task Commands_need_an_idempotency_key_replay_the_first_result_and_need_the_permission()
    {
        var person = Person("Δημήτρης", "Ψαρρός", "090000045");
        var (noKey, noKeyBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", person, withKey: false);
        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noKeyBody.Text("code").ShouldBe("PTY-ERR-IDEMPOTENCY-KEY-REQUIRED");

        var key = Guid.NewGuid();
        var (first, firstBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", person, key: key);
        var (replay, replayBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", person, key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");
        replayBody.Text("party.partyId").ShouldBe(firstBody.Text("party.partyId"));

        var (forbidden, _) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", Person("Χ", "Ψ", null), roles: "Viewer");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
