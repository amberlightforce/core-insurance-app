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

/// <summary>SL6-CHN-WITHDRAW: the withdrawal window (D-SL6-06) and the declare / confirm / close function (REQ-CHN-007, REQ-CHN-164..166).</summary>
public sealed class WithdrawalWindowTests
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    [Fact]
    public void The_last_day_ends_at_23_59_59_Athens_and_the_next_second_is_late()
    {
        var concluded = Instant.FromUtc(2026, 10, 1, 7, 0, 0); // 10:00 Athens
        var lastSecond = Instant.FromUtc(2026, 10, 15, 20, 59, 59); // 23:59:59 EEST
        var nextMidnight = Instant.FromUtc(2026, 10, 15, 21, 0, 0); // 00:00:00 on the 16th

        WithdrawalWindow.IsOpen(concluded, lastSecond, 14, Athens).ShouldBeTrue();
        WithdrawalWindow.IsOpen(concluded, nextMidnight, 14, Athens).ShouldBeFalse();
        WithdrawalWindow.Deadline(concluded, 14, Athens).ShouldBe(lastSecond);
    }

    [Fact]
    public void The_window_is_decided_by_local_calendar_days_on_the_day_the_clocks_change()
    {
        // Greek DST ends on 2026-10-25 (04:00 EEST -> 03:00 EET): the last day is 25 hours long.
        var concluded = Instant.FromUtc(2026, 10, 11, 9, 0, 0);
        var lastSecond = Instant.FromUtc(2026, 10, 25, 21, 59, 59); // 23:59:59 EET
        var nextMidnight = Instant.FromUtc(2026, 10, 25, 22, 0, 0); // 00:00:00 EET on the 26th

        WithdrawalWindow.IsOpen(concluded, lastSecond, 14, Athens).ShouldBeTrue();
        WithdrawalWindow.IsOpen(concluded, nextMidnight, 14, Athens).ShouldBeFalse();
    }
}

