using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Channels.Queries;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.Modules.Channels;

/// <summary>
/// Composition entry point of the Channels module (PRD-12 Channels).
/// The Host calls <see cref="AddChannelsModule"/>; the module registers its own services here.
/// SL6-CHN-WITHDRAW builds the customer withdrawal function (declare, confirm, get, list, close).
/// </summary>
public static class ChannelsModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "chn";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: SELECT, INSERT, UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<ChannelsDbContext>(ModuleCode.CHN, Schema)];

    /// <summary>Registers the module's services: DbContext, the withdrawal commands and queries, errors.</summary>
    public static IServiceCollection AddChannelsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ChannelsOptions>().Bind(configuration.GetSection(ChannelsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddModuleDbContext<ChannelsDbContext>(Schema);
        services.AddScoped<WithdrawalReader>();
        services.AddScoped<CoreIns.Modules.Channels.Ai.TicketService>();
        services.TryAddScoped<IWithdrawalPolicyPort, PolicyContractWithdrawalPort>();

        services.AddScoped<IValidator<DeclareWithdrawal>, DeclareWithdrawalValidator>();
        services.AddCommandAuditor<DeclareWithdrawal, WithdrawalDeclareResponse, DeclareWithdrawalAuditor>();
        services.AddCommand<DeclareWithdrawal, WithdrawalDeclareResponse, DeclareWithdrawalHandler>(CommandDescriptor.For("chn.Withdrawal.declare"));

        services.AddScoped<IValidator<ConfirmWithdrawal>, ConfirmWithdrawalValidator>();
        services.AddCommandAuditor<ConfirmWithdrawal, WithdrawalConfirmResponse, ConfirmWithdrawalAuditor>();
        services.AddCommand<ConfirmWithdrawal, WithdrawalConfirmResponse, ConfirmWithdrawalHandler>(CommandDescriptor.For("chn.Withdrawal.confirm"));

        services.AddCommand<CloseWithdrawal, WithdrawalCloseResponse, CloseWithdrawalHandler>(CommandDescriptor.For("chn.Withdrawal.close"));

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every CHN-ERR code the withdrawal function raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.CHN, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.CHN, "TOKEN-INVALID", 422, "Μη έγκυρος κωδικός επιβεβαίωσης", "The confirmation token is not valid")
            .Describe("Ο κωδικός επιβεβαίωσης δεν ταιριάζει ή έχει λήξει.", "The confirmation token does not match or has expired."),
        ErrorDefinition.For(ModuleCode.CHN, "RIGHT-EXPIRED", 422, "Το δικαίωμα υπαναχώρησης έχει λήξει", "The right of withdrawal has expired")
            .Describe("Η προθεσμία υπαναχώρησης έχει παρέλθει.", "The withdrawal period has passed."),
        ErrorDefinition.For(ModuleCode.CHN, "WRONG-STATE", 409, "Μη επιτρεπτή κατάσταση", "The request is in the wrong state")
            .Describe("Το αίτημα δεν μπορεί να κλείσει από την τρέχουσα κατάστασή του.", "The request cannot be closed from its current state."),
        ErrorDefinition.For(ModuleCode.CHN, "TICKET-EXPIRED", 422, "Το δελτίο επιβεβαίωσης έχει λήξει", "The confirmation ticket has expired")
            .Describe("Το δελτίο δεν μπορεί πλέον να εγκριθεί ή να απορριφθεί.", "The ticket can no longer be approved or rejected."),
        ErrorDefinition.For(ModuleCode.CHN, "TICKET-STALE", 409, "Το δελτίο έχει ήδη κριθεί", "The ticket has already been decided")
            .Describe("Το δελτίο έχει ήδη εγκριθεί ή απορριφθεί.", "The ticket was already approved or rejected."),
    ];
}
