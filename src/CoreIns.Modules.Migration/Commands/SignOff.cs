using CoreIns.Modules.Migration.Services.SignOff;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>mig.Signoff.waive: waives variances of the latest reconciliation run with a reason.</summary>
internal sealed record WaiveVariances(SignOffWaiveBody Body) : ICommand<SignOffWaiveView>;

internal sealed class WaiveVariancesHandler(SignOffService service) : ICommandHandler<WaiveVariances, SignOffWaiveView>
{
    public Task<Result<SignOffWaiveView>> HandleAsync(WaiveVariances command, CancellationToken cancellationToken) => service.WaiveAsync(command.Body, cancellationToken);
}

/// <summary>mig.Signoff.request: opens a sign-off of the latest run, one approval per named signatory.</summary>
internal sealed record RequestSignOff(SignOffRequestBody Body) : ICommand<SignOffStatusView>;

internal sealed class RequestSignOffHandler(SignOffService service) : ICommandHandler<RequestSignOff, SignOffStatusView>
{
    public Task<Result<SignOffStatusView>> HandleAsync(RequestSignOff command, CancellationToken cancellationToken) => service.RequestAsync(command.Body, cancellationToken);
}

/// <summary>mig.Signoff.approve: a signatory approves.</summary>
internal sealed record ApproveSignOff(SignOffDecisionBody Body) : ICommand<SignOffStatusView>;

/// <summary>mig.Signoff.reject: a signatory rejects.</summary>
internal sealed record RejectSignOff(SignOffDecisionBody Body) : ICommand<SignOffStatusView>;

internal sealed class SignOffDecisionHandler(SignOffService service) : ICommandHandler<ApproveSignOff, SignOffStatusView>, ICommandHandler<RejectSignOff, SignOffStatusView>
{
    public Task<Result<SignOffStatusView>> HandleAsync(ApproveSignOff command, CancellationToken cancellationToken) => service.DecideAsync(command.Body, reject: false, cancellationToken);

    public Task<Result<SignOffStatusView>> HandleAsync(RejectSignOff command, CancellationToken cancellationToken) => service.DecideAsync(command.Body, reject: true, cancellationToken);
}
