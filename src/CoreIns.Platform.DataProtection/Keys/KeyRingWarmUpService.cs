using Microsoft.Extensions.Hosting;

namespace CoreIns.Platform.DataProtection.Keys;

/// <summary>
/// Pre-loads the key rings of every legal entity at start-up (review F-1e m6), so EF Core value converters on request
/// threads find their keys in memory and never wait on Key Vault. The legal entities come from the Host's
/// <see cref="ILegalEntityCatalogue"/>; without one, nothing is pre-loaded and the first use of a legal entity loads its
/// keys (blocking once).
/// </summary>
public sealed class KeyRingWarmUpService(KeyRing keyRing, IEnumerable<ILegalEntityCatalogue> catalogues) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var catalogue in catalogues)
        {
            foreach (var legalEntity in await catalogue.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                await keyRing.WarmUpAsync(legalEntity, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
