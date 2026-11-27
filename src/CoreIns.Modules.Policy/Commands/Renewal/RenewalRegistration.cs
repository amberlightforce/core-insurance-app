using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.AddScoped<IValidator<DeclineRenewal>, DeclineRenewalValidator>();
        services.AddCommandAuditor<DeclineRenewal, RenewalDeclineResponse, DeclineRenewalAuditor>();
        services.AddCommand<DeclineRenewal, RenewalDeclineResponse, DeclineRenewalHandler>(CommandDescriptor.For("pol.Renewal.decline") with { SupportsDryRun = true });

        services.AddScoped<IValidator<NonRenew>, NonRenewValidator>();
        services.AddCommandAuditor<NonRenew, RenewalNonRenewResponse, NonRenewAuditor>();
        services.AddCommand<NonRenew, RenewalNonRenewResponse, NonRenewHandler>(CommandDescriptor.For("pol.Renewal.nonRenew") with { SupportsDryRun = true });

        // BO-POL-10: batch renewal run (start, pause, retry an item); the worker role processes RUNNING runs.
        services.AddOptions<RenewalRunOptions>().Bind(configuration.GetSection(RenewalRunOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddScoped<IVulnerabilityCheck, PartyVulnerabilityCheck>();
        services.AddScoped<RenewalRunReader>();
        services.AddScoped<RenewalRunItemProcessor>();
        services.AddSingleton<RenewalRunWorkerRunner>();
        services.AddScoped<IValidator<StartRenewalRun>, StartRenewalRunValidator>();
        services.AddCommandAuditor<StartRenewalRun, RenewalRunStartResponse, StartRenewalRunAuditor>();
        services.AddCommand<StartRenewalRun, RenewalRunStartResponse, StartRenewalRunHandler>(CommandDescriptor.For("pol.RenewalRun.start"));
        services.AddScoped<IValidator<PauseRenewalRun>, PauseRenewalRunValidator>();
        services.AddCommandAuditor<PauseRenewalRun, RenewalRunPauseResponse, PauseRenewalRunAuditor>();
        services.AddCommand<PauseRenewalRun, RenewalRunPauseResponse, PauseRenewalRunHandler>(CommandDescriptor.For("pol.RenewalRun.pause"));
        services.AddScoped<IValidator<RetryRenewalRunItem>, RetryRenewalRunItemValidator>();
        services.AddCommandAuditor<RetryRenewalRunItem, RenewalRunRetryItemResponse, RetryRenewalRunItemAuditor>();
        services.AddCommand<RetryRenewalRunItem, RenewalRunRetryItemResponse, RetryRenewalRunItemHandler>(CommandDescriptor.For("pol.RenewalRun.retryItem"));
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<RenewalRunSweeper>();
        }

        services.AddErrorDefinitions(RenewalErrors.Definitions);
        return services;
    }
}
