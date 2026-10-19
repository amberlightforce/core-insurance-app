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

    private static ObjectRef Subject(string id) => new(ModuleCode.CLM, "TransactionSet", id);

    private static ApprovalRequestRequest RequestBody(string subjectId, string hash, string amount, string referralRole = Manager, string authorityType = "CLM.PAYMENT") => new()
    {
        Type = "CLM.TRANSACTION_SET",
        ObjectRef = Subject(subjectId),
        PayloadHash = Sha256Hash.Parse(hash),
        Authority = new ApprovalAuthority
        {
            Type = authorityType,
            Amount = Money.Of(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "EUR"),
            Codes = new Dictionary<string, string> { ["costType"] = "INDEMNITY" },
        },
        ReferralRole = referralRole,
        Reason = "Payment above the handler's limit",
        Diff = JsonSerializer.SerializeToElement(new { amount = new { after = amount } }),
    };

    /// <summary>The owning module (CLM) creates the request in process, in its own committed transaction (review D1: no HTTP).</summary>
    private async Task<ApprovalView> RequestInProcessAsync(string maker, ApprovalRequestRequest body, ActorRef? onBehalfOf = null, ActorRef? makerActor = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Scope(maker, Handler, out var services);
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = makerActor ?? context.Actor;
        context.OnBehalfOf = onBehalfOf;
        var session = services.GetRequiredService<DbSession>();
        var transaction = await session.BeginTransactionAsync(ct);
        var response = await services.GetRequiredService<IPlatformApprovalService>().RequestAsync(body, CommandOptions.New(), ct);
        await transaction.CommitAsync(ct);
        return response.Request;
    }

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

    private async Task<string> CreateAsync(string maker, string subjectId, string hash, string amount) =>
        (await RequestInProcessAsync(maker, RequestBody(subjectId, hash, amount))).RequestId.ToString();

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
    public async Task REQ_PLT_115_the_all_roles_dev_super_user_cannot_approve_their_own_claims_request_but_can_approve_another_makers()
    {
        string[] all = ["Staff.Underwriter", "Staff.UnderwritingManager", "Staff.Billing", "Staff.Finance", Handler, Manager, "Platform.Admin"];
        var superRoles = string.Join(',', all);
        var hash = NewHash();
        var own = await CreateAsync("dev:superuser", Guid.NewGuid().ToString(), hash, "100.00");

        // Holding every role and every authority grant does not lift four-eyes: the maker is still the maker.
        var (refused, refusedBody) = await DecideAsync("dev:superuser", superRoles, own, hash);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        refusedBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{own}'")).ShouldBe("PendingApproval");

        // Another maker's request is decided by the super user (a different person), under the highest grant among the roles.
        var otherHash = NewHash();
        var other = await CreateAsync("handler-anna", Guid.NewGuid().ToString(), otherHash, "5300.00");
        var (approved, _) = await DecideAsync("dev:superuser", superRoles, other, otherHash);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
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
        var resubmitted = await RequestInProcessAsync("manager-2", RequestBody(subject, second, "6500.00"));
        var newId = resubmitted.RequestId.ToString();
        resubmitted.Supersedes.ToString().ShouldBe(requestId);
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
        (await RequestInProcessAsync("manager-2", RequestBody(subject, second, "6500.00"))).RequestId.ToString().ShouldBe(newId);

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
            var verify = new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = "CLM.TRANSACTION_SET", ObjectRef = subject };
            var ok = await approvals.VerifyForExecutionAsync(verify, ct);
            ok.Ok.ShouldBeTrue();
            ok.Authority.Type.ShouldBe("CLM.RESERVE");
            ok.Authority.Amount.ShouldBe(Money.Of(5000.01m, "EUR"));
            var mismatch = await Should.ThrowAsync<DomainException>(() => approvals.VerifyForExecutionAsync(verify with { Hash = Sha256Hash.ComputeUtf8("other") }, ct));
            mismatch.Error.Code.Value.ShouldBe("PLT-ERR-APPROVAL-HASH-MISMATCH");

            // Review D1: an approval of another subject or another approval type is never valid evidence.
            var otherSubject = await Should.ThrowAsync<DomainException>(() =>
                approvals.VerifyForExecutionAsync(verify with { ObjectRef = new ObjectRef(ModuleCode.CLM, "TransactionSet", Guid.NewGuid().ToString()) }, ct));
            otherSubject.Error.Code.Value.ShouldBe("PLT-ERR-APPROVAL-SUBJECT-MISMATCH");
            var otherType = await Should.ThrowAsync<DomainException>(() => approvals.VerifyForExecutionAsync(verify with { Type = "BIL.REFUND" }, ct));
            otherType.Error.Code.Value.ShouldBe("PLT-ERR-APPROVAL-SUBJECT-MISMATCH");
        }
    }

    [Fact]
    public async Task Owning_module_editors_are_deduplicated_inherited_and_refuse_delegated_decisions()
    {
        var subject = Guid.NewGuid().ToString();
        var hash = NewHash();
        var body = RequestBody(subject, hash, "100.00") with { Editors = ["USER:editor-a", "USER:editor-a"] };
        var first = await RequestInProcessAsync("maker-a", body);
        (await ScalarAsync<string[]>($"SELECT editors FROM plt.approval_request WHERE request_id = '{first.RequestId}'")).Length.ShouldBe(1);
        var same = await RequestInProcessAsync("maker-a", body with { Editors = [] });
        same.RequestId.ShouldBe(first.RequestId); // Dropping supplied editors cannot remove frozen participants.
        var added = await RequestInProcessAsync("maker-b", body with { Editors = ["USER:editor-b"] });
        added.RequestId.ShouldNotBe(first.RequestId); // Even at the same hash, a new participant must be frozen.
        var frozenEditors = await ScalarAsync<string[]>($"SELECT editors FROM plt.approval_request WHERE request_id = '{added.RequestId}'");
        frozenEditors.ShouldContain("USER:editor-a");
        frozenEditors.ShouldContain("USER:editor-b");
        frozenEditors.ShouldContain("USER:maker-a");
        foreach (var editor in new[] { "editor-a", "editor-b", "maker-a" })
        {
            var (refused, refusal) = await DecideAsync(editor, Manager, added.RequestId.ToString(), hash);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            refusal!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-EDITOR-CANNOT-APPROVE");
        }
        await using var scope = Scope("independent-user", Manager, out var services);
        services.GetRequiredService<RequestContext>().OnBehalfOf = ActorRef.User("editor-a");
        var error = await Should.ThrowAsync<DomainException>(() => services.GetRequiredService<IPlatformApprovalService>().DecideAsync(
            new ApprovalDecideRequest { RequestId = added.RequestId, Decision = ApprovalDecideRequest.DecisionValue.Approve, PayloadHash = Sha256Hash.Parse(hash) },
            CommandOptions.New(), TestContext.Current.CancellationToken));
        error.Error.Code.Value.ShouldBe("PLT-ERR-EDITOR-CANNOT-APPROVE");
    }

    [Theory]
    [InlineData("USER:")]
    [InlineData("UNKNOWN:editor")]
    [InlineData("USER:has space")]
    public async Task Owning_module_editor_actor_keys_are_validated(string editor)
    {
        var error = await Should.ThrowAsync<DomainException>(() => RequestInProcessAsync("maker", RequestBody(Guid.NewGuid().ToString(), NewHash(), "10.00") with { Editors = [editor] }));
        error.Error.Code.Value.ShouldBe("PLT-ERR-VALIDATION");
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

    [Fact]
    public async Task Review_D1_a_maker_cannot_create_or_supersede_a_request_over_HTTP()
    {
        var subject = Guid.NewGuid().ToString();
        var hash = NewHash();
        var requestId = await CreateAsync("clm-module-maker", subject, hash, "40000.00");
        var body = new
        {
            type = "CLM.TRANSACTION_SET",
            objectRef = new { module = "CLM", type = "TransactionSet", id = subject },
            payloadHash = NewHash(),
            authority = new { type = "CLM.PAYMENT", amount = new { amount = "1.00", currency = "EUR" } },
            referralRole = Handler,
        };

        var (response, _) = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/request", "handler-attacker", $"{Handler},{Manager},Platform.Admin", body);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("PendingApproval");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.approval_request WHERE object_id = '{subject}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Review_D2_the_principal_of_an_AI_maker_cannot_decide_the_agents_request_and_does_not_see_it()
    {
        var hash = NewHash();
        var subject = Guid.NewGuid().ToString();
        var request = await RequestInProcessAsync(
            "agent-7", RequestBody(subject, hash, "100.00"), onBehalfOf: ActorRef.User("principal-p"), makerActor: new ActorRef(ActorKind.AiAgent, "agent-7"));
        var requestId = request.RequestId.ToString();

        var (inbox, inboxBody) = await SendAsync(HttpMethod.Get, "/api/plt/v1/approval?limit=200", "principal-p", Manager);
        inbox.StatusCode.ShouldBe(HttpStatusCode.OK);
        inboxBody!["items"]!.AsArray().Select(i => i!["request"]!["requestId"]!.GetValue<string>()).ShouldNotContain(requestId);

        var (self, selfBody) = await DecideAsync("principal-p", Manager, requestId, hash);
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        selfBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-SELF-APPROVAL");

        // When the content changes, the agent and its principal become editors of the new version: still refused.
        var second = NewHash();
        var newRequest = await RequestInProcessAsync("handler-q", RequestBody(subject, second, "100.00"));
        var (editor, editorBody) = await DecideAsync("principal-p", Manager, newRequest.RequestId.ToString(), second);
        editor.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        editorBody!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-EDITOR-CANNOT-APPROVE");

        // Another person decides.
        (await DecideAsync("manager-other", Manager, newRequest.RequestId.ToString(), second)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync<string>($"SELECT maker_on_behalf_of_id FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("principal-p");
    }

    [Fact]
    public async Task Review_m1_the_trigger_freezes_every_non_decision_column_and_pending_to_pending_updates()
    {
        var hash = NewHash();
        var requestId = await CreateAsync("handler-freeze", Guid.NewGuid().ToString(), hash, "10.00");
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var ct = TestContext.Current.CancellationToken;

        foreach (var sql in new[]
                 {
                     $"UPDATE plt.approval_request SET reason = 'changed', version = version + 1 WHERE request_id = '{requestId}'",
                     $"UPDATE plt.approval_request SET diff = '{{}}'::jsonb, version = version + 1 WHERE request_id = '{requestId}'",
                     $"UPDATE plt.approval_request SET supersedes = gen_random_uuid(), status = 'Withdrawn', decided_at = now(), version = version + 1 WHERE request_id = '{requestId}'",
                     $"UPDATE plt.approval_request SET maker_on_behalf_of_kind = 'USER', maker_on_behalf_of_id = 'x', status = 'Withdrawn', decided_at = now(), version = version + 1 WHERE request_id = '{requestId}'",
                 })
        {
            await using var update = dataSource.CreateCommand(sql);
            var error = await Should.ThrowAsync<PostgresException>(() => update.ExecuteNonQueryAsync(ct));
            error.MessageText.ShouldContain("SECURITY:", Case.Sensitive, sql);
        }

        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("PendingApproval");
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
