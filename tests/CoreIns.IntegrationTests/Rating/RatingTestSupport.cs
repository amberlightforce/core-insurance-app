using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Market;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>Shared fixtures of the rating and underwriting tests. Every number and name here is synthetic test data.</summary>
internal static class RatingTestSupport
{
    public const string MotorProduct = "MOTOR-GR";
    public static readonly ConfigurationHash TestConfigurationHash = ConfigurationHash.Parse(new string('c', 64));

    /// <summary>
    /// An MKT configuration double (the generated fake) answering with the GR pack's key shapes: IPT 15% general / 20% fire (Settled,
    /// as the PRDs state, D-REG-04), the motor class, and the Auxiliary Fund 6% ceiling as Pending opinion / provisional. Test fixture only.
    /// </summary>
    public static FakeMarketConfigurationService MarketFake(bool withIpt = true, bool iptSettled = true, string? classJson = "\"general\"", bool classSettled = true)
    {
        var fake = new FakeMarketConfigurationService();
        fake.Setup<ConfigurationResolveResponse>("mkt.Configuration.resolve", _ =>
        {
            var values = new List<ConfigurationResolveResponse.ValueItem>();
            var missing = new List<string>();
            if (withIpt)
            {
                values.Add(Item("tax.ipt.rate.general", "\"0.15\"", settled: iptSettled));
                values.Add(Item("tax.ipt.rate.fire", "\"0.20\"", settled: iptSettled));
                if (classJson is not null)
                {
                    values.Add(Item("tax.ipt.motor_class", classJson, settled: classSettled, status: classSettled ? null : ConfigurationResolveResponse.ValueItem.LegalStatusValue.Verify));
                }
            }
            else
            {
                missing.AddRange(["tax.ipt.rate.general", "tax.ipt.rate.fire"]);
            }

            values.Add(Item("tax.levy.auxfund.ceiling_rate", "\"0.06\"", settled: false)); // present, but RAT must not turn it into a line

            return new ConfigurationResolveResponse
            {
                ConfigurationHash = TestConfigurationHash, Values = values, MissingKeys = missing, HasProvisionalValues = values.Any(v => v.Provisional),
            };
        });
        return fake;
    }

    private static ConfigurationResolveResponse.ValueItem Item(string key, string json, bool settled, ConfigurationResolveResponse.ValueItem.LegalStatusValue? status = null) => new()
    {
        Key = key,
        Value = JsonDocument.Parse(json).RootElement.Clone(),
        SourceLayer = "L3",
        ValueVersionId = Guid.Parse("0192f0c4-0000-7000-8000-0000000000aa"),
        Final = true,
        LegalStatus = status ?? (settled ? ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled : ConfigurationResolveResponse.ValueItem.LegalStatusValue.PendingOpinion),
        LegalSourceRef = "test fixture",
        Provisional = !settled,
    };

    /// <summary>The host with the MKT double in place of MKT (MKT is built in parallel).</summary>
    public static WebApplicationFactory<Program> WithMarket(ApiHostFactory factory, FakeMarketConfigurationService fake) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMarketConfigurationService>();
            services.AddSingleton<IMarketConfigurationService>(fake);
        }));

    public static AsyncServiceScope Scope(IServiceProvider services, string roles = "Staff.Underwriter")
    {
        var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("rating-test");
        context.Roles = [.. roles.Split(',')];
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = TestConfigurationHash;
        return scope;
    }

    /// <summary>A motor risk tree: a 2020 car worth 15,000, one driver born 1985, no claims, four coverages.</summary>
    public static JsonObject RiskTree(
        string firstRegistration = "2020", string? value = "15000.00", int cc = 1400, string usage = "PRIVATE", string birthDate = "1985-06-15",
        int claims = 0, params string[] coverages) => new()
        {
            ["vehicle"] = new JsonObject
            {
                ["elementId"] = "veh-1",
                ["registrationNumber"] = "YAA1234",
                ["firstRegistrationYear"] = int.Parse(firstRegistration, System.Globalization.CultureInfo.InvariantCulture),
                ["engineCapacityCc"] = cc,
                ["vehicleValue"] = value,
                ["usage"] = usage,
            },
            ["driver"] = new JsonObject { ["elementId"] = "drv-1", ["dateOfBirth"] = birthDate, ["claimsLast5Years"] = claims },
            ["coverages"] = new JsonArray((coverages.Length == 0 ? ["MTPL", "OWN-DAMAGE", "WINDSCREEN"] : coverages).Select(c => (JsonNode)c).ToArray()),
        };

    public static RateRateRequest RateRequest(
        JsonObject? risk = null, string mode = "FULL", string basisDate = "2026-11-01", string periodEnd = "2027-11-01", string currency = "EUR",
        string? ratingArtefactHash = null, Guid? quoteId = null)
    {
        var json = new JsonObject
        {
            ["envelope"] = new JsonObject
            {
                ["legalEntity"] = "GR-TEST",
                ["jurisdiction"] = "GR",
                ["productCode"] = MotorProduct,
                ["productVersion"] = "1.0",
                ["productArtefactHash"] = new string('b', 64),
                ["mode"] = mode,
                ["transactionType"] = "NEW_BUSINESS",
                ["ratingBasisDate"] = basisDate,
                ["currency"] = currency,
                ["lineage"] = quoteId is null ? null : new JsonObject { ["quoteId"] = quoteId.ToString() },
            },
            ["segments"] = new JsonArray(new JsonObject
            {
                ["segmentId"] = "seg-1",
                ["validPeriod"] = new JsonObject { ["from"] = basisDate, ["to"] = periodEnd },
                ["riskTree"] = risk ?? RiskTree(),
            }),
        };
        if (ratingArtefactHash is not null)
        {
            json["envelope"]!["ratingArtefactHash"] = ratingArtefactHash;
        }

        return JsonSerializer.Deserialize<RateRateRequest>(json.ToJsonString(), CoreIns.SharedKernel.Json.SharedKernelJson.Options)!;
    }
}
