using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Services;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>NX-HD-10: a CONFIRMED withdrawal that POL could not take is re-submitted by the sweeper.</summary>
public sealed class WithdrawalSweepTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly State _state = new();
    private readonly ApiHostFactory _root = new(database.AppConnectionString);
    private WebApplicationFactory<Program>? _host;

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        await _root.DisposeAsync();
    }

    private sealed class State
    {
        public Instant Now { get; set; } = Instant.FromUtc(2026, 10, 2, 9, 0, 0);

        public PolicySubmitOutcome Outcome { get; set; } = PolicySubmitOutcome.NotBound;

        public int Calls { get; set; }
    }

    private sealed class FakeClock(State state) : IClock
    {
        public Instant Now => state.Now;
    }

    private sealed class FakePort(State state) : IWithdrawalPolicyPort
    {
        public Task<WithdrawalPolicyFacts?> FindAsync(PolicyId policyId, CancellationToken cancellationToken) =>
            Task.FromResult<WithdrawalPolicyFacts?>(new WithdrawalPolicyFacts(Instant.FromUtc(2026, 10, 1, 7, 0, 0), new PartyId(Guid.NewGuid()), "POL-TEST"));

        public Task<PolicySubmitOutcome> SubmitAsync(PolicyId policyId, Instant receivedAt, string channelRef, CancellationToken cancellationToken)
        {
            state.Calls++;
            return Task.FromResult(state.Outcome);
        }
    }

    private WebApplicationFactory<Program> Host() => _host ??= _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.AddSingleton(_state);
        services.AddSingleton<IClock, FakeClock>();
        services.AddScoped<IWithdrawalPolicyPort, FakePort>();
    }));

    private async Task<(JsonNode? Declared, string Status)> ConfirmedWhilePolIsDownAsync()
    {
        using var client = Host().CreateClient();
        var (_, declared) = await SendAsync(client, HttpMethod.Post, "/api/chn/v1/withdrawals/declare",
            new { name = "Μαρία Παπαδοπούλου", contractId = Guid.CreateVersion7(), eMail = "maria@example.org" }, roles: Admin);
        var (response, confirmed) = await SendAsync(client, HttpMethod.Post, "/api/chn/v1/withdrawals/confirm",
            new { requestNumber = declared.Text("requestNumber"), token = declared.Text("confirmToken") }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, confirmed?.ToJsonString());
        return (declared, confirmed.Text("status"));
    }

    private async Task<string> StatusAsync(JsonNode? declared)
    {
        using var client = Host().CreateClient();
        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/chn/v1/withdrawals/{declared.Text("requestNumber")}", roles: Admin);
        return got.Text("status");
    }

    [Fact]
    public async Task The_sweeper_resubmits_a_confirmed_request_once_POL_accepts_it_and_not_before_the_grace_period()
    {
        var (declared, status) = await ConfirmedWhilePolIsDownAsync();
        status.ShouldBe("CONFIRMED");
        var runner = Host().Services.GetRequiredService<WithdrawalSweepRunner>();

        // Inside the grace period the request is left alone.
        _state.Outcome = PolicySubmitOutcome.Accepted;
        (await runner.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await StatusAsync(declared)).ShouldBe("CONFIRMED");

        // POL still down after the grace period: stays CONFIRMED.
        _state.Now = _state.Now.Plus(TimeSpan.FromMinutes(5));
        _state.Outcome = PolicySubmitOutcome.NotBound;
        (await runner.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await StatusAsync(declared)).ShouldBe("CONFIRMED");

        // POL back: SUBMITTED, and a further sweep has nothing to do.
        _state.Outcome = PolicySubmitOutcome.Accepted;
        (await runner.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await StatusAsync(declared)).ShouldBe("SUBMITTED");
        var calls = _state.Calls;
        (await runner.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        _state.Calls.ShouldBe(calls);
    }

    [Fact]
    public async Task The_sweeper_closes_a_confirmed_request_as_rejected_when_POL_says_the_right_has_expired()
    {
        var (declared, _) = await ConfirmedWhilePolIsDownAsync();
        _state.Now = _state.Now.Plus(TimeSpan.FromMinutes(5));
        _state.Outcome = PolicySubmitOutcome.Expired;

        (await Host().Services.GetRequiredService<WithdrawalSweepRunner>().RunAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await StatusAsync(declared)).ShouldBe("REJECTED");
    }
}