public sealed class WithdrawalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly Fakes _fakes = new();
    private readonly ApiHostFactory _root = new(database.AppConnectionString);
    private HttpClient? _client;

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _root.DisposeAsync();
    }

    private sealed class Fakes
    {
        public Instant Now { get; set; } = Instant.FromUtc(2026, 10, 2, 9, 0, 0);

        public Instant Concluded { get; set; } = Instant.FromUtc(2026, 10, 1, 7, 0, 0);

        public int PolCalls { get; set; }

        public bool PolKnown { get; set; } = true;
    }

    private sealed class FakeClock(Fakes fakes) : IClock
    {
        public Instant Now => fakes.Now;
    }

    private sealed class FakePort(Fakes fakes) : IWithdrawalPolicyPort
    {
        public Task<WithdrawalPolicyFacts?> FindAsync(PolicyId policyId, CancellationToken cancellationToken) =>
            Task.FromResult(fakes.PolKnown ? new WithdrawalPolicyFacts(fakes.Concluded, new PartyId(Guid.NewGuid()), "POL-TEST") : null);

        public Task<PolicySubmitOutcome> SubmitAsync(PolicyId policyId, Instant receivedAt, string channelRef, CancellationToken cancellationToken)
        {
            fakes.PolCalls++;
            return Task.FromResult(PolicySubmitOutcome.Accepted);
        }
    }

    private HttpClient Client() => _client ??= _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.AddSingleton(_fakes);
        services.AddSingleton<IClock, FakeClock>();
        services.AddScoped<IWithdrawalPolicyPort, FakePort>();
    })).CreateClient();

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body) =>
        SendAsync(Client(), HttpMethod.Post, path, body, roles: Admin);

    private async Task<JsonNode?> DeclareAsync(Guid policy)
    {
        var (response, body) = await PostAsync("/api/chn/v1/withdrawals/declare", new { name = "Μαρία Παπαδοπούλου", contractId = policy, eMail = "maria@example.org", reason = "Changed my mind" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ConfirmAsync(JsonNode? declared) =>
        PostAsync("/api/chn/v1/withdrawals/confirm", new { requestNumber = declared.Text("requestNumber"), token = declared.Text("confirmToken") });

    [Fact]
    public async Task REQ_CHN_007_declare_then_confirm_sets_confirmedAt_from_platform_time_and_hands_over_to_POL_once()
    {
        _fakes.Now = Instant.FromUtc(2026, 10, 15, 12, 0, 0);
        var declared = await DeclareAsync(Guid.CreateVersion7());
        declared.Text("status").ShouldBe("DECLARED");
        declared.Text("requestNumber").ShouldStartWith("WDR");

        _fakes.Now = Instant.FromUtc(2026, 10, 15, 20, 59, 59); // 23:59:59 Athens on the last day
        var (response, confirmed) = await ConfirmAsync(declared);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, confirmed?.ToJsonString());
        confirmed.Text("status").ShouldBe("SUBMITTED");
        confirmed.Text("confirmedAt").ShouldStartWith("2026-10-15T20:59:59");
        _fakes.PolCalls.ShouldBe(1);

        var (_, again) = await ConfirmAsync(declared);
        again.Text("status").ShouldBe("SUBMITTED");
        again.Text("confirmedAt").ShouldStartWith("2026-10-15T20:59:59");
        _fakes.PolCalls.ShouldBe(1); // one request, one POL call

        var (_, got) = await SendAsync(Client(), HttpMethod.Get, $"/api/chn/v1/withdrawals/{declared.Text("requestNumber")}", roles: Admin);
        got.Text("status").ShouldBe("SUBMITTED");
        got.Text("refundStatus").ShouldBe("NONE");
    }

    [Fact]
    public async Task D_SL6_06_confirming_after_the_last_day_is_declined_and_recorded_without_a_POL_call()
    {
        _fakes.Now = Instant.FromUtc(2026, 10, 15, 12, 0, 0);
        var declared = await DeclareAsync(Guid.CreateVersion7());

        _fakes.Now = Instant.FromUtc(2026, 10, 15, 21, 0, 0); // 00:00:00 Athens on the 16th
        var (response, confirmed) = await ConfirmAsync(declared);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, confirmed?.ToJsonString());
        confirmed.Text("status").ShouldBe("DECLINED");
        confirmed.Text("declineCode").ShouldBe("CHN-ERR-RIGHT-EXPIRED");
        _fakes.PolCalls.ShouldBe(0);

        var (_, got) = await SendAsync(Client(), HttpMethod.Get, $"/api/chn/v1/withdrawals/{declared.Text("requestNumber")}", roles: Admin);
        got.Text("status").ShouldBe("DECLINED");
    }

    [Fact]
    public async Task D_SL6_06_a_declaration_after_the_window_is_recorded_as_declined()
    {
        _fakes.Now = Instant.FromUtc(2026, 11, 20, 9, 0, 0);
        var declared = await DeclareAsync(Guid.CreateVersion7());
        declared.Text("status").ShouldBe("DECLINED");
        declared.Text("declineCode").ShouldBe("CHN-ERR-RIGHT-EXPIRED");

        var (_, list) = await SendAsync(Client(), HttpMethod.Get, $"/api/chn/v1/withdrawals?requestNumber={declared.Text("requestNumber")}", roles: Admin);
        list!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_wrong_token_and_an_unknown_contract_are_refused()
    {
        var declared = await DeclareAsync(Guid.CreateVersion7());
        var (wrong, wrongBody) = await PostAsync("/api/chn/v1/withdrawals/confirm", new { requestNumber = declared.Text("requestNumber"), token = "nope" });
        wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        wrongBody.Text("code").ShouldBe("CHN-ERR-TOKEN-INVALID");

        _fakes.PolKnown = false;
        var (missing, missingBody) = await PostAsync("/api/chn/v1/withdrawals/declare", new { name = "X Y", contractId = Guid.CreateVersion7(), eMail = "x@example.org" });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missingBody.Text("code").ShouldBe("CHN-ERR-NOT-FOUND");
    }

    [Fact]
    public async Task The_POL_outcome_closes_the_request()
    {
        var declared = await DeclareAsync(Guid.CreateVersion7());
        _fakes.Now = Instant.FromUtc(2026, 10, 3, 9, 0, 0);
        await ConfirmAsync(declared);

        var (response, closed) = await PostAsync($"/api/chn/v1/withdrawals/{declared.Text("requestNumber")}/close", new { outcome = "PROCESSED" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, closed?.ToJsonString());
        closed.Text("status").ShouldBe("PROCESSED");

        var (_, got) = await SendAsync(Client(), HttpMethod.Get, $"/api/chn/v1/withdrawals/{declared.Text("requestNumber")}", roles: Admin);
        got.Text("refundStatus").ShouldBe("PROCESSED");
    }
}
