using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Commands.Renewal;

/// <summary>Registration of the manual-renewal commands (SL3-POL-RENEW); <c>PolicyModule</c> appends one call.</summary>
internal static class RenewalRegistration
{
    public static IServiceCollection AddRenewalCommands(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RenewalOptions>().Bind(configuration.GetSection(RenewalOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddScoped<RenewalPricing>();

        services.AddScoped<IValidator<CreateRenewal>, CreateRenewalValidator>();
        services.AddCommandAuditor<CreateRenewal, RenewalCreateResponse, CreateRenewalAuditor>();
        services.AddCommand<CreateRenewal, RenewalCreateResponse, CreateRenewalHandler>(CommandDescriptor.For("pol.Renewal.create") with { SupportsDryRun = true });

        services.AddScoped<IValidator<OfferRenewal>, OfferRenewalValidator>();
        services.AddCommandAuditor<OfferRenewal, RenewalOfferResponse, OfferRenewalAuditor>();
        services.AddCommand<OfferRenewal, RenewalOfferResponse, OfferRenewalHandler>(CommandDescriptor.For("pol.Renewal.offer") with { SupportsDryRun = true });

        services.AddScoped<IValidator<AcceptRenewal>, AcceptRenewalValidator>();
        services.AddCommandAuditor<AcceptRenewal, RenewalAcceptResponse, AcceptRenewalAuditor>();
        services.AddCommand<AcceptRenewal, RenewalAcceptResponse, AcceptRenewalHandler>(CommandDescriptor.For("pol.Renewal.accept") with { SupportsDryRun = true });

        services.AddErrorDefinitions(RenewalErrors.Definitions);
        return services;
    }
}
