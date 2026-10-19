using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using Npgsql;

namespace CoreIns.IntegrationTests.Reinsurance.Recovery;

/// <summary>
/// SL4-RI-RECOVERY: CLM events → claim view → per-contract-year recalculation → sealed delta rows, the
/// <c>RecoveryCalculated</c> events and the recovery queries (REQ-RI-003, -113, -115, -122, -131, -132, -136; PITFALLS 8, 13).
/// Treaty: 500,000 xs 500,000, AAL 1,500,000, panel 60% lead / 40% follow, placed 100%. Everything is synthetic.
/// </summary>
public sealed class RecoveryTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static readonly string[] PayloadKeys =
    [
        "batchId", "claimIds", "claimLevelTotals", "contractType", "contractYear", "ifrs17GroupRef", "ifrs17Refs", "incurredDelta", "layerId", "occurrence",
        "outstandingDelta", "paidDelta", "participantPartyId", "postingKey", "posting", "recoveryIds", "riContractId", "siiLob", "sourceCorrelationKey",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<PostgresException> RefusedAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct), sql);
    }

    [Fact]
    public async Task REQ_RI_003_claim_movements_book_deltas_per_participant_in_cents_rows_a_to_d()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, lead, follow) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);

        // a: reserve 800,000 → UNL incurred 800,000 → layer loss 300,000 → 180,000 / 120,000 incurred, nothing paid.
        await slice.ReserveAsync(claim, exposure, "800000.00");
        var a = RecoverySlice.PerParticipant((await slice.ByClaimAsync(claim)).Body);
        a[lead].ShouldBe((180000m, 0m));
        a[follow].ShouldBe((120000m, 0m));

        // b: 600,000 paid, 200,000 still open → incurred unchanged, paid layer loss 100,000 → 60,000 / 40,000 paid.
        await slice.ReserveAsync(claim, exposure, "200000.00");
        await slice.PaidAsync(claim, exposure, "600000.00");
        var b = RecoverySlice.PerParticipant((await slice.ByClaimAsync(claim)).Body);
        b[lead].ShouldBe((180000m, 60000m));
        b[follow].ShouldBe((120000m, 40000m));

        // c: the payment is voided and the reserve restored → the paid delta is negative, paid back to 0.
        var voided = await slice.VoidedAsync(claim, exposure, "600000.00");
        await slice.ReserveAsync(claim, exposure, "800000.00");
        var c = RecoverySlice.PerParticipant((await slice.ByClaimAsync(claim)).Body);
        c[lead].ShouldBe((180000m, 0m));
        c[follow].ShouldBe((120000m, 0m));
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE claim_id = '{claim}' AND paid_delta < 0")).ShouldBe(2);
        voided.AggregateSequence.ShouldBeGreaterThan(0);

        // d: pay 600,000 and close with 200,000 released → outstanding 0, paid unchanged, incurred falls to the paid layer loss.
        await slice.PaidAsync(claim, exposure, "600000.00");
        await slice.ClosedAsync(claim);
        var page = (await slice.ByClaimAsync(claim)).Body;
        var d = RecoverySlice.PerParticipant(page);
        d[lead].ShouldBe((60000m, 60000m));
        d[follow].ShouldBe((40000m, 40000m));
        foreach (var item in page!["items"]!.AsArray())
        {
            item!["recoverableOutstanding"]!["amount"]!.GetValue<string>().ShouldBe("0.00");
            item["state"]!.GetValue<string>().ShouldBe("CALCULATED");
        }

        // The contract view carries the same booked totals.
        var byContract = RecoverySlice.PerParticipant((await slice.ByContractAsync(contract)).Body);
        byContract[lead].ShouldBe((60000m, 60000m));

        // One RecoveryCalculated per layer x participant delta, all in the outbox of the treaty aggregate.
        var events = await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RecoveryCalculated' AND aggregate_id = '{contract}'");
        events.ShouldBe(await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'"));
    }

    [Fact]
    public async Task REQ_RI_132_the_trace_of_a_batch_is_stored_and_readable()
    {
        await using var slice = new RecoverySlice(database);
        var (product, _, _, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, "800000.00");
        var batchId = (await slice.ByClaimAsync(claim)).Body!["items"]![0]!["batchId"]!.GetValue<string>();

        var (response, trace) = await slice.Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/recoveries/trace?batchId={batchId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, trace?.ToJsonString());
        trace!["engineVersion"]!.GetValue<string>().ShouldStartWith("xol-recovery");
        trace["trace"]!.AsArray().Count.ShouldBeGreaterThan(0);
        trace["recoverables"]!.AsArray().Count.ShouldBe(2);
        (await slice.Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/recoveries/trace?batchId={Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_duplicate_event_adds_no_rows_and_recalculating_twice_gives_zero_deltas()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        var reserve = await slice.ReserveAsync(claim, exposure, "800000.00");
        var paid = await slice.PaidAsync(claim, exposure, "300000.00");
        var rows = await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'");

        (await slice.DeliverAsync("RI.ReserveChanged.ClaimView", reserve)).ShouldBeFalse(); // processed marker
        (await slice.DeliverAsync("RI.PaymentIssued.ClaimView", paid, replay: true)).ShouldBeTrue(); // operator replay: event id already applied
        (await slice.DeliverAsync("RI.ReserveChanged.ClaimView", reserve, replay: true)).ShouldBeTrue();

        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'")).ShouldBe(rows);
        (await slice.ScalarAsync<decimal>($"SELECT paid FROM ri.claim_amount WHERE claim_id = '{claim}'")).ShouldBe(300000m);

        // A fresh event that changes nothing for the treaty (own damage reserve elsewhere) books no new delta either.
        var before = await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery_batch WHERE contract_id = '{contract}'");
        await slice.ReserveAsync(claim, exposure, "800000.00");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery_batch WHERE contract_id = '{contract}'")).ShouldBe(before);
    }

    [Fact]
    public async Task An_older_reserve_event_arriving_late_is_a_no_op()
    {
        await using var slice = new RecoverySlice(database);
        var (product, _, _, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        var older = await slice.ReserveAsync(claim, exposure, "700000.00");
        await slice.ReserveAsync(claim, exposure, "900000.00");

        // The same older event under a new event id and the old sequence (a parked event replayed later).
        var stale = older with { EventId = new CoreIns.SharedKernel.Identifiers.EventId(Guid.CreateVersion7()) };
        (await slice.DeliverAsync("RI.ReserveChanged.ClaimView", stale)).ShouldBeTrue();

        (await slice.ScalarAsync<decimal>($"SELECT open_reserve FROM ri.claim_amount WHERE claim_id = '{claim}'")).ShouldBe(900000m);
    }

    [Fact]
    public async Task An_exposure_outside_the_treaty_scope_books_nothing()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure, coverage: "OD"); // own damage: not in the treaty's scope
        await slice.ReserveAsync(claim, exposure, "900000.00");

        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'")).ShouldBe(0);
        (await slice.ByClaimAsync(claim)).Body!["items"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task No_active_contract_at_the_loss_date_books_nothing()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product, lossDate: "2025-06-01"); // before the treaty period
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, "900000.00");

        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Two_contracts_that_both_match_are_refused_and_logged_without_an_invented_order()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, lead, follow) = await slice.TreatyAsync();
        var (second, _) = await slice.Registry.ApprovedAsync(RegistrySlice.Body(product, RecoverySlice.Coverage, lead, follow));
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, "900000.00");

        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE claim_id = '{claim}'")).ShouldBe(0);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.intake_exception WHERE claim_id = '{claim}' AND code = 'OVERLAPPING_CONTRACTS'")).ShouldBeGreaterThan(0);
        contract.ShouldNotBe(second);
    }

    [Fact]
    public async Task Concurrent_movements_of_two_claims_in_one_contract_year_are_serialised_and_total_the_same_as_one_recalculation()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var claims = new[] { (Claim: Guid.CreateVersion7(), Exposure: Guid.CreateVersion7()), (Claim: Guid.CreateVersion7(), Exposure: Guid.CreateVersion7()) };
        foreach (var c in claims)
        {
            await slice.ReportedAsync(c.Claim, product);
            await slice.ExposureAsync(c.Claim, c.Exposure);
        }

        await Task.WhenAll(claims.Select(c => slice.ReserveAsync(c.Claim, c.Exposure, "800000.00")));

        // Each claim: layer loss 300,000. Year total 600,000 → 360,000 / 240,000, whatever the order the two batches ran in.
        var totals = RecoverySlice.PerParticipant((await slice.ByContractAsync(contract)).Body);
        totals.Values.Sum(v => v.Incurred).ShouldBe(600000m);
        totals.Values.Select(v => v.Incurred).Order().ToArray().ShouldBe([240000m, 360000m]);
    }

    [Fact]
    public async Task The_application_role_cannot_update_or_delete_booked_rows()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, "800000.00");

        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        foreach (var sql in new[]
        {
            $"UPDATE ri.recovery SET incurred_delta = 1 WHERE contract_id = '{contract}'",
            $"DELETE FROM ri.recovery WHERE contract_id = '{contract}'",
            $"UPDATE ri.recovery_batch SET calc_hash = 'x' WHERE contract_id = '{contract}'",
            $"DELETE FROM ri.recovery_batch WHERE contract_id = '{contract}'",
            "TRUNCATE ri.recovery",
        })
        {
            (await RefusedAsync(app, sql)).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // The seal trigger binds the superuser too (the app role is refused by privilege first).
        await using var admin = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await RefusedAsync(admin, $"UPDATE ri.recovery SET incurred_delta = 1 WHERE contract_id = '{contract}'")).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task RecoveryCalculated_carries_party_ids_and_amounts_only()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, _, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, "800000.00");

        var payload = JsonNode.Parse(await slice.ScalarAsync<string>(
            $"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'RecoveryCalculated' AND aggregate_id = '{contract}' LIMIT 1"))!.AsObject();

        payload.Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(PayloadKeys.Order(StringComparer.Ordinal));
        payload["ifrs17GroupRef"]!.GetValue<string>().ShouldBe("UNASSIGNED");
        payload["siiLob"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        payload["sourceCorrelationKey"]!.GetValue<string>().ShouldStartWith(claim.ToString());
    }

    [Fact]
    public async Task REQ_RI_136_listByClaim_as_of_an_earlier_record_time_gives_the_earlier_answer_and_a_future_time_is_refused()
    {
        await using var slice = new RecoverySlice(database);
        var (product, _, lead, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, "700000.00");
        var between = (slice.Registry.Clock.Now + TimeSpan.FromMinutes(30)).ToString();
        slice.Registry.Clock.Now += TimeSpan.FromHours(1);
        await slice.ReserveAsync(claim, exposure, "900000.00");

        RecoverySlice.PerParticipant((await slice.ByClaimAsync(claim, between)).Body)[lead].Incurred.ShouldBe(120000m); // 200,000 x 60%
        RecoverySlice.PerParticipant((await slice.ByClaimAsync(claim)).Body)[lead].Incurred.ShouldBe(240000m); // 400,000 x 60%

        var (response, _) = await slice.ByClaimAsync(claim, (slice.Registry.Clock.Now + TimeSpan.FromDays(1)).ToString());
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Clearing_payments_count_as_paid_and_other_roles_cannot_read_recoveries()
    {
        await using var slice = new RecoverySlice(database);
        var (product, _, lead, _) = await slice.TreatyAsync();
        var claim = Guid.CreateVersion7();
        var exposure = Guid.CreateVersion7();
        await slice.ReportedAsync(claim, product);
        await slice.ExposureAsync(claim, exposure);
        await slice.ReserveAsync(claim, exposure, "200000.00");
        await slice.PaidAsync(claim, exposure, "700000.00", method: "CLEARING");

        RecoverySlice.PerParticipant((await slice.ByClaimAsync(claim)).Body)[lead].ShouldBe((240000m, 120000m)); // incurred UNL 900,000 → layer 400,000 x 60%; paid UNL 700,000 → layer 200,000 x 60%
        var (denied, _) = await slice.Registry.SendAsync(HttpMethod.Get, $"/api/ri/v1/recoveries/list-by-claim?claimId={claim}", roles: "Staff.Underwriter");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
