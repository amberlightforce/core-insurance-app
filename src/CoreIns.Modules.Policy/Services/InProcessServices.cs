using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Services;

/// <summary>Shared plumbing of the in-process contracts: commands run through the pipeline with the caller's options.</summary>
internal static class InProcess
{
    public static async Task<T> RunAsync<TCommand, T>(RequestContext context, ICommandHandler<TCommand, T> handler, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : ICommand<T>
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false));
        }
    }

    public static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error!);

    public static DomainException NotAvailable(string operation) =>
        new(DomainError.Of(ModuleCode.POL, "NOT-AVAILABLE", $"{operation} is not built yet (SL-POL implements submission, draft, quote, bind and the as-of reads)."));

    public static Instant Valid(ValidAt? validAt, IClock clock, PolicyOptions options) =>
        validAt is { Instant: { } instant } ? instant
        : validAt is { Date: { } date } ? PolicyTime.EndOf(date, options.Zone)
        : clock.Now;
}

/// <summary>The in-process contract <see cref="IPolicySubmissionService"/> (D-ARC-16).</summary>
internal sealed class PolicySubmissionService(RequestContext context, ICommandHandler<CreateSubmission, SubmissionCreateResponse> create) : IPolicySubmissionService
{
    public Task<SubmissionCreateResponse> CreateAsync(SubmissionCreateRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, create, new CreateSubmission(request), options, cancellationToken);
}

/// <summary>The in-process contract <see cref="IPolicyJobService"/>: updateDraft, quote and bind; the rest are POL-ERR-NOT-AVAILABLE until their work packages.</summary>
internal sealed class PolicyJobService(
    RequestContext context,
    ICommandHandler<UpdateDraft, JobUpdateDraftResponse> updateDraft,
    ICommandHandler<QuoteJob, JobQuoteResponse> quote,
    ICommandHandler<BindJob, JobBindResponse> bind,
    ICommandHandler<WithdrawJob, JobWithdrawResponse> withdraw) : IPolicyJobService
{
    public Task<JobUpdateDraftResponse> UpdateDraftAsync(JobUpdateDraftRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, updateDraft, new UpdateDraft(request), options, cancellationToken);

    public Task<JobQuoteResponse> QuoteAsync(JobQuoteRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, quote, new QuoteJob(request), options, cancellationToken);

    public Task<JobBindResponse> BindAsync(JobBindRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, bind, new BindJob(request), options, cancellationToken);

    public Task<JobListPage> ListAsync(
        string? cursor = null, int? limit = null, string? account = null, string? policy = null, string? participant = null, string? state = null,
        CancellationToken cancellationToken = default) => throw InProcess.NotAvailable("pol.Job.list");

    public Task<JobNewVersionResponse> NewVersionAsync(JobNewVersionRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("pol.Job.newVersion");

    public Task<JobWithdrawResponse> WithdrawAsync(JobWithdrawRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, withdraw, new WithdrawJob(request), options, cancellationToken);
}

/// <summary>The in-process contract <see cref="IPolicyPolicyService"/>: the bitemporal get (REQ-POL-002).</summary>
internal sealed class PolicyPolicyService(
    RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IOptions<PolicyOptions> options, PolicyReader reader, PolicySearch search)
    : IPolicyPolicyService
{
    public async Task<PolicyGetResponse> GetAsync(string id, ValidAt? validAt = null, Instant? knownAt = null, CancellationToken cancellationToken = default)
    {
        var answered = Guid.TryParse(id, out var policyId)
            ? await reader.GetPolicyEffectiveAsync(
                JobSupport.LegalEntity(context, legalEntities), context.LegalEntity!.Value.Value, policyId,
                InProcess.Valid(validAt, clock, options.Value), knownAt ?? clock.Now, cancellationToken).ConfigureAwait(false)
            : null;
        return answered is var (response, effectiveKnownAt)
            ? response with { EffectiveKnownAt = effectiveKnownAt }
            : throw new DomainException(JobSupport.NotFound("policy"));
    }

    public Task<PolicyGetManyResponse> GetManyAsync(ValidAt? validAt = null, Instant? knownAt = null, IReadOnlyList<string>? ids = null, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("pol.Policy.getMany");

    public async Task<PolicySearchPage> SearchAsync(string? cursor = null, int? limit = null, PolicyNumber? policyNumber = null, CancellationToken cancellationToken = default) =>
        InProcess.Unwrap(await search.SearchAsync(policyNumber?.Value, null, null, limit, cursor, cancellationToken).ConfigureAwait(false));

    public async Task<PolicySearchPage> SearchByCriteriaAsync(
        PolicySearchCriteria request, ValidAt? validAt = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var valid = validAt is null ? (Instant?)null : InProcess.Valid(validAt, clock, options.Value);
        return InProcess.Unwrap(await search.SearchAsync(request.PolicyNumber?.Value, request.InsuredPartyId, valid, limit, cursor, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>The in-process contract <see cref="IPolicySnapshotService"/>: the claims snapshot at a loss date (REQ-POL-007).</summary>
internal sealed class PolicySnapshotService(IClock clock, IOptions<PolicyOptions> options, PolicySnapshots snapshots) : IPolicySnapshotService
{
    public async Task<SnapshotGetResponse> GetAsync(
        ValidAt? validAt = null, Instant? knownAt = null, PolicyId? policyId = null, string? snapshotRef = null, PolicyNumber? policyNumber = null,
        CancellationToken cancellationToken = default) =>
        InProcess.Unwrap(await snapshots.GetAsync(
            new SnapshotQuery(policyId?.Value, policyNumber?.Value, snapshotRef, validAt is null ? null : InProcess.Valid(validAt, clock, options.Value), knownAt),
            cancellationToken).ConfigureAwait(false));
}

/// <summary>The in-process contract <see cref="IPolicyTermService"/>: the bitemporal term get (REQ-POL-002).</summary>
internal sealed class PolicyTermService(RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IOptions<PolicyOptions> options, PolicyReader reader)
    : IPolicyTermService
{
    public async Task<TermGetResponse> GetAsync(string id, ValidAt? validAt = null, Instant? knownAt = null, CancellationToken cancellationToken = default)
    {
        var response = Guid.TryParse(id, out var termId)
            ? await reader.GetTermAsync(
                JobSupport.LegalEntity(context, legalEntities), context.LegalEntity!.Value.Value, termId,
                InProcess.Valid(validAt, clock, options.Value), knownAt ?? clock.Now, cancellationToken).ConfigureAwait(false)
            : null;
        return response ?? throw new DomainException(JobSupport.NotFound("term"));
    }

    public async Task<TermTimelineResponse> TimelineAsync(
        PolicyId policyId, ValidAt? validAt = null, Instant? knownAt = null, PolicyTermId? termId = null, CancellationToken cancellationToken = default) =>
        await reader.TimelineAsync(
            JobSupport.LegalEntity(context, legalEntities), context.LegalEntity!.Value.Value, policyId.Value, termId?.Value,
            InProcess.Valid(validAt, clock, options.Value), knownAt ?? clock.Now, cancellationToken).ConfigureAwait(false)
        ?? throw new DomainException(JobSupport.NotFound("term"));
}
