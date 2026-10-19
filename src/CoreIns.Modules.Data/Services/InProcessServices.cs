using CoreIns.Modules.Data.Commands;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Contracts;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Data.Services;

/// <summary>In-process contracts (D-ARC-16) of the DAT-CORE operations; CMP calls the DSAR ones.</summary>
internal sealed class DataInProcessServices(
    RequestContext context,
    ICommandHandler<ExportSubject, SubjectExportResponse> export,
    ICommandHandler<ExecuteErasure, ErasureExecuteResponse> erase,
    ICommandHandler<ApplyRestriction, RestrictionApplyResponse> restrict,
    ICommandHandler<RunBiasTest, BiasTestRunResponse> bias,
    ICommandHandler<RegisterModel, ModelRegisterResponse> register)
    : IDataSubjectService, IDataErasureService, IDataRestrictionService, IDataBiasTestService, IDataModelService
{
    public Task<SubjectExportResponse> ExportAsync(SubjectExportRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunCommandAsync(options, () => export.HandleAsync(new ExportSubject(request), cancellationToken));

    public Task<ErasureExecuteResponse> ExecuteAsync(ErasureExecuteRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunCommandAsync(options, () => erase.HandleAsync(new ExecuteErasure(request), cancellationToken));

    public Task<RestrictionApplyResponse> ApplyAsync(RestrictionApplyRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunCommandAsync(options, () => restrict.HandleAsync(new ApplyRestriction(request), cancellationToken));

    public Task<BiasTestRunResponse> RunAsync(BiasTestRunRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunCommandAsync(options, () => bias.HandleAsync(new RunBiasTest(request), cancellationToken));

    public Task<ModelRegisterResponse> RegisterAsync(ModelRegisterRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunCommandAsync(options, () => register.HandleAsync(new RegisterModel(request), cancellationToken));

    private async Task<T> RunCommandAsync<T>(CommandOptions options, Func<Task<Result<T>>> call)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await call().ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
        }
    }
}
