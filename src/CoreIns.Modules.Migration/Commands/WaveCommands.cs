using CoreIns.Modules.Migration.Services.Waves;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Wave.plan: selects the legacy policy terms of a renewal window from staging (BO-MIG-07).</summary>
internal sealed record PlanWave(WavePlanBody Body) : ICommand<WaveView>;

/// <summary>mig.Wave.convert: maps the planned terms through an approved spec into a DRAFT batch.</summary>
internal sealed record ConvertWave(WaveConvertBody Body) : ICommand<WaveView>;

/// <summary>mig.Wave.adjust: excludes or withdraws a term.</summary>
internal sealed record AdjustWave(WaveAdjustBody Body) : ICommand<WaveView>;

/// <summary>mig.Wave.close: closes the wave when the count equation holds and the sign-off is ready.</summary>
internal sealed record CloseWave(WaveCloseBody Body) : ICommand<WaveView>;

internal sealed class WaveCommandHandler(WaveService service)
    : ICommandHandler<PlanWave, WaveView>, ICommandHandler<ConvertWave, WaveView>, ICommandHandler<AdjustWave, WaveView>, ICommandHandler<CloseWave, WaveView>
{
    public Task<Result<WaveView>> HandleAsync(PlanWave command, CancellationToken cancellationToken) => service.PlanAsync(command.Body, cancellationToken);

    public Task<Result<WaveView>> HandleAsync(ConvertWave command, CancellationToken cancellationToken) => service.ConvertAsync(command.Body, cancellationToken);

    public Task<Result<WaveView>> HandleAsync(AdjustWave command, CancellationToken cancellationToken) => service.AdjustAsync(command.Body, cancellationToken);

    public Task<Result<WaveView>> HandleAsync(CloseWave command, CancellationToken cancellationToken) => service.CloseAsync(command.Body, cancellationToken);
}
