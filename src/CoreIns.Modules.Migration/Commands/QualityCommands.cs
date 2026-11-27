using CoreIns.Modules.Migration.Services.Quality;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Profile.run: profiles a staged snapshot and runs the DQ rule library over it (BO-MIG-03). Staging is only read.</summary>
internal sealed record RunProfile(ProfileRunBody Body) : ICommand<DqRunView>;

internal sealed class RunProfileHandler(DataQualityService service) : ICommandHandler<RunProfile, DqRunView>
{
    public Task<Result<DqRunView>> HandleAsync(RunProfile command, CancellationToken cancellationToken) => service.RunAsync(command.Body, cancellationToken);
}

/// <summary>mig.Dq.waive: a data steward waives one finding until an expiry date.</summary>
internal sealed record WaiveFinding(DqWaiveBody Body) : ICommand<DqWaiverView>;

internal sealed class WaiveFindingHandler(DataQualityService service) : ICommandHandler<WaiveFinding, DqWaiverView>
{
    public Task<Result<DqWaiverView>> HandleAsync(WaiveFinding command, CancellationToken cancellationToken) => service.WaiveAsync(command.Body, cancellationToken);
}
