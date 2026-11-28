using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>BO-PLT-05: user directory, sign-in provisioning, suspension, role grant with a second approver, LE-scoped search.</summary>
public sealed class UserDirectoryTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Admin = "Platform.Admin";
    private const string Staff = "Staff.Underwriter";

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

    private static string Name(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

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

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>The first call of an identity provisions its entry; returns its user id.</summary>
    private async Task<string> ProvisionAsync(string oid, string roles = Admin)
    {
        var (_, body) = await SendAsync(HttpMethod.Get, $"/api/plt/v1/users/search?q={oid}", Name("admin-p"), Admin);
        if (body?["items"]?.AsArray().Count is null or 0)
        {
            (await SendAsync(HttpMethod.Get, "/api/plt/v1/user-profile/get-preferences", oid, roles)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (_, body) = await SendAsync(HttpMethod.Get, $"/api/plt/v1/users/search?q={oid}", Name("admin-p"), Admin);
        }

        return body!["items"]![0]!["user"]!["userId"]!.GetValue<string>();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task BO_PLT_05_the_first_valid_token_provisions_the_profile_and_language_is_stored()
    {
        var oid = Name("first-signin");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.user_profile WHERE entra_oid = '{oid}'")).ShouldBe(0);

        var (set, setBody) = await SendAsync(HttpMethod.Get, "/api/plt/v1/user-profile/get-preferences", oid, Staff);
        // The first call creates the entry with the language of the request (Accept-Language), which the user can then read.
        set.StatusCode.ShouldBe(HttpStatusCode.OK, setBody?.ToJsonString());
        setBody!["savedPreferences"]!["uiLanguage"]!.GetValue<string>().ShouldBe("en");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.user_profile WHERE entra_oid = '{oid}' AND status = 'Active' AND language = 'en'")).ShouldBe(1);

        var (changed, _) = await SendAsync(HttpMethod.Post, "/api/plt/v1/user-profile/set-language", oid, Staff, new { uiLanguage = "el", source = "test" });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, after) = await SendAsync(HttpMethod.Get, "/api/plt/v1/user-profile/get-preferences", oid, Staff);
        after!["savedPreferences"]!["uiLanguage"]!.GetValue<string>().ShouldBe("el");

        var (bad, _) = await SendAsync(HttpMethod.Post, "/api/plt/v1/user-profile/set-language", oid, Staff, new { uiLanguage = "fr" });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BO_PLT_05_suspend_blocks_the_next_call_and_reactivate_restores_it_and_disable_is_final()
    {
        var oid = Name("victim");
        var admin = Name("admin-s");
        var userId = await ProvisionAsync(oid, Staff);

        (await SendAsync(HttpMethod.Get, "/api/plt/v1/user-profile/get-preferences", oid, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (suspended, suspendedBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/users/suspend", admin, Admin, new { userId, reason = "investigation" });
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK, suspendedBody?.ToJsonString());
        suspendedBody!["user"]!["status"]!.GetValue<string>().ShouldBe("Suspended");

        var (blocked, blockedBody) = await SendAsync(HttpMethod.Get, "/api/plt/v1/user-profile/get-preferences", oid, Staff);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        blockedBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-USER-SUSPENDED");

        (await SendAsync(HttpMethod.Post, "/api/plt/v1/users/suspend", admin, Admin, new { userId, reason = "again" })).Response.StatusCode.ShouldBe((HttpStatusCode)422);
        (await SendAsync(HttpMethod.Post, "/api/plt/v1/users/reactivate", admin, Admin, new { userId, reason = "cleared" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/plt/v1/user-profile/get-preferences", oid, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await SendAsync(HttpMethod.Post, "/api/plt/v1/users/disable", admin, Admin, new { userId, reason = "left the company" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (disabled, disabledBody) = await SendAsync(HttpMethod.Get, "/api/plt/v1/user-profile/get-preferences", oid, Staff);
        disabled.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        disabledBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-USER-DISABLED");
        (await SendAsync(HttpMethod.Post, "/api/plt/v1/users/reactivate", admin, Admin, new { userId, reason = "oops" })).Response.StatusCode.ShouldBe((HttpStatusCode)422);

        // Every change is audited.
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE object_id = '{userId}' AND operation IN ('plt.User.suspend', 'plt.User.reactivate', 'plt.User.disable') AND outcome = 'Succeeded'"))
            .ShouldBe(3);

        // Only an administrator administers users; nobody changes their own entry.
        (await SendAsync(HttpMethod.Post, "/api/plt/v1/users/suspend", oid, Staff, new { userId, reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var self = Name("self-admin");
        var selfId = await ProvisionAsync(self);
        var (selfSuspend, selfBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/users/suspend", self, Admin, new { userId = selfId, reason = "x" });
        selfSuspend.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        selfBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-SOD");
    }

    [Fact]
    public async Task BO_PLT_05_create_get_and_update_a_directory_entry()
    {
        var oid = Name("created");
        var admin = Name("admin-c");
        var (created, body) = await SendAsync(HttpMethod.Post, "/api/plt/v1/users", admin, Admin, new { entraOid = oid, displayName = "Maria Papadopoulou", email = "maria@example.test", language = "en" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var userId = body!["user"]!["userId"]!.GetValue<string>();
        body["user"]!["status"]!.GetValue<string>().ShouldBe("Active");

        (await SendAsync(HttpMethod.Post, "/api/plt/v1/users", admin, Admin, new { entraOid = oid, displayName = "Duplicate" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (patched, patchedBody) = await SendAsync(HttpMethod.Patch, $"/api/plt/v1/users/{userId}", admin, Admin, new { displayName = "Maria P." });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK, patchedBody?.ToJsonString());
        var (_, got) = await SendAsync(HttpMethod.Get, $"/api/plt/v1/users/{userId}", admin, Admin);
        got!["user"]!["displayName"]!.GetValue<string>().ShouldBe("Maria P.");
        got["user"]!["language"]!.GetValue<string>().ShouldBe("en");
        got["user"]!["roleAssignments"]!.AsArray().Count.ShouldBe(0);

        (await SendAsync(HttpMethod.Get, $"/api/plt/v1/users/{Guid.NewGuid()}", admin, Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, "/api/plt/v1/users/search", admin, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BO_PLT_05_role_grant_needs_a_second_approver_and_a_self_grant_is_refused()
    {
        var maker = Name("admin-maker");
        var checker = Name("admin-checker");
        var target = Name("grantee");
        var targetId = await ProvisionAsync(target, Staff);
        var makerId = await ProvisionAsync(maker);

        // Granting a role to yourself is refused (SoD).
        var (self, selfBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/grant", maker, Admin, new { userId = makerId, role = "Staff.Billing" });
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        selfBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-SOD");
        (await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/grant", maker, Admin, new { userId = targetId, role = "No.Such.Role" })).Response.StatusCode.ShouldBe((HttpStatusCode)422);

        // The first call only requests the approval.
        var grant = new { userId = targetId, role = "Staff.Billing", scope = "BIL" };
        var (requested, requestedBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/grant", maker, Admin, grant);
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, requestedBody?.ToJsonString());
        requestedBody!["status"]!.GetValue<string>().ShouldBe("PendingApproval");
        var approvalId = requestedBody["approvalRequestId"]!.GetValue<string>();
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.role_assignment WHERE user_id = '{targetId}'")).ShouldBe(0);

        // Not executable before approval; the maker cannot approve their own request.
        var withApproval = new { userId = targetId, role = "Staff.Billing", scope = "BIL", approvalRequestId = approvalId };
        var (early, earlyBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/grant", maker, Admin, withApproval);
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        earlyBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-APPROVAL-STALE");
        var (_, approval) = await SendAsync(HttpMethod.Get, $"/api/plt/v1/approval/{approvalId}", checker, Admin);
        var hash = approval!["request"]!["payloadHash"]!.GetValue<string>();
        var (selfDecide, selfDecideBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/decide", maker, Admin, new { requestId = approvalId, decision = "Approve", payloadHash = hash });
        selfDecide.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfDecideBody?.ToJsonString());

        // A second administrator approves; then the grant records the assignment for exactly the approved content.
        var (decided, decidedBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/decide", checker, Admin, new { requestId = approvalId, decision = "Approve", payloadHash = hash });
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decidedBody?.ToJsonString());
        var (other, otherBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/grant", maker, Admin, new { userId = targetId, role = "Staff.Billing", scope = "ALL", approvalRequestId = approvalId });
        other.StatusCode.ShouldBeOneOf((HttpStatusCode)422, HttpStatusCode.Conflict);
        otherBody!["code"]!.GetValue<string>().ShouldStartWith("PLT-ERR-");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.role_assignment WHERE user_id = '{targetId}'")).ShouldBe(0);

        var (granted, grantedBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/grant", maker, Admin, withApproval);
        granted.StatusCode.ShouldBe(HttpStatusCode.OK, grantedBody?.ToJsonString());
        grantedBody!["status"]!.GetValue<string>().ShouldBe("Granted");
        var assignmentId = grantedBody["assignment"]!["assignmentId"]!.GetValue<string>();
        grantedBody["assignment"]!["grantedBy"]!.GetValue<string>().ShouldBe(maker);
        grantedBody["assignment"]!["approvedBy"]!.GetValue<string>().ShouldBe(checker);

        // The same approval cannot be used twice.
        (await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/grant", maker, Admin, withApproval)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (_, got) = await SendAsync(HttpMethod.Get, $"/api/plt/v1/users/{targetId}", maker, Admin);
        got!["user"]!["roleAssignments"]!.AsArray().Select(a => a!["role"]!.GetValue<string>()).ShouldBe(["Staff.Billing"]);

        var (revoked, revokedBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/revoke", maker, Admin, new { assignmentId, reason = "moved team" });
        revoked.StatusCode.ShouldBe(HttpStatusCode.OK, revokedBody?.ToJsonString());
        revokedBody!["assignment"]!["status"]!.GetValue<string>().ShouldBe("Revoked");
        (await SendAsync(HttpMethod.Post, "/api/plt/v1/role-assignments/revoke", maker, Admin, new { assignmentId, reason = "again" })).Response.StatusCode.ShouldBe((HttpStatusCode)422);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE object_id = '{targetId}' AND operation IN ('plt.RoleAssignment.grant', 'plt.RoleAssignment.revoke') AND outcome = 'Succeeded'"))
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task BO_PLT_05_search_is_scoped_to_the_legal_entity_of_the_request()
    {
        var admin = Name("admin-q");
        var mine = Name("scoped-mine");
        var foreign = Name("scoped-foreign");
        var mineId = await ProvisionAsync(mine, Staff);
        var foreignId = Guid.NewGuid();
        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var insert = dataSource.CreateCommand(
                $"INSERT INTO plt.user_profile (user_id, legal_entity, entra_oid, display_name, language, status, created_by, created_at, updated_at) "
                + $"VALUES ('{foreignId}', 'XX-OTHER-LE', '{foreign}', 'Foreign {foreign}', 'el', 'Active', 'test', now(), now())");
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (_, found) = await SendAsync(HttpMethod.Get, "/api/plt/v1/users/search?q=scoped-", admin, Admin);
        var ids = found!["items"]!.AsArray().Select(i => i!["user"]!["userId"]!.GetValue<string>()).ToList();
        ids.ShouldContain(mineId);
        ids.ShouldNotContain(foreignId.ToString());
        (await SendAsync(HttpMethod.Get, $"/api/plt/v1/users/{foreignId}", admin, Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, "/api/plt/v1/users/suspend", admin, Admin, new { userId = foreignId, reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Status filter and paging.
        var (_, suspendedOnly) = await SendAsync(HttpMethod.Get, "/api/plt/v1/users/search?q=scoped-mine&status=Suspended", admin, Admin);
        suspendedOnly!["items"]!.AsArray().Count.ShouldBe(0);
        var (_, page) = await SendAsync(HttpMethod.Get, "/api/plt/v1/users/search?limit=1", admin, Admin);
        page!["items"]!.AsArray().Count.ShouldBe(1);
        page["nextCursor"]!.GetValue<string>().ShouldBe("1");
    }
}
