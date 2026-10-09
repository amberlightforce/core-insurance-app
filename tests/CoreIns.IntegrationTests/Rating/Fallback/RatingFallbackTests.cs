using System.Text.Json.Nodes;
using CoreIns.Modules.Product.Contracts.Events;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CoreIns.SharedKernel;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Rating.Fallback;

/// <summary>The published PFC fallback crosses the real outbox and preserves RAT's immutable source tariff.</summary>
public sealed class RatingFallbackTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _base = null!;
    private WebApplicationFactory<Program> _factory = null!;
    public ValueTask InitializeAsync()
    {
        _base = new ApiHostFactory(database.AppConnectionString);
        _factory = WithMarket(_base, MarketFake());
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync() { await _factory.DisposeAsync(); await _base.DisposeAsync(); }

    private async Task<RateRateResponse> RateAsync(string? version, string? pin = null, string basis = "2026-11-01")
    {
        var request = RateRequest(mode: pin is null ? "FULL" : "ENDORSEMENT", basisDate: basis,
            periodEnd: pin is null ? DateOnly.Parse(basis).AddYears(1).ToString("yyyy-MM-dd") : "2027-02-01");
        request = request with { Envelope = request.Envelope with
        {
            ProductVersion = version is null ? null : ProductVersionNumber.Parse(version),
            PinnedRatingArtefactHash = pin is null ? null : Sha256Hash.Parse(pin),
        } };
        await using var scope = Scope(_factory.Services);
        return await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<EventEnvelope> PublishAsync(string version, string? source, string product = MotorProduct)
    {
        var samples = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root,
            "tests", "CoreIns.Contracts.Tests", "Generated", "Samples", "events", "pfc.json")))!.AsArray();
        var payload = samples.Select(x => x!.AsObject()).First(x => x["eventType"]!.GetValue<string>() == "ProductVersionPublished")
            ["payload"]!.DeepClone().AsObject();
        payload["productCode"] = product;
        payload["version"] = version;
        payload["fallbackOf"] = source;
        payload["newBusinessWindow"] = new JsonObject { ["from"] = "2026-10-31T22:00:00Z", ["to"] = null };
        await using var scope = Scope(_factory.Services);
        scope.ServiceProvider.GetRequiredService<RequestContext>().Actor = ActorRef.Service("rating-fallback-test-producer");
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var id = Guid.CreateVersion7().ToString();
        var envelope = scope.ServiceProvider.GetRequiredService<IEventPublisher>().Publish(new OutgoingEvent(
            EventDescriptor.From(ProductVersionPublishedV1.Descriptor), "Product", id, payload, BusinessKeys.Empty.With("productId", id)));
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        await _factory.Services.GetRequiredService<OutboxProcessor>().DrainAsync(TestContext.Current.CancellationToken);
        return envelope;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Fallback_quotes_and_pinned_servicing_use_exact_source_hash_and_replay_is_idempotent()
    {
        var original = await RateAsync("1.0");
        var defective = await RateAsync("1.1");
        var envelope = await PublishAsync("1.2", "1.0");
        var fallback = await RateAsync("1.2");
        fallback.RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
        fallback.Rates.Select(x => x.AnnualRate).ShouldBe(original.Rates.Select(x => x.AnnualRate));
        var pin = fallback.RatingArtefactHash!.Value.Value;
        (await RateAsync("1.2", pin)).RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
        (await ScalarAsync<string>($"SELECT body->'header'->>'ratingArtefactHash' FROM rat.worksheet WHERE worksheet_id='{fallback.WorksheetId.Value}'"))
            .ShouldBe(pin);
        (await ScalarAsync<string>($"SELECT body->'header'->>'productVersion' FROM rat.worksheet WHERE worksheet_id='{fallback.WorksheetId.Value}'"))
            .ShouldBe("1.2");
        (await ScalarAsync<string>($"SELECT payload->>'productVersion' FROM plt.outbox_message WHERE event_type='RatingCalculated' AND payload->>'worksheetId'='{fallback.WorksheetId.Value}'"))
            .ShouldBe("1.2");
        (await RateAsync(null)).RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
        await using (var scope = Scope(_factory.Services))
        {
            var resolution = await scope.ServiceProvider.GetRequiredService<IRatingRatingArtifactService>().ResolveAsync(
                new RatingArtifactResolveRequest { ProductCode = MotorProduct, ProductVersion = ProductVersionNumber.Parse("1.2") },
                ValidAt.From(BusinessDate.Parse("2026-11-01")), cancellationToken: TestContext.Current.CancellationToken);
            resolution.ArtefactHash!.Value.Value.ShouldBe(pin);
            resolution.ActivationRecord!.Value.GetProperty("ProductVersion").GetString().ShouldBe("1.2");
        }
        (await _factory.Services.GetRequiredService<OutboxReplayService>().ReplayAsync("RAT.ProductVersionPublished.Fallback",
            new ReplayFilter { EventIds = [envelope.EventId.Value] }, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await ScalarAsync<long>("SELECT count(*) FROM rat.rate_activation WHERE product_code='MOTOR-GR' AND product_version='1.2'"))
            .ShouldBe(1);
        (await RateAsync("1.0")).RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
        (await RateAsync("1.1")).RatingArtefactHash.ShouldBe(defective.RatingArtefactHash);
        (await Should.ThrowAsync<DomainException>(() => RateAsync("1.9", pin))).Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        await Should.ThrowAsync<DomainException>(() => RateAsync("1.2", basis: "2026-10-31"));
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var retire = dataSource.CreateCommand("UPDATE rat.rate_activation SET status='Superseded' WHERE product_code='MOTOR-GR' AND product_version IN ('1.0','1.2')");
        await retire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        try
        {
            // Term pins survive later activations; replay cannot resurrect a closed activation or re-resolve its source.
            (await RateAsync("1.2", pin)).RatingArtefactHash.ShouldBe(original.RatingArtefactHash);
            await Should.ThrowAsync<DomainException>(() => RateAsync("1.2"));
            await _factory.Services.GetRequiredService<OutboxReplayService>().ReplayAsync("RAT.ProductVersionPublished.Fallback",
                new ReplayFilter { EventIds = [envelope.EventId.Value] }, TestContext.Current.CancellationToken);
            (await ScalarAsync<string>("SELECT status FROM rat.rate_activation WHERE product_code='MOTOR-GR' AND product_version='1.2'"))
                .ShouldBe("Superseded");
            (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation='rat.RateActivation.activateFallback' AND causation_id='{envelope.EventId.Value}' AND outcome='Rejected'"))
                .ShouldBe(0);
        }
        finally
        {
            await using var restore = dataSource.CreateCommand("UPDATE rat.rate_activation SET status='Active' WHERE product_code='MOTOR-GR' AND product_version IN ('1.0','1.2')");
            await restore.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("1.3", "9.0", "MOTOR-GR")]
    [InlineData("1.4", "1.0", "UNKNOWN-PRODUCT")]
    public async Task Missing_source_refuses_without_using_other_version_or_product(string version, string source, string product)
    {
        await RateAsync("1.0");
        var envelope = await PublishAsync(version, source, product);
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.rate_activation WHERE source_event_id='{envelope.EventId.Value}'"))
            .ShouldBe(0);
        await Should.ThrowAsync<DomainException>(() => RateAsync(version));
        (await ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation='rat.RateActivation.activateFallback' AND causation_id='{envelope.EventId.Value}' AND outcome='Rejected'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Ordinary_publication_does_not_create_a_fallback_activation()
    {
        await RateAsync("1.0");
        var envelope = await PublishAsync("1.5", null);
        (await ScalarAsync<long>($"SELECT count(*) FROM rat.rate_activation WHERE source_event_id='{envelope.EventId.Value}'"))
            .ShouldBe(0);
        await Should.ThrowAsync<DomainException>(() => RateAsync("1.5"));
    }
}
