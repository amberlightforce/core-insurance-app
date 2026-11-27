using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Product.Services;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product;

/// <summary>
/// BO-PFC-04: submit, sign-off, schedule and publish over HTTP on a real PostgreSQL with the shiftable clock. Maker <c>dev:releasemgr</c>
/// (Platform.ReleaseManager) authors and submits; checker <c>dev:designauth</c> (Platform.DesignAuthority) signs off.
/// </summary>
public sealed class ReleaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Maker = "dev:releasemgr";
    private const string MakerRole = "Platform.ReleaseManager";
    private const string Checker = "dev:designauth";
    private const string CheckerRole = "Platform.DesignAuthority";
    private const string Today = "2026-10-09";

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private ShiftableClock _clock = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = "Shiftable" });
        _client = _factory.CreateClient();
        _clock = (ShiftableClock)_factory.Services.GetRequiredService<IClock>();
        _clock.Freeze(Instant.Parse(Today + "T09:00:00Z"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(string path, string user, string roles, object body, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path + query, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(body);
        var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var value = await dataSource.CreateCommand(sql).ExecuteScalarAsync(Ct);
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>A fresh product with 1.0 Locked and a change set of it (the maker is the author).</summary>
    private async Task<(string Code, string ChangeSetId)> NewChangeSetAsync(string code)
    {
        (await ImportAsync(_client, Seed(code))).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (response, body) = await SendAsync("/api/pfc/v1/change-sets", Maker, MakerRole, new { productCode = code, baseVersion = "1.0" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (code, body.Text("changeSetState.changeSetId"));
    }

    private async Task<JsonNode?> EditAsync(string changeSetId, int recordVersion, params object[] edits)
    {
        var (response, body) = await SendAsync("/api/pfc/v1/change-sets/edit", Maker, MakerRole, new { changeSetId, recordVersion, edits });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> SubmitAsync(string changeSetId, int recordVersion, string query = "") =>
        SendAsync("/api/pfc/v1/product-versions/submit", Maker, MakerRole, new { changeSetId, recordVersion }, query);

    private Task<(HttpResponseMessage Response, JsonNode? Body)> SignOffAsync(string changeSetId, string decision = "APPROVE", string user = Checker, string roles = CheckerRole) =>
        SendAsync("/api/pfc/v1/product-versions/sign-off", user, roles, new { changeSetId, decision, comment = "Reviewed the change against the PRD." });

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ScheduleAsync(string changeSetId, string activationDate, string? newBusinessTo = null, string query = "") =>
        SendAsync("/api/pfc/v1/product-versions/schedule", Maker, MakerRole, new { changeSetId, activationDate, newBusinessTo, reason = "Planned release" }, query);

    /// <summary>Edits /version, submits and approves; the change set has record version 3 after the edit and the submit.</summary>
    private async Task<string> ApprovedAsync(string code, string version)
    {
        var (_, id) = await NewChangeSetAsync(code);
        await EditAsync(id, 1, new { op = "replace", path = "/version", value = version });
        var (submitted, submitBody) = await SubmitAsync(id, 2);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submitBody?.ToJsonString());
        var (signed, signBody) = await SignOffAsync(id);
        signed.StatusCode.ShouldBe(HttpStatusCode.OK, signBody?.ToJsonString());
        return id;
    }

    private Task<string?> VersionColumnAsync(string code, string version, string column) =>
        ScalarAsync<string>($"SELECT v.{column}::text FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = '{code}' AND v.major || '.' || v.minor = '{version}'");

    private Task<long?> EventCountAsync(string type, string code, string? version = null) =>
        ScalarAsync<long?>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = '{type}' AND payload->>'productCode' = '{code}'" + (version is null ? string.Empty : $" AND payload->>'version' = '{version}'"));

    [Fact]
    public async Task Submit_sign_off_and_schedule_for_today_publishes_the_new_version_and_closes_the_old_window()
    {
        var (code, id) = await NewChangeSetAsync("MOTOR-REL1");
        await EditAsync(id, 1, new { op = "replace", path = "/version", value = "1.1" });

        var (dry, dryBody) = await SubmitAsync(id, 2, "?dryRun=true");
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
        dryBody.Text("dryRun").ShouldBe("true");
        (await ScalarAsync<long>($"SELECT count(*) FROM pfc.release_request WHERE change_set_id = '{id}'")).ShouldBe(0);

        var (submitted, submitBody) = await SubmitAsync(id, 2);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submitBody?.ToJsonString());
        submitBody.Text("changeSetState.status").ShouldBe("SUBMITTED");
        submitBody.Text("version").ShouldBe("1.1");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.approval_request WHERE approval_type = 'PFC.Release' AND request_id = '{submitBody.Text("approvalRequestId")}'")).ShouldBe(1);
        (await EventCountAsync("ProductVersionSubmitted", code)).ShouldBe(1);

        // A submitted change set is frozen.
        var (edit, editBody) = await SendAsync("/api/pfc/v1/change-sets/edit", Maker, MakerRole,
            new { changeSetId = id, recordVersion = 3, edits = new object[] { new { op = "replace", path = "/elements/1/maximum", value = 2 } } });
        edit.StatusCode.ShouldBe(HttpStatusCode.Conflict, editBody?.ToJsonString());

        // Not approved yet: schedule is refused.
        var (early, _) = await ScheduleAsync(id, Today);
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (signed, signBody) = await SignOffAsync(id);
        signed.StatusCode.ShouldBe(HttpStatusCode.OK, signBody?.ToJsonString());
        signBody.Text("approvalState").ShouldBe("APPROVED");
        (await EventCountAsync("ProductVersionApproved", code)).ShouldBe(1);

        var (preview, previewBody) = await ScheduleAsync(id, Today, query: "?dryRun=true");
        preview.StatusCode.ShouldBe(HttpStatusCode.OK, previewBody?.ToJsonString());
        (await VersionColumnAsync(code, "1.1", "status")).ShouldBeNull();

        var (scheduled, scheduleBody) = await ScheduleAsync(id, Today);
        scheduled.StatusCode.ShouldBe(HttpStatusCode.OK, scheduleBody?.ToJsonString());
        scheduleBody.Text("schedule.status").ShouldBe("PUBLISHED");

        (await VersionColumnAsync(code, "1.1", "status")).ShouldBe("LOCKED");
        (await VersionColumnAsync(code, "1.1", "new_business_from")).ShouldBe(Today);
        (await VersionColumnAsync(code, "1.0", "new_business_to")).ShouldBe(Today);
        (await EventCountAsync("ProductVersionScheduled", code)).ShouldBe(1);
        (await EventCountAsync("ProductVersionPublished", code, "1.1")).ShouldBe(1);

        // A normal publish carries no fallbackOf, so the RAT fall-back handler does not act on it.
        (await ScalarAsync<string>($"SELECT coalesce(payload->>'fallbackOf', 'none') FROM plt.outbox_message WHERE event_type = 'ProductVersionPublished' AND payload->>'productCode' = '{code}' AND payload->>'version' = '1.1'")).ShouldBe("none");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'pfc.ProductVersion.signOff' AND outcome = 'Succeeded' AND object_id = '{id}'")).ShouldBe(1);

        // The new version resolves for new business.
        var (resolved, resolvedBody) = await ResolveAsync(_client, code, "WEB_DIRECT", Today);
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, resolvedBody?.ToJsonString());
        resolvedBody.Text("version").ShouldBe("1.1");
    }

    [Fact]
    public async Task The_submitter_cannot_sign_off_and_a_role_without_the_permission_is_refused()
    {
        var (_, id) = await NewChangeSetAsync("MOTOR-REL2");
        await EditAsync(id, 1, new { op = "replace", path = "/version", value = "1.1" });
        (await SubmitAsync(id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The maker holds the checker role too, but is the same person: maker-checker refuses.
        var (self, selfBody) = await SignOffAsync(id, user: Maker, roles: MakerRole + "," + CheckerRole);
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfBody?.ToJsonString());
        selfBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");

        var (noRole, _) = await SignOffAsync(id, user: Checker, roles: MakerRole);
        noRole.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ScalarAsync<string>($"SELECT status FROM pfc.release_request WHERE change_set_id = '{id}'")).ShouldBe("SUBMITTED");
    }

    [Fact]
    public async Task A_returned_release_reopens_the_change_set_and_can_be_resubmitted_and_approved()
    {
        var (code, id) = await NewChangeSetAsync("MOTOR-REL3");
        await EditAsync(id, 1, new { op = "replace", path = "/version", value = "1.1" });
        (await SubmitAsync(id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (returned, returnedBody) = await SignOffAsync(id, "RETURN");
        returned.StatusCode.ShouldBe(HttpStatusCode.OK, returnedBody?.ToJsonString());
        returnedBody.Text("approvalState").ShouldBe("RETURNED");
        returnedBody.Text("changeSetState.status").ShouldBe("OPEN");
        (await EventCountAsync("ProductVersionReturned", code)).ShouldBe(1);

        // Fix and resubmit: a second release request with its own approval.
        var recordVersion = int.Parse(returnedBody.Text("changeSetState.recordVersion"));
        await EditAsync(id, recordVersion, new { op = "replace", path = "/elements/1/maximum", value = 3 });
        var (again, againBody) = await SubmitAsync(id, recordVersion + 1);
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        (await SignOffAsync(id)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync<string>($"SELECT string_agg(status, ',' ORDER BY submitted_at) FROM pfc.release_request WHERE change_set_id = '{id}'")).ShouldBe("RETURNED,APPROVED");
    }

    [Fact]
    public async Task Submit_refuses_lint_findings_and_a_draft_that_keeps_or_changes_its_identity()
    {
        var (_, id) = await NewChangeSetAsync("MOTOR-REL4");

        // Unchanged version number: the draft is a copy of 1.0.
        var (same, sameBody) = await SubmitAsync(id, 1);
        same.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, sameBody?.ToJsonString());
        sameBody.Text("code").ShouldBe("PFC-ERR-CHECKS-FAILED");

        await EditAsync(id, 1, new { op = "replace", path = "/version", value = "1.1" }, new { op = "replace", path = "/product/code", value = "OTHER-CODE" });
        var (renamed, renamedBody) = await SubmitAsync(id, 2);
        renamed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, renamedBody?.ToJsonString());
        renamedBody.Text("code").ShouldBe("PFC-ERR-CHECKS-FAILED");

        await EditAsync(id, 2, new { op = "replace", path = "/product/code", value = "MOTOR-REL4" }, new { op = "replace", path = "/elements/1/parent", value = "noSuchElement" });
        var (lint, lintBody) = await SubmitAsync(id, 3);
        lint.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, lintBody?.ToJsonString());
        lintBody.Text("code").ShouldBe("PFC-ERR-CHECKS-FAILED");
        (await ScalarAsync<long>($"SELECT count(*) FROM pfc.release_request WHERE change_set_id = '{id}'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_scheduled_release_is_published_by_the_job_on_its_date_and_overlaps_and_cancel_are_handled()
    {
        var first = await ApprovedAsync("MOTOR-REL5", "1.1");
        const string code = "MOTOR-REL5";

        // Schedule 1.1 for later: nothing is locked yet.
        var (scheduled, body) = await ScheduleAsync(first, "2026-10-20");
        scheduled.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("schedule.status").ShouldBe("SCHEDULED");
        (await VersionColumnAsync(code, "1.1", "status")).ShouldBeNull();
        (await VersionColumnAsync(code, "1.0", "new_business_to")).ShouldBeNull();

        // A second release for an overlapping window is refused; a past date is invalid.
        var (_, second) = await NewSecondAsync(code, "1.2");
        var (overlap, overlapBody) = await ScheduleAsync(second, "2026-11-01");
        overlap.StatusCode.ShouldBe(HttpStatusCode.Conflict, overlapBody?.ToJsonString());
        overlapBody.Text("code").ShouldBe("PFC-ERR-WINDOW-OVERLAP");
        var (past, pastBody) = await ScheduleAsync(second, "2026-10-01");
        past.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, pastBody?.ToJsonString());
        pastBody.Text("code").ShouldBe("PFC-ERR-INVALID-WINDOW");

        // Cancel the first, schedule it again at its own pace, then run the job.
        var (cancelled, cancelBody) = await SendAsync("/api/pfc/v1/product-versions/cancel-schedule", Maker, MakerRole, new { changeSetId = first, reason = "Moving the date" });
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, cancelBody?.ToJsonString());
        cancelBody.Text("schedule.status").ShouldBe("CANCELLED");
        (await SendAsync("/api/pfc/v1/product-versions/cancel-schedule", Maker, MakerRole, new { changeSetId = first, reason = "Again" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ScheduleAsync(first, "2026-10-12", "2026-12-31")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var runner = _factory.Services.GetRequiredService<ReleaseSweepRunner>();
        (await runner.RunAsync(Ct)).ShouldBe(0);
        (await VersionColumnAsync(code, "1.1", "status")).ShouldBeNull();

        _clock.Freeze(Instant.Parse("2026-10-12T05:00:00Z"));
        (await runner.RunAsync(Ct)).ShouldBe(1);
        (await VersionColumnAsync(code, "1.1", "status")).ShouldBe("LOCKED");
        (await VersionColumnAsync(code, "1.1", "new_business_to")).ShouldBe("2026-12-31");
        (await VersionColumnAsync(code, "1.0", "new_business_to")).ShouldBe("2026-10-12");
        (await EventCountAsync("ProductVersionPublished", code, "1.1")).ShouldBe(1);
        (await runner.RunAsync(Ct)).ShouldBe(0);
        (await EventCountAsync("ProductVersionPublished", code, "1.1")).ShouldBe(1);

        // A published release can no longer be cancelled.
        (await SendAsync("/api/pfc/v1/product-versions/cancel-schedule", Maker, MakerRole, new { changeSetId = first, reason = "Too late" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary>A second approved change set of an already imported product (1.1 is pending, so the next version is 1.2).</summary>
    private async Task<(string Code, string ChangeSetId)> NewSecondAsync(string code, string version)
    {
        var (response, body) = await SendAsync("/api/pfc/v1/change-sets", Maker, MakerRole, new { productCode = code, baseVersion = "1.0" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var id = body.Text("changeSetState.changeSetId");
        await EditAsync(id, 1, new { op = "replace", path = "/version", value = version });
        (await SubmitAsync(id, 2)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SignOffAsync(id)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (code, id);
    }
}
