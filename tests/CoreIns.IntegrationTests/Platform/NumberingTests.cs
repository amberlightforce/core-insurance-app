using System.Collections.Concurrent;
using CoreIns.Platform;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>The numbering service (REQ-PLT-014, REQ-PLT-209..213) on a real PostgreSQL 17.</summary>
public sealed class NumberingTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ServiceProvider _services = null!;

    public ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stamp:LegalEntity"] = "GR-TEST",
            ["Stamp:Country"] = "GR",
            ["Platform:Numbering:Schemes:GAPLESS:Prefix"] = "G",
            ["Platform:Numbering:Schemes:GAPLESS:Width"] = "6",
            ["Platform:Numbering:Schemes:BLOCKS:Prefix"] = "B",
            ["Platform:Numbering:Schemes:BLOCKS:GapPolicy"] = "GapsAllowed",
            ["Platform:Numbering:Schemes:BLOCKS:BlockSize"] = "7",
            ["Platform:Numbering:Schemes:YEARLY:Prefix"] = "Y-",
            ["Platform:Numbering:Schemes:YEARLY:Width"] = "4",
            ["Platform:Numbering:Schemes:YEARLY:Reset"] = "Yearly",
            ["Platform:Numbering:Schemes:TINY:Prefix"] = "T",
            ["Platform:Numbering:Schemes:TINY:Width"] = "4",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new ManualClock(Instant.Parse("2026-10-07T08:00:00Z")));
        services.AddPlatformDataSource(new NpgsqlConnectionStringBuilder(database.AppConnectionString) { MaxPoolSize = 80 }.ConnectionString);
        services.AddPlatformModule(configuration);
        _services = services.BuildServiceProvider(validateScopes: true);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task Gapless_numbers_are_unique_and_contiguous_under_concurrency_and_a_rollback_consumes_none()
    {
        var issued = new ConcurrentBag<IssuedNumber>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 40), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
            issued.Add(await NextAsync("GAPLESS", commit: true, ct)));

        issued.Select(n => n.Value).Distinct().Count().ShouldBe(40);
        issued.Select(n => n.Sequence).Order().ShouldBe(Enumerable.Range(1, 40).Select(i => (long)i));
        issued.ShouldAllBe(n => n.Value.Length == 7 && n.Value.StartsWith('G'));

        var rolledBack = await NextAsync("GAPLESS", commit: false, CancellationToken.None);
        rolledBack.Sequence.ShouldBe(41);
        (await NextAsync("GAPLESS", commit: true, CancellationToken.None)).Value.ShouldBe("G000041");
    }

    [Fact]
    public async Task Gap_allowed_series_hand_out_unique_numbers_from_blocks()
    {
        var issued = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 60), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
            issued.Add((await NextAsync("BLOCKS", commit: true, ct)).Value));

        issued.Distinct().Count().ShouldBe(60);
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand("SELECT next_value FROM plt.number_series WHERE identifier_type = 'BLOCKS'");
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(64); // 9 blocks of 7 reserved
    }

    [Fact]
    public async Task Yearly_series_restart_and_carry_the_year_and_an_exhausted_series_fails_closed()
    {
        (await NextAsync("YEARLY", true, CancellationToken.None, new BusinessDate(2026, 12, 31))).Value.ShouldBe("Y-20260001");
        (await NextAsync("YEARLY", true, CancellationToken.None, new BusinessDate(2027, 1, 1))).Value.ShouldBe("Y-20270001");
        (await NextAsync("YEARLY", true, CancellationToken.None, new BusinessDate(2026, 6, 1))).Value.ShouldBe("Y-20260002");

        await database.ExecuteAsSuperuserAsync(
            "INSERT INTO plt.number_series VALUES ('GR-TEST', 'TINY', 'default', 9999, 9999, now()) ON CONFLICT DO NOTHING", CancellationToken.None);
        (await NextAsync("TINY", true, CancellationToken.None)).Value.ShouldBe("T9999");
        var exhausted = await Should.ThrowAsync<DomainException>(() => NextAsync("TINY", true, CancellationToken.None));
        exhausted.Error.Code.Value.ShouldBe("PLT-ERR-RANGE-EXHAUSTED");
        var unknown = await Should.ThrowAsync<DomainException>(() => NextAsync("NOT_DEFINED", true, CancellationToken.None));
        unknown.Error.Code.Value.ShouldBe("PLT-ERR-NUMBERING-SCHEME-UNKNOWN");
    }

    [Fact]
    public void Formats_that_could_carry_personal_data_are_refused()
    {
        var options = new NumberingOptions();
        options.Schemes["PARTY"] = new NumberingSchemeOptions { Prefix = "{birthDate}", Width = 9 };
        options.Schemes["bad type"] = new NumberingSchemeOptions { Prefix = "P", Width = 2 };
        options.Validate().Count.ShouldBe(3);
    }

    private async Task<IssuedNumber> NextAsync(string type, bool commit, CancellationToken cancellationToken, BusinessDate? validAt = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        var number = await scope.ServiceProvider.GetRequiredService<INumberingService>().NextAsync(new NumberRequest(type, validAt), cancellationToken);
        if (commit)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return number;
    }
}
