using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Underwriting;

/// <summary>
/// BO-UW-09 on a real PostgreSQL 17 through the real Host: a policy hold on a postcode blocks new business (an open UW_POLICY_HOLD issue
/// on the job, so uw.Issue.blockingStatus and POL bind gate block it), releasing it lifts the block, the impact counts in-force policies
/// through POL, and the accumulation check bands the zone exposure against the configured capacity.
/// </summary>
public sealed class PolicyHoldTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Underwriter = "Staff.Underwriter";
    private const string Manager = "Staff.UnderwritingManager";
    private const string Anna = "uw-anna";
    private const string Boss = "uw-boss";

    private static readonly string[] Coverages = ["MTPL"];

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(
            database.AppConnectionString, settings: new Dictionary<string, string?> { ["Underwriting:Accumulation:Capacity:FLOOD"] = "5" });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, string user = Boss, string roles = Manager, object? body = null)
    {
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

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Today(int days = 0) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<JsonNode> CreateAsync(string code, object[] scopes, string[]? jobTypes = null)
    {
        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/policy-holds",
            body: new { holdCode = code, description = "Flood zone moratorium", effectiveFrom = Today(-1), effectiveTo = Today(365), jobTypes = jobTypes ?? [], scopes });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!;
    }

    private async Task<JsonNode> ActivateAsync(string id)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds/activate", body: new { holdId = id });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private async Task ReleaseAsync(string id)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds/release", body: new { holdId = id, reason = "Flood defences completed." });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("hold.status").ShouldBe("Released");
    }

    private async Task<JsonNode> CheckAsync(string postcode, string jobType = "NEW_BUSINESS", string effective = "")
    {
        var (response, body) = await SendAsync(
            HttpMethod.Post, "/api/uw/v1/policy-holds/check", Anna, Underwriter,
            new
            {
                jobType, product = MotorProduct, lineOfBusiness = "MOTOR", coverages = Coverages,
                riskLocations = new[] { new { postcode } }, transactionDate = Today(), effectiveDate = effective.Length == 0 ? Today() : effective,
            });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private async Task<(Guid Job, JsonNode Result)> EvaluateAsync(string postcode)
    {
        var job = Guid.CreateVersion7();
        var risk = RiskTree(birthDate: "1985-06-15");
        risk["vehicle"]!["garagingPostcode"] = postcode;
        var result = await UwTestSupport.EvaluateInProcessAsync(
            _factory.Services,
            new { jobRef = job, checkpoint = "PRE_QUOTE", snapshotRef = $"snapshot-{Guid.NewGuid():N}", productCode = MotorProduct, effectiveDate = Today(), riskSnapshot = risk },
            Anna);
        return (job, result);
    }

    private async Task<bool> BlockedAsync(Guid job, string point)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/uw/v1/issues/blocking-status?jobRef={job}&blockingPoint={point}", Anna, Underwriter);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["blocked"]!.GetValue<bool>();
    }

    private async Task SeedInForcePolicyAsync(string postcode, string product = MotorProduct, string state = "IN_FORCE")
    {
        var policy = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();
        var transaction = Guid.CreateVersion7();
        var number = "HOLD-" + policy.ToString("N")[..12];
        var snapshot = new JsonObject
        {
            ["vehicles"] = new JsonArray(new JsonObject { ["locator"] = "v1", ["fields"] = new JsonObject { ["garagingPostcode"] = postcode } }),
            ["drivers"] = new JsonArray(),
            ["coverages"] = new JsonArray(),
            ["questionSets"] = new JsonArray(),
        }.ToJsonString();
        await database.ExecuteAsSuperuserAsync(
            $"""
            SET session_replication_role = replica;
            INSERT INTO pol.policy (policy_id, legal_entity_id, jurisdiction, policy_number, product_code, policyholder_party_id, recorded_at, last_recorded_at, created_by, record_version)
            VALUES ('{policy}', '{ApiHostFactory.LegalEntityId}', 'GR', '{number}', '{product}', gen_random_uuid(), now(), now(), 'seed', 1);
            INSERT INTO pol.policy_term (term_version_id, term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, recorded_from, recorded_to, state, product_version, artefact_hash,
                                         resolution_hash, configuration_hash, currency, payment_plan_ref, written_date, head_transaction_id, created_by)
            VALUES (gen_random_uuid(), '{term}', '{policy}', '{ApiHostFactory.LegalEntityId}', 1, now() - interval '10 days', now() + interval '300 days', now(), NULL, '{state}', '1.0',
                    '{new string('a', 64)}', '{new string('b', 64)}', '{new string('c', 64)}', 'EUR', 'ANNUAL', current_date, '{transaction}', 'seed');
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, recorded_to, snapshot_hash, snapshot)
            VALUES (gen_random_uuid(), '{term}', '{policy}', '{transaction}', '{ApiHostFactory.LegalEntityId}', now() - interval '10 days', now() + interval '300 days', now(), NULL,
                    '{new string('d', 64)}', '{snapshot}'::jsonb);
            """, Ct);
    }

    [Fact]
    public async Task A_hold_on_a_postcode_blocks_new_business_and_releasing_it_lifts_the_block()
    {
        var postcode = "7" + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
        var created = await CreateAsync(Code("FLOOD"), [new { dimension = "POSTCODE", value = postcode }]);
        var id = created.Text("hold.id");
        created.Text("hold.status").ShouldBe("Draft");

        // A Draft hold does not apply.
        (await CheckAsync(postcode))["holds"]!.AsArray().Count.ShouldBe(0);
        (await ActivateAsync(id)).Text("hold.status").ShouldBe("Active");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyHoldActivated' AND business_keys->>'holdId' = '{id}'")).ShouldBe(1);

        // check: the hold applies at that postcode, not elsewhere, and not outside its window or job types.
        var hit = await CheckAsync(postcode);
        hit["holds"]!.AsArray().Single()!.Text("holdId").ShouldBe(id);
        hit.Text("issueType").ShouldBe("UW_POLICY_HOLD");
        (await CheckAsync("99999"))["holds"]!.AsArray().Count.ShouldBe(0);
        (await CheckAsync(postcode, effective: Today(-30)))["holds"]!.AsArray().Count.ShouldBe(0);

        // The evaluation POL runs raises the hold issue: the job is referred and blocked at bind, not at quote.
        var (job, evaluation) = await EvaluateAsync(postcode);
        evaluation.Text("outcome").ShouldBe("REFER");
        evaluation["issues"]!.AsArray().Single(i => i!.Text("issueType") == "UW_POLICY_HOLD")!.Text("blockingPoint").ShouldBe("PRE_BIND");
        (await BlockedAsync(job, "PRE_BIND")).ShouldBeTrue();
        (await BlockedAsync(job, "PRE_QUOTE")).ShouldBeFalse();
        var (_, elsewhere) = await EvaluateAsync("99999");
        elsewhere.Text("outcome").ShouldBe("ACCEPT");

        // Release lifts the block when the job is evaluated again.
        await ReleaseAsync(id);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyHoldReleased' AND business_keys->>'holdId' = '{id}'")).ShouldBe(1);
        (await CheckAsync(postcode))["holds"]!.AsArray().Count.ShouldBe(0);
        var again = await UwTestSupport.EvaluateInProcessAsync(
            _factory.Services,
            new
            {
                jobRef = job, checkpoint = "PRE_QUOTE", snapshotRef = $"snapshot-{Guid.NewGuid():N}", productCode = MotorProduct, effectiveDate = Today(),
                riskSnapshot = WithPostcode(postcode),
            },
            Anna);
        again.Text("outcome").ShouldBe("ACCEPT");
        (await BlockedAsync(job, "PRE_BIND")).ShouldBeFalse();
    }

    private static JsonObject WithPostcode(string postcode)
    {
        var risk = RiskTree(birthDate: "1985-06-15");
        risk["vehicle"]!["garagingPostcode"] = postcode;
        return risk;
    }

    [Fact]
    public async Task Postcode_prefixes_and_job_types_narrow_what_a_hold_applies_to()
    {
        var prefix = "8" + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);
        var id = (await CreateAsync(Code("PREFIX"), [new { dimension = "POSTCODE", value = prefix + "*" }, new { dimension = "PRODUCT", value = MotorProduct }], ["RENEWAL"])).Text("hold.id");
        await ActivateAsync(id);

        (await CheckAsync(prefix + "12", "RENEWAL"))["holds"]!.AsArray().Count.ShouldBe(1);
        (await CheckAsync(prefix + "12", "NEW_BUSINESS"))["holds"]!.AsArray().Count.ShouldBe(0);
        (await CheckAsync("9" + prefix + "12", "RENEWAL"))["holds"]!.AsArray().Count.ShouldBe(0);
        await ReleaseAsync(id);
    }

    [Fact]
    public async Task Impact_counts_the_in_force_policies_a_hold_touches_through_POL()
    {
        var prefix = "6" + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);
        await SeedInForcePolicyAsync(prefix + "01");
        await SeedInForcePolicyAsync(prefix + "02");
        await SeedInForcePolicyAsync(prefix + "02");
        await SeedInForcePolicyAsync(prefix + "03", state: "CANCELLED");
        await SeedInForcePolicyAsync("00000");

        var wide = await CreateAsync(Code("WIDE"), [new { dimension = "POSTCODE", value = prefix + "*" }]);
        wide.Text("impactCounts.inForcePolicies").ShouldBe("3");
        wide["impactCounts"]!["byProduct"]!.Text(MotorProduct).ShouldBe("3");
        var narrow = await CreateAsync(Code("NARROW"), [new { dimension = "POSTCODE", value = prefix + "02" }]);
        narrow.Text("impactCounts.inForcePolicies").ShouldBe("2");

        var (response, impact) = await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds/impact", Anna, Underwriter, new { holdId = wide.Text("hold.id") });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, impact?.ToJsonString());
        impact.Text("impactCounts.inForcePolicies").ShouldBe("3");
        var (missing, _) = await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds/impact", Anna, Underwriter, new { holdId = Guid.CreateVersion7() });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_a_draft_changes_codes_are_unique_and_a_copy_starts_as_a_draft()
    {
        var code = Code("EDIT");
        var created = await CreateAsync(code, [new { dimension = "POSTCODE", value = "5" + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture) }]);
        var id = created.Text("hold.id");
        var version = created["hold"]!["recordVersion"]!.GetValue<int>();

        (await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds", body: new { holdCode = code, description = "Again", effectiveFrom = Today(), scopes = new[] { new { dimension = "PRODUCT", value = "X" } } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds", body: new { holdCode = Code("NOSCOPE"), description = "None", effectiveFrom = Today(), scopes = Array.Empty<object>() }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (updated, body) = await SendAsync(HttpMethod.Patch, $"/api/uw/v1/policy-holds/{id}", body: new { recordVersion = version, description = "Changed", scopes = new[] { new { dimension = "PRODUCT", value = "OTHER" } } });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("hold.description").ShouldBe("Changed");
        body!["hold"]!["scopes"]!.AsArray().Single()!.Text("value").ShouldBe("OTHER");
        (await SendAsync(HttpMethod.Patch, $"/api/uw/v1/policy-holds/{id}", body: new { recordVersion = version, description = "Stale" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var copy = await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds/copy", body: new { holdId = id, holdCode = Code("COPY") });
        copy.Response.StatusCode.ShouldBe(HttpStatusCode.OK, copy.Body?.ToJsonString());
        copy.Body.Text("hold.status").ShouldBe("Draft");
        copy.Body.Text("hold.copiedFrom").ShouldBe(id);

        await ActivateAsync(id);
        (await SendAsync(HttpMethod.Patch, $"/api/uw/v1/policy-holds/{id}", body: new { description = "Late" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds/activate", body: new { holdId = id })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds/release", body: new { holdId = copy.Body.Text("hold.id"), reason = "n/a" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/uw/v1/policy-holds", Anna, Underwriter, new { holdCode = Code("DENY"), description = "No", effectiveFrom = Today(), scopes = new[] { new { dimension = "PRODUCT", value = "X" } } }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ReleaseAsync(id);
    }

    [Fact]
    public async Task Accumulation_check_bands_the_zone_exposure_against_the_configured_capacity()
    {
        var zone = "4" + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);
        async Task<JsonNode> CheckZoneAsync(string peril = "FLOOD")
        {
            var (response, body) = await SendAsync(HttpMethod.Post, "/api/uw/v1/accumulation/check", Anna, Underwriter, new { peril, zone = new { postcode = zone + "*" } });
            return response.StatusCode == HttpStatusCode.OK ? body! : JsonNode.Parse($"{{\"status\":{(int)response.StatusCode}}}")!;
        }

        var empty = await CheckZoneAsync();
        empty.Text("band").ShouldBe("GREEN");
        empty.Text("capacity.policyCount").ShouldBe("5");
        await SeedInForcePolicyAsync(zone + "1");
        await SeedInForcePolicyAsync(zone + "2");
        await SeedInForcePolicyAsync(zone + "3");
        (await CheckZoneAsync()).Text("band").ShouldBe("GREEN");
        await SeedInForcePolicyAsync(zone + "4");
        (await CheckZoneAsync()).Text("band").ShouldBe("AMBER");
        await SeedInForcePolicyAsync(zone + "5");
        var full = await CheckZoneAsync();
        full.Text("band").ShouldBe("RED");
        full.Text("exposure.policyCount").ShouldBe("5");
        (await CheckZoneAsync("QUAKE")).Text("status").ShouldBe("503");
    }
}
