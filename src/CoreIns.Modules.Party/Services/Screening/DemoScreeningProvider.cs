using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Party.Services.Screening;

/// <summary>
/// The non-Production provider (D-SL2-05): no sanctions list is connected, so every subject is Clear with the list version
/// <see cref="PartyScreening.StubListVersion"/>, except a subject with a name containing <see cref="HitToken"/>, which "matches" a fictitious
/// list entry so the case queue, the maker-checker decision and the blocks can be demonstrated and tested. It refuses to be constructed in Production.
/// </summary>
internal sealed class DemoScreeningProvider : IScreeningProvider
{
    /// <summary>A name containing this text (any case) produces a potential hit.</summary>
    public const string HitToken = "TESTHIT";

    public DemoScreeningProvider(IHostEnvironment environment)
    {
        StubGuard.RefuseIn(environment);
    }

    public Task<ScreeningProviderResult> ScreenAsync(ScreeningSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        List<ScreeningMatch> matches = [];
        foreach (var name in subject.Names)
        {
            if (name.Contains(HitToken, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new ScreeningMatch("DEMO-LIST", name.Trim(), 95, "NAME"));
            }
        }

        return Task.FromResult(new ScreeningProviderResult([PartyScreening.StubListVersion], matches));
    }
}
