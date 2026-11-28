using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Commands.Renewal;
using CoreIns.Modules.Policy.Commands.Withdrawal;
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

/// <summary>
/// The in-process contract <see cref="IPolicyJobService"/>: updateDraft, quote, bind and get (UW's referral workbench); the
/// rest are POL-ERR-NOT-AVAILABLE until their work packages.
/// </summary>
internal sealed class PolicyJobService(
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    JobReader reader,
    ICommandHandler<UpdateDraft, JobUpdateDraftResponse> updateDraft,
    ICommandHandler<QuoteJob, JobQuoteResponse> quote,
    ICommandHandler<BindJob, JobBindResponse> bind,
    ICommandHandler<WithdrawJob, JobWithdrawResponse> withdraw,
    ICommandHandler<CoreIns.Modules.Policy.Commands.Quotes.NewVersion, JobNewVersionResponse> newVersion) : IPolicyJobService
{
    /// <summary>pol.Job.get in the caller's legal entity (another entity's job is POL-ERR-NOT-FOUND); the list filters are not built.</summary>
    public async Task<JobGetResponse> GetAsync(
        string id, string? account = null, string? policy = null, string? participant = null, string? state = null, CancellationToken cancellationToken = default)
    {
        if (account is not null || policy is not null || participant is not null || state is not null)
        {
            throw InProcess.NotAvailable("pol.Job.get with account, policy, participant or state filters");
        }

        var view = Guid.TryParse(id, out var jobId)
            ? await reader.GetAsync(JobSupport.LegalEntity(context, legalEntities), jobId, cancellationToken).ConfigureAwait(false)
            : null;
        return view is null ? throw new DomainException(JobSupport.NotFound("job")) : new JobGetResponse { Job = view };
    }

    public Task<JobUpdateDraftResponse> UpdateDraftAsync(JobUpdateDraftRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, updateDraft, new UpdateDraft(request), options, cancellationToken);

    public Task<JobQuoteResponse> QuoteAsync(JobQuoteRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, quote, new QuoteJob(request), options, cancellationToken);

    public Task<JobBindResponse> BindAsync(JobBindRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, bind, new BindJob(request), options, cancellationToken);

    /// <summary>
    /// pol.Job.list for ONE policy (the <c>policy</c> filter, required) and optionally one state: each item's <c>jobs</c> is a slim
    /// <c>{jobId, jobType, state, createdAt}</c> object, newest first. Other filters and paging are not built.
    /// TODO(hardening): account / participant filters, cursor paging, typed items.
    /// </summary>
    public async Task<JobListPage> ListAsync(
        string? cursor = null, int? limit = null, string? account = null, string? policy = null, string? participant = null, string? state = null, string? partyId = null, string? producerCode = null, string? productCode = null, string? jobType = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is not null || account is not null || participant is not null || !Guid.TryParse(policy, out var policyId))
        {
            throw InProcess.NotAvailable("pol.Job.list without the policy filter (or with cursor, account or participant)");
        }

        var rows = await reader.ListForPolicyAsync(JobSupport.LegalEntity(context, legalEntities), policyId, state, cancellationToken).ConfigureAwait(false);
        var items = rows.Take(Math.Clamp(limit ?? 50, 1, 50)).Select(r => new JobListItem
        {
            Jobs = System.Text.Json.JsonSerializer.SerializeToElement(new { jobId = r.JobId, jobType = r.JobType, state = r.State, createdAt = r.CreatedAt }),
        }).ToList();
        return new JobListPage { Items = items, NextCursor = null, Limit = items.Count };
    }

    public Task<JobNewVersionResponse> NewVersionAsync(JobNewVersionRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, newVersion, new CoreIns.Modules.Policy.Commands.Quotes.NewVersion(request), options, cancellationToken);

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

/// <summary>The in-process contract <see cref="IPolicyWithdrawalService"/> (SL6-POL-WITHDRAW): CHN declares a withdrawal, POL voids the term.</summary>
internal sealed class PolicyWithdrawalService(RequestContext context, ICommandHandler<SubmitWithdrawal, WithdrawalSubmitResponse> submit) : IPolicyWithdrawalService
{
    public Task<WithdrawalSubmitResponse> SubmitAsync(WithdrawalSubmitRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, submit, new SubmitWithdrawal(request), options, cancellationToken);
}

/// <summary>
/// The in-process contract <see cref="IPolicyPolicyChangeService"/> (BO-CHN-03): CHN starts a change job for a portal customer. The caller checks
/// the transaction-permission matrix and ownership; the command runs through the same pipeline as HTTP.
/// </summary>
internal sealed class PolicyPolicyChangeService(RequestContext context, ICommandHandler<CreatePolicyChange, PolicyChangeCreateResponse> create) : IPolicyPolicyChangeService
{
    public Task<PolicyChangeCreateResponse> CreateAsync(PolicyChangeCreateRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, create, new CreatePolicyChange(request), options, cancellationToken);
}

/// <summary>
/// The in-process contract <see cref="IPolicyRenewalService"/> (BO-CHN-03): CHN accepts or declines an offered renewal for a portal customer.
/// Create, offer and non-renew stay with the batch and staff paths.
/// </summary>
internal sealed class PolicyRenewalService(
    RequestContext context, ICommandHandler<AcceptRenewal, RenewalAcceptResponse> accept, ICommandHandler<DeclineRenewal, RenewalDeclineResponse> decline) : IPolicyRenewalService
{
    public Task<RenewalAcceptResponse> AcceptAsync(RenewalAcceptRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, accept, new AcceptRenewal(request), options, cancellationToken);

    public Task<RenewalDeclineResponse> DeclineAsync(RenewalDeclineRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, decline, new DeclineRenewal(request), options, cancellationToken);

    public Task<RenewalCreateResponse> CreateAsync(RenewalCreateRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("pol.Renewal.create");

    public Task<RenewalNonRenewResponse> NonRenewAsync(RenewalNonRenewRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("pol.Renewal.nonRenew");

    public Task<RenewalOfferResponse> OfferAsync(RenewalOfferRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("pol.Renewal.offer");
}
