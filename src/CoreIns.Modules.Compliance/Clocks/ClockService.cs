using System.Text.Json;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Compliance.Clocks;

/// <summary>
/// The in-process contract <see cref="IComplianceClockService"/> (D-ARC-16) other modules call to start, stop and read
/// clocks. Each command runs the same pipeline as the REST facade; a caller's <c>IdempotencyKey</c> makes it replay-safe and
/// the clock itself is idempotent on (definition, subject, key).
/// </summary>
internal sealed class ComplianceClockService(
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    ICommandHandler<StartClock, ClockStartResponse> start,
    ICommandHandler<PauseClock, ClockPauseResponse> pause,
    ICommandHandler<ResumeClock, ClockResumeResponse> resume,
    ICommandHandler<StopClock, ClockStopResponse> stop,
    ICommandHandler<CancelClock, ClockCancelResponse> cancel,
    ICommandHandler<ExtendClock, ClockExtendResponse> extend,
    ClockReader reader) : IComplianceClockService
{
    public Task<ClockStartResponse> StartAsync(ClockStartRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(options, () => start.HandleAsync(new StartClock(request), cancellationToken));

    public Task<ClockPauseResponse> PauseAsync(ClockPauseRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(options, () => pause.HandleAsync(new PauseClock(request), cancellationToken));

    public Task<ClockResumeResponse> ResumeAsync(ClockResumeRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(options, () => resume.HandleAsync(new ResumeClock(request), cancellationToken));

    public Task<ClockStopResponse> StopAsync(ClockStopRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(options, () => stop.HandleAsync(new StopClock(request), cancellationToken));

    public Task<ClockCancelResponse> CancelAsync(ClockCancelRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(options, () => cancel.HandleAsync(new CancelClock(request), cancellationToken));

    public Task<ClockExtendResponse> ExtendAsync(ClockExtendRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(options, () => extend.HandleAsync(new ExtendClock(request), cancellationToken));

    public Task<JsonElement> ImportAsync(ClockImportRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.CMP, "NOT-AVAILABLE", "cmp.Clock.import is not built yet (SL6-MIG-CLM-RI-IMPORT)."));

    public async Task<ClockGetResponse> GetAsync(
        string id, string? subject = null, string? code = null, string? status = null, string? deadlineRange = null, CancellationToken cancellationToken = default)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        if (Guid.TryParse(id, out var guid))
        {
            var one = await reader.GetByIdAsync(legalEntity, guid, cancellationToken).ConfigureAwait(false);
            return one is null ? throw NotFound() : new ClockGetResponse { Instances = [one] };
        }

        var parsed = ClockReader.ParseSubject(subject) ?? throw NotFound();
        var page = await reader.QueryAsync(legalEntity, null, ClockReader.MaxPage, null, parsed, code, status, deadlineRange, cancellationToken).ConfigureAwait(false);
        return page.Items.Count == 0 ? throw NotFound() : new ClockGetResponse { Instances = [.. page.Items] };
    }

    public async Task<ClockQueryPage> QueryAsync(
        string? cursor = null, int? limit = null, Guid? id = null, string? subject = null, string? code = null, string? status = null, string? deadlineRange = null,
        CancellationToken cancellationToken = default)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var page = await reader.QueryAsync(legalEntity, cursor, limit, id, ClockReader.ParseSubject(subject), code, status, deadlineRange, cancellationToken).ConfigureAwait(false);
        return new ClockQueryPage { Items = [.. page.Items.Select(ClockReader.ToItem)], NextCursor = page.NextCursor, Limit = page.Limit };
    }

    private async Task<T> RunAsync<T>(CommandOptions options, Func<Task<Result<T>>> run)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await run().ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error!);
        }
    }

    private static DomainException NotFound() => new(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The clock does not exist in your legal entity."));
}

/// <summary>The in-process contract <see cref="IComplianceClockDefService"/>: the seeded definitions.</summary>
internal sealed class ComplianceClockDefService : IComplianceClockDefService
{
    public Task<ClockDefListPage> ListAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ClockDefListPage { Items = [.. ClockDefinitions.All.Select(ClockReader.ToItem)], NextCursor = null, Limit = limit });
}
