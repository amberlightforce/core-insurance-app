using CoreIns.Modules.Migration.Services.Staging;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Staging.load: stores a legacy extract in staging (BO-MIG-02). Nothing is imported.</summary>
internal sealed record LoadStaging(StagingLoadBody Body) : ICommand<StagingSnapshotView>;

internal sealed class LoadStagingHandler(LegacyStaging staging) : ICommandHandler<LoadStaging, StagingSnapshotView>
{
    public Task<Result<StagingSnapshotView>> HandleAsync(LoadStaging command, CancellationToken cancellationToken) => staging.LoadAsync(command.Body, cancellationToken);
}
