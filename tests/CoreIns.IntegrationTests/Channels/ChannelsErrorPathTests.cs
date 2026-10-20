using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>NX-TST-07: withdrawal error paths, idempotency and authorisation (complements <see cref="WithdrawalTests"/>).</summary>
public sealed class ChannelsErrorPathTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _root = new(database.AppConnectionString);
    private HttpClient? _client;

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _root.DisposeAsync();
    }

    private sealed class KnownPolicyPort : IWithdrawalPolicyPort
    {
        public Task<WithdrawalPolicyFacts?> FindAsync(PolicyId policyId, CancellationToken cancellationToken) =>
            Task.FromResult<WithdrawalPolicyFacts?>(new WithdrawalPolicyFacts(Instant.FromUtc(2026, 10, 1, 7, 0, 0), new PartyId(Guid.NewGuid()), "POL-TEST"));

        public Task<PolicySubmitOutcome> SubmitAsync(PolicyId policyId, Instant receivedAt, string channelRef, CancellationToken cancellationToken) =>
            Task.FromResult(PolicySubmitOutcome.Accepted);
    }

    private sealed class FixedClock : IClock
    {
        public Instant Now => Instant.FromUtc(2026, 10, 2, 9, 0, 0);
    }

    private HttpClient Client() => _client ??= _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.AddSingleton<IClock, FixedClock>();
        services.AddScoped<IWithdrawalPolicyPort, KnownPolicyPort>();
    })).CreateClient();

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, string roles = Admin, Guid? key = null, bool withKey = true) =>
        SendAsync(Client(), HttpMethod.Post, path, body, roles, key, withKey);

    private static object Declaration(Guid? contract = null) =>
        new { name = "Μαρία Παπαδοπούλου", contractId = contract ?? Guid.CreateVersion7(), eMail = "maria@example.org" };

    [Fact]
    public async Task Declare_is_idempotent_on_the_key_and_needs_one()
    {
        var body = Declaration();
        var key = Guid.NewGuid();
        var (first, one) = await PostAsync("/api/chn/v1/withdrawals/declare", body, key: key);
        var (second, two) = await PostAsync("/api/chn/v1/withdrawals/declare", body, key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        two.Text("requestNumber").ShouldBe(one.Text("requestNumber"));

        var (noKey, _) = await PostAsync("/api/chn/v1/withdrawals/declare", body, withKey: false);
        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Malformed_declarations_are_refused_without_creating_a_request()
    {
        var (missingName, _) = await PostAsync("/api/chn/v1/withdrawals/declare", new { contractId = Guid.CreateVersion7(), eMail = "x@example.org" });
        ((int)missingName.StatusCode).ShouldBeInRange(400, 422);

        var (badMail, _) = await PostAsync("/api/chn/v1/withdrawals/declare", new { name = "X Y", contractId = Guid.CreateVersion7(), eMail = "not-an-address" });
        ((int)badMail.StatusCode).ShouldBeInRange(400, 422);
    }

    [Fact]
    public async Task Closing_before_confirmation_or_with_the_opposite_outcome_is_a_wrong_state()
    {
        var (_, declared) = await PostAsync("/api/chn/v1/withdrawals/declare", Declaration());
        var number = declared.Text("requestNumber");

        var (early, earlyBody) = await PostAsync($"/api/chn/v1/withdrawals/{number}/close", new { outcome = "PROCESSED" });
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict, earlyBody?.ToJsonString());
        earlyBody.Text("code").ShouldBe("CHN-ERR-WRONG-STATE");

        var (confirm, confirmed) = await PostAsync("/api/chn/v1/withdrawals/confirm", new { requestNumber = number, token = declared.Text("confirmToken") });
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK, confirmed?.ToJsonString());

        (await PostAsync($"/api/chn/v1/withdrawals/{number}/close", new { outcome = "PROCESSED" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (again, againBody) = await PostAsync($"/api/chn/v1/withdrawals/{number}/close", new { outcome = "PROCESSED" });
        again.StatusCode.ShouldBe(HttpStatusCode.OK); // idempotent on the same outcome
        againBody.Text("status").ShouldBe("PROCESSED");

        var (opposite, oppositeBody) = await PostAsync($"/api/chn/v1/withdrawals/{number}/close", new { outcome = "REJECTED" });
        opposite.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        oppositeBody.Text("code").ShouldBe("CHN-ERR-WRONG-STATE");
    }

    [Fact]
    public async Task Unknown_requests_are_not_found_for_confirm_close_and_get()
    {
        var (confirm, confirmBody) = await PostAsync("/api/chn/v1/withdrawals/confirm", new { requestNumber = "WDR-NOPE", token = "x" });
        confirm.StatusCode.ShouldBe(HttpStatusCode.NotFound, confirmBody?.ToJsonString());
        confirmBody.Text("code").ShouldBe("CHN-ERR-NOT-FOUND");

        (await PostAsync("/api/chn/v1/withdrawals/WDR-NOPE/close", new { outcome = "PROCESSED" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(Client(), HttpMethod.Get, "/api/chn/v1/withdrawals/WDR-NOPE", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Withdrawal_operations_need_their_permissions()
    {
        (await PostAsync("/api/chn/v1/withdrawals/declare", Declaration(), roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync("/api/chn/v1/withdrawals/confirm", new { requestNumber = "x", token = "y" }, roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync("/api/chn/v1/withdrawals/WDR1/close", new { outcome = "PROCESSED" }, roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(Client(), HttpMethod.Get, "/api/chn/v1/withdrawals", roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
