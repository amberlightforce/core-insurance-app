using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CancellationKind = CoreIns.Modules.Policy.Contracts.Api.CancellationKind;

namespace CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;

/// <summary>Why a non-payment cancellation goes to the PSR queue instead of running (<c>pol.psr_exception.reason</c>).</summary>
internal static class PsrReasons
{
    public const string Vulnerable = "VULNERABLE_CUSTOMER";

    public const string Hold = "HOLD";

    public const string NoticeMissing = "NOTICE_REFERENCE_MISSING";
}

/// <summary>Statuses of <c>pol.nonpay_cancellation</c>.</summary>
internal static class NonPaymentStatus
{
    public const string Scheduled = "SCHEDULED";

    public const string Executed = "EXECUTED";

    public const string Exception = "EXCEPTION";

    public const string Rescinded = "RESCINDED";

    public const string Held = "HELD";
}

/// <summary>
/// Is the policyholder a vulnerable customer? Read through the party service (PTY, REQ-PTY vulnerability flag); a flag that is absent, or a
/// party service that is not wired, means not vulnerable (D-SL6-04).
/// </summary>
internal interface IVulnerabilityCheck
{
    Task<bool> IsVulnerableAsync(PartyId party, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IVulnerabilityCheck"/>. PTY's <c>pty.Vulnerability.query</c> is not implemented in this release and its page carries no party
/// key, so the answer is "not vulnerable" until it is. TODO(hardening): call <see cref="IPartyVulnerabilityService"/> by party once PTY serves it.
/// </summary>
internal sealed class PartyVulnerabilityCheck(Dependency<IPartyVulnerabilityService> party) : IVulnerabilityCheck
{
    public Task<bool> IsVulnerableAsync(PartyId partyId, CancellationToken cancellationToken)
    {
        _ = party.TryValue;
        return Task.FromResult(false);
    }
}

/// <summary>
/// The non-payment cancellation machinery (SL6-POL-NONPAY, REQ-POL-210/211): runs the cancellation through <see cref="CancelPolicyHandler"/>
/// (source NonPayment, reason NON_PAYMENT, pro-rata refund path of E2E-03) and requests the cancellation notice.
/// </summary>
internal sealed class NonPaymentCancellationService(
    PolicyDbContext db, CancelPolicyHandler cancel, IClock clock, Dependency<IDocumentRequestService> documents, IOptions<PolicyOptions> options)
{
    public const string ReasonCode = "NON_PAYMENT";

    /// <summary>Cancels the term at <paramref name="effective"/>. A failure result must make the caller roll back (a joined transaction cannot).</summary>
    public async Task<Result<JobId>> ExecuteAsync(NonPaymentCancellationRow row, Instant effective, CancellationToken cancellationToken)
    {
        var request = new CancellationCreateRequest
        {
            PolicyId = row.PolicyId,
            Source = CancellationSources.NonPayment,
            ReasonCode = ReasonCode,
            EffectiveAt = effective,
            Kind = CancellationKind.Standard,
            RequestRef = row.RequestId.ToString("D"),
        };
        var result = await cancel.HandleAsync(new CancelPolicy(request, NonPayment: new NonPaymentCase(row.TermId, effective, row.ClockInstanceId, row.RequestId)), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        row.Status = NonPaymentStatus.Executed;
        row.JobId = result.Value.JobId;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        await RequestNoticeAsync(row, result.Value.EffectiveAt, cancellationToken).ConfigureAwait(false);
        return result.Value.JobId;
    }

    /// <summary>DT-CANCELLATION-NOTICE for the cancelled policy. Idempotent on the request number; a no-op while DOC is not wired.</summary>
    private async Task RequestNoticeAsync(NonPaymentCancellationRow row, Instant effective, CancellationToken cancellationToken)
    {
        if (documents.TryValue is not { } service)
        {
            return;
        }

        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == row.PolicyId, cancellationToken).ConfigureAwait(false);
        await service.RequestAsync(
            new DocumentRequestSpec(
                "DT-CANCELLATION-NOTICE",
                row.RequestId.ToString("D"),
                new ObjectRef(ModuleCode.POL, "Policy", row.PolicyId.Value.ToString("D")),
                policy.PolicyholderPartyId,
                "el",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // TODO(hardening): the addressee's name belongs to PTY; POL holds none, so the letter greets "Sir/Madam".
                    ["customerName"] = "Κύριε/Κυρία",
                    ["policyNo"] = policy.PolicyNumber.ToString(),
                    ["effectiveDate"] = effective.ToBusinessDate(options.Value.Zone).ToString(),
                }),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>bil.CancellationForNonPaymentRequested</c> → POL (REQ-POL-210, D-SL6-04). Exactly once per (policy, term, clock) (POL X-12): a second
/// delivery changes nothing. A vulnerable customer, an earlier HOLD decision for the term, or a missing notice reference raises a PSR exception and
/// cancels nothing; an open claim is not an exception. Otherwise the cancellation is effective at the start of the day after the clock due date
/// (Europe/Athens) and runs now, or is SCHEDULED when that moment is still ahead (and rescindable until then, REQ-POL-211).
/// </summary>
internal sealed partial class CancellationForNonPaymentRequestedHandler(
    PolicyDbContext db, ILegalEntityDirectory legalEntities, IClock clock, IOptions<PolicyOptions> options,
    IVulnerabilityCheck vulnerability, NonPaymentCancellationService service, ILogger<CancellationForNonPaymentRequestedHandler> logger)
    : IEventHandler<CancellationForNonPaymentRequestedV1>
{
    public const string Name = "POL.CancellationForNonPaymentRequested.Cancel";

    public async Task HandleAsync(EventEnvelope envelope, CancellationForNonPaymentRequestedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(payload);
        var legalEntity = legalEntities.Resolve(envelope.LegalEntity);
        var term = await db.Terms.AsNoTracking().Where(t => t.TermId == payload.TermId && t.LegalEntityId == legalEntity)
            .Select(t => new { t.PolicyId }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            // A request for a term this entity does not hold is a routing error, not a retryable one.
            LogUnknownTerm(logger, payload.TermId.Value, payload.RequestId);
            return;
        }

        if (await db.NonPaymentCancellations.AsNoTracking()
                .AnyAsync(n => n.PolicyId == term.PolicyId && n.TermId == payload.TermId && n.ClockInstanceId == payload.ClockInstanceId, cancellationToken)
                .ConfigureAwait(false))
        {
            return; // the second delivery: the first one's outcome stands
        }

        var now = clock.Now;
        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == term.PolicyId, cancellationToken).ConfigureAwait(false);
        var effective = payload.RequestedEffectiveDate.StartOfDayIn(options.Value.Zone);
        var row = new NonPaymentCancellationRow
        {
            NonPaymentId = Guid.CreateVersion7(), LegalEntityId = legalEntity, PolicyId = term.PolicyId, TermId = payload.TermId,
            ClockInstanceId = payload.ClockInstanceId, RequestId = payload.RequestId, SourceEventId = envelope.EventId.Value,
            EffectiveAt = effective, OverdueAmount = payload.OverdueAmount.Amount, Currency = payload.OverdueAmount.Currency.Code,
            NoticeDocumentId = payload.NoticeDocumentId.Value, ProofRef = payload.ProofRef ?? string.Empty, CreatedAt = now, UpdatedAt = now,
        };

        var reason = await ExceptionReasonAsync(policy, payload, cancellationToken).ConfigureAwait(false);
        if (reason is not null)
        {
            row.Status = NonPaymentStatus.Exception;
            db.NonPaymentCancellations.Add(row);
            db.PsrExceptions.Add(new PsrExceptionRow
            {
                ExceptionId = Guid.CreateVersion7(), LegalEntityId = legalEntity, NonPaymentId = row.NonPaymentId, PolicyId = row.PolicyId, TermId = row.TermId,
                Reason = reason, RaisedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (effective > now)
        {
            row.Status = NonPaymentStatus.Scheduled;
            db.NonPaymentCancellations.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        db.NonPaymentCancellations.Add(row);
        var executed = await service.ExecuteAsync(row, effective, cancellationToken).ConfigureAwait(false);
        if (executed.IsFailure)
        {
            // The cancellation handler may have saved part of its work; throwing makes the event transaction roll all of it back.
            // TODO(hardening): a permanent refusal (term already cancelled, out of sequence) retries until dead-lettered; park it in the PSR queue instead.
            throw new InvalidOperationException($"The non-payment cancellation of request {payload.RequestId} was refused: {executed.Error}");
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExceptionReasonAsync(PolicyRow policy, CancellationForNonPaymentRequestedV1 payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.ProofRef))
        {
            return PsrReasons.NoticeMissing; // REQ-POL-012: no notice reference, no cancellation
        }

        if (await vulnerability.IsVulnerableAsync(policy.PolicyholderPartyId, cancellationToken).ConfigureAwait(false))
        {
            return PsrReasons.Vulnerable;
        }

        // A hold: staff earlier decided to hold the cancellation of this term.
        return await db.PsrExceptions.AsNoTracking().AnyAsync(e => e.TermId == payload.TermId && e.Status == "HOLD", cancellationToken).ConfigureAwait(false)
            ? PsrReasons.Hold
            : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Non-payment request {RequestId} names term {TermId} which this legal entity does not hold; ignored.")]
    private static partial void LogUnknownTerm(ILogger logger, Guid termId, Guid requestId);
}

/// <summary>
/// <c>bil.DelinquencyResolved</c> → POL (REQ-POL-211): a non-payment cancellation that is still SCHEDULED, or waiting in the PSR queue, is rescinded
/// when the arrears are cured before it takes effect. One that already took effect is left alone (it is a cancellation; reinstatement is another WP).
/// </summary>
internal sealed class DelinquencyResolvedHandler(PolicyDbContext db, IClock clock) : IEventHandler<DelinquencyResolvedV1>
{
    public const string Name = "POL.DelinquencyResolved.Rescind";

    public async Task HandleAsync(EventEnvelope envelope, DelinquencyResolvedV1 payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var open = new[] { NonPaymentStatus.Scheduled, NonPaymentStatus.Exception };
        var rows = await db.NonPaymentCancellations
            .Where(n => n.RequestId == payload.ProcessId && n.TermId == payload.TermId && open.Contains(n.Status))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return;
        }

        var now = clock.Now;
        var ids = rows.Select(r => r.NonPaymentId).ToList();
        var exceptions = await db.PsrExceptions.Where(e => ids.Contains(e.NonPaymentId) && e.Status == "PENDING").ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.Status = NonPaymentStatus.Rescinded;
            row.UpdatedAt = now;
            row.RecordVersion++;
        }

        foreach (var exception in exceptions)
        {
            exception.Status = "RESCINDED";
            exception.DecidedAt = now;
            exception.DecisionNote = $"Arrears cured ({payload.Outcome}) before the cancellation took effect.";
            exception.RecordVersion++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
