using System.Net;
using CoreIns.IntegrationTests.Policy;
using CoreIns.Modules.Market.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Market.State;

/// <summary>
/// Open question Q2 of SL5-MKT-STATE: with the current state switched to GR 0.1.0 (the pack before the treatment rows) a MOTOR-GR policy still quotes and
/// binds. New-business rating uses <c>TaxCalculator.calculate</c> (the IPT rate and the currency role are in 0.1.0); <c>treatment</c> is read only on the
/// servicing path. The state is switched by a test-only helper, never by an API.
/// </summary>
public sealed class NewBusinessUnderOlderPackTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Q2_a_MOTOR_GR_policy_quotes_and_binds_under_GR_0_1_0_without_any_NEW_BUSINESS_treatment_row()
    {
        await using var slice = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        await slice.SeedAsync();
        var states = StateHarness.States(database.AppConnectionString);
        var genesis = (await states.CurrentAsync(Ct)).Hash;

        var h1 = await StateHarness.SwitchAsync(database.AppConnectionString, states, "gr", "0.1.0");
        await StateHarness.CacheExpiryAsync();
        await using (var scope = slice.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IMarketConfigurationService>().CurrentHashAsync(Ct)).Hash.ShouldBe(h1.Hash, "the api sees the new state within the bound");
        }

        h1.ShouldNotBe(genesis);
        var party = await slice.CreatePartyAsync();
        var (jobId, _, _) = await slice.DraftAsync(party, DateTimeOffset.UtcNow.AddDays(2));

        var (quoted, quote) = await slice.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("state").ShouldBe("QUOTED");
        quote!["charges"]!.AsArray().Select(c => c!["chargeType"]!.GetValue<string>()).ShouldContain("GR-IPT");

        var (bound, bind) = await slice.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND");

        // The transaction is stamped with the 0.1.0 state it was quoted and bound under.
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand($"SELECT configuration_hash FROM pol.policy_transaction WHERE transaction_id = '{bind.Text("transactionId")}'");
        ((string)(await command.ExecuteScalarAsync(Ct))!).Trim().ShouldBe(h1.ToString());
    }
}
