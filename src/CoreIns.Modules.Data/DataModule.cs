using CoreIns.Modules.Data.Commands;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Contracts;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Modules.Data.Services;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Data;

/// <summary>
/// Composition entry point of the Data module (PRD-15 Data and analytics).
/// The Host calls <see cref="AddDataModule"/>; the module registers its own services here.
/// SL6-DAT-CORE builds only subject export / erasure (crypto-shred) / restriction, the bias test that suspends a model,
/// and the minimal model registry. No lakehouse, marts, accumulation or analytics.
/// </summary>
public static class DataModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "dat";

    /// <summary>Raw (bronze) reporting layer, loaded from outbox events and control-total feeds.</summary>
    public const string ReportingRawSchema = "rpt_raw";

    /// <summary>Conformed (silver) reporting layer.</summary>
    public const string ReportingConformedSchema = "rpt_conformed";

    /// <summary>Report marts (gold) used by dashboards and regulatory outputs.</summary>
    public const string ReportingMartSchema = "rpt_mart";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema, ReportingRawSchema, ReportingConformedSchema, ReportingMartSchema];

    /// <summary>The module database for the migrate job: SELECT, INSERT, UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<DataDbContext>(ModuleCode.DAT, Schema)];

    /// <summary>Registers the module's services.</summary>
    public static IServiceCollection AddDataModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<DataDbContext>(Schema);
        services.AddScoped<ModelReader>();
        services.AddScoped<SubjectFactStore>();

        services.AddScoped<IValidator<ExportSubject>, ExportSubjectValidator>();
        services.AddScoped<IValidator<ExecuteErasure>, ExecuteErasureValidator>();
        services.AddScoped<IValidator<ApplyRestriction>, ApplyRestrictionValidator>();
        services.AddScoped<IValidator<RegisterModel>, RegisterModelValidator>();
        services.AddScoped<IValidator<RunBiasTest>, RunBiasTestValidator>();

        services.AddCommand<ExportSubject, SubjectExportResponse, ExportSubjectHandler>(CommandDescriptor.For("dat.Subject.export") with { SupportsDryRun = true });
        services.AddCommand<ExecuteErasure, ErasureExecuteResponse, ExecuteErasureHandler>(CommandDescriptor.For("dat.Erasure.execute") with { SupportsDryRun = true });
        services.AddCommand<ApplyRestriction, RestrictionApplyResponse, ApplyRestrictionHandler>(CommandDescriptor.For("dat.Restriction.apply") with { SupportsDryRun = true });
        services.AddCommand<RegisterModel, ModelRegisterResponse, RegisterModelHandler>(CommandDescriptor.For("dat.Model.register") with { SupportsDryRun = true });
        services.AddCommand<RunBiasTest, BiasTestRunResponse, RunBiasTestHandler>(CommandDescriptor.For("dat.BiasTest.run") with { SupportsDryRun = true });

        services.AddScoped<DataInProcessServices>();
        services.AddScoped<IDataSubjectService>(sp => sp.GetRequiredService<DataInProcessServices>());
        services.AddScoped<IDataErasureService>(sp => sp.GetRequiredService<DataInProcessServices>());
        services.AddScoped<IDataRestrictionService>(sp => sp.GetRequiredService<DataInProcessServices>());
        services.AddScoped<IDataBiasTestService>(sp => sp.GetRequiredService<DataInProcessServices>());
        services.AddScoped<IDataModelService>(sp => sp.GetRequiredService<DataInProcessServices>());

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status and bilingual text of the DAT-ERR codes DAT-CORE raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.DAT, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει.", "The record does not exist."),
        ErrorDefinition.For(ModuleCode.DAT, "060", 422, "Το υποκείμενο βρίσκεται υπό περιορισμό", "The subject is under restriction")
            .Describe("Η διαγραφή απορρίπτεται όσο ισχύει ο περιορισμός.", "Erasure is refused while the restriction stands."),
        ErrorDefinition.For(ModuleCode.DAT, "IDEMPOTENCY-MISMATCH", 409, "Το αναγνωριστικό αιτήματος χρησιμοποιήθηκε ήδη", "The request id was already used")
            .Describe("Το αναγνωριστικό αιτήματος αφορά άλλο υποκείμενο.", "The request id belongs to another subject."),
    ];
}
