using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Time;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Exposures;

/// <summary>
/// BO-CLM-05: a third-party MTPL exposure starts its offer and assessment clocks in CMP (UNVERIFIED values, D-REG-04), close and claim close
/// stop them, reopen starts them afresh, a breach shows on the claim, and the Production gate refuses unconfirmed values.
/// </summary>
public sealed class ExposureClocksTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private ShiftableClock Clock => (ShiftableClock)_slice.Factory.Services.GetRequiredService<IClock>();

    private async Task<int> VersionAsync(MoneyClaim claim) =>
        int.Parse((await _money.ClaimAsync(claim)).Text("summary.recordVersion"), CultureInfo.InvariantCulture);

    private async Task<JsonNode> ThirdPartyAsync(MoneyClaim claim)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures", new
        {
            claimId = claim.ClaimId, expectedRecordVersion = await VersionAsync(claim), kind = "MTPL_BI", coverageCode = "MTPL", claimantPartyId = Guid.CreateVersion7(),
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!["exposure"]!;
    }

    private async Task<JsonNode?> ExposureAsync(MoneyClaim claim, string id) =>
        (await _money.ClaimAsync(claim))["exposures"]!.AsArray().Single(e => e!.Text("exposureId") == id);

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> CloseAsync(MoneyClaim claim, string exposureId, string outcome) =>
        await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/close", new { exposureId, expectedRecordVersion = await VersionAsync(claim), outcome });

    private async Task<MoneyClaim> ClaimAsync() => await _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));

    [Fact]
    public async Task A_third_party_MTPL_exposure_starts_offer_and_assessment_clocks_from_the_notice_date()
    {
        var claim = await ClaimAsync();
        var exposure = await ThirdPartyAsync(claim);

        var clocks = exposure["statutoryClocks"]!.AsArray();
        clocks.Select(c => c!.Text("clockCode")).ShouldBe(["CLM_MTPL_OFFER", "CLM_MTPL_ASSESSMENT"]);
        clocks.ShouldAllBe(c => c!.Text("status") == "RUNNING");

        var noticeOn = DateOnly.Parse((await _money.ClaimAsync(claim)).Text("summary.noticeOn"), CultureInfo.InvariantCulture);
        var athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
        DateOnly DeadlineDay(JsonNode? c) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(c!.Text("deadline"), CultureInfo.InvariantCulture), athens).DateTime);
        DeadlineDay(clocks[0]).ShouldBe(noticeOn.AddMonths(3)); // PRD-07: reasoned offer within 3 months (UNVERIFIED, OQ-008)
        DeadlineDay(clocks[1]).ShouldBe(noticeOn.AddDays(25));
        (await _money.ScalarAsync<string>($"SELECT statutory_clocks FROM clm.exposure WHERE exposure_id = '{exposure.Text("exposureId")}'")).ShouldStartWith("CLOCKS:");

        // The own-damage exposure of the insured is not a statutory-clock case.
        (await ExposureAsync(claim, claim.ExposureId))!["statutoryClocks"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Close_is_blocked_while_the_clocks_run_and_a_denial_cancels_them_and_reopen_starts_them_again()
    {
        var claim = await ClaimAsync();
        var id = (await ThirdPartyAsync(claim)).Text("exposureId");

        var (blocked, blockedBody) = await CloseAsync(claim, id, "COMPLETED");
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blockedBody?.ToJsonString());
        blockedBody.Text("errors.0.code").ShouldBe("CLOCK_RUNNING");

        var (denied, deniedBody) = await CloseAsync(claim, id, "WITHDRAWN");
        denied.StatusCode.ShouldBe(HttpStatusCode.OK, deniedBody?.ToJsonString());
        (await ExposureAsync(claim, id))!["statutoryClocks"]!.AsArray().ShouldAllBe(c => c!.Text("status") == "CANCELLED");

        var (reopened, reopenedBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures/reopen",
            new { exposureId = id, expectedRecordVersion = await VersionAsync(claim), reasonCode = "NEW_INFORMATION" });
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK, reopenedBody?.ToJsonString());
        var after = (await ExposureAsync(claim, id))!["statutoryClocks"]!.AsArray();
        after.Count.ShouldBe(2);
        after.ShouldAllBe(c => c!.Text("status") == "RUNNING");
    }

    [Fact]
    public async Task A_breach_shows_on_the_claim_and_a_breached_clock_no_longer_blocks_the_close()
    {
        var claim = await ClaimAsync();
        var id = (await ThirdPartyAsync(claim)).Text("exposureId");

        Clock.Advance(TimeSpan.FromDays(130)); // past 3 months
        await _slice.Factory.Services.GetRequiredService<ClockSweepRunner>().RunAsync(ClaimsSlice.Ct);

        var clocks = (await ExposureAsync(claim, id))!["statutoryClocks"]!.AsArray();
        clocks.ShouldAllBe(c => c!.Text("status") == "BREACHED");
        var offer = clocks.Single(c => c!.Text("clockCode") == "CLM_MTPL_OFFER")!.Text("instanceId");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ClockBreached' AND aggregate_id = '{offer}'")).ShouldBe(1);

        (await CloseAsync(claim, id, "COMPLETED")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Closing_the_claim_cancels_the_running_clocks()
    {
        var claim = await ClaimAsync();
        var id = (await ThirdPartyAsync(claim)).Text("exposureId");

        var (response, body) = await _money.CloseAsync(claim, await VersionAsync(claim));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (await ExposureAsync(claim, id))!["statutoryClocks"]!.AsArray().ShouldAllBe(c => c!.Text("status") == "CANCELLED");
    }

    [Fact]
    public async Task The_seeded_MTPL_clock_values_are_unverified_and_the_Production_gate_refuses_them()
    {
        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<ExposureClocks>();
        (await gate.HasUnconfirmedValueAsync(ClaimsSlice.Ct)).ShouldBeTrue();

        var defs = await scope.ServiceProvider.GetRequiredService<IComplianceClockDefService>().ListAsync(cancellationToken: ClaimsSlice.Ct);
        defs.Items.Where(d => d.Code.Value.StartsWith("CLM_MTPL_", StringComparison.Ordinal)).ShouldAllBe(d => d.LegalStatus == "UNVERIFIED");

        // Once both started clocks are Verified or SecondarySource the gate opens.
        (await new ExposureClocks(null!, new StubDefs("SECONDARY_SOURCE")).HasUnconfirmedValueAsync(ClaimsSlice.Ct)).ShouldBeFalse();
        (await new ExposureClocks(null!, new StubDefs("ILLUSTRATIVE")).HasUnconfirmedValueAsync(ClaimsSlice.Ct)).ShouldBeTrue();
    }

    private sealed class StubDefs(string status) : IComplianceClockDefService
    {
        public Task<ClockDefListPage> ListAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClockDefListPage
            {
                Items = [.. ExposureClocks.StartCodes.Select(code => new ClockDefListItem
                {
                    Code = CoreIns.SharedKernel.Identifiers.ClockCode.Parse(code), Kind = ClockDefListItem.KindValue.Deadline, Duration = "P1D", LegalStatus = status,
                })],
                NextCursor = null,
            });
    }
}
