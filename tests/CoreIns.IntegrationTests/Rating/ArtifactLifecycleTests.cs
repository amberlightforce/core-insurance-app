using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>
/// BO-RAT-06: change set, build, submit, return, schedule, cancel-schedule and rollback over HTTP on a real PostgreSQL with the shiftable clock.
/// Maker <c>dev:maker</c> (Staff.UnderwritingManager) builds and submits; checker <c>dev:checker</c> (Platform.DesignAuthority) returns or schedules.
/// Every artefact is a product-version variant of the illustrative motor tariff (each test uses its own version), nothing here is a real tariff.
/// </summary>
public sealed class ArtifactLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Maker = "dev:maker";
    private const string MakerRole = "Staff.UnderwritingManager";
    private const string Checker = "dev:checker";
    private const string CheckerRole = "Platform.DesignAuthority";
    private const string Product = "MOTOR-GR";
    private const string Today = "2026-11-02";

    private static int _versionCounter = 100;

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private ShiftableClock _clock = null!;
    private string _baseHash = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = "Shiftable" });
        _client = _factory.CreateClient();
        _clock = (ShiftableClock)_factory.Services.GetRequiredService<IClock>();
        _clock.Freeze(Instant.Parse(Today + "T09:00:00Z"));

        // The illustrative motor tariff is published lazily by the first rating call; publish it now and use it as the base artefact.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var transaction = await scope.ServiceProvider.GetRequiredService<DbSession>().BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<RatingStore>().EnsureSeededAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        _baseHash = (await ScalarAsync<string>("SELECT artefact_hash FROM rat.rating_artifact WHERE product_code = 'MOTOR-GR' AND product_version = '1.0'"))!;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string NewVersion() => "7." + Interlocked.Increment(ref _versionCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string user, string roles, object? body = null)
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

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, string user, string roles, object body) =>
        SendAsync(HttpMethod.Post, "/api/rat/v1/" + path, user, roles, body);

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var value = await dataSource.CreateCommand(sql).ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>A change set for a fresh product version, built to an artefact: returns (version, artefact hash).</summary>
    private async Task<(string Version, string Hash)> BuildAsync(string? version = null, string dataStatus = "ILLUSTRATIVE_TEST_DATA", object? tableHashes = null)
    {
        version ??= NewVersion();
        var (created, change) = await PostAsync("rating-change-sets", Maker, MakerRole, new { productCode = Product, productVersion = version, baseArtefactHash = _baseHash, tableHashes, reason = "Test change" });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, change?.ToJsonString());
        var (built, body) = await PostAsync("rating-artifacts/build", Maker, MakerRole, new { changeSetId = change.Text("changeSetId"), dataStatus });
        built.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("artefactStatus").ShouldBe("DRAFT");
        return (version, body.Text("artefactHash"));
    }

    private async Task<string> LifecycleAsync(string hash)
    {
        var (response, body) = await SendAsync(HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{hash}", Maker, MakerRole);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("status.lifecycle");
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> SubmitAsync(string hash) =>
        PostAsync("rating-artifacts/submit", Maker, MakerRole, new { artefactHash = hash, reason = "Ready for review" });

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ScheduleAsync(string hash, string? effectiveFrom = null, string user = Checker, string roles = CheckerRole) =>
        PostAsync("rating-artifacts/schedule", user, roles, new { artefactHash = hash, effectiveFrom, reason = "Approved" });

    [Fact]
    public async Task Build_is_deterministic_and_pins_the_tables_of_the_base_artefact()
    {
        var (version, hash) = await BuildAsync();
        hash.Length.ShouldBe(64);

        // The same inputs give the same artefact: a second change set with the same base and version builds to the same hash and does not create a second release.
        var (_, again) = await PostAsync("rating-change-sets", Maker, MakerRole, new { productCode = Product, productVersion = version, baseArtefactHash = _baseHash });
        var (_, rebuilt) = await PostAsync("rating-artifacts/build", Maker, MakerRole, new { changeSetId = again.Text("changeSetId") });
        rebuilt.Text("artefactHash").ShouldBe(hash);
        rebuilt.Text("created").ShouldBe("false");
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.artifact_release WHERE artefact_hash = '{hash}'")).ShouldBe(1);

        var (_, artefact) = await SendAsync(HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{hash}", Maker, MakerRole);
        artefact.Text("manifest.productVersion").ShouldBe(version);
        artefact!["manifest"]!["tables"]!.AsArray().Count.ShouldBeGreaterThan(0);
        artefact.Text("manifest.dataStatus").ShouldBe("ILLUSTRATIVE_TEST_DATA");

        // A draft stays hidden from roles that only rate.
        var (hidden, _) = await SendAsync(HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{hash}", "dev:uw", Underwriter);
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_imported_table_linked_to_the_change_set_is_pinned_by_the_build()
    {
        var code = "TST_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var version = NewVersion();
        var (_, change) = await PostAsync("rating-change-sets", Maker, MakerRole, new { productCode = Product, productVersion = version, baseArtefactHash = _baseHash });
        var (import, imported) = await PostAsync("rate-tables/import", Maker, MakerRole, new
        {
            changeSet = change.Text("changeSetId"),
            rows = new object[] { new { id = "R1", coverageCode = "\"MTPL\"", value = "10.00" } },
            mapping = new
            {
                code, version = "1", hitPolicy = "First", effectiveFrom = "2027-01-01", description = "Synthetic test table",
                inputs = new[] { new { name = "coverageCode", type = "string", expression = "coverage" } },
                outputs = new[] { new { name = "value", type = "decimal" } },
            },
        });
        import.StatusCode.ShouldBe(HttpStatusCode.OK, imported?.ToJsonString());
        var tableHash = imported.Text("tableVersion.hash");

        var (_, built) = await PostAsync("rating-artifacts/build", Maker, MakerRole, new { changeSetId = change.Text("changeSetId") });
        var (_, artefact) = await SendAsync(HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{built.Text("artefactHash")}", Maker, MakerRole);
        artefact!["manifest"]!["tables"]!.AsArray().Any(t => t!["slot"]!.GetValue<string>() == code && t["hash"]!.GetValue<string>() == tableHash).ShouldBeTrue();

        // A change set of someone else's id, or one already built, is refused.
        var (bad, _) = await PostAsync("rate-tables/import", Maker, MakerRole, new
        {
            changeSet = Guid.NewGuid().ToString(),
            rows = new object[] { new { id = "R1", coverageCode = "\"MTPL\"", value = "10.00" } },
            mapping = new { code, version = "1", hitPolicy = "First", effectiveFrom = "2027-01-01", inputs = new[] { new { name = "coverageCode", type = "string", expression = "coverage" } }, outputs = new[] { new { name = "value", type = "decimal" } } },
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Submit_then_the_checker_schedules_and_the_artefact_activates_today()
    {
        var (version, hash) = await BuildAsync();
        (await PostAsync("rating-artifacts/schedule", Checker, CheckerRole, new { artefactHash = hash })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict); // not submitted yet

        var (submit, submitted) = await SubmitAsync(hash);
        submit.StatusCode.ShouldBe(HttpStatusCode.OK, submitted?.ToJsonString());
        submitted.Text("artefactStatus").ShouldBe("SUBMITTED");
        (await LifecycleAsync(hash)).ShouldBe("SUBMITTED");

        var (schedule, scheduled) = await ScheduleAsync(hash);
        schedule.StatusCode.ShouldBe(HttpStatusCode.OK, scheduled?.ToJsonString());
        scheduled.Text("artefactStatus").ShouldBe("ACTIVE");
        scheduled.Text("effectiveFrom").ShouldBe(Today);
        (await LifecycleAsync(hash)).ShouldBe("ACTIVE");
        (await ScalarAsync<string>($"SELECT status FROM rat.artifact_release WHERE artefact_hash = '{hash}'")).ShouldBe("SCHEDULED");
        (await ScalarAsync<string>($"SELECT approval_type FROM plt.approval_request WHERE request_id = '{submitted.Text("approvalRequestId")}'")).ShouldBe("RAT.Release");
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{submitted.Text("approvalRequestId")}'")).ShouldBe("Approved");

        // Once released the artefact is visible to the roles that rate, and it cannot be scheduled twice.
        var (visible, _) = await SendAsync(HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{hash}", "dev:uw", Underwriter);
        visible.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScheduleAsync(hash)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        version.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_builder_and_the_submitter_cannot_approve_their_own_release()
    {
        var (_, hash) = await BuildAsync();
        (await SubmitAsync(hash)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The maker also holds the checker role: still refused (builder and submitter are the same person).
        var (own, problem) = await ScheduleAsync(hash, user: Maker, roles: MakerRole + "," + CheckerRole);
        own.StatusCode.ShouldBe(HttpStatusCode.Forbidden, problem?.ToJsonString());
        problem.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await PostAsync("rating-artifacts/return", Maker, MakerRole + "," + CheckerRole, new { artefactHash = hash, reason = "Mine" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A role without the schedule permission is refused before anything else.
        (await ScheduleAsync(hash, user: "dev:other", roles: MakerRole)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LifecycleAsync(hash)).ShouldBe("SUBMITTED");

        // A different person who built the artefact is also barred from approving a release someone else submitted.
        var (version, built) = await BuildAsync();
        var (_, again) = await PostAsync("rating-artifacts/submit", "dev:submitter", MakerRole, new { artefactHash = built });
        again.Text("artefactStatus").ShouldBe("SUBMITTED");
        var (builderApproves, builderProblem) = await ScheduleAsync(built, user: Maker, roles: CheckerRole);
        builderApproves.StatusCode.ShouldBe(HttpStatusCode.Forbidden, builderProblem?.ToJsonString());
        version.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_returned_artefact_goes_back_to_draft_and_can_be_resubmitted()
    {
        var (_, hash) = await BuildAsync();
        (await SubmitAsync(hash)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync("rating-artifacts/return", Checker, CheckerRole, new { artefactHash = hash })).Response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity); // a reason is required

        var (returned, body) = await PostAsync("rating-artifacts/return", Checker, CheckerRole, new { artefactHash = hash, reason = "Check the minimum premium." });
        returned.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (await LifecycleAsync(hash)).ShouldBe("DRAFT");
        (await ScalarAsync<string>($"SELECT status FROM rat.artifact_release WHERE artefact_hash = '{hash}'")).ShouldBe("RETURNED");
        (await ScheduleAsync(hash)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (resubmit, resubmitted) = await SubmitAsync(hash);
        resubmit.StatusCode.ShouldBe(HttpStatusCode.OK, resubmitted?.ToJsonString());
        (await ScheduleAsync(hash)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LifecycleAsync(hash)).ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task A_future_schedule_can_be_cancelled_and_a_date_in_the_past_is_refused()
    {
        var (version, hash) = await BuildAsync();
        (await SubmitAsync(hash)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScheduleAsync(hash, "2026-10-01")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (schedule, scheduled) = await ScheduleAsync(hash, "2026-11-12");
        schedule.StatusCode.ShouldBe(HttpStatusCode.OK, scheduled?.ToJsonString());
        scheduled.Text("artefactStatus").ShouldBe("SCHEDULED");
        (await LifecycleAsync(hash)).ShouldBe("SCHEDULED");

        var (cancel, cancelled) = await PostAsync("rating-artifacts/cancel-schedule", Maker, "Platform.ReleaseManager", new { artefactHash = hash, reason = "Moving the date" });
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK, cancelled?.ToJsonString());
        (await LifecycleAsync(hash)).ShouldBe("APPROVED");
        (await ScalarAsync<string>($"SELECT status FROM rat.rate_activation WHERE artefact_hash = '{hash}'")).ShouldBe("Cancelled");
        (await PostAsync("rating-artifacts/cancel-schedule", Maker, "Platform.ReleaseManager", new { artefactHash = hash })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // An approved artefact can be scheduled again without a new approval, on another date.
        var (again, rescheduled) = await ScheduleAsync(hash, "2026-11-20");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, rescheduled?.ToJsonString());
        (await LifecycleAsync(hash)).ShouldBe("SCHEDULED");
        version.ShouldNotBeNull();
    }

    [Fact]
    public async Task Activation_is_effective_dated_and_rollback_reactivates_the_prior_artefact_keeping_history()
    {
        var version = NewVersion();
        var (_, first) = await BuildAsync(version);
        (await SubmitAsync(first)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScheduleAsync(first, Today)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A second artefact for the same product version (approved data status is part of the artefact, so another hash) from five days later.
        var (_, second) = await BuildAsync(version, dataStatus: "APPROVED");
        second.ShouldNotBe(first);
        (await SubmitAsync(second)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (schedule, scheduled) = await ScheduleAsync(second, "2026-11-07");
        schedule.StatusCode.ShouldBe(HttpStatusCode.OK, scheduled?.ToJsonString());
        (await ScalarAsync<string>($"SELECT effective_to::text FROM rat.rate_activation WHERE artefact_hash = '{first}'")).ShouldBe("2026-11-07");
        (await LifecycleAsync(first)).ShouldBe("ACTIVE");
        (await LifecycleAsync(second)).ShouldBe("SCHEDULED");

        _clock.Freeze(Instant.Parse("2026-11-08T09:00:00Z"));
        (await LifecycleAsync(first)).ShouldBe("SUPERSEDED");
        (await LifecycleAsync(second)).ShouldBe("ACTIVE");

        var (rollback, rolled) = await PostAsync("rating-artifacts/rollback", Checker, CheckerRole, new { productCode = Product, productVersion = version, reason = "Wrong tariff published" });
        rollback.StatusCode.ShouldBe(HttpStatusCode.OK, rolled?.ToJsonString());
        rolled.Text("withdrawnHash").ShouldBe(second);
        rolled.Text("reactivatedHash").ShouldBe(first);
        (await LifecycleAsync(first)).ShouldBe("ACTIVE");
        (await LifecycleAsync(second)).ShouldBe("WITHDRAWN");

        // History is kept and nothing was deleted: both artefacts and both activations are still there.
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.rate_activation WHERE product_version = '{version}'")).ShouldBe(2);
        (await ScalarAsync<string>($"SELECT status FROM rat.rate_activation WHERE artefact_hash = '{second}'")).ShouldBe("RolledBack");
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.rating_artifact WHERE artefact_hash IN ('{first}', '{second}')")).ShouldBe(2);
        (await ScalarAsync<string>($"SELECT status FROM rat.artifact_release WHERE artefact_hash = '{second}'")).ShouldBe("ROLLED_BACK");

        // The first artefact is now the oldest activation: nothing earlier to go back to.
        (await PostAsync("rating-artifacts/rollback", Checker, CheckerRole, new { productCode = Product, productVersion = version, reason = "Again" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync("rating-artifacts/rollback", Checker, CheckerRole, new { productCode = Product, productVersion = version })).Response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Approved_data_status_builds_a_distinct_artefact_over_the_same_tables()
    {
        var version = NewVersion();
        var (_, illustrative) = await BuildAsync(version);
        var (_, approved) = await BuildAsync(version, dataStatus: "APPROVED");
        approved.ShouldNotBe(illustrative);
        var (_, body) = await SendAsync(HttpMethod.Get, $"/api/rat/v1/rating-artifacts/{approved}", Maker, MakerRole);
        body.Text("manifest.dataStatus").ShouldBe("APPROVED");
        body!["manifest"]!["tables"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }
}
