using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Product;
using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product.Fallback;

/// <summary>
/// SL5-PFC-FALLBACK (REQ-PFC-213, -033, -166, -178; D-SL5-09) on PostgreSQL 17 through the real Host with the shiftable clock:
/// MOTOR-GR 1.0 and 1.1 are imported, 1.1 is declared defective, and 1.2 (a copy of 1.0) is published by maker-checker.
/// Maker <c>rm-1</c> (Platform.ReleaseManager); checkers <c>da-1</c>, <c>da-2</c> (Platform.DesignAuthority).
/// </summary>
public sealed class FallbackTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Maker = "rm-1";
    private const string MakerRole = "Platform.ReleaseManager";
    private const string Checker = "da-1";
    private const string Checker2 = "da-2";
    private const string CheckerRole = "Platform.DesignAuthority";
    private const string Reason = "MOTOR-GR 1.1 day count defect found in production testing";

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private ShiftableClock _clock = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(
            database.AppConnectionString,
            settings: new Dictionary<string, string?>
            {
                [ClockConfiguration.ModeKey] = "Shiftable",
                // Staff.Underwriter is also given the permission so that the authority check (not the permission) refuses it.
                ["Platform:Permissions:Grants:pfc.ProductVersion.decideFallback:1"] = "Staff.Underwriter",
            });
        _client = _factory.CreateClient();
        _clock = (ShiftableClock)_factory.Services.GetRequiredService<IClock>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static JsonObject Definition(string json, string code)
    {
        var definition = JsonNode.Parse(json)!.AsObject();
        definition["product"]!["code"] = code;
        return definition;
    }

    /// <summary>Imports 1.0 and 1.1 of a fresh product with the clock frozen at <paramref name="now"/> (UTC).</summary>
    private async Task<(string Code, string Hash10, string Hash11)> SeedAsync(string code, string now = "2026-10-09T09:00:00Z")
    {
        _clock.Freeze(Instant.Parse(now));
        var (_, v10) = await ImportAsync(_client, Definition(ProductSeeds.MotorPrivateCarJson(), code));
        var (_, v11) = await ImportAsync(_client, Definition(ProductSeeds.MotorPrivateCar11Json(), code));
        return (code, v10.Text("artefactHash"), v11.Text("artefactHash"));
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, string user, string roles, object? body = null, Guid? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> RequestAsync(string code, string version = "1.1", string user = Maker, string roles = MakerRole, bool dryRun = false, Guid? key = null) =>
        SendAsync(HttpMethod.Post, "/api/pfc/v1/product-versions/fallback" + (dryRun ? "?dryRun=true" : string.Empty), user, roles,
            new { productCode = code, defectiveVersion = version, reason = Reason }, key);

    private Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(string fallbackId, string decision = "APPROVE", string user = Checker, string roles = CheckerRole) =>
        SendAsync(HttpMethod.Post, "/api/pfc/v1/product-versions/decide-fallback", user, roles,
            new { fallbackId, decision, reason = "Reviewed the defect report and the 1.0 content." });

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default : (T)value;
    }

    private async Task ExecuteAsync(string sql, string? connectionString = null)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString ?? database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private Task<string?> VersionColumnAsync(string code, string version, string column) =>
        ScalarAsync<string>($"SELECT v.{column}::text FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = '{code}' AND v.major || '.' || v.minor = '{version}'");

    private async Task<string> ResolveVersionAsync(string code, string validAt, string type = "NewBusiness")
    {
        var (response, body) = await ResolveAsync(_client, code, "WEB_DIRECT", validAt, type);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("version");
    }

    private async Task<string> RequestOkAsync(string code)
    {
        var (response, body) = await RequestAsync(code);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("fallbackId");
    }

    [Fact]
    public async Task REQ_PFC_213_033_166_178_request_then_another_user_approves_publishes_1_2_as_a_copy_of_1_0_and_closes_1_1_new_business_only()
    {
        var (code, hash10, hash11) = await SeedAsync("MOTOR-FB-A");
        var before10 = await ScalarAsync<string>($"SELECT row_to_json(v)::text FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = '{code}' AND v.minor = 0");

        var (requested, request) = await RequestAsync(code);
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, request?.ToJsonString());
        request.Text("status").ShouldBe("PENDING_APPROVAL");
        request.Text("preview.newVersion").ShouldBe("1.2");
        request.Text("preview.source.version").ShouldBe("1.0");
        request.Text("preview.source.artefactHash").ShouldBe(hash10);
        var fallbackId = request.Text("fallbackId");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.approval_request WHERE approval_type = 'PFC.Fallback' AND request_id = '{request.Text("approvalRequestId")}'")).ShouldBe(1);

        var (decided, decision) = await DecideAsync(fallbackId);

        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decision?.ToJsonString());
        decision.Text("fallback.status").ShouldBe("APPLIED");
        decision.Text("fallback.newVersion").ShouldBe("1.2");
        decision.Text("fallback.defectiveVersion").ShouldBe("1.1");
        decision.Text("fallback.requestedBy").ShouldBe("USER:" + Maker);
        decision.Text("fallback.decidedBy").ShouldBe("USER:" + Checker);

        // 1.2: Locked, window from the Athens date of the approval, linked to 1.0 (copy source) and 1.1 (replaced).
        (await VersionColumnAsync(code, "1.2", "status")).ShouldBe("LOCKED");
        (await VersionColumnAsync(code, "1.2", "new_business_from")).ShouldBe("2026-10-09");
        (await VersionColumnAsync(code, "1.2", "new_business_to")).ShouldBeNull();
        (await VersionColumnAsync(code, "1.2", "renewal_from")).ShouldBe("2026-10-09");
        (await ScalarAsync<bool>($"SELECT f.minor = 0 AND r.minor = 1 FROM pfc.product_version v JOIN pfc.product p USING (product_id) JOIN pfc.product_version f ON f.product_version_id = v.fallback_of_version_id JOIN pfc.product_version r ON r.product_version_id = v.replaces_version_id WHERE p.code = '{code}' AND v.minor = 2")).ShouldBeTrue();

        // 1.1: only its new-business end moved; renewal window and artefact untouched.
        (await VersionColumnAsync(code, "1.1", "new_business_to")).ShouldBe("2026-10-09");
        (await VersionColumnAsync(code, "1.1", "renewal_to")).ShouldBeNull();
        (await VersionColumnAsync(code, "1.1", "artefact_hash")).ShouldBe(hash11);

        // 1.0 row unchanged (record version included).
        (await ScalarAsync<string>($"SELECT row_to_json(v)::text FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = '{code}' AND v.minor = 0")).ShouldBe(before10);

        // The artefact of 1.2 is the content of 1.0 with only version and windows replaced.
        var (_, a10) = await GetAsync(_client, $"/api/pfc/v1/artifacts/{hash10}");
        var (_, a12) = await GetAsync(_client, $"/api/pfc/v1/artifacts/{await VersionColumnAsync(code, "1.2", "artefact_hash")}");
        var copy = a12!["canonicalJsonArtefact"]!.AsObject();
        var original = a10!["canonicalJsonArtefact"]!.AsObject();
        copy["version"]!.GetValue<string>().ShouldBe("1.2");
        copy["dayCount"]!.GetValue<string>().ShouldBe("ACT/365F");
        copy["refundMethods"].ShouldBeNull();
        foreach (var key in new[] { "version", "windows" })
        {
            copy.Remove(key);
            original.Remove(key);
        }

        copy.ToJsonString().ShouldBe(original.ToJsonString());

        // Event, audit and approval record in the same transaction; ProductVersionPublished exactly once with fallbackOf and replaces.
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ProductVersionPublished' AND payload->>'productCode' = '{code}' AND payload->>'version' = '1.2'")).ShouldBe(1);
        (await ScalarAsync<string>($"SELECT payload->>'fallbackOf' || '/' || (payload->>'replaces') FROM plt.outbox_message WHERE event_type = 'ProductVersionPublished' AND payload->>'productCode' = '{code}' AND payload->>'version' = '1.2'")).ShouldBe("1.0/1.1");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'pfc.ProductVersion.fallback' AND outcome = 'Succeeded' AND object_id = '{fallbackId}'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'pfc.ProductVersion.decideFallback' AND outcome = 'Succeeded' AND object_id = '{fallbackId}'")).ShouldBe(1);

        // A second fall-back of the replaced version is refused.
        var (again, againBody) = await RequestAsync(code);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("PFC-ERR-FALLBACK-STATE");
    }

    [Fact]
    public async Task REQ_PFC_167_resolution_switches_to_1_2_on_the_fall_back_date_and_1_1_stays_retrievable_by_hash()
    {
        var (code, hash10, hash11) = await SeedAsync("MOTOR-FB-B");
        (await ResolveVersionAsync(code, "2026-10-09")).ShouldBe("1.1");
        var fallbackId = await RequestOkAsync(code);
        (await DecideAsync(fallbackId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Half-open: the last day of 1.1 is the day before; renewal windows of 1.1 are not touched.
        (await ResolveVersionAsync(code, "2026-10-08")).ShouldBe("1.1");
        (await ResolveVersionAsync(code, "2026-10-09")).ShouldBe("1.2");
        (await ResolveVersionAsync(code, "2026-11-01")).ShouldBe("1.2");
        (await ResolveVersionAsync(code, "2026-10-05", "Renewal")).ShouldBe("1.1");
        (await ResolveVersionAsync(code, "2026-10-09", "Renewal")).ShouldBe("1.2");

        // Terms pinned to 1.0 and 1.1 keep resolving their own artefacts by hash.
        foreach (var hash in new[] { hash10, hash11 })
        {
            var (response, body) = await GetAsync(_client, $"/api/pfc/v1/artifacts/{hash}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        }

        // 1.2 has the same content as 1.0 apart from version and windows, so its own hash differs from 1.0's (the artefact embeds both).
        var (_, resolved) = await ResolveAsync(_client, code, "WEB_DIRECT", "2026-10-09");
        resolved.Text("artefactHash").ShouldNotBe(hash10);
    }

    [Fact]
    public async Task PITFALL_14_the_fall_back_date_is_the_athens_date_of_the_approval_instant_on_a_dst_change_day()
    {
        // 2026-10-24T22:30Z is 2026-10-25 01:30 EEST (the clocks go back at 04:00 EEST that day): the UTC date is the 24th, the Athens date the 25th.
        var (code, _, _) = await SeedAsync("MOTOR-FB-C", "2026-10-24T22:30:00Z");
        var fallbackId = await RequestOkAsync(code);
        (await DecideAsync(fallbackId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await VersionColumnAsync(code, "1.1", "new_business_to")).ShouldBe("2026-10-25");
        (await VersionColumnAsync(code, "1.2", "new_business_from")).ShouldBe("2026-10-25");
        (await ResolveVersionAsync(code, "2026-10-24")).ShouldBe("1.1");
        (await ResolveVersionAsync(code, "2026-10-25")).ShouldBe("1.2");

        // An instant-form validAt resolves on its Athens date too, not on the UTC date.
        var (response, body) = await ResolveAsync(_client, code, "WEB_DIRECT", "2026-10-24T22:30:00Z");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("version").ShouldBe("1.2");
        var (before, beforeBody) = await ResolveAsync(_client, code, "WEB_DIRECT", "2026-10-24T20:59:00Z");
        before.StatusCode.ShouldBe(HttpStatusCode.OK, beforeBody?.ToJsonString());
        beforeBody.Text("version").ShouldBe("1.1");
    }

    [Fact]
    public async Task Dry_run_returns_the_preview_and_writes_nothing()
    {
        var (code, hash10, _) = await SeedAsync("MOTOR-FB-D");
        var (response, body) = await RequestAsync(code, dryRun: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["dryRun"]!.GetValue<bool>().ShouldBeTrue();
        body.Text("preview.newVersion").ShouldBe("1.2");
        body.Text("preview.source.artefactHash").ShouldBe(hash10);
        body.Text("preview.newBusinessWindow.from").ShouldContain("2026-10-08T21:00:00");
        body["fallbackId"].ShouldBeNull();
        (await ScalarAsync<long>($"SELECT count(*) FROM pfc.fallback_request f JOIN pfc.product p USING (product_id) WHERE p.code = '{code}'")).ShouldBe(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.approval_request WHERE approval_type = 'PFC.Fallback' AND object_id IN (SELECT f.fallback_id::text FROM pfc.fallback_request f JOIN pfc.product p USING (product_id) WHERE p.code = '{code}')")).ShouldBe(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = '{code}'")).ShouldBe(2);
    }

    [Fact]
    public async Task PITFALLS_3_4_5_the_maker_cannot_approve_and_a_checker_without_the_authority_grant_is_refused()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-E");
        var fallbackId = await RequestOkAsync(code);

        // The maker holds the checker role as well but is the maker.
        var (own, ownBody) = await DecideAsync(fallbackId, user: Maker, roles: CheckerRole + "," + MakerRole);
        own.StatusCode.ShouldBe(HttpStatusCode.Forbidden, ownBody?.ToJsonString());
        ownBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");

        // A user with the permission but no PFC.EMERGENCY_CHANGE grant: refused by the authority check.
        var (ungranted, ungrantedBody) = await DecideAsync(fallbackId, user: "uw-1", roles: "Staff.Underwriter");
        ungranted.StatusCode.ShouldNotBe(HttpStatusCode.OK, ungrantedBody?.ToJsonString());
        ungrantedBody.Text("code").ShouldBe("PLT-ERR-AUTHORITY-DENIED");

        // The maker cannot even ask without the release-manager role; the design authority cannot ask either.
        (await RequestAsync(code, roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RequestAsync(code, user: Checker, roles: CheckerRole)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await ScalarAsync<string>($"SELECT status FROM pfc.fallback_request WHERE fallback_id = '{fallbackId}'")).ShouldBe("PENDING_APPROVAL");
        (await VersionColumnAsync(code, "1.2", "status")).ShouldBeNull();
        (await VersionColumnAsync(code, "1.1", "new_business_to")).ShouldBeNull();
    }

    [Fact]
    public async Task PITFALL_5_a_checker_acting_for_the_maker_is_refused_as_the_makers_principal()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-F");
        var fallbackId = await RequestOkAsync(code);

        // An AI or service actor (a different identity) acting on behalf of the maker.
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("da-1");
        context.OnBehalfOf = ActorRef.User(Maker);
        context.Roles = [CheckerRole];
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        using var commandContext = context.Use(IdempotencyKey.New(), dryRun: false);
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<DecideFallback, ProductVersionDecideFallbackResponse>>();

        var result = await handler.HandleAsync(
            new DecideFallback(new ProductVersionDecideFallbackRequest
            {
                FallbackId = Guid.Parse(fallbackId),
                Decision = ProductVersionDecideFallbackRequest.DecisionValue.Approve,
                Reason = "Acting for the maker.",
            }),
            Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ToString().ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await ScalarAsync<string>($"SELECT status FROM pfc.fallback_request WHERE fallback_id = '{fallbackId}'")).ShouldBe("PENDING_APPROVAL");
    }

    [Fact]
    public async Task PITFALL_3_a_tampered_request_is_refused_and_the_database_refuses_edits_of_the_bound_content()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-G");
        var fallbackId = await RequestOkAsync(code);

        // The trigger blocks the edit even for a superuser; disable it only to prove the application re-checks the hash.
        var edit = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"UPDATE pfc.fallback_request SET reason = 'A different reason that is long enough' WHERE fallback_id = '{fallbackId}'"));
        edit.SqlState.ShouldBe("23000");
        await ExecuteAsync("ALTER TABLE pfc.fallback_request DISABLE TRIGGER trg_fallback_request_guard");
        try
        {
            await ExecuteAsync($"UPDATE pfc.fallback_request SET reason = 'A different reason that is long enough' WHERE fallback_id = '{fallbackId}'");
            var (response, body) = await DecideAsync(fallbackId);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body?.ToJsonString());
            body.Text("code").ShouldBe("PFC-ERR-SOD");
            (await VersionColumnAsync(code, "1.2", "status")).ShouldBeNull();
        }
        finally
        {
            await ExecuteAsync("ALTER TABLE pfc.fallback_request ENABLE TRIGGER trg_fallback_request_guard");
        }
    }

    [Fact]
    public async Task PITFALL_15_two_concurrent_approvals_one_wins_the_other_gets_409_and_1_2_is_published_once()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-H");
        var fallbackId = await RequestOkAsync(code);

        var results = await Task.WhenAll(DecideAsync(fallbackId, user: Checker), DecideAsync(fallbackId, user: Checker2));

        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        var loser = results.Single(r => r.Response.StatusCode != HttpStatusCode.OK);
        loser.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict, loser.Body?.ToJsonString());
        (await ScalarAsync<long>($"SELECT count(*) FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = '{code}' AND v.minor = 2")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ProductVersionPublished' AND payload->>'productCode' = '{code}' AND payload->>'version' = '1.2'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_PFC_033_no_two_locked_versions_overlap_in_new_business_before_or_after_and_the_database_enforces_it()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-I");
        var overlap = $"SELECT count(*) FROM pfc.product_version a JOIN pfc.product p USING (product_id) JOIN pfc.product_version b ON a.product_id = b.product_id AND a.product_version_id < b.product_version_id WHERE p.code = '{code}' AND a.status = 'LOCKED' AND b.status = 'LOCKED' AND daterange(a.new_business_from, a.new_business_to, '[)') && daterange(b.new_business_from, b.new_business_to, '[)')";
        (await ScalarAsync<long>(overlap)).ShouldBe(0);

        var fallbackId = await RequestOkAsync(code);
        (await DecideAsync(fallbackId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ScalarAsync<long>(overlap)).ShouldBe(0);
        // The window is gap-free too: 1.0 [.., 10-01), 1.1 [10-01, 10-09), 1.2 [10-09, open).
        (await ScalarAsync<string>($"SELECT string_agg(v.minor || ':' || v.new_business_from || '>' || coalesce(v.new_business_to::text, 'open'), ' ' ORDER BY v.minor) FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = '{code}'"))
            .ShouldBe("0:2026-01-01>2026-10-01 1:2026-10-01>2026-10-09 2:2026-10-09>open");
    }

    [Fact]
    public async Task Rejecting_ends_the_request_publishes_nothing_and_allows_a_new_request_while_a_pending_one_blocks_a_second()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-J");
        var first = await RequestOkAsync(code);

        var (duplicate, duplicateBody) = await RequestAsync(code);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict, duplicateBody?.ToJsonString());
        duplicateBody.Text("code").ShouldBe("PFC-ERR-FALLBACK-STATE");

        var (rejected, rejectedBody) = await DecideAsync(first, "REJECT");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody.Text("fallback.status").ShouldBe("REJECTED");
        (await VersionColumnAsync(code, "1.2", "status")).ShouldBeNull();
        (await VersionColumnAsync(code, "1.1", "new_business_to")).ShouldBeNull();
        (await DecideAsync(first)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var second = await RequestOkAsync(code);
        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task PITFALLS_4_7_the_client_cannot_choose_the_source_the_new_number_or_the_dates()
    {
        var (code, hash10, _) = await SeedAsync("MOTOR-FB-K");
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/pfc/v1/product-versions/fallback", Maker, MakerRole, new
        {
            productCode = code, defectiveVersion = "1.1", reason = Reason,
            sourceVersion = "1.1", newVersion = "9.9", fallbackDate = "2020-01-01", newBusinessWindow = new { start = "2020-01-01" }, artefactHash = hash10,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("preview.newVersion").ShouldBe("1.2");
        body.Text("preview.source.version").ShouldBe("1.0");
        (await ScalarAsync<string>($"SELECT fallback_date::text FROM pfc.fallback_request WHERE fallback_id = '{body.Text("fallbackId")}'")).ShouldBe("2026-10-09");

        // 1.0 has no predecessor: nothing to fall back to.
        var (noSource, noSourceBody) = await RequestAsync(code, "1.0");
        noSource.StatusCode.ShouldBe(HttpStatusCode.Conflict, noSourceBody?.ToJsonString());
        noSourceBody.Text("code").ShouldBe("PFC-ERR-FALLBACK-STATE");
        var (unknown, unknownBody) = await RequestAsync(code, "7.0");
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, unknownBody?.ToJsonString());
        unknownBody.Text("code").ShouldBe("PFC-ERR-NO-VERSION");
    }

    [Fact]
    public async Task Approval_on_the_next_athens_day_preserves_the_frozen_configuration_and_opens_windows_on_the_decision_date()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-L");
        var fallbackId = await RequestOkAsync(code);
        var boundHash = await ScalarAsync<string>($"SELECT payload_hash FROM pfc.fallback_request WHERE fallback_id = '{fallbackId}'");
        var sourceBefore = await VersionColumnAsync(code, "1.0", "artefact_hash");
        var defectiveBefore = await VersionColumnAsync(code, "1.1", "artefact_hash");

        _clock.Freeze(Instant.Parse("2026-10-09T21:30:00Z")); // 2026-10-10 00:30 Athens
        var (late, lateBody) = await DecideAsync(fallbackId);

        late.StatusCode.ShouldBe(HttpStatusCode.OK, lateBody?.ToJsonString());
        lateBody.Text("fallback.status").ShouldBe("APPLIED");
        (await ScalarAsync<string>($"SELECT payload_hash FROM pfc.fallback_request WHERE fallback_id = '{fallbackId}'")).ShouldBe(boundHash);
        (await VersionColumnAsync(code, "1.2", "new_business_from")).ShouldBe("2026-10-10");
        (await VersionColumnAsync(code, "1.1", "new_business_to")).ShouldBe("2026-10-10");
        (await VersionColumnAsync(code, "1.0", "artefact_hash")).ShouldBe(sourceBefore);
        (await VersionColumnAsync(code, "1.1", "artefact_hash")).ShouldBe(defectiveBefore);
        (await ResolveVersionAsync(code, "2026-10-09T09:00:00Z")).ShouldBe("1.1");
        (await ResolveVersionAsync(code, "2026-10-10T09:00:00Z")).ShouldBe("1.2");
    }

    [Fact]
    public async Task PITFALL_48_the_generic_plt_inbox_refuses_fallback_and_the_owner_decides_and_executes_it()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-M");
        var (_, request) = await RequestAsync(code);
        var fallbackId = request.Text("fallbackId");
        var hash = await ScalarAsync<string>($"SELECT payload_hash FROM pfc.fallback_request WHERE fallback_id = '{fallbackId}'");

        var (inbox, inboxBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/decide", Checker, CheckerRole + ",Staff.ClaimsManager",
            new { requestId = request.Text("approvalRequestId"), decision = "Approve", payloadHash = hash, comment = "ok" });
        inbox.StatusCode.ShouldBe(HttpStatusCode.Conflict, inboxBody?.ToJsonString());
        inboxBody.Text("code").ShouldBe("PLT-ERR-OWNER-DECIDED");

        // The maker cannot use the inbox approval to execute their own request; another checker can.
        (await DecideAsync(fallbackId, user: Maker, roles: CheckerRole + "," + MakerRole)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (executed, body) = await DecideAsync(fallbackId, user: Checker2);
        executed.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("fallback.status").ShouldBe("APPLIED");
        body.Text("fallback.decidedBy").ShouldBe("USER:" + Checker2);
    }

    [Fact]
    public async Task PITFALLS_17_47_the_app_role_cannot_rewrite_a_locked_version_or_reopen_a_closed_window()
    {
        var (code, _, _) = await SeedAsync("MOTOR-FB-N");
        var fallbackId = await RequestOkAsync(code);
        (await DecideAsync(fallbackId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string where(string version) => $"product_id = (SELECT product_id FROM pfc.product WHERE code = '{code}') AND major || '.' || minor = '{version}'";
        var app = database.AppConnectionString;

        // Re-opening the closed window, moving the start, changing the artefact or the fall-back links: all refused for the app role.
        foreach (var update in new[]
        {
            $"UPDATE pfc.product_version SET new_business_to = NULL WHERE {where("1.1")}",
            $"UPDATE pfc.product_version SET new_business_to = DATE '2026-12-31' WHERE {where("1.1")}",
            $"UPDATE pfc.product_version SET new_business_from = DATE '2026-09-01' WHERE {where("1.0")}",
            $"UPDATE pfc.product_version SET renewal_to = DATE '2026-12-31' WHERE {where("1.1")}",
            $"UPDATE pfc.product_version SET replaces_version_id = NULL, fallback_of_version_id = NULL WHERE {where("1.2")}",
            $"UPDATE pfc.fallback_request SET status = 'PENDING_APPROVAL' WHERE fallback_id = '{fallbackId}'",
        })
        {
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(update, app), update);
        }

        // Shortening a window stays allowed (that is what the fall-back does).
        await ExecuteAsync($"UPDATE pfc.product_version SET new_business_to = DATE '2026-10-08' WHERE {where("1.1")} AND false", app);
    }
}
