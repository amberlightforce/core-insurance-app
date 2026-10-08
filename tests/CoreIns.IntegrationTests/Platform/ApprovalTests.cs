using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Claims.Authority;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>
/// SL2-PLT maker-checker (REQ-PLT-004, -114, -115, -116, -117, -121) on PostgreSQL 17 through the real Host, with the
/// claims authority types and the illustrative D-SL2-03 grants of appsettings.json.
/// </summary>
public sealed class ApprovalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Handler = "Staff.ClaimsHandler";
    private const string Manager = "Staff.ClaimsManager";

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

    private static string NewHash() => Sha256Hash.ComputeUtf8(Guid.NewGuid().ToString()).Value;

    private static object RequestBody(string subjectId, string hash, string amount, string referralRole = Manager, string authorityType = "CLM.PAYMENT") => new
    {
        type = "CLM.TRANSACTION_SET",
        objectRef = new { module = "CLM", type = "TransactionSet", id = subjectId },
        payloadHash = hash,
        authority = new { type = authorityType, amount = new { amount, currency = "EUR" }, codes = new { costType = "INDEMNITY" } },
        referralRole,
        reason = "Payment above the handler's limit",
        diff = new { amount = new { after = amount } },
    };

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

    private async Task<string> CreateAsync(string maker, string subjectId, string hash, string amount)
    {
        var (response, body) = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/request", maker, Handler, RequestBody(subjectId, hash, amount));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["request"]!["requestId"]!.GetValue<string>();
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> DecideAsync(string checker, string roles, string requestId, string hash, string decision = "Approve", string? comment = null) =>
        SendAsync(HttpMethod.Post, "/api/plt/v1/approval/decide", checker, roles, new { requestId, decision, payloadHash = hash, comment });

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task REQ_PLT_004_a_manager_approves_a_handlers_request_with_event_audit_and_inbox()
    {
        var subject = Guid.NewGuid().ToString();
        var hash = NewHash();
        var requestId = await CreateAsync("handler-a", subject, hash, "5000.01");

        // The manager's inbox lists it; the maker's own inbox (and another role's) does not.
        var (inbox, inboxBody) = await SendAsync(HttpMethod.Get, "/api/plt/v1/approval?limit=200", "manager-b", Manager);
        inbox.StatusCode.ShouldBe(HttpStatusCode.OK, inboxBody?.ToJsonString());
        inboxBody!["items"]!.AsArray().Select(i => i!["request"]!["requestId"]!.GetValue<string>()).ShouldContain(requestId);
        var (_, ownInbox) = await SendAsync(HttpMethod.Get, "/api/plt/v1/approval?limit=200&role=Staff.ClaimsManager", "handler-a", $"{Handler},{Manager}");
        ownInbox!["items"]!.AsArray().Select(i => i!["request"]!["requestId"]!.GetValue<string>()).ShouldNotContain(requestId);

        var (decided, body) = await DecideAsync("manager-b", Manager, requestId, hash);

        decided.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["request"]!["status"]!.GetValue<string>().ShouldBe("Approved");
        body["decision"]!["decision"]!.GetValue<string>().ShouldBe("Approved");
        body["decision"]!["checker"]!["id"]!.GetValue<string>().ShouldBe("manager-b");
        var (get, getBody) = await SendAsync(HttpMethod.Get, $"/api/plt/v1/approval/{requestId}", "manager-b", Manager);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        getBody!["request"]!["status"]!.GetValue<string>().ShouldBe("Approved");
        getBody["request"]!["authority"]!["amount"]!["amount"]!.GetValue<string>().ShouldBe("5000.01");

        // REQ-PLT-116: ApprovalDecided in the outbox, with the subject and hash; the decision is audited with its authority check.
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ApprovalDecided' AND aggregate_id = '{requestId}' "
            + $"AND payload->>'decision' = 'APPROVED' AND payload->>'payloadHash' = '{hash}' AND payload->'objectRef'->>'id' = '{subject}'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.Approval.decide' AND object_id = '{requestId}' "
            + "AND outcome = 'Succeeded' AND authority_check_id IS NOT NULL")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.Approval.request' AND object_id = '{requestId}'")).ShouldBe(1);

        // A decided request is decided once: a second approval is stale, never a 500.
        var (again, againBody) = await DecideAsync("manager-c", Manager, requestId, hash);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        againBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-APPROVAL-STALE");
    }

    [Fact]
    public async Task REQ_PLT_115_the_maker_cannot_decide_their_own_request()
    {
        var hash = NewHash();
        var requestId = await CreateAsync("maker-self", Guid.NewGuid().ToString(), hash, "100.00");

        // Even holding the manager role (and enough authority), the maker is refused; the request stays pending.
        var (response, body) = await DecideAsync("maker-self", $"{Handler},{Manager}", requestId, hash);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        body!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("PendingApproval");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{requestId}'")).ShouldBe(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.Approval.decide' AND object_id = '{requestId}' "
            + "AND outcome = 'Rejected' AND error_code = 'PLT-ERR-SELF-APPROVAL'")).ShouldBe(1);
    }

    [Fact]
    public async Task D_SL2_03_a_checker_without_enough_authority_is_refused()
    {
        var hash = NewHash();
        var requestId = await CreateAsync("handler-x", Guid.NewGuid().ToString(), hash, "5000.01");

        // Another handler: 5000.01 is above the handler limit, so the decision is referred to the manager role.
        var (refer, referBody) = await DecideAsync("handler-y", Handler, requestId, hash);
        refer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        referBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-AUTHORITY-REFERRAL-REQUIRED");
        referBody["referralTargets"]!.GetValue<string>().ShouldBe($"ROLE:{Manager}");

        // A role without the plt.Approval.decide permission never reaches the service.
        var (noPermission, _) = await DecideAsync("underwriter-z", "Staff.Underwriter", requestId, hash);
        noPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Above the manager's limit nobody may approve: DENY.
        var bigHash = NewHash();
        var big = await CreateAsync("handler-x", Guid.NewGuid().ToString(), bigHash, "50000.01");
        var (denied, deniedBody) = await DecideAsync("manager-m", Manager, big, bigHash);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        deniedBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-AUTHORITY-DENIED");
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("PendingApproval");
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{big}'")).ShouldBe("PendingApproval");
    }

    [Fact]
    public async Task REQ_PLT_117_a_decision_on_a_changed_content_hash_is_stale_and_editors_cannot_approve()
    {
        var subject = Guid.NewGuid().ToString();
        var first = NewHash();
        var requestId = await CreateAsync("handler-1", subject, first, "6000.00");

        // The checker saw other content than the request's: stale.
        var (wrongHash, wrongBody) = await DecideAsync("manager-1", Manager, requestId, NewHash());
        wrongHash.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        wrongBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-APPROVAL-STALE");

        // The subject changes (another user edits it): the owning module requests again with the new hash; the old
        // request is withdrawn (ApprovalDecided WITHDRAWN) and deciding it with the old hash is stale.
        var second = NewHash();
        var (resubmitted, resubmittedBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/request", "manager-2", $"{Handler},{Manager}", RequestBody(subject, second, "6500.00"));
        resubmitted.StatusCode.ShouldBe(HttpStatusCode.OK, resubmittedBody?.ToJsonString());
        var newId = resubmittedBody!["request"]!["requestId"]!.GetValue<string>();
        resubmittedBody["request"]!["supersedes"]!.GetValue<string>().ShouldBe(requestId);
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("Withdrawn");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{requestId}' AND payload->>'decision' = 'WITHDRAWN'")).ShouldBe(1);

        var (old, oldBody) = await DecideAsync("manager-1", Manager, requestId, first);
        old.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        oldBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-APPROVAL-STALE");
        var (oldHashOnNew, oldHashOnNewBody) = await DecideAsync("manager-1", Manager, newId, first);
        oldHashOnNew.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        oldHashOnNewBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-APPROVAL-STALE");

        // handler-1 made the earlier version: an editor of the content, so it may not decide the new one (even as manager).
        var (editor, editorBody) = await DecideAsync("handler-1", Manager, newId, second);
        editor.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        editorBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-EDITOR-CANNOT-APPROVE");

        // Re-submitting identical content keeps the pending request.
        var (same, sameBody) = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/request", "manager-2", Handler, RequestBody(subject, second, "6500.00"));
        same.StatusCode.ShouldBe(HttpStatusCode.OK);
        sameBody!["request"]!["requestId"]!.GetValue<string>().ShouldBe(newId);

        var (rejectWithoutComment, rejectBody) = await DecideAsync("manager-1", Manager, newId, second, "Reject");
        rejectWithoutComment.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        rejectBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-VALIDATION");
        var (rejected, rejectedBody) = await DecideAsync("manager-1", Manager, newId, second, "Reject", "Repair estimate missing");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody!["decision"]!["comment"]!.GetValue<string>().ShouldBe("Repair estimate missing");
    }

    [Fact]
    public async Task Concurrent_decisions_on_one_request_exactly_one_wins_and_the_others_are_stale()
    {
        var hash = NewHash();
        var requestId = await CreateAsync("handler-c", Guid.NewGuid().ToString(), hash, "7000.00");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            DecideAsync($"manager-{i}", Manager, requestId, hash, i % 2 == 0 ? "Approve" : "Reject", "concurrent")));

        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        results.Where(r => r.Response.StatusCode != HttpStatusCode.OK)
            .ShouldAllBe(r => r.Response.StatusCode == HttpStatusCode.Conflict && r.Body!["code"]!.GetValue<string>() == "PLT-ERR-APPROVAL-STALE");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{requestId}' AND event_type = 'ApprovalDecided'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.Approval.decide' AND object_id = '{requestId}' AND outcome = 'Succeeded'")).ShouldBe(1);
    }

    [Fact]
    public async Task In_process_the_request_and_the_decision_commit_with_the_callers_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = new ObjectRef(ModuleCode.CLM, "TransactionSet", Guid.NewGuid().ToString());
        var hash = Sha256Hash.ComputeUtf8(subject.Id);
        var request = new ApprovalRequestRequest
        {
            Type = "CLM.TRANSACTION_SET",
            ObjectRef = subject,
            PayloadHash = hash,
            Authority = new ApprovalAuthority { Type = "CLM.RESERVE", Amount = Money.Of(5000.01m, "EUR") },
            ReferralRole = Manager,
        };

        // A caller that rolls back leaves no request behind.
        await using (var scope = Scope("clm-handler", Handler, out var services))
        {
            var session = services.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(ct);
            await services.GetRequiredService<IPlatformApprovalService>().RequestAsync(request, CommandOptions.New(), ct);
            await transaction.RollbackAsync(ct);
        }

        (await ScalarAsync<long>($"SELECT count(*) FROM plt.approval_request WHERE object_id = '{subject.Id}'")).ShouldBe(0);

        Guid requestId;
        await using (var scope = Scope("clm-handler", Handler, out var services))
        {
            var session = services.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(ct);
            requestId = (await services.GetRequiredService<IPlatformApprovalService>().RequestAsync(request, CommandOptions.New(), ct)).Request.RequestId;
            await transaction.CommitAsync(ct);
        }

        // The decision and its ApprovalDecided event are one transaction: rolled back together…
        var decide = new ApprovalDecideRequest { RequestId = requestId, Decision = ApprovalDecideRequest.DecisionValue.Approve, PayloadHash = hash };
        await using (var scope = Scope("clm-manager", Manager, out var services))
        {
            var session = services.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(ct);
            var response = await services.GetRequiredService<IPlatformApprovalService>().DecideAsync(decide, CommandOptions.New(), ct);
            response.Request.Status.ShouldBe(ApprovalStatus.Approved);
            await transaction.RollbackAsync(ct);
        }

        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("PendingApproval");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{requestId}'")).ShouldBe(0);

        // …and committed together.
        await using (var scope = Scope("clm-manager", Manager, out var services))
        {
            var session = services.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(ct);
            await services.GetRequiredService<IPlatformApprovalService>().DecideAsync(decide, CommandOptions.New(), ct);
            await transaction.CommitAsync(ct);
        }

        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("Approved");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{requestId}' AND event_type = 'ApprovalDecided'")).ShouldBe(1);

        // REQ-PLT-117: the owning module verifies the approved hash before executing.
        await using (var scope = Scope("clm-handler", Handler, out var services))
        {
            var approvals = services.GetRequiredService<IPlatformApprovalService>();
            (await approvals.GetAsync(requestId.ToString(), ct)).Decision!.Checker.Id.ShouldBe("clm-manager");
            var ok = await approvals.VerifyForExecutionAsync(new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash }, ct);
            ok.Ok.ShouldBeTrue();
            var mismatch = await Should.ThrowAsync<DomainException>(() =>
                approvals.VerifyForExecutionAsync(new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = Sha256Hash.ComputeUtf8("other") }, ct));
            mismatch.Error.Code.Value.ShouldBe("PLT-ERR-APPROVAL-HASH-MISMATCH");
        }
    }

    [Fact]
    public async Task REQ_PLT_121_a_service_or_AI_identity_can_never_decide()
    {
        var ct = TestContext.Current.CancellationToken;
        var hash = NewHash();
        var requestId = Guid.Parse(await CreateAsync("handler-ai", Guid.NewGuid().ToString(), hash, "10.00"));

        foreach (var actor in new[] { new ActorRef(ActorKind.AiAgent, "agent-1"), ActorRef.Service("batch") })
        {
            await using var scope = Scope(actor.Id, Manager, out var services);
            services.GetRequiredService<RequestContext>().Actor = actor;
            var error = await Should.ThrowAsync<DomainException>(() => services.GetRequiredService<IPlatformApprovalService>().DecideAsync(
                new ApprovalDecideRequest { RequestId = requestId, Decision = ApprovalDecideRequest.DecisionValue.Approve, PayloadHash = Sha256Hash.Parse(hash) },
                CommandOptions.New(),
                ct));
            error.Error.Code.Value.ShouldBe("PLT-ERR-CHECKER-MUST-BE-HUMAN");
        }
    }

    [Theory]
    [InlineData(Handler, "5000.00", AuthorityDecision.Allow, null)]
    [InlineData(Handler, "5000.01", AuthorityDecision.Refer, Manager)]
    [InlineData(Manager, "50000.00", AuthorityDecision.Allow, null)]
    [InlineData(Manager, "50000.01", AuthorityDecision.Deny, null)]
    public async Task D_SL2_03_illustrative_claims_limits_allow_the_exact_limit_and_refer_one_cent_above(
        string role, string amount, AuthorityDecision expected, string? referral)
    {
        var authority = _factory.Services.GetRequiredService<IAuthorityService>();
        foreach (var type in new[] { ClaimsAuthorityTypes.Reserve, ClaimsAuthorityTypes.Payment })
        {
            var result = await authority.CheckAsync(
                new CoreIns.Platform.Authority.AuthorityCheckRequest(
                    ActorRef.User("someone"), [role], type,
                    new Dictionary<string, DimensionValue> { ["amount"] = DimensionValue.Of(Money.Of(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "EUR")) },
                    null,
                    Instant.FromUtc(2026, 10, 8)),
                TestContext.Current.CancellationToken);
            result.Decision.ShouldBe(expected, $"{type} {role} {amount}");
            if (referral is not null)
            {
                result.ReferralTargets.ShouldBe([new ReferralTarget("ROLE", referral)]);
            }
        }
    }

    [Fact]
    public async Task The_app_role_can_neither_delete_nor_change_a_decided_request()
    {
        var hash = NewHash();
        var requestId = await CreateAsync("handler-db", Guid.NewGuid().ToString(), hash, "10.00");
        (await DecideAsync("manager-db", Manager, requestId, hash)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var ct = TestContext.Current.CancellationToken;
        await using (var privilege = dataSource.CreateCommand("SELECT has_table_privilege('app', 'plt.approval_request', 'DELETE')"))
        {
            ((bool)(await privilege.ExecuteScalarAsync(ct))!).ShouldBeFalse();
        }

        await using var update = dataSource.CreateCommand(
            $"UPDATE plt.approval_request SET status = 'Rejected', decision = 'Rejected', decision_comment = 'x', version = version + 1 WHERE request_id = '{requestId}'");
        var error = await Should.ThrowAsync<PostgresException>(() => update.ExecuteNonQueryAsync(ct));
        error.MessageText.ShouldContain("cannot change");
    }

    private AsyncServiceScope Scope(string user, string roles, out IServiceProvider services)
    {
        var scope = _factory.Services.CreateAsyncScope();
        services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User(user);
        context.Roles = roles.Split(',');
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        return scope;
    }
}
