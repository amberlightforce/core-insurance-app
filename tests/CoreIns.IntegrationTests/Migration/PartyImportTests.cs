using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// BO-MIG-06: the Party importer. Person, Organisation and Contact objects imported through a batch (dry run writes nothing, AFM validated and
/// stored encrypted, origin marker), the legacy id rerun is skipped by the xref, a duplicate AFM is never merged (and a DSAR-restricted
/// holder is reported as such), and the reversal removes only the batch's parties and blocks a DSAR-restricted one. Synthetic data only.
/// </summary>
public sealed class PartyImportTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Roles = "Platform.Admin,Staff.MigrationManager";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A valid Greek AFM (9 digits, weighted check digit) from a random 8-digit stem.</summary>
    private static string NewAfm()
    {
        var stem = Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += (stem[i] - '0') << (8 - i);
        }

        return stem + ((sum % 11) % 10);
    }

    private static object PersonPayload(string afm, string family, string given = "Νικόλαος") => new
    {
        person = new { givenNames = given, familyName = family, fatherName = "Γεώργιος", birthDate = "1978-02-11" },
        afm,
        addresses = new[] { new { types = new[] { "LEGAL", "MAILING" }, country = "GR", street = "Λεωφ. Κηφισίας", number = "124", postcode = "11526", locality = "Αθήνα" } },
        contactPoints = new[] { new { type = "EMAIL", value = "legacy.person@example.org", primary = true } },
    };

    [Fact]
    public async Task Person_organisation_and_contact_import_idempotently_and_reverse_removes_only_the_batch_parties()
    {
        await using var host = new ApiHostFactory(database.AppConnectionString);
        var client = host.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var afm = NewAfm();
        var legacyPerson = $"LEG-P-{suffix}";
        var legacyOrg = $"LEG-O-{suffix}";
        var legacyContact = $"LEG-C-{suffix}";
        var family = "Δημητρίου" + suffix;

        // A party that exists before the migration: the reversal must leave it alone.
        var (created, existing) = await CoreIns.IntegrationTests.Party.PartyApi.SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Ιωάννα", "Προϋπάρχουσα" + suffix, NewAfm()));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, existing?.ToJsonString());
        var existingParty = existing.Text("party.partyId");

        object[] items =
        [
            new { module = "PTY", objectType = "Person", legacyId = legacyPerson, payload = PersonPayload(afm, family) },
            new { module = "PTY", objectType = "Organisation", legacyId = legacyOrg, payload = new { organisation = new { legalName = $"Συνθετική Α.Ε. {suffix}" }, afm = NewAfm() } },
        ];
        var batch = await CreateBatchAsync(client, items);

        // Dry run: validated, nothing written.
        var dry = await StartAsync(client, batch, dryRun: true);
        dry!["outcomes"]!.AsArray().Select(o => o.Text("outcome")).First().ShouldBe("IMPORTED", dry.ToJsonString());
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party WHERE source_channel LIKE 'MIGRATION:{batch}:%'")).ShouldBe(0);

        (await StartAsync(client, batch, dryRun: false)).Text("batch.status").ShouldBe("PENDING_APPROVAL");
        var (approved, done) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, Roles, user: "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        Outcomes(done).ShouldBe(["IMPORTED", "IMPORTED"], done?.ToJsonString());
        var personId = done!["outcomes"]![0].Text("coreId");

        // Marked as migrated, AFM stored encrypted with a blind index (no clear value), contact marked with the batch.
        (await ScalarAsync<string>($"SELECT source_channel FROM pty.party WHERE party_id = '{personId}'")).ShouldBe($"MIGRATION:{batch}:{legacyPerson}");
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party_identifier WHERE party_id = '{personId}' AND scheme = 'AFM' AND value_blind_index <> '' AND value_encrypted <> '\\x'::bytea")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party_identifier WHERE party_id = '{personId}' AND position(convert_to('{afm}', 'UTF8') in value_encrypted) > 0")).ShouldBe(0);

        // A contact point added to the imported party by legacy id (its own batch, as the party must exist first).
        var contact = await RunAsync(client, new { module = "PTY", objectType = "Contact", legacyId = legacyContact, payload = new { partyLegacyId = legacyPerson, type = "MOBILE", value = "691 234 5678", purpose = "PERSONAL" } });
        contact.Text("outcomes.0.outcome").ShouldBe("IMPORTED", contact.ToJsonString());
        var contactBatch = contact.Text("batch.batchId");
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party_contact_point WHERE party_id = '{personId}' AND source = 'MIGRATION:{contactBatch}' AND type = 'MOBILE'")).ShouldBe(1);
        var (_, contactReversed) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = contactBatch, dryRun = false }, Roles, user: "alice");
        Outcomes(contactReversed).ShouldBe(["REVERSED"], contactReversed?.ToJsonString());
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party_contact_point WHERE party_id = '{personId}' AND type = 'MOBILE'")).ShouldBe(0);

        // The xref is registered by the framework: the same legacy ids in a second batch are skipped, one party each.
        var again = await CreateBatchAsync(client, items[..2]);
        await StartAsync(client, again, dryRun: true);
        await StartAsync(client, again, dryRun: false);
        var (_, second) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = again, decision = "APPROVE" }, Roles, user: "bob");
        Outcomes(second).ShouldBe(["SKIPPED_EXISTING", "SKIPPED_EXISTING"]);
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party WHERE source_channel LIKE 'MIGRATION:%:{legacyPerson}'")).ShouldBe(1);

        // Reverse: both batch parties go (their children with them); the pre-existing party stays.
        var (reversedResponse, reversed) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, Roles, user: "alice");
        reversedResponse.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        Outcomes(reversed).ShouldBe(["REVERSED", "REVERSED"], reversed?.ToJsonString());
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party WHERE source_channel LIKE 'MIGRATION:{batch}:%'")).ShouldBe(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party_contact_point WHERE party_id = '{personId}'")).ShouldBe(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party WHERE party_id = '{existingParty}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_duplicate_afm_is_not_merged_and_a_dsar_restricted_party_is_reported_and_blocks_its_reversal()
    {
        await using var host = new ApiHostFactory(database.AppConnectionString);
        var client = host.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var afm = NewAfm();
        var (created, existing) = await CoreIns.IntegrationTests.Party.PartyApi.SendAsync(client, HttpMethod.Post, "/api/pty/v1/parties", Person("Ελένη", "Υπάρχουσα" + suffix, afm));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, existing?.ToJsonString());
        var existingParty = existing.Text("party.partyId");

        // The AFM is already held: the item fails naming the existing party, nothing is created or merged.
        var duplicate = new { module = "PTY", objectType = "Person", legacyId = $"LEG-D-{suffix}", payload = PersonPayload(afm, "Διπλότυπο" + suffix) };
        var first = await RunAsync(client, duplicate);
        first.Text("outcomes.0.outcome").ShouldBe("FAILED", first.ToJsonString());
        first.Text("outcomes.0.errorCode").ShouldBe("MIG-ERR-DUPLICATE");
        first.Text("outcomes.0.message").ShouldContain(existingParty);

        // The holder is under a DSAR restriction: reported as such.
        await ExecuteAsync(
            $"INSERT INTO pty.dsar_restriction (restriction_id, party_id, record_class, legal_basis, retention_end, reason, created_at, created_by) VALUES (gen_random_uuid(), '{existingParty}', 'PARTY_RECORD', 'TEST', 'UNSET_F404', 'test', now(), 'test')");
        var restricted = await RunAsync(client, duplicate with { legacyId = $"LEG-D2-{suffix}" });
        restricted.Text("outcomes.0.errorCode").ShouldBe("MIG-ERR-DSAR-RESTRICTED");

        // A migrated party that later comes under a DSAR restriction cannot be reversed.
        var newAfm = NewAfm();
        var imported = await RunAsync(client, new { module = "PTY", objectType = "Person", legacyId = $"LEG-R-{suffix}", payload = PersonPayload(newAfm, "Περιορισμένος" + suffix) });
        imported.Text("outcomes.0.outcome").ShouldBe("IMPORTED", imported.ToJsonString());
        var partyId = imported.Text("outcomes.0.coreId");
        var batch = imported.Text("batch.batchId");
        await ExecuteAsync(
            $"INSERT INTO pty.dsar_restriction (restriction_id, party_id, record_class, legal_basis, retention_end, reason, created_at, created_by) VALUES (gen_random_uuid(), '{partyId}', 'PARTY_RECORD', 'TEST', 'UNSET_F404', 'test', now(), 'test')");
        var (_, reversed) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, Roles, user: "alice");
        Outcomes(reversed).ShouldBe(["REVERSAL_BLOCKED"], reversed?.ToJsonString());
        reversed!["outcomes"]![0].Text("errorCode").ShouldBe("MIG-ERR-REVERSAL-BLOCKED");
        (await ScalarAsync<long>($"SELECT count(*) FROM pty.party WHERE party_id = '{partyId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task An_invalid_afm_fails_the_item_in_the_dry_run_with_the_validation_code()
    {
        await using var host = new ApiHostFactory(database.AppConnectionString);
        var client = host.CreateClient();
        var result = await RunAsync(client, new { module = "PTY", objectType = "Person", legacyId = $"LEG-X-{Guid.NewGuid():N}", payload = PersonPayload("123456789", "Άκυρος") }, dryRunOnly: true);
        result.Text("outcomes.0.outcome").ShouldBe("FAILED", result.ToJsonString());
        result.Text("outcomes.0.errorCode").ShouldBe("MIG-ERR-VALIDATION");
    }

    /// <summary>Creates a one-item batch, starts it (dry run, then real) and approves it; returns the approval response (or the dry-run response).</summary>
    private static async Task<JsonNode> RunAsync(HttpClient client, object item, bool dryRunOnly = false)
    {
        var batch = await CreateBatchAsync(client, [item]);
        var dry = await StartAsync(client, batch, dryRun: true);
        if (dryRunOnly || dry!["outcomes"]![0].Text("outcome") == "FAILED")
        {
            return dry!;
        }

        await StartAsync(client, batch, dryRun: false);
        var (response, done) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, Roles, user: "bob");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        return done!;
    }

    private static async Task<string> CreateBatchAsync(HttpClient client, object[] items)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches", new { name = "parties", items }, Roles, user: "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("batch.batchId");
    }

    private static async Task<JsonNode?> StartAsync(HttpClient client, string batchId, bool dryRun)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId, dryRun }, Roles, user: "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private static string[] Outcomes(JsonNode? body) => [.. body!["outcomes"]!.AsArray().Select(o => o.Text("outcome"))];

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body, string roles, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
