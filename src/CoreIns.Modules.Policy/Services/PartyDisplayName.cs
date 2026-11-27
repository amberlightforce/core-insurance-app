using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Services;

/// <summary>
/// The addressee's name for a letter, read from PTY (<c>pty.Party.get</c>) so the letter greets the person instead of "Sir/Madam". Native-script name
/// first; the neutral greeting is the fallback whenever PTY is absent, throws or holds no usable name (a letter must not fail for want of a name).
/// </summary>
internal sealed class PartyDisplayName(IServiceProvider services)
{
    /// <summary>The neutral Greek greeting used when no name can be read.</summary>
    public const string Greeting = "Κύριε/Κυρία";

    public async Task<string> ResolveAsync(PartyId partyId, CancellationToken cancellationToken)
    {
        var service = services.GetService<IPartyPartyService>();
        if (service is null)
        {
            return Greeting;
        }

        try
        {
            var response = await service.GetAsync(partyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
            return Pick(response.Party.Names) ?? Greeting;
        }
#pragma warning disable CA1031 // A missing name never blocks the letter.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Greeting;
        }
    }

    /// <summary>The display name of the best name record (native form first), or null when none has usable text.</summary>
    internal static string? Pick(IReadOnlyList<PartyNameView> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names.OrderBy(n => n.Form == PartyNameView.FormValue.Native ? 0 : 1))
        {
            var text = !string.IsNullOrWhiteSpace(name.OrganisationName)
                ? name.OrganisationName
                : string.Join(' ', new[] { name.GivenNames, name.FamilyName }.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }
}
