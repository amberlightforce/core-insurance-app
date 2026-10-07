using System.Text.Json;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands;

/// <summary><c>pol.Job.bind</c>: binds a quoted submission (Quoted → Bound) into a policy, term and issuance transaction.</summary>
internal sealed record BindJob(JobBindRequest Request) : ICommand<JobBindResponse>;

internal sealed class BindJobValidator : AbstractValidator<BindJob>
{
    public BindJobValidator()
    {
        RuleFor(c => c.Request.VersionNo).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.PaymentPlanOption).NotEmpty().MaximumLength(128);
    }
}

/// <summary>
/// Binds a quoted submission at its quoted price (REQ-POL-158, REQ-POL-182) after the bind gates in one evaluation
/// (REQ-POL-003, SL-POL subset: explicit human confirmation REQ-POL-181, quote validity, no retroactive new-business cover
/// REQ-POL-137, and no open UW issue blocking PRE_BIND). A failed gate leaves the job Quoted, creates no transaction and
/// explains itself in <c>gateResults</c>. On success, inside one database transaction:
/// <list type="bullet">
/// <item>the job is claimed by its record version (concurrent binds of one job: POL-ERR-STALE);</item>
/// <item>the policy number comes from the gapless PLT series in this transaction (REQ-POL-030, D-SLC-08);</item>
/// <item>Policy, PolicyTerm #1 (Scheduled or InForce, REQ-POL-130), the append-only Issuance transaction and the first
/// segment are written bitemporally: valid time = the term period, record time = the bind instant (REQ-POL-075/077/084);</item>
/// <item>the quote's charge lines are frozen on the transaction as NET charge deltas (REQ-POL-119/121/125) and published
/// as one complete <c>ChargeDeltaEmitted</c> set for BIL (D4), with <c>PolicyBound</c>, through the outbox.</item>
/// </list>
/// Dry-run computes the same and rolls back (no number, row, event or delta survives, REQ-POL-071/129).
/// </summary>
internal sealed class BindJobHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    Dependency<IUnderwritingRulesService> underwriting,
    RatingInput ratingInput,
    IOptions<PolicyOptions> options) : ICommandHandler<BindJob, JobBindResponse>
{
    private const string Block = "BLOCK";

    public async Task<Result<JobBindResponse>> HandleAsync(BindJob command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var zone = options.Value.Zone;
        if (request.Confirmation != true)
        {
            return DomainError.Of(ModuleCode.POL, "HUMAN-CONFIRMATION-REQUIRED", "The acting user must confirm the bind explicitly (REQ-POL-181).");
        }

        var loaded = await JobSupport.LoadAsync(db, JobSupport.LegalEntity(context, legalEntities), request.JobId, request.VersionNo, cancellationToken)
            .ConfigureAwait(false);
        if (loaded is not var (job, version))
        {
            return JobSupport.NotFound("job or quote version");
        }

        var bound = JobSupport.Fire(job, JobTrigger.Bind);
        if (bound.IsFailure)
        {
            return bound.Error!;
        }

        if (version.VersionNo != job.CurrentVersionNo || Codes.Parse<QuoteState>(version.State) != QuoteState.Quoted)
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "Only the job's current quoted version can be bound.");
        }

        if (job.QuoteType != "FULL" || version.Bindable != true)
        {
            return DomainError.Of(ModuleCode.POL, "QUICK-QUOTE-NOT-BINDABLE", "Quick-quote prices are indicative; complete a full quote first (REQ-POL-148).");
        }

        if (version.ValidUntil is not { } validUntil || validUntil <= now)
        {
            return DomainError.Of(ModuleCode.POL, "QUOTE-STALE", "The quote's validity has ended; requote it.");
        }

        var tree = JobSupport.Tree(version);

        // Gates in one evaluation (REQ-POL-003).
        var gates = new List<JobBindResponse.GateResultItem>
        {
            Gate("EFFECTIVE_DATE", job.EffectiveAt >= now, "RETROACTIVE_NEW_BUSINESS"),
        };
        var view = await ratingInput.BuildAsync(tree, job.EffectiveAt, cancellationToken).ConfigureAwait(false);
        if (view.IsFailure)
        {
            return view.Error!;
        }

        var uw = await UwEvaluation.EvaluateAsync(
            underwriting.Value, context, job, version, view.Value, RulesEvaluateRequest.CheckpointValue.PreBind, zone, cancellationToken).ConfigureAwait(false);
        if (uw.IsFailure)
        {
            return uw.Error!;
        }

        var (evaluation, issues) = uw.Value;
        var uwBlocked = evaluation.Outcome == RulesEvaluateResponse.OutcomeValue.Decline || issues.Any(i => UwOutcome.Blocks(i, BlockingPoint.PreBind));
        gates.Add(Gate("UW_ISSUES", !uwBlocked, evaluation.Outcome == RulesEvaluateResponse.OutcomeValue.Decline ? "UW_DECLINE" : "UW_ISSUES_OPEN"));
        job.Referred = uwBlocked;
        version.Issues = JobSupport.Json(issues);
        job.RecordVersion++;
        job.UpdatedAt = now;

        if (gates.Any(g => !g.Passed))
        {
            // The job stays Quoted; the gate results (and the UW evaluation) are kept.
            return await SaveAsync(new JobBindResponse { JobId = job.JobId, State = Codes.Api(Codes.Parse<JobState>(job.State)), GateResults = gates }, cancellationToken)
                .ConfigureAwait(false);
        }

        // Claim the job first: a concurrent bind of the same job fails here, before a number is taken.
        job.State = Codes.Of(bound.Value);
        var claimed = await SaveAsync(true, cancellationToken).ConfigureAwait(false);
        if (claimed.IsFailure)
        {
            return claimed.Error!;
        }

        var currency = Currency.FromCode(job.Currency);
        var charges = JobSupport.FromJson<List<ChargeLine>>(version.Charges!);
        var (premium, taxes, total) = Charges.Totals(charges, currency);
        // The configuration the quote was priced under (RAT returns MKT's hash) is the one the term pins (REQ-POL-088, REQ-POL-033).
        var configuration = version.ConfigurationHash is { } priced ? ConfigurationHash.Parse(priced)
            : context.ConfigurationHash ?? throw new InvalidOperationException("No configuration hash is pinned for this command (REQ-POL-088).");
        var today = now.ToBusinessDate(zone);
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Policy, today), cancellationToken).ConfigureAwait(false);
        var policyNumber = PolicyNumber.Parse(number.Value);
        var termId = PolicyTermId.New();
        var transactionId = PolicyTransactionId.New();
        var termState = PolicyTermStateModel.Machine.Start(job.EffectiveAt > now ? PolicyTermState.Scheduled : PolicyTermState.InForce).Value;
        var actor = context.Actor.ToString();
        var snapshot = JobSupport.Json(tree);

        db.Policies.Add(new PolicyRow
        {
            PolicyId = job.PolicyId, LegalEntityId = job.LegalEntityId, Jurisdiction = job.Jurisdiction, PolicyNumber = policyNumber,
            ProductCode = job.ProductCode, PolicyholderPartyId = job.PolicyholderPartyId, AccountId = job.AccountId, RecordedAt = now,
            CreatedBy = actor, RecordVersion = 1,
        });
        db.Terms.Add(new PolicyTermRow
        {
            TermVersionId = Guid.CreateVersion7(), TermId = termId, PolicyId = job.PolicyId, LegalEntityId = job.LegalEntityId, TermNumber = 1,
            ValidFrom = job.EffectiveAt, ValidTo = job.ExpirationAt, RecordedFrom = now, State = Codes.Of(termState),
            ProductVersion = job.ProductVersion, ArtefactHash = job.ArtefactHash, RatingArtefactHash = job.RatingArtefactHash,
            ResolutionHash = job.ResolutionHash, ConfigurationHash = configuration.Hash.Value, Currency = job.Currency,
            ProducerCode = job.ProducerCode, PaymentPlanRef = request.PaymentPlanOption!, WrittenDate = today, HeadTransactionId = transactionId,
            CreatedBy = actor,
        });
        db.Transactions.Add(new PolicyTransactionRow
        {
            TransactionId = transactionId, PolicyId = job.PolicyId, TermId = termId, JobId = job.JobId, LegalEntityId = job.LegalEntityId,
            Kind = Codes.Of(PolicyTransactionKind.Issuance), Sequence = 1, EffectiveAt = job.EffectiveAt, RecordedAt = now,
            ConfigurationHash = configuration.Hash.Value, ArtefactHash = job.ArtefactHash, RatingArtefactHash = job.RatingArtefactHash,
            ResolutionHash = job.ResolutionHash, WorksheetId = version.WorksheetId,
            Intent = JobSupport.Json(new { quoteId = version.QuoteId, versionNo = version.VersionNo, riskTree = tree }),
            Premium = premium.Amount, Taxes = taxes.Amount, Total = total.Amount, Currency = job.Currency, Actor = actor,
            CorrelationId = context.CorrelationId.ToString(), Origin = context.Origin.ToCode(),
        });
        db.Segments.Add(new SegmentRow
        {
            SegmentId = SegmentId.New(), TermId = termId, PolicyId = job.PolicyId, TransactionId = transactionId, LegalEntityId = job.LegalEntityId,
            ValidFrom = job.EffectiveAt, ValidTo = job.ExpirationAt, RecordedFrom = now,
            SnapshotHash = CanonicalJson.Hash(snapshot).Value, Snapshot = snapshot, WorksheetId = version.WorksheetId,
        });

        // Charge deltas: one complete NET set per transaction (D4: set_id = transaction id).
        var period = PolicyTime.Dates(job.EffectiveAt, job.ExpirationAt, zone);
        var frozen = new List<ChargeLine>();
        for (var i = 0; i < charges.Count; i++)
        {
            var line = charges[i];
            var chargeId = ChargeId.New();
            db.ChargeLines.Add(new ChargeLineRow
            {
                ChargeId = chargeId, TransactionId = transactionId, TermId = termId, PolicyId = job.PolicyId, LegalEntityId = job.LegalEntityId,
                ElementLocator = line.ElementLocator, CoverageCode = line.CoverageCode, ChargeType = line.ChargeType, ChargeCategory = line.ChargeCategory,
                DeltaKind = DeltaKinds.Net, AnnualRate = line.AnnualRate, Amount = line.Amount.Amount, Currency = job.Currency,
                ValidFrom = period.Start, ValidTo = period.End!.Value, BookingDate = today, CorrelationKey = transactionId.Value.ToString(),
                SetIndex = i + 1, SetSize = charges.Count, RecordedAt = now,
            });
            frozen.Add(line with { ChargeId = chargeId, TransactionId = transactionId });
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ChargeDeltaEmittedV1.Descriptor), "Policy", job.PolicyId.Value.ToString(),
                new ChargeDeltaEmittedV1
                {
                    ChargeId = chargeId, TermId = termId, ElementLocator = line.ElementLocator, CoverageCode = line.CoverageCode,
                    ChargeType = line.ChargeType, ChargeCategory = line.ChargeCategory, DeltaKind = DeltaKinds.Net, NetAmount = line.Amount,
                    ValidPeriod = period, BookingDate = today, TransactionId = transactionId, CorrelationKey = transactionId.Value.ToString(),
                },
                BusinessKeys.Empty.With("policyId", job.PolicyId.Value.ToString()).With("chargeId", chargeId.Value.ToString())
                    .With("policyTermId", termId.Value.ToString()).With("transactionId", transactionId.Value.ToString()))
            {
                OccurredAt = now,
                Set = new EventSet(transactionId.Value, charges.Count, i + 1),
            });
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyBoundV1.Descriptor), "Policy", job.PolicyId.Value.ToString(),
            new PolicyBoundV1
            {
                PolicyId = job.PolicyId, PolicyNumber = policyNumber, TermId = termId, TermNumber = 1, TransactionId = transactionId,
                EffectivePeriod = InstantRange.Of(job.EffectiveAt, job.ExpirationAt), ProductCode = job.ProductCode,
                ProductVersion = ProductVersionNumber.Parse(job.ProductVersion), ArtefactHash = Sha256Hash.Parse(job.ArtefactHash),
                ResolutionHash = ResolutionHash.Parse(job.ResolutionHash), ConfigurationHash = configuration, ProducerOfRecord = job.ProducerCode,
                AccountId = job.AccountId, PayerPartyId = job.PolicyholderPartyId, PaymentPlanRef = request.PaymentPlanOption!,
                // IFRS 17 proposals only (FIN assigns, REQ-POL-034): annual cohort of inception and the PRD's default model PAA.
                Ifrs17Tags = JsonSerializer.SerializeToElement(new { cohort = job.EffectiveAt.ToBusinessDate(zone).Value.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), measurementModel = "PAA" }),
                Motor = tree.Vehicles.Count > 0,
            },
            BusinessKeys.Empty.With("policyId", job.PolicyId.Value.ToString()).With("policyTermId", termId.Value.ToString())
                .With("transactionId", transactionId.Value.ToString()).With("jobId", job.JobId.Value.ToString())
                .With("quoteId", version.QuoteId.Value.ToString())));

        job.BoundTransactionId = transactionId;
        job.RecordVersion++;
        var saved = await SaveAsync(true, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        return new JobBindResponse
        {
            JobId = job.JobId,
            State = Codes.Api(bound.Value),
            TransactionId = transactionId,
            TermId = termId,
            TermNumber = 1,
            TermState = Codes.Api(termState),
            PolicyId = job.PolicyId,
            PolicyNumber = policyNumber,
            RecordedAt = now,
            ChargeDeltas = frozen,
            GateResults = gates,
        };
    }

    private async Task<Result<T>> SaveAsync<T>(T value, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return value;
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

    private static JobBindResponse.GateResultItem Gate(string gate, bool passed, string reason) => new()
    {
        Gate = gate, Passed = passed, Severity = JobBindResponse.GateResultItem.SeverityValue.Block, Reason = passed ? null : reason,
    };
}

