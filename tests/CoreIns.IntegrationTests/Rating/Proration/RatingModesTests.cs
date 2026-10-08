using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Market;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Rating.RatingTestSupport;

namespace CoreIns.IntegrationTests.Rating.Proration;

/// <summary>
/// SL3-RAT-PRORATE: the ENDORSEMENT and RENEWAL modes of <c>rat.Rate.rate</c> on a real PostgreSQL 17, with a second, newer
/// artefact (the minimum MTPL premium raised, illustrative test data) active from 2026-06-01. REQ-POL-093: an endorsement is rated
/// under the artefact pinned to the term, never the one active now. REQ-POL-249 (subset): a renewal is rated under the artefact the
/// caller resolved for the new term start.
/// </summary>
public sealed class RatingModesTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private string _oldHash = string.Empty;
    private string _newHash = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _baseFactory = new ApiHostFactory(database.AppConnectionString);
        _factory = WithMarket(_baseFactory, MarketFake());

        // The first rating seeds artefact A (active from 2026-01-01).
        var first = await RateAsync(RateRequest(basisDate: "2026-02-01", periodEnd: "2027-02-01"));
        _oldHash = first.RatingArtefactHash!.Value.Value;
        first.Rates!.First(r => r.CoverageCode == "MTPL").AnnualRate.ShouldBe(121.50m);

        _newHash = await InsertNewerArtefactAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    private async Task<RateRateResponse> RateAsync(RateRateRequest request)
    {
        await using var scope = Scope(_factory.Services);
        return await scope.ServiceProvider.GetRequiredService<IRatingRateService>().RateAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<string> ErrorCodeAsync(RateRateRequest request) =>
        (await Should.ThrowAsync<DomainException>(() => RateAsync(request))).Error.Code.Value;

    /// <summary>Artefact B: the same tables except the MTPL minimum premium (80.00 to 200.00), active from 2026-06-01. Superuser SQL, test fixture only.</summary>
    private async Task<string> InsertNewerArtefactAsync()
    {
        var tables = BuiltInArtefacts.Tables().Select(t => t.Code == "MINIMUM_PREMIUM"
            ? t with { Rules = [.. t.Rules.Select(r => r.Id == "MIN-MTPL" ? r with { Outputs = ["200.00"] } : r)] }
            : t).Select(t => CompiledArtefact.CompileTable(t)).ToList();
        var definition = BuiltInArtefacts.Definition(
            BuiltInArtefacts.DefaultProductCode, BuiltInArtefacts.DefaultProductVersion, tables.ToDictionary(t => t.Dto.Code, t => t.Hash, StringComparer.Ordinal));
        var hash = ArtefactJson.HashOf(definition).Value;

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        foreach (var (tableHash, dto) in tables.Select(t => (t.Hash, t.Dto)))
        {
            await using var insert = dataSource.CreateCommand(
                """
                INSERT INTO rat.rate_table_version (table_hash, table_code, version_no, hit_policy, data_status, definition, created_at, created_by)
                VALUES ($1, $2, $3, $4, $5, $6::jsonb, now(), 'test') ON CONFLICT DO NOTHING
                """);
            insert.Parameters.AddWithValue(tableHash);
            insert.Parameters.AddWithValue(dto.Code);
            insert.Parameters.AddWithValue(dto.Version);
            insert.Parameters.AddWithValue(dto.HitPolicy);
            insert.Parameters.AddWithValue(dto.DataStatus);
            insert.Parameters.AddWithValue(ArtefactJson.Serialize(dto));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var artefact = dataSource.CreateCommand(
            """
            INSERT INTO rat.rating_artifact (artefact_hash, artefact_code, label, product_code, product_version, engine_version, data_status, definition, created_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, now(), 'test') ON CONFLICT DO NOTHING
            """);
        artefact.Parameters.AddWithValue(hash);
        artefact.Parameters.AddWithValue(definition.Code);
        artefact.Parameters.AddWithValue(definition.Label);
        artefact.Parameters.AddWithValue(definition.ProductCode);
        artefact.Parameters.AddWithValue(definition.ProductVersion);
        artefact.Parameters.AddWithValue(definition.EngineVersion);
        artefact.Parameters.AddWithValue(definition.Metadata.DataStatus);
        artefact.Parameters.AddWithValue(ArtefactJson.Serialize(definition));
        await artefact.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await using var activation = dataSource.CreateCommand(
            """
            INSERT INTO rat.rate_activation (activation_id, artefact_hash, product_code, product_version, effective_from, effective_to, status, created_at, created_by)
            VALUES ($1, $2, $3, $4, DATE '2026-06-01', NULL, 'Active', now(), 'test') ON CONFLICT DO NOTHING
            """);
        activation.Parameters.AddWithValue(Guid.CreateVersion7());
        activation.Parameters.AddWithValue(hash);
        activation.Parameters.AddWithValue(definition.ProductCode);
        activation.Parameters.AddWithValue(definition.ProductVersion);
        await activation.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return hash;
    }

    private static RateRateRequest Endorsement(string? pinned, string basis = "2026-11-01", string end = "2027-02-01", string? productVersion = null)
    {
        var request = RateRequest(mode: "ENDORSEMENT", basisDate: basis, periodEnd: end);
        request = request with { Envelope = request.Envelope with { PinnedRatingArtefactHash = pinned is null ? null : Sha256Hash.Parse(pinned) } };
        return productVersion is null
            ? request
            : request with { Envelope = request.Envelope with { ProductVersion = ProductVersionNumber.Parse(productVersion) } };
    }

    [Fact]
    public async Task REQ_POL_093_a_new_business_rating_after_the_newer_activation_uses_the_newer_artefact()
    {
        var response = await RateAsync(RateRequest(basisDate: "2026-11-01"));

        response.RatingArtefactHash!.Value.Value.ShouldBe(_newHash);
        response.Rates!.First(r => r.CoverageCode == "MTPL").AnnualRate.ShouldBe(200.00m);
        response.AutomatedDecision.ShouldBe(false); // FULL is unchanged
    }

    [Fact]
    public async Task REQ_POL_093_an_ENDORSEMENT_is_rated_under_the_pinned_old_artefact_although_a_newer_one_is_active()
    {
        // Effective 2026-11-01, a 3-month part of a term bound in February 2026 under artefact A. B has been active since June.
        var response = await RateAsync(Endorsement(_oldHash, end: "2027-02-01"));

        response.RatingArtefactHash!.Value.Value.ShouldBe(_oldHash);
        response.Rates!.First(r => r.CoverageCode == "MTPL").AnnualRate.ShouldBe(121.50m); // A's 121.50, not B's 200.00
        response.Rates!.First(r => r.CoverageCode == "OWN-DAMAGE").AnnualRate.ShouldBe(283.50m);
        response.CoveragePremiumTotal!.Value.Amount.ShouldBe(430.00m); // E2E-01 amounts unchanged
        response.TaxTotal!.Value.Amount.ShouldBe(64.51m);
        response.AutomatedDecision.ShouldBe(true);
        response.Bindable.ShouldBe(true);
    }

    [Fact]
    public async Task REQ_POL_093_an_ENDORSEMENT_under_the_newer_pin_gets_the_newer_rates()
    {
        var response = await RateAsync(Endorsement(_newHash));

        response.RatingArtefactHash!.Value.Value.ShouldBe(_newHash);
        response.Rates!.First(r => r.CoverageCode == "MTPL").AnnualRate.ShouldBe(200.00m);
    }

    [Fact]
    public async Task REQ_POL_093_an_ENDORSEMENT_without_a_pin_an_unknown_pin_or_a_pin_of_another_version_is_RAT_ERR_INPUT()
    {
        (await ErrorCodeAsync(Endorsement(null))).ShouldBe("RAT-ERR-INPUT"); // never falls back to the active artefact
        (await ErrorCodeAsync(Endorsement(new string('d', 64)))).ShouldBe("RAT-ERR-INPUT");
        (await ErrorCodeAsync(Endorsement(_oldHash, productVersion: "2.0"))).ShouldBe("RAT-ERR-INPUT");
        // a named ratingArtefactHash that disagrees with the pin is refused too
        var disagree = Endorsement(_oldHash);
        (await ErrorCodeAsync(disagree with { Envelope = disagree.Envelope with { RatingArtefactHash = Sha256Hash.Parse(_newHash) } })).ShouldBe("RAT-ERR-INPUT");
        // the old way of pinning (ratingArtefactHash alone) is not a pin
        (await ErrorCodeAsync(RateRequest(mode: "ENDORSEMENT", basisDate: "2026-11-01", periodEnd: "2027-02-01", ratingArtefactHash: _oldHash))).ShouldBe("RAT-ERR-INPUT");
        var otherProduct = Endorsement(_oldHash);
        (await ErrorCodeAsync(otherProduct with { Envelope = otherProduct.Envelope with { ProductCode = "HOME-GR" } })).ShouldBe("RAT-ERR-INPUT");
    }

    [Fact]
    public async Task An_ENDORSEMENT_segment_is_the_changed_part_of_a_term_up_to_a_year_and_not_more()
    {
        (await RateAsync(Endorsement(_oldHash, basis: "2026-11-01", end: "2027-11-01"))).Rates.ShouldNotBeEmpty(); // exactly a year
        (await ErrorCodeAsync(Endorsement(_oldHash, basis: "2026-11-01", end: "2027-11-02"))).ShouldBe("RAT-ERR-PERIOD");
    }

    [Fact]
    public async Task A_term_bound_on_29_February_is_ratable_as_a_full_year_and_as_its_changed_part()
    {
        // 2028-02-29 + 1 year = 2029-02-28.
        var full = await RateAsync(RateRequest(basisDate: "2028-02-29", periodEnd: "2029-02-28"));
        full.Rates.ShouldNotBeEmpty();
        var part = await RateAsync(Endorsement(_newHash, basis: "2028-03-15", end: "2029-02-28"));
        part.Rates.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task REQ_POL_249_a_RENEWAL_uses_the_artefact_the_caller_resolved_for_the_new_term_start()
    {
        // New term starts 2027-02-01: B is active. The caller names B.
        var named = await RateAsync(RateRequest(mode: "RENEWAL", basisDate: "2027-02-01", periodEnd: "2028-02-01", ratingArtefactHash: _newHash));
        named.RatingArtefactHash!.Value.Value.ShouldBe(_newHash);
        named.Rates!.First(r => r.CoverageCode == "MTPL").AnnualRate.ShouldBe(200.00m);
        named.AutomatedDecision.ShouldBe(true);
        named.Bindable.ShouldBe(true);

        // Without a named artefact RAT resolves the one active on the rating basis date (the new term start).
        var resolved = await RateAsync(RateRequest(mode: "RENEWAL", basisDate: "2027-02-01", periodEnd: "2028-02-01"));
        resolved.RatingArtefactHash!.Value.Value.ShouldBe(_newHash);
        resolved.WorksheetId.ShouldBe(named.WorksheetId);

        // A stale hash (B was already active at the new term start, so A is not) is refused, not repriced under the old tariff.
        (await ErrorCodeAsync(RateRequest(mode: "RENEWAL", basisDate: "2027-02-01", periodEnd: "2028-02-01", ratingArtefactHash: _oldHash))).ShouldBe("RAT-ERR-INPUT");
        // ...and so is a forged one.
        (await ErrorCodeAsync(RateRequest(mode: "RENEWAL", basisDate: "2027-02-01", periodEnd: "2028-02-01", ratingArtefactHash: new string('e', 64)))).ShouldBe("RAT-ERR-INPUT");

        // A renewal is a full year, like new business.
        (await ErrorCodeAsync(RateRequest(mode: "RENEWAL", basisDate: "2027-02-01", periodEnd: "2027-08-01"))).ShouldBe("RAT-ERR-PERIOD");
    }

    [Fact]
    public async Task MOTOR_GR_1_1_is_rated_with_the_same_tariff_as_1_0_in_new_business_endorsement_and_renewal()
    {
        static RateRateRequest V11(RateRateRequest r) => r with { Envelope = r.Envelope with { ProductVersion = ProductVersionNumber.Parse("1.1") } };

        // New business, no artefact named: resolved for 1.1 on the basis date (1.1 is active from 2026-10-01).
        var nb = await RateAsync(V11(RateRequest(basisDate: "2026-11-01", periodEnd: "2027-11-01")));
        nb.CoveragePremiumTotal!.Value.Amount.ShouldBe(430.00m);
        nb.TaxTotal!.Value.Amount.ShouldBe(64.51m);
        nb.Rates!.First(r => r.CoverageCode == "MTPL").AnnualRate.ShouldBe(121.50m);
        var onePointZero = await RateAsync(RateRequest(basisDate: "2026-02-01", periodEnd: "2027-02-01"));
        nb.RatingArtefactHash!.Value.Value.ShouldNotBe(onePointZero.RatingArtefactHash!.Value.Value); // bound to 1.1, not 1.0's artefact

        // An endorsement pinned to the 1.1 artefact, and a renewal into 1.1, give the same amounts.
        var endorsement = await RateAsync(V11(Endorsement(nb.RatingArtefactHash.Value.Value)));
        endorsement.CoveragePremiumTotal!.Value.Amount.ShouldBe(430.00m);
        var renewal = await RateAsync(V11(RateRequest(mode: "RENEWAL", basisDate: "2027-11-01", periodEnd: "2028-11-01")));
        renewal.CoveragePremiumTotal!.Value.Amount.ShouldBe(430.00m);
        renewal.TaxTotal!.Value.Amount.ShouldBe(64.51m);

        // 1.0 is untouched: its artefact still refuses a 1.1 envelope.
        (await ErrorCodeAsync(V11(Endorsement(_oldHash)))).ShouldBe("RAT-ERR-INPUT");
    }

    [Fact]
    public async Task An_undefined_mode_value_is_RAT_ERR_ENVELOPE()
    {
        var request = RateRequest();

        (await ErrorCodeAsync(request with { Envelope = request.Envelope with { Mode = (RateRateRequest.EnvelopeDetail.ModeValue)99 } })).ShouldBe("RAT-ERR-ENVELOPE");
    }

    [Fact]
    public async Task The_worksheet_of_an_ENDORSEMENT_and_of_a_RENEWAL_records_the_mode()
    {
        var endorsement = await RateAsync(Endorsement(_oldHash));
        var renewal = await RateAsync(RateRequest(mode: "RENEWAL", basisDate: "2027-02-01", periodEnd: "2028-02-01"));

        endorsement.WorksheetId.ShouldNotBe(renewal.WorksheetId);
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT string_agg(DISTINCT mode, ',' ORDER BY mode) FROM rat.worksheet_index WHERE worksheet_id IN ($1, $2)");
        command.Parameters.AddWithValue(endorsement.WorksheetId.Value);
        command.Parameters.AddWithValue(renewal.WorksheetId.Value);
        ((string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe("ENDORSEMENT,RENEWAL");
    }
}
