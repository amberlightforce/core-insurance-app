using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Contracts.Events;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Commands;

/// <summary><c>dat.Model.submitValidation</c>: DRAFT to IN_VALIDATION.</summary>
internal sealed record SubmitModelValidation(ModelSubmitValidationRequest Request) : ICommand<ModelSubmitValidationResponse>;

/// <summary><c>dat.Model.recordValidation</c>: a validator other than the developer records PASSED or FAILED.</summary>
internal sealed record RecordModelValidation(ModelRecordValidationRequest Request) : ICommand<ModelRecordValidationResponse>;

/// <summary><c>dat.Model.approve</c>: a second person approves a validated model.</summary>
internal sealed record ApproveModel(ModelApproveRequest Request) : ICommand<ModelApproveResponse>;

/// <summary><c>dat.Model.retire</c>.</summary>
internal sealed record RetireModel(ModelRetireRequest Request) : ICommand<ModelRetireResponse>;

/// <summary><c>dat.Monitor.metrics</c>: metric records of a model version; PSI/drift breaches raise ModelDriftDetected.</summary>
internal sealed record RecordModelMetrics(MonitorMetricsRequest Request) : ICommand<MonitorMetricsResponse>;

internal sealed class SubmitModelValidationValidator : AbstractValidator<SubmitModelValidation>
{
    public SubmitModelValidationValidator() => RuleFor(c => c.Request.ModelId).NotEmpty();
}

internal sealed class RecordModelValidationValidator : AbstractValidator<RecordModelValidation>
{
    public RecordModelValidationValidator()
    {
        RuleFor(c => c.Request.ModelId).NotEmpty();
        RuleFor(c => c.Request.Findings).MaximumLength(2000);
    }
}

