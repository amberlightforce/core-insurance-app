namespace CoreIns.IntegrationTests.Reinsurance.Recovery;

/// <summary>NX-EC-08: concurrent recovery booking on one contract year (advisory lock + sealed deltas).</summary>
public sealed class RecoveryRaceTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_movements_on_one_contract_year_book_each_delta_once()
    {
        await using var slice = new RecoverySlice(database);
        var (product, contract, lead, follow) = await slice.TreatyAsync();
        var (claimA, exposureA) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (claimB, exposureB) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await slice.ReportedAsync(claimA, product);
        await slice.ExposureAsync(claimA, exposureA);
        await slice.ReportedAsync(claimB, product);
        await slice.ExposureAsync(claimB, exposureB);

        // Two claims move at once: the per-contract-year advisory lock serialises the recalculations; neither is lost or doubled.
        await Task.WhenAll(
            Task.Run(() => slice.ReserveAsync(claimA, exposureA, "800000.00"), Ct),
            Task.Run(() => slice.ReserveAsync(claimB, exposureB, "800000.00"), Ct));
        foreach (var claim in new[] { claimA, claimB })
        {
            var perParticipant = RecoverySlice.PerParticipant((await slice.ByClaimAsync(claim)).Body);
            perParticipant[lead].ShouldBe((180000m, 0m));
            perParticipant[follow].ShouldBe((120000m, 0m));
            (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE claim_id = '{claim}'")).ShouldBe(2);
        }

        // The same event redelivered by three workers at once adds nothing.
        var again = await slice.ReserveAsync(claimA, exposureA, "800000.00");
        var before = await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => slice.DeliverAsync("RI.ReserveChanged.ClaimView", again, replay: true), Ct)));
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.recovery WHERE contract_id = '{contract}'")).ShouldBe(before);
    }
}
