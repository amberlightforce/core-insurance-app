using CoreIns.Platform.DataProtection;
using CoreIns.Platform.DataProtection.Keys;

namespace CoreIns.Host.Hosting;

/// <summary>
/// Field-level protection for api and worker (F-1e, D-ARC-14): the durable key ring in <c>plt.data_key</c> and the
/// key-encryption-key provider. No legal-entity catalogue is registered for the start-up warm-up: modules use the
/// asynchronous encryptor API (keys load on first use), and a warm-up would turn an unreachable database into a failed
/// start instead of "alive but not ready". The local provider (KEKs derived
/// from <c>DataProtection:LocalMasterKey</c>) is accepted only in Development and in the integration-test environment;
/// anywhere else the Host refuses to start until the Key Vault provider is configured (<c>DataProtection:KeyVaultUri</c>,
/// wiring with a managed identity is a W1-PLT follow-up, see the SL-0 report).
/// </summary>
internal static class DataProtectionHosting
{
    public const string LocalMasterKey = "DataProtection:LocalMasterKey";

    /// <summary>Environment of the integration tests (WebApplicationFactory sets it).</summary>
    public const string TestingEnvironment = "Testing";

    public static IServiceCollection AddCoreInsDataProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var localAllowed = environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment);
        if (!localAllowed)
        {
            throw new InvalidOperationException(
                $"Field-level protection needs the Key Vault key provider in environment '{environment.EnvironmentName}'; "
                + "the local provider is refused outside Development (D-ARC-14). Configure DataProtection:KeyVaultUri once the provider wiring lands.");
        }

        var encoded = configuration[LocalMasterKey];
        byte[] master;
        try
        {
            master = Convert.FromBase64String(encoded ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{LocalMasterKey} must be base64.", ex);
        }

        if (master.Length != 32)
        {
            throw new InvalidOperationException($"{LocalMasterKey} must be 32 bytes, base64-encoded (a synthetic development value, never a real secret).");
        }

        services.AddSingleton<IKeyProvider>(new LocalKeyProvider(master));
        services.AddSingleton<IDataKeyStore, PostgresDataKeyStore>();
        services.AddFieldLevelProtection();
        return services;
    }
}