internal sealed class ApproveModelValidator : AbstractValidator<ApproveModel>
{
    public ApproveModelValidator()
    {
        RuleFor(c => c.Request.ModelId).NotEmpty();
        RuleFor(c => c.Request.EffectiveDate).Must(d => d is null || DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .WithMessage("effectiveDate must be yyyy-MM-dd.");
    }
}

internal sealed class RetireModelValidator : AbstractValidator<RetireModel>
{
    public RetireModelValidator()
    {
        RuleFor(c => c.Request.ModelId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class RecordModelMetricsValidator : AbstractValidator<RecordModelMetrics>
{
    public RecordModelMetricsValidator()
    {
        RuleFor(c => c.Request.Version).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.MetricRecords).NotEmpty().Must(r => r.Count <= 100);
        RuleForEach(c => c.Request.MetricRecords).ChildRules(r =>
        {
            r.RuleFor(x => x.Metric).NotEmpty().MaximumLength(128);
            r.RuleFor(x => x.SampleSize).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>The model registry lifecycle (BO-DAT-06): DRAFT, IN_VALIDATION, APPROVED, ACTIVE, RETIRED, plus SUSPENDED from the bias test.</summary>
internal static class ModelLifecycle
{
    /// <summary>Default breach level of PSI and DRIFT records (illustrative; the caller can pass its own threshold).</summary>
    public const decimal DefaultDriftThreshold = 0.25m;

    /// <summary>Card keys that must be filled before a model can be submitted for validation (DAT-ERR-031).</summary>
    public static readonly string[] RequiredCardKeys = ["purpose", "trainingData", "limitations"];

    public static T MapStatus<T>(string status)
        where T : struct, Enum =>
        Enum.Parse<T>(string.Concat(status.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant())));

    public static ModelTransitionRow Transition(ModelRow model, Instant at, string? actor, string? note) =>
        new() { TransitionId = Guid.CreateVersion7(), ModelId = model.ModelId, Status = model.Status, ActorKey = actor ?? "system", Note = note, At = at };

    /// <summary>
    /// 95% interval of a ratio of two rates from the sample size: the log-ratio standard error is approximated by 1/sqrt(n).
    /// Without a sample size the interval is the point itself.
    /// </summary>
    public static (decimal Lower, decimal Upper) RatioInterval(decimal ratio, int sampleSize)
    {
        if (sampleSize <= 0 || ratio <= 0m)
        {
            return (ratio, ratio);
        }

        var factor = Exp(1.96m / Sqrt(sampleSize));
        return (Trim6(ratio / factor), Trim6(ratio * factor));
    }

    private static decimal Trim6(decimal value) => decimal.Truncate(value * 1_000_000m) / 1_000_000m;

    private static decimal Sqrt(int n)
    {
        var x = (decimal)n;
        for (var i = 0; i < 40; i++)
        {
            x = (x + (n / x)) / 2m;
        }

        return x;
    }

    private static decimal Exp(decimal x)
    {
        decimal term = 1m, sum = 1m;
        for (var i = 1; i < 40; i++)
        {
            term = term * x / i;
            sum += term;
        }

        return sum;
    }

    /// <summary>Stable user id for events: the actor id when it is a GUID, else a name-based GUID of the actor key.</summary>
    public static UserId UserIdOf(string actorKey)
    {
        var raw = actorKey[(actorKey.IndexOf(':', StringComparison.Ordinal) + 1)..];
        if (Guid.TryParse(raw, out var guid) && guid != Guid.Empty)
        {
            return UserId.From(guid);
        }

        // TODO(hardening): resolve the approver to the real PLT user id once the user directory exposes it.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(actorKey));
        return UserId.From(new Guid(hash.AsSpan(0, 16)));
    }

    public static DomainError NotFound() => DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The model does not exist.");

    public static DomainError BadTransition(string status, string action) =>
        DomainError.Of(ModuleCode.DAT, "033", $"A {status} model cannot be {action}.");
}

internal sealed class SubmitModelValidationHandler(DataDbContext db, IClock clock, RequestContext context)
    : ICommandHandler<SubmitModelValidation, ModelSubmitValidationResponse>
{
    public async Task<Result<ModelSubmitValidationResponse>> HandleAsync(SubmitModelValidation command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var model = await db.Models.SingleOrDefaultAsync(m => m.ModelId == request.ModelId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return ModelLifecycle.NotFound();
        }

        if (model.Status != "DRAFT")
        {
            return ModelLifecycle.BadTransition(model.Status, "submitted for validation");
        }

        var card = request.ModelCard?.GetRawText() ?? model.Card;
        if (MissingCardKeys(card) is { Count: > 0 } missing)
        {
            return DomainError.Of(ModuleCode.DAT, "031", $"The model card is incomplete: {string.Join(", ", missing)}.");
        }

        var actor = MartActors.Keys(context)[0];
        model.Card = card;
        model.Status = "IN_VALIDATION";
        model.SubmittedBy = actor;
        model.ValidationOutcome = null;
        model.ValidationFindings = null;
        model.ValidatedBy = null;
        db.ModelTransitions.Add(ModelLifecycle.Transition(model, clock.Now, actor, "SUBMITTED"));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ModelSubmitValidationResponse { ModelId = model.ModelId, Status = ModelLifecycle.MapStatus<ModelSubmitValidationResponse.StatusValue>(model.Status) };
    }

    private static List<string> MissingCardKeys(string card)
    {
        try
        {
            using var doc = JsonDocument.Parse(card);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ModelLifecycle.RequiredCardKeys
                    .Where(k => !doc.RootElement.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())).ToList()
                : [.. ModelLifecycle.RequiredCardKeys];
        }
        catch (JsonException)
        {
            return [.. ModelLifecycle.RequiredCardKeys];
        }
    }
}

internal sealed class RecordModelValidationHandler(DataDbContext db, IClock clock, RequestContext context)
    : ICommandHandler<RecordModelValidation, ModelRecordValidationResponse>
{
    public async Task<Result<ModelRecordValidationResponse>> HandleAsync(RecordModelValidation command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var model = await db.Models.SingleOrDefaultAsync(m => m.ModelId == request.ModelId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return ModelLifecycle.NotFound();
        }

        if (model.Status != "IN_VALIDATION" || model.ValidationOutcome is not null)
        {
            return ModelLifecycle.BadTransition(model.Status, "validated again");
        }

        var actor = MartActors.Keys(context)[0];
        if (actor == model.CreatedBy || actor == model.SubmittedBy)
        {
            return DomainError.Of(ModuleCode.DAT, "030", "The validator is the developer of this model; a second person must validate it.");
        }

        var passed = request.Outcome == ModelRecordValidationRequest.OutcomeValue.Passed;
        model.ValidationOutcome = passed ? "PASSED" : "FAILED";
        model.ValidationFindings = request.Findings;
        model.ValidatedBy = actor;
        if (!passed)
        {
            model.Status = "DRAFT";
        }

        db.ModelTransitions.Add(ModelLifecycle.Transition(model, clock.Now, actor, $"VALIDATION_{model.ValidationOutcome}"));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ModelRecordValidationResponse
        {
            ModelId = model.ModelId,
            Status = ModelLifecycle.MapStatus<ModelRecordValidationResponse.StatusValue>(model.Status),
            Outcome = model.ValidationOutcome,
        };
    }
}

internal sealed class ApproveModelHandler(DataDbContext db, IClock clock, RequestContext context, IEventPublisher events)
    : ICommandHandler<ApproveModel, ModelApproveResponse>
{
    public async Task<Result<ModelApproveResponse>> HandleAsync(ApproveModel command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var model = await db.Models.SingleOrDefaultAsync(m => m.ModelId == request.ModelId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return ModelLifecycle.NotFound();
        }

        if (model.Status != "IN_VALIDATION" || model.ValidationOutcome != "PASSED")
        {
            return ModelLifecycle.BadTransition(model.Status, "approved before a passed validation");
        }

        // Segregation of duties: the developer, the submitter and the validator cannot approve (DAT-ERR-030).
        var actor = MartActors.Keys(context)[0];
        if (actor == model.CreatedBy || actor == model.SubmittedBy || actor == model.ValidatedBy)
        {
            return DomainError.Of(ModuleCode.DAT, "030", "The approver developed, submitted or validated this model; a second person must approve it.");
        }

        var now = clock.Now;
        var today = now.UtcDate.Value;
        var effective = request.EffectiveDate is { } text ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) : today;

        // TODO(hardening): a future-dated APPROVED model needs a sweeper to turn it ACTIVE on its effective date.
        model.Status = effective <= today ? "ACTIVE" : "APPROVED";
        model.ApprovedBy = actor;
        model.EffectiveDate = effective;
        db.ModelTransitions.Add(ModelLifecycle.Transition(model, now, actor, "APPROVED"));
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ModelVersionApprovedV1.Descriptor), "Model", model.ModelId.ToString("D"),
            new ModelVersionApprovedV1
            {
                ModelId = model.ModelId,
                Version = model.Version,
                ApproverUserIds = [ModelLifecycle.UserIdOf(actor)],
                EffectiveDate = new BusinessDate(effective),
            },
            BusinessKeys.Empty.With("modelId", model.ModelId.ToString("D"))) { OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ModelApproveResponse
        {
            ModelId = model.ModelId,
            Status = ModelLifecycle.MapStatus<ModelApproveResponse.StatusValue>(model.Status),
            EffectiveDate = effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }
}

internal sealed class RetireModelHandler(DataDbContext db, IClock clock, RequestContext context, IEventPublisher events)
    : ICommandHandler<RetireModel, ModelRetireResponse>
{
    public async Task<Result<ModelRetireResponse>> HandleAsync(RetireModel command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var model = await db.Models.SingleOrDefaultAsync(m => m.ModelId == request.ModelId, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return ModelLifecycle.NotFound();
        }

        if (model.Status == "RETIRED")
        {
            return ModelLifecycle.BadTransition(model.Status, "retired again");
        }

        var now = clock.Now;
        model.Status = "RETIRED";
        model.RetiredReason = request.Reason;
        db.ModelTransitions.Add(ModelLifecycle.Transition(model, now, MartActors.Keys(context)[0], request.Reason));
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ModelVersionRetiredV1.Descriptor), "Model", model.ModelId.ToString("D"),
            new ModelVersionRetiredV1 { ModelId = model.ModelId, Version = model.Version, Reason = request.Reason },
            BusinessKeys.Empty.With("modelId", model.ModelId.ToString("D"))) { OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ModelRetireResponse { ModelId = model.ModelId, Status = ModelLifecycle.MapStatus<ModelRetireResponse.StatusValue>(model.Status) };
    }
}

/// <summary>
/// Stores metric records. A PSI or DRIFT value at or above its threshold (default 0.25) breaches: one ModelDriftDetected per record and the
/// model is flagged. A call whose thresholded records all pass clears the flag.
/// </summary>
internal sealed class RecordModelMetricsHandler(DataDbContext db, IClock clock, IEventPublisher events)
    : ICommandHandler<RecordModelMetrics, MonitorMetricsResponse>
{
    public async Task<Result<MonitorMetricsResponse>> HandleAsync(RecordModelMetrics command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var featureKey = request.Feature.Value;
        var model = await db.Models.SingleOrDefaultAsync(m => m.FeatureKey == featureKey && m.Version == request.Version, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return DomainError.Of(ModuleCode.DAT, "032", "No registered model for this feature and version.");
        }

        if (model.Status == "RETIRED")
        {
            return ModelLifecycle.BadTransition(model.Status, "monitored");
        }

        var now = clock.Now;
        var breachIds = new List<Guid>();
        var thresholded = 0;
        string? reason = null;
        foreach (var record in request.MetricRecords)
        {
            var metric = record.Metric.Trim().ToUpperInvariant();
            var threshold = record.Threshold ?? (metric is "PSI" or "DRIFT" ? ModelLifecycle.DefaultDriftThreshold : (decimal?)null);
            var breached = threshold is { } t && record.Value >= t;
            var to = record.WindowTo ?? now;
            var from = record.WindowFrom ?? to.Minus(TimeSpan.FromDays(30));
            if (from >= to)
            {
                return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "windowFrom must be before windowTo.");
            }

            var row = new ModelMetricRow
            {
                MetricId = Guid.CreateVersion7(), ModelId = model.ModelId, Metric = metric, Value = record.Value, Threshold = threshold, Breached = breached,
                SampleSize = record.SampleSize ?? 0, WindowFrom = from, WindowTo = to, RecordedAt = now,
            };
            thresholded += threshold is null ? 0 : 1;
            if (breached)
            {
                row.BreachId = Guid.CreateVersion7();
                breachIds.Add(row.BreachId.Value);
                reason = $"{metric}_{record.Value.ToString(CultureInfo.InvariantCulture)}_AT_OR_ABOVE_{threshold!.Value.ToString(CultureInfo.InvariantCulture)}";
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(ModelDriftDetectedV1.Descriptor), "Model", model.ModelId.ToString("D"),
                    new ModelDriftDetectedV1
                    {
                        FeatureId = request.Feature,
                        ModelVersion = model.Version,
                        Metric = metric,
                        Value = record.Value,
                        Threshold = threshold.Value,
                        Window = new InstantRange(from, to),
                        SampleSize = row.SampleSize,
                        Severity = record.Value >= threshold.Value * 2m ? "HIGH" : "MEDIUM",
                        BreachId = row.BreachId.Value,
                    },
                    BusinessKeys.Empty.With("modelId", model.ModelId.ToString("D")).With("breachId", row.BreachId.Value.ToString("D"))) { OccurredAt = now });
            }

            db.ModelMetrics.Add(row);
        }

        if (breachIds.Count > 0)
        {
            model.DriftFlagged = true;
            model.DriftReason = reason;
        }
        else if (thresholded > 0)
        {
            model.DriftFlagged = false;
            model.DriftReason = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new MonitorMetricsResponse { Accepted = request.MetricRecords.Count, Breached = breachIds.Count, DriftFlagged = model.DriftFlagged, BreachIds = breachIds };
    }
}
