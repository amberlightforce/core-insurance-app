using System.Net;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>
/// SL4-RI-REGISTRY time: activation at the period start and expiry at the period end by the lifecycle scanner on an
/// <c>IClock</c> test double, Athens business dates, half-open periods, and <c>applicable</c> (REQ-RI-001, -058;
/// PITFALLS 13, 14, 17).
/// </summary>
public sealed class ContractTemporalTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private static async Task<(RegistrySlice Slice, string Lead, string Follow)> NewSliceAsync(PostgresFixture database)
    {
        var slice = new RegistrySlice(database);
        return (slice, await slice.OrganisationAsync("Σύνθετη Αντασφαλιστική Α.Ε. " + Guid.NewGuid().ToString("N")[..6]), await slice.OrganisationAsync("Synthetic Re " + Guid.NewGuid().ToString("N")[..6]));
    }

    private static async Task<long> EventsAsync(RegistrySlice slice, string type, string id) =>
        await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = '{type}' AND aggregate_id = '{id}'");

    [Fact]
    public async Task REQ_RI_058_A_future_treaty_stays_Approved_until_the_scanner_runs_after_the_clock_reaches_the_period_start()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        var product = NewProduct();
        var (id, approved) = await slice.ApprovedAsync(Body(product, "OD", lead, follow, from: "2026-03-01", to: "2027-03-01"));
        approved.Text("contract.status").ShouldBe("APPROVED");
        approved.Text("contract.activatedAt").ShouldBe("null");
        (await EventsAsync(slice, "RIContractActivated", id)).ShouldBe(0);

        // Still February in Athens: the last instant before the period starts does nothing, however often the scanner runs.
        slice.Clock.Now = AthensMidnight(2026, 3, 1).Minus(Tick);
        await slice.ScanAsync();
        await slice.ScanAsync();
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("APPROVED");
        (await EventsAsync(slice, "RIContractActivated", id)).ShouldBe(0);
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/applicable?productCode={product}&coverageCode=OD&validAt=2026-03-05")).Body!["contracts"]!.AsArray().Count.ShouldBe(0);

        // The Athens midnight that starts the period: activated once, however often the scanner runs afterwards.
        slice.Clock.Now = AthensMidnight(2026, 3, 1);
        await slice.ScanAsync();
        var active = await slice.GetAsync(id);
        active.Text("contract.status").ShouldBe("ACTIVE");
        active.Text("contract.activatedAt").ShouldContain("2026-02-28T22:00:00");
        await slice.ScanAsync();
        await Task.WhenAll(slice.ScanAsync(), slice.ScanAsync());
        (await EventsAsync(slice, "RIContractActivated", id)).ShouldBe(1);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'ri.Contract.applyDueLifecycle' AND object_id = '{id}' AND outcome = 'Succeeded'"))
            .ShouldBeGreaterThanOrEqualTo(1);
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/applicable?productCode={product}&coverageCode=OD&validAt=2026-03-05")).Body!["contracts"]!.AsArray().Count.ShouldBe(1);

        // Expiry at the period end: the last instant of the period leaves it Active, the next Athens midnight expires it.
        slice.Clock.Now = AthensMidnight(2027, 3, 1).Minus(Tick);
        await slice.ScanAsync();
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("ACTIVE");
        slice.Clock.Now = AthensMidnight(2027, 3, 1);
        await slice.ScanAsync();
        await slice.ScanAsync();
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("EXPIRED");
        (await EventsAsync(slice, "RIContractExpired", id)).ShouldBe(1);
        (await EventsAsync(slice, "RIContractActivated", id)).ShouldBe(1);

        // Only Active contracts are applicable (brief): an expired treaty is not returned, even for a loss inside its period.
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/applicable?productCode={product}&coverageCode=OD&validAt=2026-03-05")).Body!["contracts"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task PITFALLS_14_Activation_follows_the_Athens_calendar_across_the_daylight_saving_change()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        // DST starts on 2026-03-29 (Athens UTC+2 → UTC+3): 2026-03-30 00:00 local is 2026-03-29T21:00Z, not 22:00Z or 00:00Z.
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow, from: "2026-03-30", to: "2027-03-30"));
        approved.Text("contract.status").ShouldBe("APPROVED");

        slice.Clock.Now = Instant.FromUtc(2026, 3, 29, 20, 59, 59);
        await slice.ScanAsync();
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("APPROVED");
        slice.Clock.Now = Instant.FromUtc(2026, 3, 29, 21, 0, 0);
        await slice.ScanAsync();
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task REQ_RI_058_Approval_on_the_period_start_day_activates_in_the_same_transaction_and_a_late_scan_does_nothing_more()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        // 2026-02-10 11:00 Athens; the period starts today (Athens date) so the approval itself activates.
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow, from: "2026-02-10", to: "2027-02-10"));
        approved.Text("contract.status").ShouldBe("ACTIVE");
        (await EventsAsync(slice, "RIContractActivated", id)).ShouldBe(1);
        await slice.ScanAsync();
        (await EventsAsync(slice, "RIContractActivated", id)).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_RI_001_116_Applicable_respects_the_half_open_Athens_period_and_the_scope()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        var product = NewProduct();
        var (id, _) = await slice.ApprovedAsync(Body(product, "OD", lead, follow));

        // A draft and a submitted treaty for the same scope and period are never applicable.
        var (draft, _, _) = await slice.CreateAsync(Body(product, "OD", lead, follow));
        var (pending, pendingVersion, _) = await slice.CreateAsync(Body(product, "OD", lead, follow));
        await slice.SubmitAsync(pending, pendingVersion);

        async Task<JsonArray> Applicable(string product1, string coverage, string validAt, string roles = ClaimsHandler)
        {
            var (response, body) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/applicable?productCode={product1}&coverageCode={coverage}&validAt={validAt}", roles: roles);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            return body!["contracts"]!.AsArray();
        }

        // Period [2026-01-01, 2027-01-01) in Athens: winter time is UTC+2.
        (await Applicable(product, "OD", "2025-12-31T21:59:59.9999999Z")).Count.ShouldBe(0);
        (await Applicable(product, "OD", "2025-12-31T22:00:00Z")).Count.ShouldBe(1);
        (await Applicable(product, "OD", "2026-12-31T21:59:59.9999999Z")).Count.ShouldBe(1);
        (await Applicable(product, "OD", "2026-12-31T22:00:00Z")).Count.ShouldBe(0);

        // A date-form validAt is that Athens business day: the first day is in, the end date is out.
        (await Applicable(product, "OD", "2026-01-01")).Count.ShouldBe(1);
        (await Applicable(product, "OD", "2026-12-31")).Count.ShouldBe(1);
        (await Applicable(product, "OD", "2027-01-01")).Count.ShouldBe(0);
        (await Applicable(product, "OD", "2025-12-31")).Count.ShouldBe(0);

        // Scope: both the product and the coverage must be named.
        (await Applicable(product, "MTPL", "2026-06-01")).Count.ShouldBe(0);
        (await Applicable(NewProduct(), "OD", "2026-06-01")).Count.ShouldBe(0);

        var hit = (await Applicable(product, "OD", "2026-06-01")).Single()!;
        hit["contractId"]!.GetValue<string>().ShouldBe(id);
        hit["status"]!.GetValue<string>().ShouldBe("ACTIVE");
        hit["layers"]![0]!["limit"]!["amount"]!.GetValue<string>().ShouldBe("500000.00");
        hit["participations"]!.AsArray().Count.ShouldBe(2);
        draft.ShouldNotBe(id);

        // No loss instant means now (Athens date of the clock); missing codes are a validation error.
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/applicable?productCode={product}&coverageCode=OD", roles: ClaimsHandler)).Body!["contracts"]!.AsArray().Count.ShouldBe(1);
        var (missing, missingBody) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/contracts/applicable?coverageCode=OD", roles: ClaimsHandler);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missingBody.Text("code").ShouldBe("RI-ERR-VALIDATION");
        var (badAt, badAtBody) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/applicable?productCode={product}&coverageCode=OD&validAt=yesterday", roles: ClaimsHandler);
        badAt.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badAtBody.Text("code").ShouldBe("RI-ERR-VALIDATION");

        // The date form answers with the end of that Athens day.
        var (dated, datedBody) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/applicable?productCode={product}&coverageCode=OD&validAt=2026-12-31", roles: ClaimsHandler);
        dated.StatusCode.ShouldBe(HttpStatusCode.OK);
        datedBody.Text("validAt").ShouldContain("2026-12-31T21:59:59.99");
    }

    [Fact]
    public async Task PITFALLS_3_The_scanner_never_activates_an_approved_treaty_whose_content_no_longer_matches_its_approval()
    {
        var (slice, lead, follow) = await NewSliceAsync(database);
        await using var _ = slice;
        var (id, approved) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow, from: "2026-04-01", to: "2027-04-01"));
        approved.Text("contract.status").ShouldBe("APPROVED");

        // The approved version is sealed, so tamper with a child row as the owner with triggers off (a corrupted store).
        await slice.ExecuteAsync(
            $"""
            BEGIN;
            SET LOCAL session_replication_role = replica;
            UPDATE ri.participation SET signed_line_pct = 55 WHERE signed_line_pct = 60 AND version_id IN (SELECT version_id FROM ri.contract_version WHERE contract_id = '{id}');
            COMMIT;
            """);
        slice.Clock.Now = AthensMidnight(2026, 4, 1);
        await slice.ScanAsync();
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("APPROVED");
        (await EventsAsync(slice, "RIContractActivated", id)).ShouldBe(0);
    }
}
