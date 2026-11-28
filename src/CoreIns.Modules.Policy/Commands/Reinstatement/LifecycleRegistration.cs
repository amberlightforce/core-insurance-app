using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Commands.Reinstatement;

/// <summary>Registration of the reinstatement and rewrite commands (BO-POL-07); <c>PolicyModule</c> appends one call.</summary>
internal static class LifecycleRegistration
{
    public static IServiceCollection AddLifecycleCommands(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IValidator<ReinstatePolicy>, ReinstatePolicyValidator>();
        services.AddCommandAuditor<ReinstatePolicy, ReinstatementCreateResponse, ReinstatePolicyAuditor>();
        services.AddCommand<ReinstatePolicy, ReinstatementCreateResponse, ReinstatePolicyHandler>(CommandDescriptor.For("pol.Reinstatement.create") with { SupportsDryRun = true });

        services.AddScoped<IValidator<RewritePolicy>, RewritePolicyValidator>();
        services.AddCommandAuditor<RewritePolicy, RewriteCreateResponse, RewritePolicyAuditor>();
        services.AddCommand<RewritePolicy, RewriteCreateResponse, RewritePolicyHandler>(CommandDescriptor.For("pol.Rewrite.create") with { SupportsDryRun = true });
        return services;
    }
}
