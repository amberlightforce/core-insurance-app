using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Channels.Ai;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Channels.Services;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PolicyApi = CoreIns.Modules.Policy.Contracts.Api;

namespace CoreIns.Modules.Channels.Commands;

/// <summary>
/// Shared plumbing of the customer self-service commands (BO-CHN-03): the matrix answer, the ownership rule, referral tickets and the keys of the
/// nested owner-module calls. Every command here starts with the caller's portal scope (no link: 404), checks the matrix before it does anything
/// (DENY: 403 CHN-ERR-NOT-PERMITTED; REFER: a ticket is raised and the action is NOT performed) and only then calls the owner in-process.
/// </summary>
internal sealed class PortalServicing(
    PortalScope scope, PermissionMatrixService matrix, TicketService tickets, RequestContext context, IServiceProvider services, IOptions<ChannelsOptions> options)
{
    public const string Channel = "WEB_DIRECT";

    public ChannelsOptions Options => options.Value;

    public Task<PortalCaller?> CallerAsync(CancellationToken cancellationToken) => scope.ResolveAsync(cancellationToken);

    public Task<bool> OwnsPolicyAsync(PolicyId policy, CancellationToken cancellationToken) => scope.OwnsPolicyAsync(policy, cancellationToken);

    public T Service<T>() where T : class => services.GetService<T>() ?? throw new DomainException(DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "Not found."));

    /// <summary>The matrix decision for this customer action. A matrix that cannot answer is a DENY (fail closed).</summary>
    public async Task<PermissionAnswer> EvaluateAsync(string transaction, decimal? amount, JsonElement? objectRef, bool raiseTicket, CancellationToken cancellationToken)
    {
        var answer = await matrix.EvaluateAsync(new PermissionCheck(transaction, Channel, null, null, amount, objectRef, raiseTicket), cancellationToken).ConfigureAwait(false);
        return answer.IsSuccess ? answer.Value : new PermissionAnswer(new MatrixDecision(MatrixOutcomes.Deny, null, null, ["MATRIX_UNAVAILABLE"], "chn.permission.noEntry"), null, null);
    }

    /// <summary>A referral ticket for a customer request that staff must handle (no matrix involved).</summary>
    public async Task<Guid> RaiseTicketAsync(string tool, object arguments, CancellationToken cancellationToken) =>
        (await tickets.CreatePendingAsync(tool, JsonSerializer.SerializeToElement(arguments), null, cancellationToken).ConfigureAwait(false)).TicketId;

    /// <summary>A deterministic key per outer request and step: a replay of the same request repeats the same owner calls, which POL and PTY replay.</summary>
    public CommandOptions Step(string step)
    {
        var outer = (context.IdempotencyKey ?? IdempotencyKey.New()).ToString();
        var key = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("chn-portal:" + outer + ":" + step))[..16]);
        return new CommandOptions(IdempotencyKey.From(key));
    }

    /// <summary>
    /// True for a customer who is flagged vulnerable or whose flag cannot be read (unknown = vulnerable, NX-HD-02): irreversible requests are then
    /// referred to staff instead of being performed online.
    /// </summary>
    public async Task<bool> IsVulnerableAsync(PartyId party, CancellationToken cancellationToken)
    {
        if (services.GetService<IPartyVulnerabilityService>() is not { } vulnerability)
        {
            return true;
        }

        try
        {
            var page = await vulnerability.QueryAsync(party, null, 10, cancellationToken).ConfigureAwait(false);
            return page.Items.Any(i => i.Flag);
        }
        catch (DomainException)
        {
            return true;
        }
    }

    public static DomainError NotFound() => PortalEngagementSupport.NotFound();

    public static DomainError NotPermitted() => PortalEngagementSupport.NotPermitted();

    public static DomainError Invalid(string message) => DomainError.Of(ModuleCode.CHN, "NOT-PERMITTED", message);

    public static string Reasons(MatrixDecision decision) => string.Join(",", decision.Reasons);
}

// ---- chn.Change.preview ----------------------------------------------------------------------------------------------------------

internal sealed record PreviewChange(ChangePreviewRequest Request) : ICommand<ChangePreviewResponse>;

internal sealed class PreviewChangeValidator : AbstractValidator<PreviewChange>
{
    public PreviewChangeValidator()
    {
        RuleFor(c => c.Request.Instructions).NotEmpty().Must(i => i.Count <= 20).WithMessage("Give 1-20 instructions.");
    }
}

