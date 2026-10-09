using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Product;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Rating.Fallback;

/// <summary>Real owner approval, publication and rating: no test-produced fallback event.</summary>
public sealed class RatingOwnerFallbackTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Owner_approved_product_fallback_is_ratable_under_its_exact_source_tariff()
    {
        await using var baseFactory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?>
        {
            [ClockConfiguration.ModeKey] = "Shiftable",
        });
        await using var factory = WithMarket(baseFactory, MarketFake());
        using var client = factory.CreateClient();
        ((ShiftableClock)factory.Services.GetRequiredService<IClock>()).Freeze(Instant.Parse("2026-11-01T09:00:00Z"));

        async Task<RateRateResponse> Rate(string version, string? pin = null)
        {
            var request = RateRequest(mode: pin is null ? "FULL" : "ENDORSEMENT");
            request = request with { Envelope = request.Envelope with
            {
                ProductVersion = ProductVersionNumber.Parse(version),
                PinnedRatingArtefactHash = pin is null ? null : Sha256Hash.Parse(pin),
            } };
            await using var scope = Scope(factory.Services);
            return await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request, TestContext.Current.CancellationToken);
        }

        async Task<JsonNode> OwnerPost(string path, string actor, string role, object body)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            message.Headers.Add(TestAuthHandler.UserHeader, actor);
            message.Headers.Add(TestAuthHandler.RolesHeader, role);
            message.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
            using var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            response.StatusCode.ShouldBe(HttpStatusCode.OK, json.ToJsonString());
            return json;
        }

        var original = await Rate("1.0");
        var defective = await Rate("1.1");
        var (import10, body10) = await ProductApi.ImportAsync(client, JsonNode.Parse(ProductSeeds.MotorPrivateCarJson())!.AsObject());
        import10.StatusCode.ShouldBe(HttpStatusCode.OK, body10?.ToJsonString());
        var (import11, body11) = await ProductApi.ImportAsync(client, JsonNode.Parse(ProductSeeds.MotorPrivateCar11Json())!.AsObject());
        import11.StatusCode.ShouldBe(HttpStatusCode.OK, body11?.ToJsonString());
        var request = await OwnerPost("/api/pfc/v1/product-versions/fallback", "dev:releasemgr", "Platform.ReleaseManager",
            new { productCode = MotorProduct, defectiveVersion = "1.1", reason = "Synthetic defect: restore the previous locked content." });
        var decision = await OwnerPost("/api/pfc/v1/product-versions/decide-fallback", "dev:designauth", "Platform.DesignAuthority",
            new { fallbackId = request["fallbackId"]!.GetValue<string>(), decision = "APPROVE", reason = "Reviewed the exact frozen source and defective versions." });
        decision["fallback"]!["status"]!.GetValue<string>().ShouldBe("APPLIED");
        await factory.Services.GetRequiredService<OutboxProcessor>().DrainAsync(TestContext.Current.CancellationToken);

        var fallback = await Rate("1.2");
        fallback.RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
        fallback.Rates.Select(x => x.AnnualRate).ShouldBe(original.Rates.Select(x => x.AnnualRate));
        (await Rate("1.2", fallback.RatingArtefactHash!.Value.Value)).RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
        (await Rate("1.0")).RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
        (await Rate("1.1")).RatingArtefactHash.ShouldBe(defective.RatingArtefactHash);
    }
}
