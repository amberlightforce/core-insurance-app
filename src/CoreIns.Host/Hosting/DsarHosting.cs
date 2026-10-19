using CoreIns.SharedKernel.Dsar;

namespace CoreIns.Host.Hosting;

/// <summary>Registers the DSAR registry and retention policy. Modules add their own <see cref="IDsarParticipant"/> in their module registration.</summary>
internal static class DsarHosting
{
    public static IServiceCollection AddCoreInsDsar(this IServiceCollection services)
    {
        services.AddSingleton<DsarRetentionPolicy>();
        services.AddSingleton<IDsarParticipant, CoreIns.Platform.Dsar.PlatformDsarParticipant>();
        services.AddSingleton<DsarParticipantRegistry>();
        return services;
    }
}