/// <summary>
/// Starts a POL change job for one of the caller's own policies, edits the draft, quotes it and records what was previewed. Nothing is bound.
/// The matrix is asked twice: first without an amount (DENY stops here, before any job exists), then with the quoted total change, which decides
/// whether the customer may confirm (ALLOW) or staff must (REFER).
/// </summary>
internal sealed class PreviewChangeHandler(PortalServicing portal, ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<PreviewChange, ChangePreviewResponse>
{
    public async Task<Result<ChangePreviewResponse>> HandleAsync(PreviewChange command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (await portal.CallerAsync(cancellationToken).ConfigureAwait(false) is not { } caller || !await portal.OwnsPolicyAsync(request.PolicyId, cancellationToken).ConfigureAwait(false))
        {
            return PortalServicing.NotFound();
        }

        var objectRef = JsonSerializer.SerializeToElement(new { policyId = request.PolicyId.Value.ToString("D") });
        var first = await portal.EvaluateAsync("policy.change", null, objectRef, false, cancellationToken).ConfigureAwait(false);
        if (first.Decision.Outcome == MatrixOutcomes.Deny)
        {
            return PortalServicing.NotPermitted();
        }

        var draft = new List<PolicyApi.DraftInstruction>();
        foreach (var instruction in request.Instructions)
        {
            // The same member names and ops as POL's own instruction: the customer can send nothing POL would not take from staff.
            if (JsonSerializer.Deserialize<PolicyApi.DraftInstruction>(JsonSerializer.Serialize(instruction)) is not { } mapped)
            {
                return PortalServicing.Invalid("The instruction is not valid.");
            }

            draft.Add(mapped);
        }

        try
        {
            var policies = portal.Service<IPolicyPolicyService>();
            var policy = await policies.GetAsync(request.PolicyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
            var now = clock.Now;
            var zone = portal.Options.Zone;
            var effectiveAt = request.EffectiveDate is { } date ? date.StartOfDayIn(zone) : now;
            if (effectiveAt < now)
            {
                effectiveAt = now;
            }

            var created = await portal.Service<IPolicyPolicyChangeService>().CreateAsync(
                new PolicyApi.PolicyChangeCreateRequest { PolicyId = request.PolicyId, EffectiveAt = effectiveAt, Description = "Customer self-service change (portal)" },
                portal.Step("create"), cancellationToken).ConfigureAwait(false);
            var jobs = portal.Service<IPolicyJobService>();
            var updated = await jobs.UpdateDraftAsync(
                new PolicyApi.JobUpdateDraftRequest { JobId = created.JobId, VersionNo = 1, ExpectedDraftVersion = 0, Instructions = draft }, portal.Step("draft"), cancellationToken)
                .ConfigureAwait(false);
            var quote = await jobs.QuoteAsync(new PolicyApi.JobQuoteRequest { JobId = created.JobId, VersionNo = updated.VersionNo }, portal.Step("quote"), cancellationToken).ConfigureAwait(false);

            var total = quote.ServicingPreview?.TotalChange ?? Money.Of(0m, quote.Total.Currency.Code);
            var amount = Math.Abs(total.Amount);
            var second = await portal.EvaluateAsync("policy.change", amount, objectRef, false, cancellationToken).ConfigureAwait(false);
            if (second.Decision.Outcome == MatrixOutcomes.Deny)
            {
                return PortalServicing.NotPermitted();
            }

            var reasons = second.Decision.Reasons.ToList();
            var refer = second.Decision.Outcome == MatrixOutcomes.Refer;
            if (quote.Referred || !quote.Bindable)
            {
                refer = true;
                reasons.Add(quote.Referred ? "UNDERWRITING_REFERRAL" : "NOT_BINDABLE");
            }

            var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
            var row = new PortalChangeRow
            {
                ChangeId = Guid.CreateVersion7(), LegalEntityId = entity, PartyId = caller.PartyId, PolicyId = request.PolicyId, JobId = created.JobId, VersionNo = quote.VersionNo,
                EffectiveAt = effectiveAt, Amount = amount, Currency = total.Currency.Code, PaymentPlanOption = policy.Term?.PaymentPlanRef ?? string.Empty,
                Status = PortalChangeStatuses.Previewed, CreatedBy = context.Actor.ToString(), CreatedAt = now, UpdatedAt = now,
            };
            db.PortalChanges.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var servicing = quote.ServicingPreview;
            return new ChangePreviewResponse
            {
                ChangeId = row.ChangeId, PolicyId = row.PolicyId, JobId = row.JobId, VersionNo = row.VersionNo, EffectiveAt = effectiveAt,
                Outcome = refer ? ChangePreviewResponse.OutcomeValue.Refer : ChangePreviewResponse.OutcomeValue.Allow, Reasons = reasons,
                AnnualBefore = servicing?.AnnualBefore, AnnualAfter = servicing?.AnnualAfter, PremiumChange = servicing?.PremiumChange ?? quote.Premium,
                TaxChange = servicing?.TaxChange ?? quote.Taxes, TotalChange = total, AdditionalDue = servicing?.AdditionalDue, RefundDue = servicing?.RefundDue, ValidUntil = quote.ValidUntil,
            };
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class PreviewChangeAuditor : ICommandAuditor<PreviewChange, ChangePreviewResponse>
{
    public CommandAuditFacts Describe(PreviewChange command, Result<ChangePreviewResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("policyId", command.Request.PolicyId.Value.ToString("D")) };
}

// ---- chn.Change.confirm ----------------------------------------------------------------------------------------------------------

internal sealed record ConfirmChange(ChangeConfirmRequest Request) : ICommand<ChangeConfirmResponse>;

internal sealed class ConfirmChangeValidator : AbstractValidator<ConfirmChange>
{
    public ConfirmChangeValidator()
    {
        RuleFor(c => c.Request.ChangeId).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.Acknowledged).Equal(true).WithMessage("The customer must acknowledge the previewed change.");
    }
}

/// <summary>
/// Binds exactly the previewed quote version, after the matrix is asked again (it may have changed since the preview) with the previewed amount.
/// REFER binds nothing: a ticket is raised instead. A second confirm of a confirmed or referred change returns the first result.
/// </summary>
internal sealed class ConfirmChangeHandler(PortalServicing portal, ChannelsDbContext db, ILegalEntityDirectory legalEntities, RequestContext context, IClock clock)
    : ICommandHandler<ConfirmChange, ChangeConfirmResponse>
{
    public async Task<Result<ChangeConfirmResponse>> HandleAsync(ConfirmChange command, CancellationToken cancellationToken)
    {
        if (await portal.CallerAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return PortalServicing.NotFound();
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var id = command.Request.ChangeId;
        var row = await db.PortalChanges.SingleOrDefaultAsync(c => c.LegalEntityId == entity && c.ChangeId == id && c.PartyId == caller.PartyId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await portal.OwnsPolicyAsync(row.PolicyId, cancellationToken).ConfigureAwait(false))
        {
            return PortalServicing.NotFound();
        }

        if (row.Status == PortalChangeStatuses.Confirmed)
        {
            return Result(row, ChangeConfirmResponse.OutcomeValue.Confirmed);
        }

        if (row.Status == PortalChangeStatuses.Referred)
        {
            return Result(row, ChangeConfirmResponse.OutcomeValue.Referred);
        }

        var objectRef = JsonSerializer.SerializeToElement(new { policyId = row.PolicyId.Value.ToString("D"), changeId = row.ChangeId.ToString("D") });
        var answer = await portal.EvaluateAsync("policy.change", row.Amount, objectRef, true, cancellationToken).ConfigureAwait(false);
        if (answer.Decision.Outcome == MatrixOutcomes.Deny)
        {
            return PortalServicing.NotPermitted();
        }

        var now = clock.Now;
        if (answer.Decision.Outcome == MatrixOutcomes.Refer)
        {
            row.Status = PortalChangeStatuses.Referred;
            row.TicketId = answer.TicketId ?? await portal.RaiseTicketAsync("portal.change.refer", new { changeId = row.ChangeId, policyId = row.PolicyId.Value, jobId = row.JobId.Value }, cancellationToken).ConfigureAwait(false);
            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result(row, ChangeConfirmResponse.OutcomeValue.Referred);
        }

        try
        {
            var bound = await portal.Service<IPolicyJobService>().BindAsync(
                new PolicyApi.JobBindRequest { JobId = row.JobId, VersionNo = row.VersionNo, PaymentPlanOption = row.PaymentPlanOption, Confirmation = true },
                portal.Step("bind"), cancellationToken).ConfigureAwait(false);
            row.Status = PortalChangeStatuses.Confirmed;
            row.TransactionId = bound.TransactionId?.Value;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result(row, ChangeConfirmResponse.OutcomeValue.Confirmed);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    private static ChangeConfirmResponse Result(PortalChangeRow row, ChangeConfirmResponse.OutcomeValue outcome) => new()
    {
        ChangeId = row.ChangeId, PolicyId = row.PolicyId, Outcome = outcome, TicketId = row.TicketId, TransactionId = row.TransactionId is { } t ? new PolicyTransactionId(t) : null,
        EffectiveAt = row.EffectiveAt, TotalChange = Money.Of(row.Amount, row.Currency),
    };
}

internal sealed class ConfirmChangeAuditor : ICommandAuditor<ConfirmChange, ChangeConfirmResponse>
{
    public CommandAuditFacts Describe(ConfirmChange command, Result<ChangeConfirmResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("changeId", command.Request.ChangeId.ToString("D")) };
}

// ---- chn.Change.refer ------------------------------------------------------------------------------------------------------------

internal sealed record ReferChange(ChangeReferRequest Request) : ICommand<ChangeReferResponse>;

/// <summary>The customer hands a previewed change to staff: a ticket is raised and the customer cannot bind it themselves any more.</summary>
internal sealed class ReferChangeHandler(PortalServicing portal, ChannelsDbContext db, ILegalEntityDirectory legalEntities, RequestContext context, IClock clock)
    : ICommandHandler<ReferChange, ChangeReferResponse>
{
    public async Task<Result<ChangeReferResponse>> HandleAsync(ReferChange command, CancellationToken cancellationToken)
    {
        if (await portal.CallerAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return PortalServicing.NotFound();
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await db.PortalChanges.SingleOrDefaultAsync(c => c.LegalEntityId == entity && c.ChangeId == command.Request.ChangeId && c.PartyId == caller.PartyId, cancellationToken).ConfigureAwait(false);
        if (row is null || !await portal.OwnsPolicyAsync(row.PolicyId, cancellationToken).ConfigureAwait(false))
        {
            return PortalServicing.NotFound();
        }

        if (row.Status == PortalChangeStatuses.Confirmed)
        {
            return DomainError.Of(ModuleCode.CHN, "WRONG-STATE", "The change was already confirmed.");
        }

        if (row.Status == PortalChangeStatuses.Previewed)
        {
            row.TicketId = await portal.RaiseTicketAsync("portal.change.refer", new { changeId = row.ChangeId, policyId = row.PolicyId.Value, jobId = row.JobId.Value }, cancellationToken).ConfigureAwait(false);
            row.Status = PortalChangeStatuses.Referred;
            row.UpdatedAt = clock.Now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ChangeReferResponse { ChangeId = row.ChangeId, PolicyId = row.PolicyId, Outcome = ChangeReferResponse.OutcomeValue.Referred, TicketId = row.TicketId };
    }
}

internal sealed class ReferChangeAuditor : ICommandAuditor<ReferChange, ChangeReferResponse>
{
    public CommandAuditFacts Describe(ReferChange command, Result<ChangeReferResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("changeId", command.Request.ChangeId.ToString("D")) };
}

// ---- chn.PortalRenewal.accept / decline ------------------------------------------------------------------------------------------

internal sealed record AcceptPortalRenewal(PortalRenewalAcceptRequest Request) : ICommand<PortalRenewalAcceptResponse>;

internal sealed class AcceptPortalRenewalValidator : AbstractValidator<AcceptPortalRenewal>
{
    public AcceptPortalRenewalValidator() => RuleFor(c => c.Request.Acknowledged).Equal(true).WithMessage("The customer must acknowledge the renewal terms.");
}

/// <summary>Accepts the offered renewal job of an own policy (matrix policy.renewal.accept). The expiring term is the policy's current term.</summary>
internal sealed class AcceptPortalRenewalHandler(PortalServicing portal, IClock clock) : ICommandHandler<AcceptPortalRenewal, PortalRenewalAcceptResponse>
{
    public async Task<Result<PortalRenewalAcceptResponse>> HandleAsync(AcceptPortalRenewal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (await portal.CallerAsync(cancellationToken).ConfigureAwait(false) is null || !await portal.OwnsPolicyAsync(request.PolicyId, cancellationToken).ConfigureAwait(false))
        {
            return PortalServicing.NotFound();
        }

        var objectRef = JsonSerializer.SerializeToElement(new { policyId = request.PolicyId.Value.ToString("D"), jobId = request.JobId.Value.ToString("D") });
        var answer = await portal.EvaluateAsync("policy.renewal.accept", null, objectRef, true, cancellationToken).ConfigureAwait(false);
        if (answer.Decision.Outcome == MatrixOutcomes.Deny)
        {
            return PortalServicing.NotPermitted();
        }

        if (answer.Decision.Outcome == MatrixOutcomes.Refer)
        {
            return new PortalRenewalAcceptResponse { PolicyId = request.PolicyId, JobId = request.JobId, Outcome = PortalRenewalAcceptResponse.OutcomeValue.Referred, TicketId = answer.TicketId };
        }

        try
        {
            if (await RenewalTermAsync(portal, request.PolicyId, request.JobId, cancellationToken).ConfigureAwait(false) is not { } termId)
            {
                return PortalServicing.NotFound();
            }

            var accepted = await portal.Service<IPolicyRenewalService>().AcceptAsync(
                new PolicyApi.RenewalAcceptRequest { JobId = request.JobId, TermId = termId, Channel = PortalServicing.Channel, AcceptedAt = clock.Now, AcceptanceEvidence = "portal:renewal-accept" },
                portal.Step("accept"), cancellationToken).ConfigureAwait(false);
            return new PortalRenewalAcceptResponse
            {
                PolicyId = request.PolicyId, JobId = request.JobId, Outcome = PortalRenewalAcceptResponse.OutcomeValue.Accepted, NewTermId = accepted.NewTermId, NewTermNumber = accepted.NewTermNumber,
            };
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    /// <summary>The job must be a renewal job of this policy (never trust a job id from the client); the expiring term is the current term.</summary>
    internal static async Task<PolicyTermId?> RenewalTermAsync(PortalServicing portal, PolicyId policyId, JobId jobId, CancellationToken cancellationToken)
    {
        var job = (await portal.Service<IPolicyJobService>().GetAsync(jobId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Job;
        if (job.PolicyId != policyId || !string.Equals(job.JobType, "RENEWAL", StringComparison.Ordinal))
        {
            return null;
        }

        var policy = await portal.Service<IPolicyPolicyService>().GetAsync(policyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
        return policy.Term?.TermId;
    }
}

internal sealed class AcceptPortalRenewalAuditor : ICommandAuditor<AcceptPortalRenewal, PortalRenewalAcceptResponse>
{
    public CommandAuditFacts Describe(AcceptPortalRenewal command, Result<PortalRenewalAcceptResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("policyId", command.Request.PolicyId.Value.ToString("D")).With("jobId", command.Request.JobId.Value.ToString("D")) };
}

internal sealed record DeclinePortalRenewal(PortalRenewalDeclineRequest Request) : ICommand<PortalRenewalDeclineResponse>;

/// <summary>
/// Declines the offered renewal (matrix policy.renewal.decline). A customer flagged vulnerable, or whose flag cannot be read, is never declined
/// online: a ticket asks staff to call them (NX-HD-02). Nothing is performed when the answer is REFER.
/// </summary>
internal sealed class DeclinePortalRenewalHandler(PortalServicing portal, IClock clock) : ICommandHandler<DeclinePortalRenewal, PortalRenewalDeclineResponse>
{
    public async Task<Result<PortalRenewalDeclineResponse>> HandleAsync(DeclinePortalRenewal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (await portal.CallerAsync(cancellationToken).ConfigureAwait(false) is not { } caller || !await portal.OwnsPolicyAsync(request.PolicyId, cancellationToken).ConfigureAwait(false))
        {
            return PortalServicing.NotFound();
        }

        PolicyTermId? expiring;
        try
        {
            expiring = await AcceptPortalRenewalHandler.RenewalTermAsync(portal, request.PolicyId, request.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (expiring is not { } termId)
        {
            return PortalServicing.NotFound();
        }

        var objectRef = JsonSerializer.SerializeToElement(new { policyId = request.PolicyId.Value.ToString("D"), jobId = request.JobId.Value.ToString("D") });
        var vulnerable = await portal.IsVulnerableAsync(caller.PartyId, cancellationToken).ConfigureAwait(false);
        var answer = await portal.EvaluateAsync("policy.renewal.decline", null, objectRef, !vulnerable, cancellationToken).ConfigureAwait(false);
        if (answer.Decision.Outcome == MatrixOutcomes.Deny)
        {
            return PortalServicing.NotPermitted();
        }

        if (vulnerable || answer.Decision.Outcome == MatrixOutcomes.Refer)
        {
            var ticket = answer.TicketId ?? await portal.RaiseTicketAsync("portal.renewal.decline", new { policyId = request.PolicyId.Value, jobId = request.JobId.Value, reasonCode = request.ReasonCode.ToString() }, cancellationToken).ConfigureAwait(false);
            return new PortalRenewalDeclineResponse { PolicyId = request.PolicyId, JobId = request.JobId, Outcome = PortalRenewalDeclineResponse.OutcomeValue.Referred, TicketId = ticket };
        }

        try
        {
            await portal.Service<IPolicyRenewalService>().DeclineAsync(
                new PolicyApi.RenewalDeclineRequest { JobId = request.JobId, TermId = termId, ReasonCode = request.ReasonCode.ToString().ToUpperInvariant(), DeclinedAt = clock.Now, Evidence = "portal:renewal-decline" },
                portal.Step("decline"), cancellationToken).ConfigureAwait(false);
            return new PortalRenewalDeclineResponse { PolicyId = request.PolicyId, JobId = request.JobId, Outcome = PortalRenewalDeclineResponse.OutcomeValue.Declined };
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class DeclinePortalRenewalAuditor : ICommandAuditor<DeclinePortalRenewal, PortalRenewalDeclineResponse>
{
    public CommandAuditFacts Describe(DeclinePortalRenewal command, Result<PortalRenewalDeclineResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("policyId", command.Request.PolicyId.Value.ToString("D")).With("jobId", command.Request.JobId.Value.ToString("D")) };
}

// ---- chn.PortalCancellation.submit -----------------------------------------------------------------------------------------------

internal sealed record RequestPortalCancellation(PortalCancellationSubmitRequest Request) : ICommand<PortalCancellationSubmitResponse>;

/// <summary>
/// A request to cancel an own policy (matrix policy.cancel). It never cancels: staff are given a ticket and act on it. DENY refuses; ALLOW and
/// REFER both raise the request. TODO(hardening): self-serve cancellation through POL when the matrix says ALLOW and the customer is not vulnerable.
/// </summary>
internal sealed class RequestPortalCancellationHandler(PortalServicing portal) : ICommandHandler<RequestPortalCancellation, PortalCancellationSubmitResponse>
{
    public async Task<Result<PortalCancellationSubmitResponse>> HandleAsync(RequestPortalCancellation command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (await portal.CallerAsync(cancellationToken).ConfigureAwait(false) is null || !await portal.OwnsPolicyAsync(request.PolicyId, cancellationToken).ConfigureAwait(false))
        {
            return PortalServicing.NotFound();
        }

        var objectRef = JsonSerializer.SerializeToElement(new { policyId = request.PolicyId.Value.ToString("D") });
        var answer = await portal.EvaluateAsync("policy.cancel", null, objectRef, true, cancellationToken).ConfigureAwait(false);
        if (answer.Decision.Outcome == MatrixOutcomes.Deny)
        {
            return PortalServicing.NotPermitted();
        }

        var ticket = answer.TicketId ?? await portal.RaiseTicketAsync(
            "portal.cancellation.request",
            new { policyId = request.PolicyId.Value, effectiveDate = request.EffectiveDate?.ToString(), reasonCode = request.ReasonCode.ToString() }, cancellationToken).ConfigureAwait(false);
        return new PortalCancellationSubmitResponse { PolicyId = request.PolicyId, Outcome = PortalCancellationSubmitResponse.OutcomeValue.Requested, TicketId = ticket };
    }
}

internal sealed class RequestPortalCancellationAuditor : ICommandAuditor<RequestPortalCancellation, PortalCancellationSubmitResponse>
{
    public CommandAuditFacts Describe(RequestPortalCancellation command, Result<PortalCancellationSubmitResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("policyId", command.Request.PolicyId.Value.ToString("D")) };
}
