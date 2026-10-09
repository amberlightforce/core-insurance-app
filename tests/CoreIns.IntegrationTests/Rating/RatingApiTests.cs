using CoreIns.SharedKernel.Identifiers;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;
using CoreIns.Testing.Contracts.Fakes.Market;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>
/// SL-RAT-UW: the rating service POL calls in-process, and its REST facade, on a real PostgreSQL 17 with the generated MKT
/// double supplying the (synthetic) tax configuration. Rates are illustrative test data (D-SLC-04).
/// </summary>
public sealed class RatingApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private FakeMarketConfigurationService _market = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _baseFactory = new ApiHostFactory(database.AppConnectionString);
        _market = MarketFake();
        _factory = WithMarket(_baseFactory, _market);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<RateRateResponse> RateAsync(RateRateRequest request)
    {
        await using var scope = Scope(_factory.Services);
        return await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<string> ErrorCodeAsync(RateRateRequest request)
    {
        var ex = await Should.ThrowAsync<DomainException>(() => RateAsync(request));
        return ex.Error.Code.Value;
    }

    // REQ-RAT-001, -009, -036, -105, -110, D-API-12: premium per coverage, tax and levy lines from MKT configuration, a worksheet id.
    [Fact]
    public async Task REQ_RAT_001_rating_returns_premium_per_coverage_tax_and_levy_lines_totals_and_a_worksheet_hash()
    {
        var response = await RateAsync(RateRequest());

        response.Rates.Select(r => (r.CoverageCode, r.ChargeType, r.AnnualRate)).ShouldBe(
            [("MTPL", "PREM-MTPL", 121.50m), ("OWN-DAMAGE", "PREM-OD", 283.50m), ("WINDSCREEN", "PREM-WINDSCREEN", 25.00m)]);
        response.Rates.ShouldAllBe(r => r.ElementId == "veh-1" && r.ChargeCategory == "PREMIUM" && r.Currency == Currency.EUR);
        response.Taxes!.Select(t => (t.CoverageCode, t.ChargeType, t.Amount.Amount)).ShouldBe(
            [("MTPL", "GR-IPT", 18.23m), ("OWN-DAMAGE", "GR-IPT", 42.53m), ("WINDSCREEN", "GR-IPT", 3.75m)]);
        response.Taxes!.Select(t => t.ChargeType).ShouldNotContain(c => c.StartsWith("GR-AUXF", StringComparison.Ordinal)); // D-REG-06a: no fund line
        response.Taxes!.ShouldAllBe(t => t.ConfigurationKey.StartsWith("tax.", StringComparison.Ordinal) && t.Amount.Currency == Currency.EUR);
        response.CoveragePremiumTotal!.Value.Amount.ShouldBe(430.00m);
        response.TaxTotal!.Value.Amount.ShouldBe(64.51m);
        response.GrossTotal!.Value.Amount.ShouldBe(494.51m);
        response.Bindable.ShouldBeTrue();
        response.WorksheetId.Value.Length.ShouldBe(64);
        response.WorksheetHash.ShouldBe(response.WorksheetId);
        response.ConfigurationHash.ShouldBe(TestConfigurationHash);
    }

    // D-SLC-04: the result says it is illustrative test data.
    [Fact]
    public async Task D_SLC_04_the_response_and_the_worksheet_carry_the_illustrative_test_data_marking()
    {
        var response = await RateAsync(RateRequest());

        response.Warnings!.Select(w => w.Code).ShouldContain("RAT-WARN-ILLUSTRATIVE-TARIFF");
        var (get, body) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/{response.WorksheetId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("worksheet.header.dataStatus").ShouldBe("ILLUSTRATIVE_TEST_DATA");
        body.Text("worksheet.header.notATariff").ShouldBe("true");
        body.Text("worksheet.header.notice").ShouldContain("not an approved tariff");
        (await ScalarAsync<string>("SELECT data_status FROM rat.rating_artifact LIMIT 1")).ShouldBe("ILLUSTRATIVE_TEST_DATA");
        (await ScalarAsync<long>("SELECT count(*) FROM rat.rate_table_version WHERE data_status <> 'ILLUSTRATIVE_TEST_DATA'")).ShouldBe(0);
    }

    // REQ-RAT-003, -104, -227, -228, D-API-12: the stored worksheet is content-addressed and explains every step.
    [Fact]
    public async Task REQ_RAT_228_the_worksheet_is_stored_content_addressed_with_the_step_trace_and_the_hashes_of_inputs_tables_and_engine()
    {
        var response = await RateAsync(RateRequest(quoteId: Guid.CreateVersion7()));

        var (get, body) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/{response.WorksheetId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var worksheet = body!["worksheet"]!;
        CanonicalJson.Hash(worksheet).ShouldBe(response.WorksheetId); // the id is the SHA-256 of the canonical worksheet
        var header = worksheet["header"]!;
        header["inputHash"]!.GetValue<string>().Length.ShouldBe(64);
        header["engineVersion"]!.GetValue<string>().ShouldContain("cel-subset/1.0");
        header["configurationHash"]!.GetValue<string>().ShouldBe(TestConfigurationHash.ToString());
        header["ratingArtefactHash"]!.GetValue<string>().ShouldBe(response.RatingArtefactHash!.Value.ToString());
        header["tableHashes"]!.AsObject().Count.ShouldBe(5);
        header["tableHashes"]!.AsObject().ShouldAllBe(t => t.Value!.GetValue<string>().Length == 64);
        var own = worksheet["lines"]!.AsArray().Single(l => l!["coverageCode"]!.GetValue<string>() == "OWN-DAMAGE")!;
        own["annualPremium"]!.GetValue<string>().ShouldBe("283.50");
        own["steps"]!.AsArray().Select(s => s!["step"]!.GetValue<string>()).ShouldBe(
            ["BASE_RATE", "DRIVER_AGE", "VEHICLE_AGE", "CLAIMS", "MINIMUM_PREMIUM", "ROUND_PREMIUM"]);
        own["steps"]![1]!["explanation"]!["el"]!.GetValue<string>().ShouldContain("οδηγού");
        worksheet["taxes"]!.AsArray().Count.ShouldBe(3);

        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet_index WHERE worksheet_id = '{response.WorksheetId}' AND retention_state = 'QUOTE'")).ShouldBeGreaterThanOrEqualTo(1);
    }

    // REQ-RAT-046: the same input gives the same worksheet; nothing is duplicated.
    [Fact]
    public async Task REQ_RAT_046_rating_the_same_input_twice_gives_the_same_worksheet_id_and_one_stored_worksheet()
    {
        var request = RateRequest(RiskTree(value: "31234.56", coverages: ["OWN-DAMAGE"]));
        var first = await RateAsync(request);
        var second = await RateAsync(request);

        second.WorksheetId.ShouldBe(first.WorksheetId);
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet WHERE worksheet_id = '{first.WorksheetId}'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet_index WHERE worksheet_id = '{first.WorksheetId}'")).ShouldBe(1); // a retry of the same lineage adds no index row

        var other = await RateAsync(RateRequest(RiskTree(value: "31234.57", coverages: ["OWN-DAMAGE"])));
        other.WorksheetId.ShouldNotBe(first.WorksheetId);
    }

    [Fact]
    public async Task The_rating_event_is_published_in_the_same_transaction_without_personal_data()
    {
        var response = await RateAsync(RateRequest(RiskTree(value: "44444.44", coverages: ["MTPL"]), quoteId: Guid.CreateVersion7()));

        (await ScalarAsync<long>(
            $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RatingCalculated' AND business_keys->>'worksheetId' = '{response.WorksheetId}'"))
            .ShouldBeGreaterThanOrEqualTo(1);
        var payload = await ScalarAsync<string>(
            $"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'RatingCalculated' AND business_keys->>'worksheetId' = '{response.WorksheetId}' LIMIT 1");
        payload.ShouldNotContain("1985-06-15"); // no birth date or other driver data (the full date: ids and hashes can contain "1985" by chance, D-ARC-37)
        payload.ShouldContain("coveragePremiumTotal");
    }

    // REQ-RAT-042: DRY_RUN stores nothing and is not bindable; QUICK is stored but not bindable (REQ-RAT-040).
    [Fact]
    public async Task REQ_RAT_042_a_dry_run_stores_no_worksheet_and_a_quick_rating_is_not_bindable()
    {
        var dry = await RateAsync(RateRequest(RiskTree(value: "50001.00"), mode: "DRY_RUN"));
        var quick = await RateAsync(RateRequest(RiskTree(value: "50002.00"), mode: "QUICK"));

        dry.Bindable.ShouldBeFalse();
        quick.Bindable.ShouldBeFalse();
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet WHERE worksheet_id = '{dry.WorksheetId}'")).ShouldBe(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet WHERE worksheet_id = '{quick.WorksheetId}'")).ShouldBe(1);
    }

    // REQ-RAT-112: no IPT rate from MKT, no price.
    [Fact]
    public async Task REQ_RAT_112_an_ipt_rate_missing_from_market_configuration_fails_closed_and_stores_nothing()
    {
        await using var noIpt = WithMarket(_baseFactory, MarketFake(withIpt: false));
        await using var scope = Scope(noIpt.Services);
        var service = scope.ServiceProvider.GetRequiredService<IRatingRateService>();
        var before = await ScalarAsync<long>("SELECT count(*) FROM rat.worksheet");

        var ex = await Should.ThrowAsync<DomainException>(() => service.RateAsync(RateRequest(RiskTree(value: "60000.00")), TestContext.Current.CancellationToken));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");
        ex.Error.Detail!.ShouldContain("tax.ipt.");
        (await ScalarAsync<long>("SELECT count(*) FROM rat.worksheet")).ShouldBe(before);
    }

    // D-REG-06/06a: the Auxiliary Fund split, stamp duty and base are Pending opinion; RAT produces IPT only and no fund line,
    // even when MKT offers the 6% ceiling.
    [Fact]
    public async Task D_REG_06a_no_auxiliary_fund_line_is_produced_even_when_the_ceiling_is_configured()
    {
        var response = await RateAsync(RateRequest(RiskTree(coverages: ["MTPL"])));

        response.Taxes!.Select(t => t.ChargeType).Distinct().ShouldBe(["GR-IPT"]);
        response.Taxes!.Select(t => t.ConfigurationKey).ShouldNotContain(k => k.Contains("auxfund", StringComparison.Ordinal));
    }

    // D-REG-01/02: the line carries the weakest legal status of the class and the rate, and is provisional if either is.
    [Fact]
    public async Task D_REG_01_a_settled_rate_with_a_settled_class_gives_a_settled_non_provisional_line()
    {
        var settled = await RateAsync(RateRequest(RiskTree(value: "11111.11")));

        settled.Taxes!.ShouldAllBe(t => t.LegalStatus == "Settled" && t.Provisional == false && t.ConfigurationKey == "tax.ipt.rate.general"
            && t.ClassConfigurationKey == "tax.ipt.motor_class" && t.ChargeCategory == "TAX" && t.CoverageCode != null);
        settled.Warnings!.Select(w => w.Code).ShouldNotContain("RAT-WARN-PROVISIONAL-TAX");
    }

    [Fact]
    public async Task D_REG_01_a_verify_class_makes_the_line_verify_and_provisional_even_when_the_rate_is_settled()
    {
        await using var verify = WithMarket(_baseFactory, MarketFake(classSettled: false));
        await using var scope = Scope(verify.Services);

        var response = await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(RateRequest(RiskTree(value: "22222.22")), TestContext.Current.CancellationToken);

        response.Taxes!.ShouldAllBe(t => t.LegalStatus == "Verify" && t.Provisional == true && t.ClassLegalStatus == "Verify" && t.ClassConfigurationValueVersionId != null);
        response.Warnings!.Select(w => w.Code).ShouldContain("RAT-WARN-PROVISIONAL-TAX");
        var (_, body) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/{response.WorksheetId}");
        body!["worksheet"]!["taxes"]!.AsArray().ShouldAllBe(t => t!["provisional"]!.GetValue<bool>() && t["legalStatus"]!.GetValue<string>() == "Verify"
            && t["classConfigurationKey"]!.GetValue<string>() == "tax.ipt.motor_class" && t["classConfigurationValueVersionId"] != null);
    }

    [Fact]
    public async Task D_REG_01_a_pending_opinion_rate_beats_a_verify_class_as_the_weaker_status()
    {
        await using var weak = WithMarket(_baseFactory, MarketFake(iptSettled: false, classSettled: false));
        await using var scope = Scope(weak.Services);

        var response = await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(RateRequest(RiskTree(value: "33333.33")), TestContext.Current.CancellationToken);

        response.Taxes!.ShouldAllBe(t => t.LegalStatus == "PendingOpinion" && t.Provisional == true);
    }

    [Theory]
    [InlineData(null)] // class key absent
    [InlineData("\"luxury\"")] // a class this artefact does not know
    [InlineData("42")] // not text
    [InlineData("\"\"")]
    public async Task D_REG_01_a_missing_or_malformed_tax_class_fails_closed_with_RAT_ERR_TAX_and_no_default(string? classJson)
    {
        await using var broken = WithMarket(_baseFactory, MarketFake(classJson: classJson));
        await using var scope = Scope(broken.Services);
        var before = await ScalarAsync<long>("SELECT count(*) FROM rat.worksheet");

        var ex = await Should.ThrowAsync<DomainException>(() =>
            scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(RateRequest(RiskTree(value: "44444.01")), TestContext.Current.CancellationToken));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");
        ex.Error.Detail!.ShouldContain("tax.ipt.motor_class");
        (await ScalarAsync<long>("SELECT count(*) FROM rat.worksheet")).ShouldBe(before);
    }

    // m1: a retried rate for the same lineage neither duplicates the index row nor the event.
    [Fact]
    public async Task A_retried_rate_for_the_same_quote_publishes_one_event_and_one_index_row()
    {
        var quote = Guid.CreateVersion7();
        var request = RateRequest(RiskTree(value: "55555.55"), quoteId: quote);
        var first = await RateAsync(request);
        await RateAsync(request);
        await RateAsync(request);

        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet_index WHERE quote_id = '{quote}'")).ShouldBe(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RatingCalculated' AND business_keys->>'worksheetId' = '{first.WorksheetId}'")).ShouldBe(1);
        (await RateAsync(RateRequest(RiskTree(value: "55555.55"), quoteId: Guid.CreateVersion7()))).WorksheetId.ShouldBe(first.WorksheetId);
    }

    // m2/m3: equal amounts are one input; an amount that cannot be held exactly is refused.
    [Fact]
    public async Task Equal_amounts_written_differently_give_the_same_worksheet_id()
    {
        var a = await RateAsync(RateRequest(RiskTree(value: "70004")));
        var b = await RateAsync(RateRequest(RiskTree(value: "70004.00")));
        var c = await RateAsync(RateRequest(RiskTree(value: "70004.0")));

        b.WorksheetId.ShouldBe(a.WorksheetId);
        c.WorksheetId.ShouldBe(a.WorksheetId);
    }

    [Theory]
    [InlineData("70004.001")]
    [InlineData("1e3")]
    [InlineData("0.1234567890123456789012345678901")]
    [InlineData("-5")]
    [InlineData("1000000000")]
    public async Task An_amount_that_does_not_round_trip_exactly_is_an_input_error(string value)
    {
        var tree = RiskTree();
        tree["vehicle"]!["vehicleValue"] = JsonNode.Parse(value);

        (await ErrorCodeAsync(RateRequest(tree))).ShouldBe("RAT-ERR-INPUT");
    }

    // m4: the input hash follows the derived ages, not the dates, so the same age band is the same input.
    [Fact]
    public async Task The_input_hash_uses_the_derived_ages_not_the_birth_date()
    {
        var a = await RateAsync(RateRequest(RiskTree(birthDate: "1985-06-15", value: "66666.66")));
        var b = await RateAsync(RateRequest(RiskTree(birthDate: "1985-09-30", value: "66666.66")));
        var (_, body) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/{a.WorksheetId}");

        b.WorksheetId.ShouldBe(a.WorksheetId);
        body!["worksheet"]!.ToJsonString().ShouldNotContain("1985-0");
    }

    [Fact]
    public async Task A_dry_run_is_named_DRY_RUN_in_the_worksheet_header()
    {
        await using var scope = Scope(_factory.Services);
        var request = RateRequest(RiskTree(value: "77777.77"), mode: "DRY_RUN");
        var response = await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request, TestContext.Current.CancellationToken);

        response.Bindable.ShouldBeFalse();
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet_index WHERE mode = 'DRYRUN'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_coverage_the_product_does_not_rate_is_an_input_error()
    {
        (await ErrorCodeAsync(RateRequest(RiskTree(coverages: ["MTPL", "THEFT"])))).ShouldBe("RAT-ERR-INPUT");
    }

    // The same rating against the real MKT module (merged from SL-MKT): its GR pack keys, no double.
    [Fact]
    public async Task Rating_works_against_the_real_market_configuration_service()
    {
        await using var scope = Scope(_baseFactory.Services);
        // The fake-market helper pins a placeholder; the real service requires a recorded unit-of-work state.
        var current = await scope.ServiceProvider.GetRequiredService<IMarketConfigurationService>().CurrentHashAsync(TestContext.Current.CancellationToken);
        var currentHash = new ConfigurationHash(current.Hash ?? throw new InvalidOperationException("The real MKT fixture must have a current hash."));
        scope.ServiceProvider.GetRequiredService<RequestContext>().ConfigurationHash = currentHash;

        var response = await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(RateRequest(RiskTree(value: "91000.00")), TestContext.Current.CancellationToken);

        response.Taxes!.First(t => t.ChargeType == "GR-IPT").Rate.ShouldBe(0.15m);
        response.Taxes!.First(t => t.ChargeType == "GR-IPT").Provisional.ShouldBe(true); // the real pack marks the motor class Verify
        response.Taxes!.First(t => t.ChargeType == "GR-IPT").LegalStatus.ShouldBe("Verify");
        response.ConfigurationHash.ShouldBe(currentHash);
    }

    [Fact]
    public async Task REQ_RAT_112_a_failing_market_configuration_service_also_fails_closed()
    {
        var failing = new FakeMarketConfigurationService();
        failing.Fail("mkt.Configuration.resolve", new InvalidOperationException("MKT is down"));
        await using var factory = WithMarket(_baseFactory, failing);
        await using var scope = Scope(factory.Services);

        var ex = await Should.ThrowAsync<DomainException>(() =>
            scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(RateRequest(), TestContext.Current.CancellationToken));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");
    }

    [Fact]
    public async Task Rates_are_resolved_from_mkt_for_the_tax_point_date_with_the_keys_the_artefact_names()
    {
        await RateAsync(RateRequest());

        var call = _market.CallsTo("mkt.Configuration.resolve")[^1];
        var request = (ConfigurationResolveRequest)call.Arguments[0]!;
        request.Keys!.ShouldBe(["tax.ipt.rate.general", "tax.ipt.motor_class"], ignoreOrder: true);
        request.Jurisdiction.ShouldBe("GR");
        request.ProductCode.ShouldBe(MotorProduct);
        request.TimeBasisDates!["TAX_POINT_DATE"].ShouldBe(new BusinessDate(2026, 11, 1));
        ((ValidAt)call.Arguments[1]!).Date.ShouldBe(new BusinessDate(2026, 11, 1));
    }

    [Theory]
    [InlineData("currency", "RAT-ERR-CURRENCY")]
    [InlineData("period", "RAT-ERR-PERIOD")]
    [InlineData("artefact", "RAT-ERR-UNKNOWN-ARTEFACT")]
    [InlineData("undeclared", "RAT-ERR-INPUT-UNDECLARED")]
    [InlineData("input", "RAT-ERR-INPUT")]
    [InlineData("version", "RAT-ERR-NO-ACTIVE-ARTEFACT")]
    public async Task Rating_errors_use_the_RAT_ERR_codes_of_the_contract(string what, string expected)
    {
        var request = what switch
        {
            "currency" => RateRequest(currency: "USD"),
            "period" => RateRequest(periodEnd: "2027-05-01"),
            "artefact" => RateRequest(ratingArtefactHash: new string('d', 64)),
            "undeclared" => RateRequest(Undeclared()),
            "input" => RateRequest(RiskTree(value: "0")),
            _ => WithVersion(RateRequest(), "9.9"),
        };

        (await ErrorCodeAsync(request)).ShouldBe(expected);
    }

    private static JsonObject Undeclared()
    {
        var tree = RiskTree();
        tree["driver"]!["shoeSize"] = 43;
        return tree;
    }

    private static RateRateRequest WithVersion(RateRateRequest request, string version) =>
        request with { Envelope = request.Envelope with { ProductVersion = ProductVersionNumber.Parse(version) } };

    [Fact]
    public async Task REQ_RAT_049_a_named_artefact_is_used_as_named_and_the_resolved_one_gives_the_same_worksheet()
    {
        await using var scope = Scope(_factory.Services);
        var artifacts = scope.ServiceProvider.GetRequiredService<IRatingRatingArtifactService>();
        var resolved = await artifacts.ResolveAsync(
            new RatingArtifactResolveRequest { ProductCode = MotorProduct, ProductVersion = ProductVersionNumber.Parse("1.0"), TransactionType = "NEW_BUSINESS" },
            ValidAt.From(new BusinessDate(2026, 11, 1)), cancellationToken: TestContext.Current.CancellationToken);
        var floating = await RateAsync(RateRequest());
        var named = await RateAsync(RateRequest(ratingArtefactHash: resolved.ArtefactHash!.Value.Value));

        named.WorksheetId.ShouldBe(floating.WorksheetId);
        named.RatingArtefactHash.ShouldBe(resolved.ArtefactHash);

        var before = await Should.ThrowAsync<DomainException>(() => artifacts.ResolveAsync(
            new RatingArtifactResolveRequest { ProductCode = MotorProduct, TransactionType = "NEW_BUSINESS" },
            ValidAt.From(new BusinessDate(2025, 12, 31)), cancellationToken: TestContext.Current.CancellationToken));
        before.Error.Code.Value.ShouldBe("RAT-ERR-NO-ACTIVE-ARTEFACT"); // not active before 2026-01-01
    }

    // The worksheet is written in the caller's transaction (POL's), so a rollback leaves nothing behind.
    [Fact]
    public async Task The_worksheet_and_its_event_join_the_callers_transaction()
    {
        var request = RateRequest(RiskTree(value: "70001.00", coverages: ["MTPL"]));
        await using (var scope = Scope(_factory.Services))
        {
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var response = await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request, TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet WHERE worksheet_id = '{response.WorksheetId}'")).ShouldBe(0);
        }

        await using (var scope = Scope(_factory.Services))
        {
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var response = await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request, TestContext.Current.CancellationToken);
            (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet WHERE worksheet_id = '{response.WorksheetId}'")).ShouldBe(0); // not yet committed
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            (await ScalarAsync<long>($"SELECT count(*) FROM rat.worksheet WHERE worksheet_id = '{response.WorksheetId}'")).ShouldBe(1);
        }
    }

    [Fact]
    public async Task The_rest_facade_rates_without_an_idempotency_key_and_maps_errors_to_problem_details()
    {
        var body = JsonNode.Parse(JsonSerializer.Serialize(RateRequest(RiskTree(value: "80001.00")), SharedKernelJson.Options))!;
        var (ok, response) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rates/rate", body, withKey: false);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, response?.ToJsonString());
        response.Text("rates.0.coverageCode").ShouldBe("MTPL");
        response.Text("worksheetId").Length.ShouldBe(64);

        var bad = JsonNode.Parse(JsonSerializer.Serialize(RateRequest(Undeclared()), SharedKernelJson.Options))!;
        var (rejected, problem) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rates/rate", bad, withKey: false);
        rejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        problem.Text("code").ShouldBe("RAT-ERR-INPUT-UNDECLARED");

        var (resolve, resolved) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rating-artifacts/resolve?validAt=2026-11-01",
            new { productCode = MotorProduct, productVersion = "1.0", transactionType = "NEW_BUSINESS" }, withKey: false);
        resolve.StatusCode.ShouldBe(HttpStatusCode.OK, resolved?.ToJsonString());
        resolved.Text("artefactHash").Length.ShouldBe(64);

        var (missing, notFound) = await SendAsync(_client, HttpMethod.Get, $"/api/rat/v1/worksheets/{new string('f', 64)}");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        notFound.Text("code").ShouldBe("RAT-ERR-WORKSHEET-NOT-FOUND");
    }

    [Fact]
    public async Task The_rating_operations_need_authentication_and_a_granted_role()
    {
        var body = JsonNode.Parse(JsonSerializer.Serialize(RateRequest(), SharedKernelJson.Options))!;
        var (anonymous, _) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rates/rate", body, roles: "", withKey: false);
        var (other, _) = await SendAsync(_client, HttpMethod.Post, "/api/rat/v1/rates/rate", body, roles: "Staff.Unrelated", withKey: false);

        anonymous.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_app_role_can_read_and_insert_rating_tables_but_never_delete()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        foreach (var table in new[] { "rate_table_version", "rating_artifact", "rate_activation", "worksheet", "worksheet_index" })
        {
            foreach (var (privilege, expected) in new[] { ("SELECT", true), ("INSERT", true), ("DELETE", false), ("TRUNCATE", false) })
            {
                await using var command = dataSource.CreateCommand($"SELECT has_table_privilege('app', 'rat.{table}', '{privilege}')");
                ((bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(expected, $"{privilege} on rat.{table}");
            }
        }
    }
}
