using System.Text.Json;
using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Suspension;

/// <summary>The inputs of a suspension or reactivation, already checked by the command's validator.</summary>
internal sealed record SuspensionInput(PolicyTermId TermId, Instant EffectiveAt, string Reason, JsonElement? Evidence, JsonElement? ElementLocator);

/// <summary>
/// The one implementation of <c>pol.Suspension.create</c> and <c>pol.Reactivation.create</c> (REQ-POL-236, BO-POL-08). In the command's
/// transaction and under the policy write lock: the rules of <see cref="SuspensionRules"/> are applied to the current term version, which
/// is then superseded by a version that carries (or clears) <c>suspended_from</c>; a zero-value Suspension or Reactivation transaction and
/// a Bound job record it, and <c>PolicyChanged</c> (kind SUSPENSION / REACTIVATION) is published. There are no charge lines: nothing is
/// refunded or charged, premium simply stops being earned while the flag is set (BO-POL-11).
/// </summary>
internal sealed class SuspensionService(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    IOptions<PolicyOptions> options)
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public async Task<Result<JobId>> ApplyAsync(SuspensionAction action, SuspensionInput input, CancellationToken cancellationToken)
    {
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var policyIds = await db.Terms.AsNoTracking().Where(t => t.TermId == input.TermId && t.LegalEntityId == legalEntity && t.RecordedTo == null)
            .Select(t => t.PolicyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (policyIds.Count == 0)
        {
            return JobSupport.NotFound("term");
        }

        var policyId = policyIds[0];
        var locked = await PolicyWriteLock.AcquireAsync(db, clock, options.Value.LockWait, legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        if (locked.IsFailure)
        {
            return locked.Error!;
        }

        var t = locked.Value;
        var zone = options.Value.Zone;
        var term = await db.Terms.SingleOrDefaultAsync(x => x.TermId == input.TermId && x.LegalEntityId == legalEntity && x.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        var transactions = await db.Transactions.AsNoTracking().Where(x => x.PolicyId == policyId).OrderBy(x => x.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var termTransactions = transactions.Where(x => x.TermId == term.TermId).ToList();
        var latest = termTransactions.Count == 0 ? term.ValidFrom : Instant.Max(term.ValidFrom, termTransactions.Max(x => x.EffectiveAt));
        var refused = SuspensionRules.Check(
            action, Codes.Parse<PolicyTermState>(term.State), term.CancelledAt, term.SuspendedFrom, term.ValidFrom, term.ValidTo, latest, input.EffectiveAt, t, Skew);
        if (refused is not null)
        {
            return refused;
        }

        var segments = await db.Segments.AsNoTracking().Where(s => s.TermId == term.TermId && s.RecordedTo == null).OrderBy(s => s.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = (segments.FirstOrDefault(s => s.ValidFrom <= input.EffectiveAt && input.EffectiveAt < s.ValidTo) ?? segments.LastOrDefault())?.Snapshot ?? JobSupport.Json(RiskTrees.Empty);
        var vehicles = JobSupport.FromJson<RiskTree>(snapshot).Vehicles.Select(v => v.Locator ?? string.Empty).Where(l => l.Length > 0).Order(StringComparer.Ordinal).ToList();
        if (ElementRefusal(input.ElementLocator, vehicles) is { } element)
        {
            return element;
        }

        var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.PolicyId == policyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        var actor = context.Actor.ToString();
        var effective = input.EffectiveAt;
        var suspending = action == SuspensionAction.Suspend;
        var transactionId = PolicyTransactionId.New();
        var sequence = (transactions.Count == 0 ? 0 : transactions[^1].Sequence) + 1;

        // First close what is superseded (the exclusion constraints forbid overlapping record periods), then insert.
        term.RecordedTo = t;
        var closed = await SaveAsync(cancellationToken).ConfigureAwait(false);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        db.Terms.Add(new PolicyTermRow
        {
            TermVersionId = Guid.CreateVersion7(), TermId = term.TermId, PolicyId = term.PolicyId, LegalEntityId = term.LegalEntityId, TermNumber = term.TermNumber,
            ValidFrom = term.ValidFrom, ValidTo = term.ValidTo, RecordedFrom = t, State = term.State,
            ProductVersion = term.ProductVersion, ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash,
            ResolutionHash = term.ResolutionHash, ConfigurationHash = term.ConfigurationHash, Currency = term.Currency,
            ProducerCode = term.ProducerCode, PaymentPlanRef = term.PaymentPlanRef, WrittenDate = term.WrittenDate,
            HeadTransactionId = transactionId, CreatedBy = term.CreatedBy, PredecessorTermId = term.PredecessorTermId, CancelledAt = term.CancelledAt,
            SuspendedFrom = suspending ? effective : null,
        });

        var jobId = JobId.New();
        var jobNumber = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Job, t.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var intent = JobSupport.Json(new
        {
            action = suspending ? "SUSPEND" : "REACTIVATE", reason = input.Reason, effectiveAt = effective,
            evidence = input.Evidence, elementLocators = vehicles, refund = 0m,
        });
        var kind = suspending ? PolicyTransactionKind.Suspension : PolicyTransactionKind.Reactivation;
        db.Transactions.Add(new PolicyTransactionRow
        {
            TransactionId = transactionId, PolicyId = term.PolicyId, TermId = term.TermId, JobId = jobId, LegalEntityId = legalEntity,
            Kind = Codes.Of(kind), Sequence = sequence, EffectiveAt = effective, RecordedAt = t,
            ConfigurationHash = term.ConfigurationHash, ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash,
            ResolutionHash = term.ResolutionHash, Intent = intent, Premium = 0m, Taxes = 0m, Total = 0m,
            Currency = term.Currency, Actor = actor, CorrelationId = context.CorrelationId.ToString(), Origin = context.Origin.ToCode(),
        });

        var quoted = JobStateModel.Machine.Fire(JobState.Draft, JobTrigger.Quote).Value;
        var boundState = JobStateModel.Machine.Fire(quoted, JobTrigger.Bind).Value;
        db.Jobs.Add(new JobRow
        {
            JobId = jobId, LegalEntityId = legalEntity, Jurisdiction = policy.Jurisdiction.Trim(), JobNumber = JobNumber.Parse(jobNumber.Value),
            JobType = Codes.Of(suspending ? JobType.Suspension : JobType.Reactivation), State = Codes.Of(boundState), PolicyId = policy.PolicyId,
            PolicyholderPartyId = policy.PolicyholderPartyId, AccountId = policy.AccountId, ProductCode = policy.ProductCode, ProductVersion = term.ProductVersion,
            ArtefactHash = term.ArtefactHash, RatingArtefactHash = term.RatingArtefactHash, ResolutionHash = term.ResolutionHash, Channel = context.Channel ?? "STAFF",
            ProducerCode = term.ProducerCode, QuoteType = "FULL", EffectiveAt = effective, ExpirationAt = term.ValidTo, Currency = term.Currency,
            CurrentVersionNo = 1, BoundTransactionId = transactionId, RecordVersion = 1, CreatedAt = t, CreatedBy = actor, Participants = [actor], UpdatedAt = t,
            TargetTermId = term.TermId, ReasonCode = input.Reason.Length > 64 ? input.Reason[..64] : input.Reason,
            BaseTransactionId = termTransactions.Count == 0 ? null : termTransactions[^1].TransactionId,
        });
        db.QuoteVersions.Add(new QuoteVersionRow
        {
            QuoteId = QuoteId.New(), JobId = jobId, LegalEntityId = legalEntity, VersionNo = 1, State = Codes.Of(QuoteState.Quoted), DraftVersion = 0,
            RiskTree = snapshot, Bindable = true, Charges = "[]", Premium = 0m, Taxes = 0m, Total = 0m, QuotedAt = t, ValidUntil = t,
            RecordVersion = 1, CreatedAt = t, UpdatedAt = t,
        });

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyChangedV1.Descriptor), "Policy", policyId.Value.ToString(),
            new PolicyChangedV1
            {
                TransactionId = transactionId,
                Kind = suspending ? PolicyChangedV1.KindValue.Suspension : PolicyChangedV1.KindValue.Reactivation,
                EffectiveDate = effective.ToBusinessDate(zone),
                ChangedElements = [.. vehicles.Select(locator => new ChangedElement { Locator = locator, ElementType = ChangeNames.VehicleElementType })],
                VehicleCoverFacts = new VehicleCoverFacts
                {
                    Added = [], Removed = [], Suspended = suspending ? vehicles : [], Reactivated = suspending ? [] : vehicles,
                },
                PriorTerm = false,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", term.TermId.Value.ToString())
                .With("transactionId", transactionId.Value.ToString()).With("jobId", jobId.Value.ToString()))
        {
            OccurredAt = t,
        });

        var saved = await SaveAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsFailure ? saved.Error! : jobId;
    }

    /// <summary>
    /// Whole-term suspension only: an element locator is accepted when it names every vehicle of the term (a single-vehicle policy).
    /// TODO(hardening): suspend one vehicle of several (needs a per-element flag and per-vehicle earning, BO-POL-11).
    /// </summary>
    private static DomainError? ElementRefusal(JsonElement? locator, List<string> vehicles)
    {
        if (locator is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String && vehicles.Count > 0 && vehicles.All(v => string.Equals(v, element.GetString(), StringComparison.Ordinal))
            ? null
            : new DomainError(ErrorCode.For(ModuleCode.POL, PolicyErrorNames.Validation), "Only the whole term can be suspended in this release; an element locator must name every vehicle of the term.")
            {
                FieldErrors = [new FieldError("elementLocator", "ELEMENT_NOT_AVAILABLE", "ELEMENT_NOT_AVAILABLE")],
            };
    }

    private async Task<Result<bool>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return JobSupport.Stale();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation })
        {
            return JobSupport.Stale();
        }
    }
}
