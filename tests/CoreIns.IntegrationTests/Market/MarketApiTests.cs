using System.Net;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market;

/// <summary>
/// SL-MKT over HTTP and in process on a real PostgreSQL 17: the legal-entity registry (D-CON-33), mkt.Configuration.resolve,
/// currentHash and mkt.Rounding.apply (W1-MKT-01/04 subset), permissions and database privileges.
/// </summary>
public sealed class MarketApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static object ResolveBody(params string[] keys) => new
    {
        legalEntity = "GR-TEST",
        jurisdiction = "GR",
        keys,
        timeBasisDates = new Dictionary<string, string> { ["TAX_POINT_DATE"] = "2027-01-15" },
    };

    [Fact]
    public async Task REQ_MKT_151_D_CON_33_the_registry_maps_GR_TEST_to_the_legal_entity_id_and_replaces_the_stamp_placeholder()
    {
        using var scope = _factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<ILegalEntityDirectory>();

        directory.GetType().Name.ShouldBe("MarketLegalEntityDirectory");
        directory.Resolve(LegalEntityCode.Parse("GR-TEST")).Value.ShouldBe(Guid.Parse(ApiHostFactory.LegalEntityId));
        directory.All.Count.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => directory.Resolve(LegalEntityCode.Parse("CY-TEST")));

        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var command = source.CreateCommand("SELECT status || ',' || timezone || ',' || functional_currency || ',' || pack_id FROM mkt.legal_entity WHERE code = 'GR-TEST'");
        ((string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe("ACTIVE,Europe/Athens,EUR,gr");
    }

    [Fact]
    public async Task REQ_MKT_044_resolve_returns_motor_charge_values_with_status_source_hash_and_needs_no_idempotency_key()
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/mkt/v1/configuration/resolve",
            ResolveBody("tax.ipt.rate.general", "tax.levy.auxfund.ceiling_rate", "tax.levy.auxfund.ph_stamp_duty_rate"), withKey: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("configurationHash").Length.ShouldBe(64);
        body.Text("values.0.key").ShouldBe("tax.ipt.rate.general");
        body.Text("values.0.value").ShouldBe("0.15");
        body.Text("values.0.legalStatus").ShouldBe("Settled");
        body.Text("values.0.provisional").ShouldBe("false");
        body.Text("values.1.legalStatus").ShouldBe("PendingOpinion");
        body.Text("values.1.provisional").ShouldBe("true");
        body.Text("hasProvisionalValues").ShouldBe("true");
        body.Text("missingKeys.0").ShouldBe("tax.levy.auxfund.ph_stamp_duty_rate");
    }

    [Fact]
    public async Task REQ_MKT_047_current_hash_equals_the_hash_resolve_reports()
    {
        var (_, resolved) = await SendAsync(_client, HttpMethod.Post, "/api/mkt/v1/configuration/resolve", ResolveBody("tax.ipt.rate.general"), withKey: false);
        var (response, current) = await SendAsync(_client, HttpMethod.Get, "/api/mkt/v1/configuration/current-hash");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        current.Text("hash").ShouldBe(resolved.Text("configurationHash"));
    }

    [Fact]
    public async Task REQ_MKT_033_an_unknown_key_is_a_problem_with_the_mkt_code()
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/mkt/v1/configuration/resolve", ResolveBody("tax.made.up"), withKey: false);

        response.StatusCode.ShouldBe((HttpStatusCode)422);
        body.Text("code").ShouldBe("MKT-ERR-CFG-UNKNOWN-KEY");
    }

    [Fact]
    public async Task REQ_MKT_195_rounding_applies_over_http()
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/mkt/v1/rounding/apply", new
        {
            amount = new { amount = "2.345", currency = "EUR" },
            currency = "EUR",
            purpose = "charge.line",
            context = new { legalEntity = "GR-TEST" },
        }, withKey: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("amountAfterRounding.amount").ShouldBe("2.35");
        body.Text("residual.amount").ShouldBe("-0.005");
        body.Text("mode").ShouldBe("HALF_UP");
    }

    [Fact]
    public async Task Resolve_requires_authentication_and_a_granted_role()
    {
        var (anonymous, _) = await SendAsync(_client, HttpMethod.Post, "/api/mkt/v1/configuration/resolve", ResolveBody("tax.ipt.rate.general"), roles: "", withKey: false);
        var (other, _) = await SendAsync(_client, HttpMethod.Post, "/api/mkt/v1/configuration/resolve", ResolveBody("tax.ipt.rate.general"), roles: "Staff.Claims", withKey: false);

        anonymous.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_in_process_contracts_resolve_and_round_for_other_modules()
    {
        using var scope = _factory.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IMarketConfigurationService>();
        var rounding = scope.ServiceProvider.GetRequiredService<IMarketRoundingService>();
        var ct = TestContext.Current.CancellationToken;

        var resolved = await configuration.ResolveAsync(
            new ConfigurationResolveRequest
            {
                LegalEntity = "GR-TEST",
                Jurisdiction = "GR",
                Keys = ["tax.ipt.rate.general"],
                TimeBasisDates = new Dictionary<string, BusinessDate> { ["TAX_POINT_DATE"] = new(2027, 1, 15) },
            },
            cancellationToken: ct);
        var hash = await configuration.CurrentHashAsync(ct);
        var rounded = await rounding.ApplyAsync(
            new RoundingApplyRequest { Amount = Money.Of(1.005m, "EUR"), Purpose = "charge.line", Context = new RoundingApplyRequest.ContextDetail { LegalEntity = "GR-TEST" } }, ct);

        resolved.Values.Single().Value.GetString().ShouldBe("0.15");
        resolved.ConfigurationHash.Hash.ShouldBe(hash.Hash!.Value);
        rounded.AmountAfterRounding!.Value.Amount.ShouldBe(1.01m);
    }

    [Fact]
    public async Task The_app_role_can_read_and_write_but_never_delete_the_registry()
    {
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var ct = TestContext.Current.CancellationToken;

        async Task<bool> Can(string privilege)
        {
            await using var command = source.CreateCommand($"SELECT has_table_privilege('app', 'mkt.legal_entity', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }

        (await Can("SELECT")).ShouldBeTrue();
        (await Can("UPDATE")).ShouldBeTrue();
        (await Can("DELETE")).ShouldBeFalse();
    }
}
