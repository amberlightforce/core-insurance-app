using CoreIns.SharedKernel;

namespace CoreIns.Modules.Party.Services.Screening;

/// <summary>What a screening provider is asked to check: every name form of the subject plus the identifying attributes it may match on.</summary>
/// <param name="Names">Every name form of the subject (native and Latin, given and family, organisation, trade name).</param>
/// <param name="BirthDate">Birth date, when known.</param>
/// <param name="Country">Country of residence or registration, when known.</param>
/// <param name="Nationality">Nationality, when known.</param>
/// <param name="Identifiers">Identifiers as (scheme, value), when known.</param>
public sealed record ScreeningSubject(
    IReadOnlyList<string> Names, BusinessDate? BirthDate, string? Country, string? Nationality, IReadOnlyList<(string Scheme, string Value)> Identifiers);

/// <summary>One match a provider found.</summary>
/// <param name="ListName">The sanctions or watch list.</param>
/// <param name="MatchedName">The name on the list.</param>
/// <param name="Score">Match strength, 0 to 100.</param>
/// <param name="Reason">Why it matched (name, birth date, identifier), when the provider says.</param>
public sealed record ScreeningMatch(string ListName, string MatchedName, int Score, string? Reason);

/// <summary>A provider's answer: the list versions it screened against and the matches (none means clear).</summary>
public sealed record ScreeningProviderResult(IReadOnlyList<string> ListVersions, IReadOnlyList<ScreeningMatch> Matches);

/// <summary>
/// The SPI behind <c>pty.Screening.screen</c> (BO-PTY-07): the sanctions-list adapter. PTY owns the case queue, the maker-checker decision, the
/// payment and bind blocks and the WRK task; a vendor adapter only answers "what does this subject match". Register one implementation next to
/// <c>AddPartyScreeningEngine()</c>. With no provider the service fails closed (PTY-ERR-NOT-AVAILABLE). The non-Production
/// <c>DemoScreeningProvider</c> is the only implementation in the repository.
/// </summary>
public interface IScreeningProvider
{
    /// <summary>Screens one subject.</summary>
    Task<ScreeningProviderResult> ScreenAsync(ScreeningSubject subject, CancellationToken cancellationToken);
}
