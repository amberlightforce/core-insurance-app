using CoreIns.Platform.DataProtection.Keys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.Platform.DataProtection;

/// <summary>Registration of the field-level protection services.</summary>
public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="KeyRing"/>, <see cref="FieldEncryptor"/>, <see cref="BlindIndexer"/>,
    /// <see cref="ICurrentLegalEntity"/> and the start-up <see cref="KeyRingWarmUpService"/>. The caller (Host) must
    /// register an <see cref="IKeyProvider"/> (Key Vault in Azure, <see cref="LocalKeyProvider"/> only in development/CI),
    /// a durable <see cref="IDataKeyStore"/> — there is deliberately no default store, because losing wrapped keys loses
    /// data — and an <see cref="ILegalEntityCatalogue"/> for the warm-up.
    /// </summary>
    public static IServiceCollection AddFieldLevelProtection(this IServiceCollection services, KeyRingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentLegalEntity, AmbientLegalEntity>();
        services.TryAddSingleton(provider => new KeyRing(
            provider.GetRequiredService<IKeyProvider>(),
            provider.GetRequiredService<IDataKeyStore>(),
            provider.GetRequiredService<TimeProvider>(),
            options));
        services.TryAddSingleton<FieldEncryptor>();
        services.TryAddSingleton<BlindIndexer>();
        services.AddHostedService<KeyRingWarmUpService>();
        return services;
    }
}
