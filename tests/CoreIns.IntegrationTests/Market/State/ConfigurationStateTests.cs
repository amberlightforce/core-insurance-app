using System.Text.Json.Nodes;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CoreIns.IntegrationTests.Market.State;

/// <summary>
/// SL5-MKT-STATE on a real PostgreSQL 17: persisted, append-only configuration states built from the registered pack versions; genesis;
/// resolution by any recorded hash (REQ-MKT-048, P-07); the 1 s bound on the current state (REQ-MKT-050); unit-of-work pinning (REQ-MKT-051);
/// and the treatment of the GR pack versions (D-SL5-07). Facts that write a state put the pack back on 0.2.0 before they end.
/// </summary>
public sealed class ConfigurationStateTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateOnly TaxPoint = new(2027, 1, 15);

    private static readonly LegalEntityInfo Entity = new(
        new LegalEntityId(Guid.Parse("0192f0c4-0000-7000-8000-000000000001")), LegalEntityCode.Parse("GR-TEST"), "GR", "gr", "EUR", "Europe/Athens", "ACTIVE", true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await StateHarness.States(database.AppConnectionString).EnsureGenesisAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (PersistedConfigurationStates States, ConfigurationEngine Engine, ManualClock Clock) Build(string environment = "Development", IEnumerable<IPackConfigurationSource>? sources = null)
    {
        var clock = new ManualClock(SystemClock.Instance.Now);
        var states = StateHarness.States(database.AppConnectionString, clock, sources);
        return (states, new ConfigurationEngine(states, new LegalEntityRegistry([Entity]), new FakeEnvironment(environment), clock), clock);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<ConfigurationHash> GenesisHashAsync() => ConfigurationHash.Parse((await ScalarAsync<string>("SELECT hash FROM mkt.config_state ORDER BY seq LIMIT 1")).Trim());

    private static ConfigurationResolveRequest Request(ConfigurationHash? hash = null, params string[] keys) => new()
    {
        LegalEntity = "GR-TEST",
        Jurisdiction = "GR",
        Keys = keys.Length == 0 ? ["tax.ipt.rate.general", "tax.levy.auxfund.ceiling_rate", "cur.transaction"] : keys,
        ConfigurationHash = hash,
        TimeBasisDates = new Dictionary<string, BusinessDate> { [TimeBases.TaxPointDate] = new(TaxPoint) },
    };

    private static TaxTreatmentRequest Cancellation() => new()
    {
        LegalEntityId = Entity.Id.Value,
        RiskJurisdiction = "GR",
        TaxPointDate = TaxPoint,
        ChargeType = "IPT",
        Category = TaxCategory.Tax,
        ChargeOrigin = ChargeOrigin.Pol,
        TransactionKind = TaxTransactionKind.Cancellation,
        CancellationSource = "Policyholder",
        PolicyholderType = PolicyholderType.Consumer,
        BusinessBasis = "ESTABLISHMENT",
    };

    private static TaxCalculationRequest Calculation() => new()
    {
        LegalEntityId = Entity.Id.Value,
        RiskJurisdiction = "GR",
        TaxPointDate = TaxPoint,
        PolicyholderType = PolicyholderType.Consumer,
        BusinessBasis = "ESTABLISHMENT",
        ChargeLines =
        [
            new TaxChargeLine
            {
                Element = "MTPL", ChargeCategory = ChargeLineCategory.Premium, ChargeType = "PREM-MTPL", ProductLine = "MOTOR", TaxClass = "general",
                PremiumAmount = new SpiMoney(300.00m, "EUR"), PeriodStart = TaxPoint, PeriodEnd = TaxPoint.AddDays(365), TransactionType = "NEW_BUSINESS",
            },
        ],
    };

    private static string VersionOf(ConfigurationCatalogue state) => state.Manifest.Packs.Single(p => p.PackId == "gr").Version;

    private static string Other(ConfigurationCatalogue state) => VersionOf(state) == "0.2.0" ? "0.1.0" : "0.2.0";

    [Fact]
    public async Task D_SL5_06_genesis_registers_every_shipped_version_by_digest_and_writes_one_genesis_state_with_an_activation_per_entity()
    {
        var shipped = StateHarness.States(database.AppConnectionString).Shipped;

        var versions = new List<(string Pack, string Version, string Digest, string Status)>();
        await using (var source = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        await using (var command = source.CreateCommand("SELECT pack_id, version, content_digest, status FROM mkt.pack_version ORDER BY pack_id, version"))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                versions.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2).Trim(), reader.GetString(3)));
            }
        }

        versions.Select(v => $"{v.Pack}@{v.Version}").ShouldBe(["core@1.0.0", "gr@0.1.0", "gr@0.2.0"]);
        versions.ShouldAllBe(v => v.Status == "Published");
        foreach (var v in versions)
        {
            v.Digest.ShouldBe(shipped.Single(s => s.PackId == v.Pack && s.Version == v.Version).Digest.ToString());
        }

        (await ScalarAsync<long>("SELECT count(*) FROM mkt.config_state WHERE cause = 'GENESIS'")).ShouldBe(1);
        var genesis = ConfigStateManifest.FromJson(JsonNode.Parse(await ScalarAsync<string>("SELECT manifest::text FROM mkt.config_state WHERE cause = 'GENESIS'")));
        genesis.Packs.Select(p => $"{p.PackId}@{p.Version}").ShouldBe(["gr@0.2.0"]);
        genesis.ParentHash.ShouldBeNull();
        genesis.CoreDigest.ShouldBe(CoreDefaults.Content.Digest);
        (await GenesisHashAsync()).ShouldBe(new ConfigurationCatalogue([new GrPackConfiguration()], Instant.FromUtc(2026, 10, 9)).Hash);

        (await ScalarAsync<long>("SELECT count(*) FROM mkt.pack_activation WHERE kind = 'ACTIVATE' AND status = 'ACTIVE' AND pack_id = 'gr' AND version = '0.2.0'"))
            .ShouldBe(await ScalarAsync<long>("SELECT count(*) FROM mkt.legal_entity WHERE pack_id = 'gr'"));
        (await ScalarAsync<string>("SELECT string_agg(DISTINCT resulting_hash, ',') FROM mkt.pack_activation WHERE requested_by = 'system:genesis'")).Trim().ShouldBe((await GenesisHashAsync()).ToString());
    }

    [Fact]
    public async Task D_SL5_06_a_second_start_changes_nothing()
    {
        var before = await ScalarAsync<string>("SELECT count(*) || '/' || max(seq) FROM mkt.config_state");
        var versionsBefore = await ScalarAsync<long>("SELECT count(*) FROM mkt.pack_version");
        var activationsBefore = await ScalarAsync<long>("SELECT count(*) FROM mkt.pack_activation");

        await StateHarness.States(database.AppConnectionString).EnsureGenesisAsync(Ct);
        await StateHarness.States(database.AppConnectionString).EnsureGenesisAsync(Ct);

        (await ScalarAsync<string>("SELECT count(*) || '/' || max(seq) FROM mkt.config_state")).ShouldBe(before);
        (await ScalarAsync<long>("SELECT count(*) FROM mkt.pack_version")).ShouldBe(versionsBefore);
        (await ScalarAsync<long>("SELECT count(*) FROM mkt.pack_activation")).ShouldBe(activationsBefore);
    }

    [Fact]
    public async Task REQ_MKT_048_a_state_read_back_from_the_database_has_the_hash_and_the_values_of_the_shipped_pack()
    {
        var (states, _, _) = Build();
        var genesis = (await states.ByHashAsync(await GenesisHashAsync(), Ct))!;
        var direct = new ConfigurationCatalogue([new GrPackConfiguration()], genesis.ActivatedAt);

        genesis.Hash.ShouldBe(direct.Hash);
        genesis.Entries.Count.ShouldBe(direct.Entries.Count);
        foreach (var expected in direct.Entries)
        {
            var actual = genesis.Entries.Single(e => e.Key == expected.Key && e.Node == expected.Node && e.ValidFrom == expected.ValidFrom);
            (actual.Value, actual.LegalStatus, actual.SourceRef, actual.MotorPath, actual.Note, actual.PackId, actual.PackVersion, actual.Type)
                .ShouldBe((expected.Value, expected.LegalStatus, expected.SourceRef, expected.MotorPath, expected.Note, expected.PackId, expected.PackVersion, expected.Type));
        }
    }

    [Fact]
    public async Task P_07_resolve_by_a_hash_is_byte_identical_before_and_after_a_later_state_is_written()
    {
        var (states, engine, clock) = Build();
        var h1 = (await states.CurrentAsync(Ct)).Hash;
        var before = System.Text.Json.JsonSerializer.Serialize(await engine.ResolveAsync(Request(h1), null, null, null, Ct));
        var target = Other(await states.CurrentAsync(Ct));

        var h2 = await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", target);
        try
        {
            clock.Advance(PersistedConfigurationStates.CacheLifetime + TimeSpan.FromMilliseconds(1));
            (await states.CurrentAsync(Ct)).Hash.ShouldBe(h2);
            var after = System.Text.Json.JsonSerializer.Serialize(await engine.ResolveAsync(Request(h1), null, null, null, Ct));
            var viaNewState = System.Text.Json.JsonSerializer.Serialize(await engine.ResolveAsync(Request(h2), null, null, null, Ct));

            after.ShouldBe(before);
            before.ShouldContain(h1.ToString());
            viaNewState.ShouldContain(h2.ToString());
        }
        finally
        {
            await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.2.0", StateCauses.PackActivation);
        }
    }

    [Fact]
    public async Task REQ_MKT_048_a_hash_that_was_never_recorded_is_CFG_HASH_UNKNOWN_and_knownAt_before_the_first_state_is_NOT_AVAILABLE()
    {
        var (states, engine, _) = Build();
        var genesisHash = await GenesisHashAsync();
        var genesis = (await states.ByHashAsync(genesisHash, Ct))!;

        var unknown = await Should.ThrowAsync<DomainException>(async () =>
            await engine.ResolveAsync(Request(new ConfigurationHash(Sha256Hash.ComputeUtf8("never recorded"))), null, null, null, Ct));
        var early = await Should.ThrowAsync<DomainException>(async () =>
            await engine.ResolveAsync(Request(), null, genesis.ActivatedAt.Minus(TimeSpan.FromSeconds(1)), null, Ct));

        unknown.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-HASH-UNKNOWN");
        early.Error.Code.ToString().ShouldBe("MKT-ERR-NOT-AVAILABLE");
        (await states.ByHashAsync(new ConfigurationHash(Sha256Hash.ComputeUtf8("never recorded")), Ct)).ShouldBeNull();
        (await states.AtAsync(genesis.ActivatedAt.Minus(TimeSpan.FromTicks(10)), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task REQ_MKT_048_knownAt_resolves_the_state_that_was_current_at_that_instant()
    {
        var (states, engine, _) = Build();
        var genesis = (await states.ByHashAsync(await GenesisHashAsync(), Ct))!;
        await Task.Delay(20, Ct);
        var h2 = await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.1.0");
        var second = (await states.ByHashAsync(h2, Ct))!;
        await Task.Delay(20, Ct);
        var h3 = await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.2.0", StateCauses.PackActivation);
        var third = (await states.ByHashAsync(h3, Ct))!;

        (await states.AtAsync(genesis.ActivatedAt, Ct))!.Hash.ShouldBe(genesis.Hash);
        (await states.AtAsync(second.ActivatedAt.Minus(TimeSpan.FromTicks(10)), Ct))!.Hash.ShouldNotBe(h2);
        (await states.AtAsync(second.ActivatedAt, Ct))!.Hash.ShouldBe(h2);
        (await states.AtAsync(third.ActivatedAt.Minus(TimeSpan.FromTicks(10)), Ct))!.Hash.ShouldBe(h2);
        (await states.AtAsync(third.ActivatedAt, Ct))!.Hash.ShouldBe(h3);

        // The response is stamped with the state it was resolved against, and an explicit hash beats knownAt.
        (await engine.ResolveAsync(Request(), null, second.ActivatedAt, null, Ct)).ConfigurationHash.ShouldBe(h2);
        (await engine.ResolveAsync(Request(genesis.Hash), null, second.ActivatedAt, null, Ct)).ConfigurationHash.ShouldBe(genesis.Hash);
        third.Manifest.ParentHash.ShouldBe(h2);
        second.Manifest.ParentHash.ShouldNotBeNull();
        third.Hash.ShouldNotBe(genesis.Hash, "re-activating 0.2.0 is a new state, not the genesis again");
    }

    [Fact]
    public async Task REQ_MKT_050_the_current_state_is_served_from_memory_for_less_than_one_second_and_then_re_read()
    {
        var (states, _, clock) = Build();
        var first = await states.CurrentAsync(Ct);
        var h2 = await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", Other(first), clock: clock);
        try
        {
            clock.Advance(TimeSpan.FromMilliseconds(999));
            (await states.CurrentAsync(Ct)).Hash.ShouldBe(first.Hash, "inside the bound the old state may still be served");

            clock.Advance(TimeSpan.FromMilliseconds(1));
            (await states.CurrentAsync(Ct)).Hash.ShouldBe(h2, "at one second the cache has expired: nothing sees the old hash");

            clock.Set(clock.Now.Minus(TimeSpan.FromHours(1)));
            (await states.CurrentAsync(Ct)).Hash.ShouldBe(h2, "a clock that moved backwards never keeps a cache");
        }
        finally
        {
            await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.2.0", StateCauses.PackActivation);
        }
    }

    [Fact]
    public async Task REQ_MKT_050_two_processes_both_see_an_activation_within_a_second_of_real_time()
    {
        // Two real processes: each has its own cache and the real clock.
        var api = StateHarness.States(database.AppConnectionString);
        var worker = StateHarness.States(database.AppConnectionString);
        var first = await api.CurrentAsync(Ct);
        (await worker.CurrentAsync(Ct)).Hash.ShouldBe(first.Hash);

        var h2 = await StateHarness.SwitchAsync(database.AppConnectionString, api, "gr", Other(first));
        try
        {
            await StateHarness.CacheExpiryAsync();
            (await api.CurrentAsync(Ct)).Hash.ShouldBe(h2);
            (await worker.CurrentAsync(Ct)).Hash.ShouldBe(h2);
        }
        finally
        {
            await StateHarness.SwitchAsync(database.AppConnectionString, api, "gr", "0.2.0", StateCauses.PackActivation);
        }
    }

    [Fact]
    public async Task D_SL5_11_treatment_of_a_policyholder_cancellation_follows_the_state_GR_0_2_0_has_the_rule_and_0_1_0_fails_closed()
    {
        var (states, engine, clock) = Build();
        var genesis = await GenesisHashAsync();
        var h1 = await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.1.0");
        try
        {
            var calculator = new MarketTaxCalculator(engine);

            var under02 = await calculator.TreatmentAsync(Cancellation() with { ConfigurationHash = genesis.ToString() }, Ct);
            var under01 = await Should.ThrowAsync<SpiException>(async () => await calculator.TreatmentAsync(Cancellation() with { ConfigurationHash = h1.ToString() }, Ct));

            under02.RuleId.ShouldBe("GR-TRT-IPT-CANCEL-POLICYHOLDER");
            under02.Action.ShouldBe(TreatmentAction.KeepNotReduced);
            under02.ConfigurationHash.ShouldBe(genesis.ToString());
            under01.Error.Category.ShouldBe(SpiErrorCategory.RuleMissing);
            under01.Error.Code.ShouldBe("RULE_MISSING");

            // Without a hash the current state decides: 0.1.0 now.
            clock.Advance(TimeSpan.FromSeconds(2));
            (await Should.ThrowAsync<SpiException>(async () => await calculator.TreatmentAsync(Cancellation(), Ct))).Error.Code.ShouldBe("RULE_MISSING");

            // Calculate prices IPT from the rate row of 0.1.0 too: new business needs no treatment row, only the servicing path does.
            var calculated = await calculator.CalculateAsync(Calculation() with { ConfigurationHash = h1.ToString() }, Ct);
            calculated.Lines.Single().Amount.Amount.ShouldBe(45.00m);
            calculated.Lines.Single().ConfigurationHash.ShouldBe(h1.ToString());
        }
        finally
        {
            await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.2.0", StateCauses.PackActivation);
        }
    }

    [Fact]
    public async Task REQ_MKT_051_a_command_that_captured_H1_completes_under_H1_when_a_state_is_written_in_the_middle()
    {
        var (states, engine, clock) = Build();
        var h1 = (await states.CurrentAsync(Ct)).Hash;
        var pinned = new RequestContext { ConfigurationHash = h1 };
        var service = new MarketConfigurationService(engine, pinned);
        var calculator = new MarketTaxCalculator(engine, pinned);
        var h2 = await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", Other(await states.CurrentAsync(Ct)));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));

            (await service.CurrentHashAsync(Ct)).Hash.ShouldBe(h2.Hash, "currentHash is the truth, whoever asks");
            (await service.ResolveAsync(Request(), cancellationToken: Ct)).ConfigurationHash.ShouldBe(h1, "resolve inside the command stays on H1");
            (await calculator.CalculateAsync(Calculation(), Ct)).Lines.Single().ConfigurationHash.ShouldBe(h1.ToString());
            (await new MarketRoundingService(engine, pinned).ApplyAsync(
                new RoundingApplyRequest { Amount = Money.Of(2.345m, "EUR"), Purpose = "tax.line", Context = new RoundingApplyRequest.ContextDetail { LegalEntity = "GR-TEST" } }, Ct))
                .RuleKey.ShouldNotBeNull();

            // A command that starts now pins H2; an explicit hash beats the pin.
            var fresh = new RequestContext { ConfigurationHash = (await states.CurrentAsync(Ct)).Hash };
            (await new MarketConfigurationService(engine, fresh).ResolveAsync(Request(), cancellationToken: Ct)).ConfigurationHash.ShouldBe(h2);
            (await service.ResolveAsync(Request(h2), cancellationToken: Ct)).ConfigurationHash.ShouldBe(h2);
        }
        finally
        {
            await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.2.0", StateCauses.PackActivation);
        }
    }

    [Fact]
    public async Task A_pin_that_names_no_recorded_state_falls_back_to_the_current_state_and_the_response_says_which_hash_it_used()
    {
        var (states, engine, _) = Build();
        var unrecorded = new ConfigurationHash(Sha256Hash.ComputeUtf8("pre-slice-5 hash"));
        var service = new MarketConfigurationService(engine, new RequestContext { ConfigurationHash = unrecorded });

        var resolved = await service.ResolveAsync(Request(), cancellationToken: Ct);

        resolved.ConfigurationHash.ShouldBe((await states.CurrentAsync(Ct)).Hash);
        resolved.ConfigurationHash.ShouldNotBe(unrecorded);

        // An explicit hash is never replaced: asking for a state that was never recorded is an error.
        var refused = await Should.ThrowAsync<DomainException>(async () => await service.ResolveAsync(Request(unrecorded), cancellationToken: Ct));
        refused.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-HASH-UNKNOWN");
    }

    [Fact]
    public async Task PITFALLS_36_the_production_gate_over_a_persisted_state_still_serves_only_Settled_values()
    {
        var (states, engine, _) = Build("Production");
        var calculator = new MarketTaxCalculator(engine);

        // The IPT rate is Settled; the Auxiliary Fund ceiling is PendingOpinion and the treatment rows are PendingOpinion.
        (await engine.ResolveAsync(Request(null, "tax.ipt.rate.general"), null, null, null, Ct)).Values.Single().Provisional.ShouldBeFalse();
        var gate = await Should.ThrowAsync<DomainException>(async () => await engine.ResolveAsync(Request(null, "tax.levy.auxfund.ceiling_rate"), null, null, null, Ct));
        var treatment = await Should.ThrowAsync<DomainException>(async () => await calculator.TreatmentAsync(Cancellation(), Ct));

        gate.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-NOT-SETTLED");
        treatment.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-NOT-SETTLED");
        (await states.CurrentAsync(Ct)).Entries.Where(e => !e.IsSettled).ShouldNotBeEmpty("the stored statuses survived the round trip");
    }

    [Fact]
    public async Task D_SL5_07_a_shipped_version_whose_content_differs_from_its_recorded_digest_stops_the_host()
    {
        var changed = new TamperedGr();
        var (states, _, _) = Build(sources: [changed]);

        var error = await Should.ThrowAsync<PackVersionChangedException>(async () => await states.EnsureGenesisAsync(Ct));
        var startup = new ConfigurationStatesStartup(states, NullLogger<ConfigurationStatesStartup>.Instance);
        await Should.ThrowAsync<PackVersionChangedException>(async () => await startup.StartAsync(Ct));

        error.Message.ShouldContain("gr@0.2.0");
        error.Message.ShouldContain("never changes");
        (await ScalarAsync<long>("SELECT count(*) FROM mkt.pack_version WHERE pack_id = 'gr'")).ShouldBe(2, "nothing was registered or changed by the refused start");
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_at_startup_only_logs_and_the_first_read_retries()
    {
        var unreachable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=coreins;Username=app;Password=none;Timeout=2");
        var states = new PersistedConfigurationStates(unreachable, StateHarness.Gr, SystemClock.Instance, NullLogger<PersistedConfigurationStates>.Instance);
        var startup = new ConfigurationStatesStartup(states, NullLogger<ConfigurationStatesStartup>.Instance);

        await Should.NotThrowAsync(async () => await startup.StartAsync(Ct));
        await Should.ThrowAsync<NpgsqlException>(async () => await states.CurrentAsync(Ct));
        states.ExpectedGenesisHash.ShouldBe(await GenesisHashAsync());
    }

    [Fact]
    public async Task PITFALLS_8_17_the_app_role_can_read_and_insert_states_and_versions_but_never_update_delete_or_truncate_them()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        async Task<bool> Can(string table, string privilege)
        {
            await using var command = app.CreateCommand($"SELECT has_table_privilege('app', 'mkt.{table}', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(Ct))!;
        }

        foreach (var table in new[] { "pack_version", "config_state" })
        {
            (await Can(table, "SELECT")).ShouldBeTrue(table);
            (await Can(table, "INSERT")).ShouldBeTrue(table);
            (await Can(table, "UPDATE")).ShouldBeFalse(table);
            (await Can(table, "DELETE")).ShouldBeFalse(table);
            (await Can(table, "TRUNCATE")).ShouldBeFalse(table);
        }

        (await Can("pack_activation", "UPDATE")).ShouldBeTrue();
        (await Can("pack_activation", "DELETE")).ShouldBeFalse();

        // Connected as the app role, every rewrite is refused by the privilege check.
        foreach (var sql in new[]
                 {
                     "UPDATE mkt.config_state SET cause = 'PACK_ROLLBACK'",
                     "UPDATE mkt.config_state SET manifest = '{}'::jsonb",
                     "DELETE FROM mkt.config_state",
                     "TRUNCATE mkt.config_state",
                     "UPDATE mkt.pack_version SET \"values\" = '[]'::jsonb",
                     "UPDATE mkt.pack_version SET content_digest = repeat('0', 64)",
                     "DELETE FROM mkt.pack_version",
                     "TRUNCATE mkt.pack_version",
                 })
        {
            await using var command = app.CreateCommand(sql);
            var refused = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Ct));
            refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
        }
    }

    [Fact]
    public async Task PITFALLS_8_17_the_triggers_refuse_a_rewrite_even_to_the_owner()
    {
        foreach (var sql in new[]
                 {
                     "UPDATE mkt.config_state SET cause = 'PACK_ROLLBACK'",
                     "DELETE FROM mkt.config_state",
                     "TRUNCATE mkt.config_state",
                     "UPDATE mkt.pack_version SET status = 'Removed'",
                     "DELETE FROM mkt.pack_version",
                     "TRUNCATE mkt.pack_version",
                 })
        {
            var refused = await Should.ThrowAsync<PostgresException>(async () => await database.ExecuteAsSuperuserAsync(sql, Ct));

            // A TRUNCATE of a table another table references is refused by PostgreSQL itself (0A000) before the trigger would fire.
            if (sql.StartsWith("TRUNCATE", StringComparison.Ordinal))
            {
                refused.SqlState.ShouldBeOneOf(PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.FeatureNotSupported);
            }
            else
            {
                refused.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation, sql);
            }
        }
    }

    [Fact]
    public async Task PITFALLS_40_a_state_is_written_only_under_the_state_lock_of_the_same_transaction_on_top_of_the_newest_state()
    {
        var (states, _, _) = Build();
        var newest = (await states.CurrentAsync(Ct)).Manifest;
        var forged = new ConfigStateManifest(newest.Packs, newest.CoreDigest, newest.Hash, StateCauses.PackRollback);
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);

        // No lock: refused, however well formed the row is.
        await using (var connection = await app.OpenConnectionAsync(Ct))
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            var noLock = await Should.ThrowAsync<PostgresException>(async () =>
                await ConfigStateWriter.AppendAsync(connection, transaction, forged, SystemClock.Instance.Now, null, Ct));
            noLock.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
            noLock.MessageText.ShouldContain("advisory lock");
        }

        // A lock committed or held by another transaction does not count: only this transaction's own lock does.
        await using (var holder = await app.OpenConnectionAsync(Ct))
        await using (var holderTx = await holder.BeginTransactionAsync(Ct))
        await using (var other = await app.OpenConnectionAsync(Ct))
        await using (var otherTx = await other.BeginTransactionAsync(Ct))
        {
            await ConfigStateWriter.LockAsync(holder, holderTx, Ct);
            var foreignLock = await Should.ThrowAsync<PostgresException>(async () =>
                await ConfigStateWriter.AppendAsync(other, otherTx, forged, SystemClock.Instance.Now, null, Ct));
            foreignLock.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        }

        // Under the lock: a wrong parent (a fork), a missing parent (a second genesis) and a backwards instant are each refused.
        await using (var connection = await app.OpenConnectionAsync(Ct))
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await ConfigStateWriter.LockAsync(connection, transaction, Ct);
            var fork = new ConfigStateManifest(newest.Packs, newest.CoreDigest, await GenesisParentOtherThan(newest.Hash), StateCauses.PackRollback);
            if (fork.ParentHash != newest.Hash)
            {
                (await Should.ThrowAsync<PostgresException>(async () =>
                    await ConfigStateWriter.AppendAsync(connection, transaction, fork, SystemClock.Instance.Now, null, Ct))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
            }
        }

        await using (var connection = await app.OpenConnectionAsync(Ct))
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await ConfigStateWriter.LockAsync(connection, transaction, Ct);
            var second = new ConfigStateManifest(newest.Packs, newest.CoreDigest, null, StateCauses.Genesis);
            (await Should.ThrowAsync<PostgresException>(async () =>
                await ConfigStateWriter.AppendAsync(connection, transaction, second, SystemClock.Instance.Now, null, Ct))).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        }

        await using (var connection = await app.OpenConnectionAsync(Ct))
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await ConfigStateWriter.LockAsync(connection, transaction, Ct);
            var backwards = await Should.ThrowAsync<PostgresException>(async () =>
                await ConfigStateWriter.AppendAsync(connection, transaction, forged, Instant.FromUtc(2020, 1, 1), null, Ct));
            backwards.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
            backwards.MessageText.ShouldContain("backwards");
        }

        (await states.CurrentAsync(Ct)).Hash.ShouldBe(newest.Hash);
    }

    [Fact]
    public async Task PITFALLS_47_a_pack_activation_moves_only_forward_is_decided_by_someone_else_and_is_born_active_only_as_the_genesis()
    {
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var entity = Entity.Id.Value;
        var genesis = await GenesisHashAsync();
        var id = Guid.NewGuid();
        var approval = Guid.NewGuid();

        async Task<PostgresException> Refused(string sql)
        {
            await using var command = app.CreateCommand(sql);
            var error = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Ct));
            error.SqlState.ShouldBeOneOf(PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.InsufficientPrivilege);
            return error;
        }

        async Task Run(string sql)
        {
            await using var command = app.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(Ct);
        }

        string Insert(Guid rowId, string status, string requestedBy = "user:maker", string? decidedBy = null, string kind = "ROLLBACK") =>
            $"INSERT INTO mkt.pack_activation (id, legal_entity_id, pack_id, version, kind, status, requested_by, decided_by, activated_at, resulting_hash, created_at) "
            + $"VALUES ('{rowId}', '{entity}', 'gr', '0.1.0', '{kind}', '{status}', '{requestedBy}', {(decidedBy is null ? "NULL" : "'" + decidedBy + "'")}, "
            + (status == "ACTIVE" ? $"now(), '{genesis}', now())" : "NULL, NULL, now())");

        // Born ACTIVE, approved or decided: refused (only the genesis row is born active, and only for the genesis state).
        await Refused(Insert(Guid.NewGuid(), "ACTIVE", decidedBy: "user:checker"));
        await Refused(Insert(Guid.NewGuid(), "ACTIVE", requestedBy: "system:genesis", decidedBy: "system:genesis", kind: "ROLLBACK"));
        await Refused(Insert(Guid.NewGuid(), "APPROVED"));
        await Refused(Insert(Guid.NewGuid(), "PENDING_APPROVAL", decidedBy: "user:checker"));

        await Run(Insert(id, "PENDING_APPROVAL"));

        // Approved or activated by the requester, by nobody, or without an approval request: refused.
        await Refused($"UPDATE mkt.pack_activation SET status = 'ACTIVE', decided_by = 'user:maker', approval_request_id = '{approval}', resulting_hash = '{genesis}', activated_at = now() WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET status = 'APPROVED' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET status = 'APPROVED', decided_by = 'user:checker' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET status = 'ACTIVE', decided_by = 'user:checker', approval_request_id = '{approval}' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET status = 'SUPERSEDED' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET requested_by = 'user:checker' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET version = '0.2.0' WHERE id = '{id}'");
        await Refused($"DELETE FROM mkt.pack_activation WHERE id = '{id}'");

        // The proper path: decided by someone else, naming the approval, becoming active with its state and instant.
        await Run($"UPDATE mkt.pack_activation SET status = 'ACTIVE', decided_by = 'user:checker', approval_request_id = '{approval}', resulting_hash = '{genesis}', activated_at = now() WHERE id = '{id}'");

        // Frozen once written; no way back; superseded is final.
        await Refused($"UPDATE mkt.pack_activation SET decided_by = 'user:other' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET approval_request_id = '{Guid.NewGuid()}' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET status = 'PENDING_APPROVAL' WHERE id = '{id}'");
        await Run($"UPDATE mkt.pack_activation SET status = 'SUPERSEDED' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET status = 'ACTIVE' WHERE id = '{id}'");
        await Refused($"UPDATE mkt.pack_activation SET decided_by = 'user:later' WHERE id = '{id}'");

        var truncate = await Should.ThrowAsync<PostgresException>(async () => await database.ExecuteAsSuperuserAsync("TRUNCATE mkt.pack_activation", Ct));
        truncate.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        var owner = await Should.ThrowAsync<PostgresException>(async () => await database.ExecuteAsSuperuserAsync($"DELETE FROM mkt.pack_activation WHERE id = '{id}'", Ct));
        owner.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    private async Task<ConfigurationHash?> GenesisParentOtherThan(ConfigurationHash newest)
    {
        var genesis = await GenesisHashAsync();
        return genesis == newest ? new ConfigurationHash(Sha256Hash.ComputeUtf8("not the newest")) : genesis;
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>The GR pack with one value of 0.2.0 changed: a release that rewrote a shipped version.</summary>
    private sealed class TamperedGr : IVersionedPackConfigurationSource
    {
        private readonly GrPackConfiguration _inner = new();

        public string PackId => _inner.PackId;

        public string PackVersion => _inner.PackVersion;

        public string Country => _inner.Country;

        public IReadOnlyList<PackConfigValue> Values => Versions[^1].Values;

        public IReadOnlyList<PackVersionData> Versions =>
        [
            _inner.Versions[0],
            new(_inner.Versions[1].Version, [.. _inner.Versions[1].Values.Select(v => v.Key == "tax.ipt.rate.general" ? v with { Value = "0.16" } : v)]),
        ];
    }
}
