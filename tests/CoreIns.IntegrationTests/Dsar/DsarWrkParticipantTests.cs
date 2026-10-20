using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Dsar;

/// <summary>NX-HD-03: WRK takes part in DSARs (a party without tasks holds nothing; NX-HD-05 tests real task data) and no DSAR map module is left without a participant.</summary>
public sealed class DsarWrkParticipantTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    // The DSAR map (DsarEngine DsarOptions): PTY POL BIL CLM UW RI DOC CHN WRK PLT FIN RAT DAT MIG CMP. DAT is served by the Compliance adapter.
    private static readonly string[] Modules = ["PTY", "POL", "BIL", "CLM", "UW", "RI", "DOC", "CHN", "WRK", "PLT", "FIN", "RAT", "MIG", "CMP"];

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void Every_map_module_has_a_registered_participant()
    {
        var registered = _factory.Services.GetServices<IDsarParticipant>().Select(p => p.Module).ToList();
        registered.Order().ShouldBe(Modules.Order());
    }

    [Fact]
    public async Task Wrk_reports_nothing_held_for_every_step_and_never_not_built()
    {
        var ct = TestContext.Current.CancellationToken;
        var wrk = _factory.Services.GetServices<IDsarParticipant>().Single(p => p.Module == "WRK");
        var subject = SubjectRef.ForParty(PartyId.New());
        var context = new DsarContext(Guid.NewGuid(), "tester", "dpo");

        var export = await wrk.ExportAsync(subject, context, ct);
        var restrict = await wrk.RestrictAsync(subject, "test", "test basis", null, ct);
        var erase = await wrk.EraseAsync(subject, context, ct);

        export.ExportPayload.ShouldNotBeNull().ShouldContain("\"module\":\"WRK\"");
        foreach (var result in new[] { export, restrict, erase })
        {
            result.Outcomes.ShouldNotBeEmpty();
            result.Outcomes.ShouldAllBe(o => o.Kind == DsarOutcomeKind.NotHeld && !string.IsNullOrEmpty(o.Detail));
            result.HasNotBuilt.ShouldBeFalse();
        }
    }
}