/// <summary>Audit facts of <c>pol.Job.bind</c>: the job, the policy number, lineage keys and gate outcome.</summary>
internal sealed class BindJobAuditor : ICommandAuditor<BindJob, JobBindResponse>
{
    public CommandAuditFacts Describe(BindJob command, Result<JobBindResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", command.Request.JobId) };
        }

        var r = success.Value;
        var keys = BusinessKeys.Empty.With("jobId", r.JobId.Value.ToString());
        if (r.PolicyId is { } policyId && r.TransactionId is { } transactionId && r.TermId is { } termId)
        {
            keys = keys.With("policyId", policyId.Value.ToString()).With("transactionId", transactionId.Value.ToString())
                .With("policyTermId", termId.Value.ToString());
            return new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.POL, "Policy", policyId),
                ObjectNumber = r.PolicyNumber?.Value,
                BusinessKeys = keys,
                Changes = AuditDiff.Compute(null, new { state = r.State.ToString(), termState = r.TermState?.ToString(), charges = r.ChargeDeltas?.Count }),
            };
        }

        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.POL, "Job", r.JobId),
            BusinessKeys = keys,
            Changes = AuditDiff.Compute(null, new { failedGates = r.GateResults.Where(g => !g.Passed).Select(g => g.Gate).ToArray() }),
        };
    }
}
