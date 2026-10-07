using System.Globalization;
using System.Net;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// Quote → bind with every module real (PTY, PFC MOTOR-GR, MKT rounding and tax configuration, RAT's illustrative tariff,
/// UW's illustrative rule sets UW-MOTOR-GR-Q / -B): the E2E-01 path up to the BIL hand-off. Amounts are not asserted
/// against fixed values (the tariff is illustrative test data, D-SLC-04); their consistency is.
/// </summary>
public sealed class PolicyWithRealModulesTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset InTwoDays => DateTimeOffset.UtcNow.AddDays(2);

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task E2E_01_quote_with_real_RAT_and_UW_binds_and_hands_charges_to_billing()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);

        var (quoted, quote) = await _slice.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("state").ShouldBe("QUOTED");
        quote.Text("decision").ShouldBe("ACCEPT");
        var premium = decimal.Parse(quote.Text("premium.amount"), CultureInfo.InvariantCulture);
        var taxes = decimal.Parse(quote.Text("taxes.amount"), CultureInfo.InvariantCulture);
        var total = decimal.Parse(quote.Text("total.amount"), CultureInfo.InvariantCulture);
        premium.ShouldBeGreaterThan(0m);
        taxes.ShouldBeGreaterThan(0m);
        total.ShouldBe(premium + taxes);
        var charges = quote!["charges"]!.AsArray();
        charges.Select(c => c!["chargeType"]!.GetValue<string>()).ShouldContain("GR-IPT");

        // D-SLC-11: the IPT value's legal status travels onto the charge line (the GR pack's IPT is not Settled yet).
        var ipt = charges.First(c => c!["chargeType"]!.GetValue<string>() == "GR-IPT")!;
        ipt["legalStatus"]!.GetValue<string>().ShouldBe("Verify");
        ipt["provisional"]!.GetValue<bool>().ShouldBeTrue();
        charges.Sum(c => decimal.Parse(c!["amount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture)).ShouldBe(total);

        var (bound, bind) = await _slice.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND");
        var transactionId = bind.Text("transactionId");
        var policyId = bind.Text("policyId");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<decimal>(dataSource, $"SELECT total FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(total);
        (await ScalarAsync<decimal>(dataSource,
                $"SELECT sum((payload->'netAmount'->>'amount')::numeric) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transactionId}'"))
            .ShouldBe(total);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyBound' AND aggregate_id = '{policyId}'")).ShouldBe(1);
        (await ScalarAsync<long>(dataSource,
                $"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'GR-IPT' AND legal_status = 'Verify' AND provisional"))
            .ShouldBeGreaterThanOrEqualTo(1);

        // RAT's worksheet committed with the quote (rat.Rate.rate runs inside the quote's unit of work).
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RatingCalculated' AND payload->>'jobId' = '{jobId}'"))
            .ShouldBeGreaterThanOrEqualTo(1);

        // The driver's date of birth (P2) reached RAT and UW in-process but is stored nowhere in pol, nor in POL's events.
        (await ScalarAsync<long>(dataSource,
                "SELECT (SELECT count(*) FROM pol.quote_version WHERE risk_tree::text LIKE '%1980-05-17%' OR issues::text LIKE '%1980-05-17%')"
                + " + (SELECT count(*) FROM pol.segment WHERE snapshot::text LIKE '%1980-05-17%')"
                + " + (SELECT count(*) FROM pol.policy_transaction WHERE intent::text LIKE '%1980-05-17%')"
                + $" + (SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{policyId}' AND payload::text LIKE '%1980-05-17%')"
                + " + (SELECT count(*) FROM plt.idempotency_record WHERE convert_from(response_body, 'UTF8') LIKE '%1980-05-17%')"))
            .ShouldBe(0);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM plt.audit_event WHERE operation = 'pty.Party.revealP2' AND reason = 'RATING'"))
            .ShouldBeGreaterThanOrEqualTo(2); // quote and bind each read the date of birth through PTY's audited reveal
    }

    [Fact]
    public async Task REQ_POL_157_a_UW_decline_at_PRE_QUOTE_leaves_the_job_unquoted()
    {
        var party = await _slice.CreatePartyAsync();
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays, usage: "BUSINESS");

        var (response, quote) = await _slice.QuoteAsync(jobId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("decision").ShouldBe("DECLINE");
        quote.Text("state").ShouldBe("DRAFT");
        quote!["validUntil"].ShouldBeNull();
        (await _slice.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task REQ_POL_003_a_young_driver_referral_at_PRE_BIND_fails_the_bind_gate()
    {
        var birth = DateTime.UtcNow.AddYears(-19).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var party = await _slice.CreatePartyAsync(birth);
        var (jobId, _, _) = await _slice.DraftAsync(party, InTwoDays);

        var (_, quote) = await _slice.QuoteAsync(jobId);
        quote.Text("state").ShouldBe("QUOTED", quote?.ToJsonString());

        var (gated, bind) = await _slice.BindAsync(jobId);
        gated.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("QUOTED");
        bind!["transactionId"].ShouldBeNull();
        var gate = bind["gateResults"]!.AsArray().Single(g => g!["gate"]!.GetValue<string>() == "UW_ISSUES")!;
        gate["passed"]!.GetValue<bool>().ShouldBeFalse();
        gate["reason"]!.GetValue<string>().ShouldBe("UW_ISSUES_OPEN");
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
