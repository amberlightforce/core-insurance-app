using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Finance;
using CoreIns.IntegrationTests.Reinsurance.Recovery;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Reinsurance.Cession;

/// <summary>
/// SL6-RI-CESSION-DELTA: premium delta sets to quota-share cession (target minus booked, once per set), the exception rows for
/// anything the treaty list does not know, and the idempotent opening-aggregate tracker. Treaty and amounts are ILLUSTRATIVE.
/// </summary>
public sealed class CessionDeltaTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Home = "HOME_PROP";
    private const string Motor = "MTPL_XOL";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Treaties(IServiceCollection services) => services.Configure<CessionOptions>(o =>
    {
        o.Mode = CessionMode.Net;
        o.Treaties =
        [
            new() { TreatyId = "QS-HOME-2026", Kind = TreatyKinds.QuotaShare, ContractYear = 2026, SharePct = 30m, CoverageCodes = [Home], ValidFrom = new(2026, 1, 1), ValidTo = new(2027, 1, 1) },
            new() { TreatyId = "XOL-MTPL-2026", Kind = TreatyKinds.XolNoCession, ContractYear = 2026, CoverageCodes = [Motor], ValidFrom = new(2026, 1, 1), ValidTo = new(2027, 1, 1) },
        ];
    });

    private async Task<(RecoverySlice Slice, FinanceSlice Pub, IServiceProvider Svc)> NewAsync()
    {
        var slice = new RecoverySlice(database);
        var factory = slice.Registry.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(Treaties));
        return (slice, new FinanceSlice(factory), factory.Services);
    }

    private static async Task<EventEnvelope> DeltaAsync(FinanceSlice pub, Guid policy, Guid term, string coverage, string amount, EventSet? set = null, string category = "PREMIUM", bool correction = false)
    {
        set ??= new EventSet(Guid.CreateVersion7(), 1, 1); // every bound transaction is a set (D4), size 1 when ordinary
        var payload = FinanceSlice.Sample("pol", "ChargeDeltaEmitted");
        payload["termId"] = term.ToString();
        payload["opening"] = false; // the sample is an opening delta; these are ordinary deltas (opening ones are not ceded)
        payload["coverageCode"] = coverage;
        payload["chargeCategory"] = category;
        payload["chargeId"] = Guid.CreateVersion7().ToString();
        payload["transactionId"] = Guid.CreateVersion7().ToString();
        payload["netAmount"] = new JsonObject { ["amount"] = amount, ["currency"] = "EUR" };
        payload["validPeriod"] = new JsonObject { ["from"] = "2026-03-01", ["to"] = "2027-03-01" };
        payload["correctionOf"] = correction ? Guid.CreateVersion7().ToString() : null;
        payload["oosSetId"] = correction ? set?.SetId.ToString() : null;
        return await pub.PublishAsync(ChargeDeltaEmittedV1.Descriptor, "Policy", policy.ToString(), payload,
            BusinessKeys.Empty.With("policyId", policy.ToString()).With("chargeId", payload["chargeId"]!.GetValue<string>())
                .With("policyTermId", term.ToString()).With("transactionId", payload["transactionId"]!.GetValue<string>()), set);
    }

    private static async Task<bool> Deliver(IServiceProvider svc, string handlerName, EventEnvelope envelope, bool replay = false)
    {
        var registration = svc.GetRequiredService<EventHandlerRegistry>().Find(handlerName) ?? throw new InvalidOperationException(handlerName);
        return await new HandlerInvoker(svc.GetRequiredService<IServiceScopeFactory>())
            .InvokeAsync(registration, envelope, replay, svc.GetRequiredService<CoreIns.Platform.Time.IClock>(), Ct);
    }

    private static string Sql(Guid term, string what) => $"SELECT {what} FROM ri.cession WHERE term_id = '{term}'";

    [Fact]
    public async Task Quota_share_books_target_minus_booked_after_an_out_of_sequence_set_and_replay_changes_nothing()
    {
        var (slice, pub, svc) = await NewAsync();
        await using var _ = slice;
        var policy = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();

        // Ordinary bind: 1,000 written, 30% share, so 300 ceded.
        var bind = await DeltaAsync(pub, policy, term, Home, "1000.00");
        (await Deliver(svc, ChargeDeltaCessionHandler.Name, bind)).ShouldBeTrue();
        (await slice.ScalarAsync<decimal>(Sql(term, "COALESCE(SUM(delta), 0)"))).ShouldBe(300m);

        // OOS correction set of two (reverse 1,000, reapply 1,200): nothing booked on the partial set, then target 360 - booked 300 = 60.
        var set = new EventSet(Guid.CreateVersion7(), 2, 1);
        var reverse = await DeltaAsync(pub, policy, term, Home, "-1000.00", set, correction: true);
        var reapply = await DeltaAsync(pub, policy, term, Home, "1200.00", new EventSet(set.SetId, 2, 2), correction: true);
        (await Deliver(svc, ChargeDeltaCessionHandler.Name, reverse)).ShouldBeTrue();
        (await slice.ScalarAsync<long>(Sql(term, "COUNT(*)"))).ShouldBe(1);
        (await Deliver(svc, ChargeDeltaCessionHandler.Name, reapply)).ShouldBeTrue();

        (await slice.ScalarAsync<long>(Sql(term, "COUNT(*)"))).ShouldBe(2);
        (await slice.ScalarAsync<decimal>(Sql(term, "COALESCE(SUM(delta), 0)"))).ShouldBe(360m); // booked + difference == target
        (await slice.ScalarAsync<decimal>($"SELECT delta FROM ri.cession WHERE term_id = '{term}' AND set_key = '{set.SetId}'")).ShouldBe(60m);

        // Replay and redelivery: no change.
        await Deliver(svc, ChargeDeltaCessionHandler.Name, reapply, replay: true);
        await Deliver(svc, ChargeDeltaCessionHandler.Name, bind, replay: true);
        (await slice.ScalarAsync<long>(Sql(term, "COUNT(*)"))).ShouldBe(2);
        (await slice.ScalarAsync<decimal>(Sql(term, "SUM(delta)"))).ShouldBe(360m);
    }

    [Fact]
    public async Task Tax_lines_xol_and_unknown_coverage_are_not_ceded_and_the_unknown_is_an_exception_row()
    {
        var (slice, pub, svc) = await NewAsync();
        await using var _ = slice;
        var policy = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();

        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, Home, "120.00", category: "TAX"));
        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, Motor, "500.00"));
        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, "FIRE_UNKNOWN", "700.00"));

        (await slice.ScalarAsync<long>(Sql(term, "COUNT(*)"))).ShouldBe(0);
        (await slice.ScalarAsync<long>($"SELECT COUNT(*) FROM ri.cession_exception WHERE term_id = '{term}' AND code = 'CESSION_NO_TREATY'")).ShouldBe(1);
        (await slice.ScalarAsync<long>($"SELECT COUNT(*) FROM ri.premium_delta WHERE term_id = '{term}'")).ShouldBe(2); // the tax line (kept so a set counts as complete) and the XoL line are in the ledger, never ceded
    }

    [Fact]
    public async Task Opening_aggregate_twice_gives_one_tracker()
    {
        var (slice, _, svc) = await NewAsync();
        await using var _ = slice;
        var treaty = "XOL-MTPL-" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 2; i++)
        {
            await using var scope = svc.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service("mig-test");
            context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
            context.Jurisdiction = Jurisdiction.Parse("GR");
            context.ConfigurationHash = ConfigurationHash.Parse(new string('c', 64));
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            await using var transaction = await session.BeginTransactionAsync(Ct);
            var row = await scope.ServiceProvider.GetRequiredService<TrackerService>().OpeningAggregateAsync(treaty, 2026, 250000m, null, Ct);
            await transaction.CommitAsync(Ct);
            row.OpeningAggregate.ShouldBe(250000m);
        }

        (await slice.ScalarAsync<long>($"SELECT COUNT(*) FROM ri.tracker WHERE treaty_id = '{treaty}' AND contract_year = 2026")).ShouldBe(1);
    }

    [Fact]
    public void Gross_mode_capability_includes_every_charge_line_and_net_only_premium()
    {
        CessionEngine.InBase(CessionMode.Net, "PREMIUM").ShouldBeTrue();
        CessionEngine.InBase(CessionMode.Net, "TAX").ShouldBeFalse();
        CessionEngine.InBase(CessionMode.Gross, "TAX").ShouldBeTrue();
        CessionEngine.Target(30m, 1200m).ShouldBe(360m);
        CessionEngine.Target(33.3333m, 100.01m).ShouldBe(33.34m);
        CessionEngine.Difference(360m, 300m).ShouldBe(60m);
        CessionEngine.Difference(0m, 300m).ShouldBe(-300m);
    }

    private static async Task<Guid> InsertTreatyAsync(
        RecoverySlice slice, IServiceProvider svc, string treatyId, string coverage, string status = "APPROVED", string cessionBase = "NET", string currency = "EUR", bool superseded = false, decimal share = 25m)
    {
        var legalEntity = svc.GetRequiredService<ILegalEntityDirectory>().Resolve(LegalEntityCode.Parse("GR-TEST")).Value;
        return await slice.ScalarAsync<Guid>(
            "INSERT INTO ri.cession_treaty (treaty_version_id, legal_entity_id, treaty_id, kind, contract_year, share_pct, cession_base, currency, coverage_codes, valid_from, valid_to, status, source, known_from, known_to) " +
            $"VALUES (gen_random_uuid(), '{legalEntity}', '{treatyId}', 'QUOTA_SHARE', 2026, {share}, '{cessionBase}', '{currency}', ARRAY['{coverage}'], '2026-01-01', '2027-01-01', '{status}', 'REGISTRY', now() - interval '1 day', {(superseded ? "now() - interval '1 hour'" : "NULL")}) RETURNING treaty_version_id");
    }

    [Fact]
    public async Task Only_approved_treaties_in_force_in_the_registry_are_ceded_to_and_config_is_not_needed()
    {
        var slice = new RecoverySlice(database);
        await using var _ = slice;
        var pub = new FinanceSlice(slice.Registry.Factory);
        var svc = slice.Registry.Factory.Services;
        var policy = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();
        await InsertTreatyAsync(slice, svc, "QS-REG-OK", "REG_OK", share: 25m);
        await InsertTreatyAsync(slice, svc, "QS-REG-DRAFT", "REG_DRAFT", status: "DRAFT");
        await InsertTreatyAsync(slice, svc, "QS-REG-WITHDRAWN", "REG_WD", status: "WITHDRAWN");
        await InsertTreatyAsync(slice, svc, "QS-REG-OLD", "REG_OLD", superseded: true);

        foreach (var coverage in new[] { "REG_OK", "REG_DRAFT", "REG_WD", "REG_OLD" })
        {
            await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, coverage, "1000.00"));
        }

        (await slice.ScalarAsync<decimal>(Sql(term, "COALESCE(SUM(delta) FILTER (WHERE treaty_id = 'QS-REG-OK'), 0)"))).ShouldBe(250m);
        (await slice.ScalarAsync<long>(Sql(term, "COUNT(*)"))).ShouldBe(1);
        (await slice.ScalarAsync<long>($"SELECT COUNT(*) FROM ri.cession_exception WHERE term_id = '{term}' AND code = 'CESSION_NO_TREATY'")).ShouldBe(3); // draft, withdrawn and superseded are never in force
    }

    [Fact]
    public async Task A_gross_treaty_cedes_the_tax_line_and_records_mode_gross_while_a_net_treaty_does_not()
    {
        var slice = new RecoverySlice(database);
        await using var _ = slice;
        var pub = new FinanceSlice(slice.Registry.Factory);
        var svc = slice.Registry.Factory.Services;
        var policy = Guid.CreateVersion7();
        var term = Guid.CreateVersion7();
        await InsertTreatyAsync(slice, svc, "QS-GROSS", "GROSS_COV", cessionBase: "GROSS", share: 50m);
        await InsertTreatyAsync(slice, svc, "QS-NET", "NET_COV", share: 50m);

        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, "GROSS_COV", "1000.00"));
        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, "GROSS_COV", "120.00", category: "TAX"));
        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, "NET_COV", "1000.00"));
        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, policy, term, "NET_COV", "120.00", category: "TAX"));

        (await slice.ScalarAsync<decimal>(Sql(term, "SUM(delta) FILTER (WHERE treaty_id = 'QS-GROSS')"))).ShouldBe(560m); // 50% of 1,120
        (await slice.ScalarAsync<string>(Sql(term, "MIN(mode) FILTER (WHERE treaty_id = 'QS-GROSS')"))).ShouldBe("GROSS");
        (await slice.ScalarAsync<decimal>(Sql(term, "SUM(delta) FILTER (WHERE treaty_id = 'QS-NET')"))).ShouldBe(500m);
        (await slice.ScalarAsync<string>(Sql(term, "MIN(mode) FILTER (WHERE treaty_id = 'QS-NET')"))).ShouldBe("NET");
    }

    [Fact]
    public async Task A_treaty_in_another_currency_gets_an_exception_row_not_a_guessed_conversion()
    {
        var slice = new RecoverySlice(database);
        await using var _ = slice;
        var pub = new FinanceSlice(slice.Registry.Factory);
        var svc = slice.Registry.Factory.Services;
        var term = Guid.CreateVersion7();
        await InsertTreatyAsync(slice, svc, "QS-USD", "USD_COV", currency: "USD");

        await Deliver(svc, ChargeDeltaCessionHandler.Name, await DeltaAsync(pub, Guid.CreateVersion7(), term, "USD_COV", "1000.00"));

        (await slice.ScalarAsync<long>(Sql(term, "COUNT(*)"))).ShouldBe(0);
        (await slice.ScalarAsync<long>($"SELECT COUNT(*) FROM ri.cession_exception WHERE term_id = '{term}' AND code = 'CESSION_CURRENCY'")).ShouldBe(1);
    }
}
