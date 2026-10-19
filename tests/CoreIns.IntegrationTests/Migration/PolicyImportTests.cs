using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using CoreIns.IntegrationTests.Policy.Renewal;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// SL6-MIG-POL-IMPORT: an in-force policy imported through a migration batch (dry run, maker-checker approval, xref idempotency), the
/// OPENING transaction and its opening deltas, the renewal job reaching Quoted.Offered on an imported expiring term, and the
/// reversal that is refused after post-migration activity (D-SL6-05). RAT/UW are the scripted doubles; amounts are illustrative.
/// </summary>
public sealed class PolicyImportTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Roles = "Platform.Admin,Staff.MigrationManager";
    private RenewalHarness _harness = null!;
    private string _holder = string.Empty;
    private string _second = string.Empty;

    private PolicySlice Slice => _harness.Slice;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var slice = new PolicySlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });
        await slice.SeedAsync();
        _harness = new RenewalHarness(slice, database.SuperuserConnectionString);
        _holder = await slice.CreatePartyAsync();
        _second = await slice.CreatePartyAsync("1992-03-02");
    }

    public async ValueTask DisposeAsync() => await Slice.DisposeAsync();

    [Fact]
    public async Task Import_twice_gives_one_policy_with_flagged_opening_deltas_and_the_renewal_job_reaches_offered_and_reverse_is_blocked_after_activity()
    {
        var legacyA = $"LEG-A-{Guid.NewGuid():N}";
        var legacyB = $"LEG-B-{Guid.NewGuid():N}";
        var legacyC = $"LEG-C-{Guid.NewGuid():N}";
        var batch = await CreateBatchAsync(Item(legacyA, "OPENING"), Item(legacyB, "RENEWAL_BASED"), Item(legacyC, "OPENING"));

        // Dry run: validated, nothing written, no xref.
        var dry = await StartAsync(batch, dryRun: true);
        Outcomes(dry).ShouldBe(["IMPORTED", "IMPORTED", "IMPORTED"]);
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM pol.migration_import WHERE legacy_number IN ('{legacyA}', '{legacyB}', '{legacyC}')")).ShouldBe(0);

        // Real run: the starter cannot approve; another user can.
        (await StartAsync(batch, dryRun: false)).Text("batch.status").ShouldBe("PENDING_APPROVAL");
        var (self, _) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "alice");
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (approved, done) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = batch, decision = "APPROVE" }, "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        Outcomes(done).ShouldBe(["IMPORTED", "IMPORTED", "IMPORTED"]);
        var policyA = done!["outcomes"]![0].Text("coreId");
        var policyB = done["outcomes"]![1].Text("coreId");
        var policyC = done["outcomes"]![2].Text("coreId");

        // OPENING transaction, origin MIGRATION, baseline pinned, premium as given (312.35 + 120.01 + tax 46.85), deltas flagged opening.
        (await _harness.ScalarAsync<string>($"SELECT kind || '/' || origin || '/' || sequence FROM pol.policy_transaction WHERE policy_id = '{policyA}'")).ShouldBe("OPENING/MIGRATION/1");
        (await _harness.ScalarAsync<decimal>($"SELECT premium FROM pol.policy_transaction WHERE policy_id = '{policyA}'")).ShouldBe(432.36m);
        (await _harness.ScalarAsync<decimal>($"SELECT total FROM pol.policy_transaction WHERE policy_id = '{policyA}'")).ShouldBe(479.21m);
        (await _harness.ScalarAsync<bool>($"SELECT t.configuration_hash = m.baseline_hash FROM pol.policy_transaction t JOIN pol.migration_import m USING (policy_id) WHERE policy_id = '{policyA}'"))
            .ShouldBeTrue();
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{policyA}' AND opening")).ShouldBe(3);
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{policyA}' AND NOT opening")).ShouldBe(0);
        (await _harness.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND aggregate_id = '{policyA}' AND payload->>'opening' = 'true' AND origin = 'MIGRATION'")).ShouldBe(3);
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyBound' AND aggregate_id = '{policyA}' AND origin = 'MIGRATION'")).ShouldBe(1);

        // Import twice -> one policy: the second batch with the same legacy numbers is skipped by the xref.
        var again = await CreateBatchAsync(Item(legacyA, "OPENING"));
        await StartAsync(again, dryRun: true);
        await StartAsync(again, dryRun: false);
        var (_, second) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId = again, decision = "APPROVE" }, "bob");
        Outcomes(second).ShouldBe(["SKIPPED_EXISTING"]);
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM pol.migration_import WHERE legacy_number = '{legacyA}'")).ShouldBe(1);

        // Renewal-based conversion: the existing renewal command creates the job on the imported term and offers it.
        var termB = await _harness.ScalarAsync<Guid>($"SELECT term_id FROM pol.policy_term WHERE policy_id = '{policyB}' AND recorded_to IS NULL");
        var (created, create) = await _harness.PostAsync("/api/pol/v1/renewals", new { termId = termB });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, create?.ToJsonString());
        var jobB = create.Text("jobId");
        var (offered, offer) = await _harness.PostAsync("/api/pol/v1/renewals/offer", new { jobId = jobB, termId = termB });
        offered.StatusCode.ShouldBe(HttpStatusCode.OK, offer?.ToJsonString());
        offer.Text("state").ShouldBe("QUOTED");
        (await _harness.ScalarAsync<string>($"SELECT sub_state FROM pol.job WHERE job_id = '{jobB}'")).ShouldBe("OFFERED");

        // Post-migration activity on C: an endorsement transaction after the OPENING.
        await AddLaterChangeAsync(policyC);

        // Reverse: A is untouched (removed), B has a renewal job and C an endorsement (blocked).
        var (response, reversed) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        reversed.Text("batch.status").ShouldBe("REVERSAL_BLOCKED");
        Outcomes(reversed).ShouldBe(["REVERSED", "REVERSAL_BLOCKED", "REVERSAL_BLOCKED"]);
        reversed!["outcomes"]![2].Text("errorCode").ShouldBe("MIG-ERR-REVERSAL-BLOCKED");
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM pol.policy WHERE policy_id = '{policyA}'")).ShouldBe(0);
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policyA}'")).ShouldBe(0);
        (await _harness.ScalarAsync<long>($"SELECT count(*) FROM pol.policy WHERE policy_id IN ('{policyB}', '{policyC}')")).ShouldBe(2);
    }

    [Fact]
    public async Task A_policy_the_legacy_data_cannot_support_fails_with_a_code_and_writes_nothing()
    {
        var legacy = $"LEG-X-{Guid.NewGuid():N}";
                var item = new { module = "POL", objectType = "Policy", legacyId = legacy, payload = Payload(legacy, "OPENING").With("status", "CANCELLED") };
        var batch = await CreateBatchAsync(item);
        var dry = await StartAsync(batch, dryRun: true);
        Outcomes(dry).ShouldBe(["FAILED"]);
        dry!["outcomes"]![0].Text("errorCode").ShouldBe("MIG-ERR-UNSUPPORTED-STATUS");
    }

    private JsonNode Payload(string legacy, string mode)
    {
        var start = DateTimeOffset.UtcNow.AddDays(-330);
        return JsonNode.Parse($$"""
            {
              "legacyNumber": "{{legacy}}",
              "mode": "{{mode}}",
              "policyholderPartyId": "{{_holder}}",
              "product": "{{Slice.Product}}",
              "currency": "EUR",
              "termStart": "{{start:yyyy-MM-ddTHH:mm:ssZ}}",
              "status": "IN_FORCE",
              "paymentPlan": "ANNUAL",
              "risk": {
                "vehicle": { "plate": "ikx-1234", "make": "Toyota", "model": "Yaris", "firstRegistrationYear": 2021, "engineCapacityCc": 1400, "use": "PRIVATE",
                             "value": { "amount": "15000.00", "currency": "EUR" } },
                "drivers": [
                  { "partyId": "{{_holder}}", "driverType": "MAIN", "yearFirstLicensed": 2010, "usagePercent": 70, "claimsLast5Years": 0 },
                  { "partyId": "{{_second}}", "driverType": "OCCASIONAL", "yearFirstLicensed": 2014, "usagePercent": 30, "claimsLast5Years": 0 }
                ],
                "coverages": ["MTPL", "OWN-DAMAGE"]
              },
              "charges": [
                { "coverageCode": "MTPL", "chargeType": "PREM-MTPL", "chargeCategory": "PREMIUM", "amount": 312.35 },
                { "coverageCode": "OWN-DAMAGE", "chargeType": "PREM-OD", "chargeCategory": "PREMIUM", "amount": 120.01 },
                { "coverageCode": "MTPL", "chargeType": "GR-IPT", "chargeCategory": "TAX", "amount": 46.85, "rate": 0.15, "legalStatus": "Unverified", "provisional": true }
              ]
            }
            """)!;
    }

    private object Item(string legacy, string mode) => new { module = "POL", objectType = "Policy", legacyId = legacy, payload = Payload(legacy, mode) };

    private async Task<string> CreateBatchAsync(params object[] items)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "policies", items }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("batch.batchId");
    }

    private async Task<JsonNode?> StartAsync(string batchId, bool dryRun)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId, dryRun }, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private static string[] Outcomes(JsonNode? body) => [.. body!["outcomes"]!.AsArray().Select(o => o.Text("outcome"))];

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Roles);
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

        var response = await Slice.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>Writes a later CHANGE transaction at a fresh watermark, the shape the change command uses.</summary>
    private async Task AddLaterChangeAsync(string policyId)
    {
        var sql = """
            UPDATE pol.policy SET last_recorded_at = last_recorded_at + interval '1 second', record_version = record_version + 1 WHERE policy_id = '@P';
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, rating_artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            SELECT gen_random_uuid(), policy_id, term_id, gen_random_uuid(), legal_entity_id, 'CHANGE', 2, effective_at,
                   (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '@P'), configuration_hash, artefact_hash, rating_artefact_hash,
                   resolution_hash, '{}'::jsonb, 0, 0, 0, currency, 'simulated', 'simulated', 'LIVE'
              FROM pol.policy_transaction WHERE policy_id = '@P';
            """;
        await _harness.ExecuteAsync(sql.Replace("@P", policyId, StringComparison.Ordinal));
    }
}

internal static class JsonNodeExtensions
{
    public static JsonNode With(this JsonNode node, string name, string value)
    {
        node[name] = value;
        return node;
    }
}
