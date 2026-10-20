using CoreIns.Modules.Work.Commands;
using CoreIns.Modules.Work.Contracts;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Work.Services;

/// <summary>
/// The in-process contract <see cref="IWorkActivityService"/> (D-ARC-16): other modules raise a review or hold activity (PSR, notice returned)
/// and read activities. Commands run through the same pipeline as HTTP. <c>update</c> is not built yet.
/// </summary>
internal sealed class WorkActivityService(
    RequestContext context,
    ActivityReader reader,
    ICommandHandler<CreateActivity, ActivityCreateResponse> create,
    ICommandHandler<CompleteActivity, ActivityCompleteResponse> complete) : IWorkActivityService
{
    public Task<ActivityCreateResponse> CreateAsync(ActivityCreateRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(create, new CreateActivity(request), options, cancellationToken);

    public Task<ActivityCompleteResponse> CompleteAsync(ActivityCompleteRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(complete, new CompleteActivity(request), options, cancellationToken);

    public async Task<ActivityListPage> ListAsync(string? cursor = null, int? limit = null, Guid? id = null, string? filters = null, CancellationToken cancellationToken = default) =>
        Unwrap(await reader.ListAsync(id, cursor, limit, filters, cancellationToken).ConfigureAwait(false));

    public async Task<ActivityBlockingStatusResponse> BlockingStatusAsync(string? objectRef = null, CancellationToken cancellationToken = default) =>
        Unwrap(await reader.BlockingStatusAsync(objectRef, cancellationToken).ConfigureAwait(false));

    public Task<ActivityUpdateResponse> UpdateAsync(string id, ActivityUpdateRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw new DomainException(WorkErrors.Of(WorkErrors.NotAvailable, "wrk.Activity.update is not built yet (NX-HD-05 serves create, list, get, assign, complete, skip, cancel and getNext)."));

    private async Task<T> RunAsync<TCommand, T>(ICommandHandler<TCommand, T> handler, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : ICommand<T>
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false));
        }
    }

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
}
