using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

public sealed class WithdrawApprovalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddCommand<ClmWithdraw, ApprovalWithdrawResponse, ClmWithdrawHandler>(CommandDescriptor.For("clm.WithdrawProbe.run"));
            services.AddCommand<BilWithdraw, ApprovalWithdrawResponse, BilWithdrawHandler>(CommandDescriptor.For("bil.WithdrawProbe.run"));
        }));
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private AsyncServiceScope Scope(string user = "maker", string role = "Staff.ClaimsHandler")
    {
        var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User(user);
        context.Roles = [role];
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        context.IdempotencyKey = IdempotencyKey.New();
        return scope;
    }

    private async Task<ApprovalView> CreateAsync(ModuleCode module = ModuleCode.CLM)
    {
        await using var scope = Scope();
        var services = scope.ServiceProvider;
        var ct = TestContext.Current.CancellationToken;
        var transaction = await services.GetRequiredService<DbSession>().BeginTransactionAsync(ct);
        var response = await services.GetRequiredService<IPlatformApprovalService>().RequestAsync(new ApprovalRequestRequest
        {
            Type = "CLM.TRANSACTION_SET", ObjectRef = new ObjectRef(module, "TransactionSet", Guid.NewGuid().ToString()),
            PayloadHash = Sha256Hash.ComputeUtf8(Guid.NewGuid().ToString()),
            Authority = new ApprovalAuthority { Type = "CLM.PAYMENT", Amount = Money.Of(5000.01m, "EUR"), Codes = new Dictionary<string, string> { ["costType"] = "INDEMNITY" } },
            ReferralRole = "Staff.ClaimsManager", Reason = "Approval required",
        }, CommandOptions.New(), ct);
        await transaction.CommitAsync(ct);
        return response.Request;
    }

    private async Task<Result<ApprovalWithdrawResponse>> WithdrawAsync(ApprovalView row, bool billing = false, IdempotencyKey? key = null, string reason = "APPROVAL_WITHDRAWN", bool throwAfter = false)
    {
        await using var scope = Scope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.IdempotencyKey = key ?? IdempotencyKey.New();
        var request = new ApprovalWithdrawRequest { ApprovalRequestId = new ApprovalRequestId(row.RequestId), Reason = reason };
        var result = billing
            ? await services.GetRequiredService<ICommandHandler<BilWithdraw, ApprovalWithdrawResponse>>().HandleAsync(new BilWithdraw(request), TestContext.Current.CancellationToken)
            : await services.GetRequiredService<ICommandHandler<ClmWithdraw, ApprovalWithdrawResponse>>().HandleAsync(new ClmWithdraw(request, throwAfter), TestContext.Current.CancellationToken);
        context.CurrentCommandModule.ShouldBeNull();
        return result;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, "Staff.ClaimsManager");
        request.Headers.Add(TestAuthHandler.UserHeader, "checker");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null) { request.Content = JsonContent.Create(body); }
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> DecideAsync(ApprovalView row) => SendAsync(HttpMethod.Post, "/api/plt/v1/approval/decide", new
    {
        requestId = row.RequestId, decision = "Approve", payloadHash = row.PayloadHash,
    });

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Owner_withdrawal_is_audited_published_once_idempotent_and_removed_from_inbox()
    {
        var row = await CreateAsync();
        var key = IdempotencyKey.New();
        var first = await WithdrawAsync(row, key: key);
        first.IsSuccess.ShouldBeTrue(first.IsSuccess ? null : first.Error.ToString());
        first.Value!.Status.ShouldBe(ApprovalStatus.Withdrawn);
        (await WithdrawAsync(row, key: key)).Value.ShouldBe(first.Value);
        (await WithdrawAsync(row)).Value.ShouldBe(first.Value);
        (await WithdrawAsync(row, key: key, reason: "changed")).Error!.Code.Value.ShouldBe("CLM-ERR-IDEMPOTENCY-MISMATCH");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type='ApprovalDecided' AND aggregate_id='{row.RequestId}' AND payload->>'decision'='WITHDRAWN'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation='plt.Approval.withdraw' AND object_id='{row.RequestId}' AND outcome='Succeeded' AND reason='APPROVAL_WITHDRAWN'")).ShouldBe(2);
        (await ScalarAsync<string>($"SELECT decision_comment FROM plt.approval_request WHERE request_id='{row.RequestId}'")).ShouldBe("APPROVAL_WITHDRAWN");
        var inbox = await SendAsync(HttpMethod.Get, "/api/plt/v1/approval?limit=200");
        inbox.Status.ShouldBe(HttpStatusCode.OK);
        inbox.Body!["items"]!.AsArray().Select(i => i!["request"]!["requestId"]!.GetValue<string>()).ShouldNotContain(row.RequestId.ToString());
        var decided = await DecideAsync(row);
        decided.Status.ShouldBe(HttpStatusCode.Conflict);
        decided.Body!["code"]!.GetValue<string>().ShouldBe("PLT-ERR-APPROVAL-STALE");
    }

    [Fact]
    public async Task Owner_guard_rejects_wrong_module_and_unstamped_direct_calls_but_accepts_BIL_owner()
    {
        var clm = await CreateAsync();
        (await WithdrawAsync(clm, billing: true)).Error!.Code.Value.ShouldBe("PLT-ERR-NOT-OWNER");
        await using (var scope = Scope())
        {
            var error = await Should.ThrowAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IPlatformApprovalService>().WithdrawAsync(
                new ApprovalWithdrawRequest { ApprovalRequestId = new ApprovalRequestId(clm.RequestId), Reason = "cancel" }, CommandOptions.New(), TestContext.Current.CancellationToken));
            error.Error.Code.Value.ShouldBe("PLT-ERR-NOT-OWNER");
            scope.ServiceProvider.GetRequiredService<RequestContext>().CurrentCommandModule.ShouldBeNull();
        }
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id='{clm.RequestId}'")).ShouldBe("PendingApproval");
        var bil = await CreateAsync(ModuleCode.BIL);
        (await WithdrawAsync(bil, billing: true)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Decided_request_cannot_be_withdrawn_and_no_REST_route_exists()
    {
        var row = await CreateAsync();
        (await DecideAsync(row)).Status.ShouldBe(HttpStatusCode.OK);
        (await WithdrawAsync(row)).Error!.Code.Value.ShouldBe("PLT-ERR-APPROVAL-STALE");
        var route = await SendAsync(HttpMethod.Post, "/api/plt/v1/approval/withdraw", new { approvalRequestId = row.RequestId, reason = "cancel" });
        route.Status.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Concurrent_decide_and_withdraw_have_exactly_one_winner_and_one_conflict()
    {
        for (var i = 0; i < 5; i++)
        {
            var row = await CreateAsync();
            var withdrawal = WithdrawAsync(row);
            var decision = DecideAsync(row);
            await Task.WhenAll(withdrawal, decision);
            var withdrawn = await withdrawal;
            var decided = await decision;
            if (withdrawn.IsSuccess)
            {
                decided.Status.ShouldBe(HttpStatusCode.Conflict);
            }
            else
            {
                withdrawn.Error!.Code.Value.ShouldBe("PLT-ERR-APPROVAL-STALE");
                decided.Status.ShouldBe(HttpStatusCode.OK);
            }
            (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type='ApprovalDecided' AND aggregate_id='{row.RequestId}'")).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Concurrent_double_withdrawal_returns_the_same_result_and_emits_once()
    {
        var row = await CreateAsync();
        var results = await Task.WhenAll(WithdrawAsync(row), WithdrawAsync(row));
        results.All(r => r.IsSuccess).ShouldBeTrue();
        results[0].Value.ShouldBe(results[1].Value);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type='ApprovalDecided' AND aggregate_id='{row.RequestId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Nested_command_stamp_restores_after_failure_and_outer_exception_rolls_back()
    {
        var row = await CreateAsync();
        await using var scope = Scope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        await Should.ThrowAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ICommandHandler<ClmWithdraw, ApprovalWithdrawResponse>>()
            .HandleAsync(new ClmWithdraw(new ApprovalWithdrawRequest { ApprovalRequestId = new ApprovalRequestId(row.RequestId), Reason = "cancel" }, true), TestContext.Current.CancellationToken));
        context.CurrentCommandModule.ShouldBeNull();
        (await ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id='{row.RequestId}'")).ShouldBe("PendingApproval");
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type='ApprovalDecided' AND aggregate_id='{row.RequestId}'")).ShouldBe(0);
    }

    public sealed record ClmWithdraw(ApprovalWithdrawRequest Request, bool ThrowAfter = false) : ICommand<ApprovalWithdrawResponse>;
    public sealed record BilWithdraw(ApprovalWithdrawRequest Request) : ICommand<ApprovalWithdrawResponse>;
    public sealed class ClmWithdrawHandler(IPlatformApprovalService approval, RequestContext context) : ICommandHandler<ClmWithdraw, ApprovalWithdrawResponse>
    {
        public async Task<Result<ApprovalWithdrawResponse>> HandleAsync(ClmWithdraw command, CancellationToken cancellationToken)
        {
            context.CurrentCommandModule.ShouldBe(ModuleCode.CLM);
            try
            {
                var response = await approval.WithdrawAsync(command.Request, new CommandOptions(context.IdempotencyKey!.Value), cancellationToken);
                context.CurrentCommandModule.ShouldBe(ModuleCode.CLM);
                if (command.ThrowAfter) { throw new InvalidOperationException("outer failure"); }
                return response;
            }
            catch (DomainException error)
            {
                context.CurrentCommandModule.ShouldBe(ModuleCode.CLM);
                return error.Error;
            }
        }
    }
    public sealed class BilWithdrawHandler(IPlatformApprovalService approval, RequestContext context) : ICommandHandler<BilWithdraw, ApprovalWithdrawResponse>
    {
        public async Task<Result<ApprovalWithdrawResponse>> HandleAsync(BilWithdraw command, CancellationToken cancellationToken)
        {
            context.CurrentCommandModule.ShouldBe(ModuleCode.BIL);
            try
            {
                var response = await approval.WithdrawAsync(command.Request, new CommandOptions(context.IdempotencyKey!.Value), cancellationToken);
                context.CurrentCommandModule.ShouldBe(ModuleCode.BIL);
                return response;
            }
            catch (DomainException error)
            {
                context.CurrentCommandModule.ShouldBe(ModuleCode.BIL);
                return error.Error;
            }
        }
    }
}
