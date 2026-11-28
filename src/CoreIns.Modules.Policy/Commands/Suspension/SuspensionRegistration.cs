using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Commands.Suspension;

/// <summary>Registration of the suspension commands (BO-POL-08); <c>PolicyModule</c> appends one call.</summary>
internal static class SuspensionRegistration
{
    public static IServiceCollection AddSuspensionCommands(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<SuspensionService>();

        services.AddScoped<IValidator<SuspendPolicy>, SuspendPolicyValidator>();
        services.AddCommandAuditor<SuspendPolicy, SuspensionCreateResponse, SuspendPolicyAuditor>();
        services.AddCommand<SuspendPolicy, SuspensionCreateResponse, SuspendPolicyHandler>(CommandDescriptor.For("pol.Suspension.create") with { SupportsDryRun = true });

        services.AddScoped<IValidator<ReactivatePolicy>, ReactivatePolicyValidator>();
        services.AddCommandAuditor<ReactivatePolicy, ReactivationCreateResponse, ReactivatePolicyAuditor>();
        services.AddCommand<ReactivatePolicy, ReactivationCreateResponse, ReactivatePolicyHandler>(CommandDescriptor.For("pol.Reactivation.create") with { SupportsDryRun = true });
        return services;
    }
}
