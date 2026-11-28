using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Contracts.Events;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Commands;

/// <summary><c>dat.Model.register</c>.</summary>
internal sealed record RegisterModel(ModelRegisterRequest Request) : ICommand<ModelRegisterResponse>;

/// <summary><c>dat.BiasTest.run</c>.</summary>
internal sealed record RunBiasTest(BiasTestRunRequest Request) : ICommand<BiasTestRunResponse>;

internal sealed class RegisterModelValidator : AbstractValidator<RegisterModel>
{
    public RegisterModelValidator()
    {
        RuleFor(c => c.Request.FeatureKey).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.Version).NotEmpty().MaximumLength(64);
    }
}

internal sealed class RunBiasTestValidator : AbstractValidator<RunBiasTest>
{
    public RunBiasTestValidator()
    {
        RuleFor(c => c.Request.Metric).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.Ratio).GreaterThanOrEqualTo(0m);
        RuleFor(c => c).Must(c => c.Request.Feature.HasValue ^ c.Request.Artefact.HasValue).WithMessage("Exactly one of feature or artefact is required.");
    }
}

internal sealed class RegisterModelHandler(DataDbContext db, IClock clock, RequestContext context) : ICommandHandler<RegisterModel, ModelRegisterResponse>
{
    public async Task<Result<ModelRegisterResponse>> HandleAsync(RegisterModel command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var existing = await db.Models.AsNoTracking()
            .SingleOrDefaultAsync(m => m.FeatureKey == request.FeatureKey && m.Version == request.Version, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ModelRegisterResponse { ModelId = existing.ModelId, Status = ModelLifecycle.MapStatus<ModelRegisterResponse.StatusValue>(existing.Status) };
        }

        var row = new ModelRow
        {
            ModelId = Guid.CreateVersion7(),
            FeatureKey = request.FeatureKey,
            Version = request.Version,
            Card = request.ModelCard?.GetRawText() ?? "{}",
            // TODO(hardening): flip the default to DRAFT once PLT-AI registers through the validation lifecycle (BO-DAT-06 keeps the legacy ACTIVE registration unless draft=true).
            Status = request.Draft == true ? "DRAFT" : "ACTIVE",
            CreatedAt = clock.Now,
            CreatedBy = MartActors.Keys(context)[0],
        };
        db.Models.Add(row);
        db.ModelTransitions.Add(ModelLifecycle.Transition(row, clock.Now, row.CreatedBy, "REGISTERED"));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ModelRegisterResponse { ModelId = row.ModelId, Status = ModelLifecycle.MapStatus<ModelRegisterResponse.StatusValue>(row.Status) };
    }
}

/// <summary>
/// Records a bias ratio. Outside the band 0.8 to 1.25 (illustrative, REQ-DAT-218) the feature's models become SUSPENDED and one
/// BiasThresholdBreached event is emitted (PLT-AI consumes it and disables the feature). Inside the band nothing is emitted.
/// </summary>
internal sealed class RunBiasTestHandler(DataDbContext db, IClock clock, IEventPublisher events) : ICommandHandler<RunBiasTest, BiasTestRunResponse>
{
    /// <summary>Lower bound of the accepted ratio band (illustrative).</summary>
    public const decimal BandMin = 0.8m;

    /// <summary>Upper bound of the accepted ratio band (illustrative).</summary>
    public const decimal BandMax = 1.25m;

    public async Task<Result<BiasTestRunResponse>> HandleAsync(RunBiasTest command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var featureKey = request.Feature?.Value ?? request.Artefact!.Value.Value;
        var breached = request.Ratio < BandMin || request.Ratio > BandMax;
        var band = $"{BandMin.ToString(CultureInfo.InvariantCulture)}..{BandMax.ToString(CultureInfo.InvariantCulture)}";

        var models = await db.Models.Where(m => m.FeatureKey == featureKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (breached && models.Count == 0)
        {
            // No registered model for the feature: register one so the suspension is recorded.
            var auto = new ModelRow { ModelId = Guid.CreateVersion7(), FeatureKey = featureKey, Version = "UNREGISTERED", Status = "ACTIVE", CreatedAt = now };
            db.Models.Add(auto);
            models.Add(auto);
        }

        var model = models.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
        if (breached)
        {
            foreach (var m in models.Where(m => m.Status is not ("SUSPENDED" or "RETIRED")))
            {
                m.Status = "SUSPENDED";
                m.SuspendedReason = $"BIAS_RATIO_{request.Ratio.ToString(CultureInfo.InvariantCulture)}_OUTSIDE_{band}";
                db.ModelTransitions.Add(ModelLifecycle.Transition(m, now, "system:bias-test", m.SuspendedReason));
            }
        }

        var report = new BiasReportRow
        {
            BiasReportId = Guid.CreateVersion7(),
            ModelId = model?.ModelId,
            FeatureKey = featureKey,
            Metric = request.Metric,
            ProxyGroup = request.ProxyGroup ?? "UNSPECIFIED",
            Ratio = request.Ratio,
            SampleSize = request.SampleSize ?? 0,
            Breached = breached,
            CreatedAt = now,
        };
        db.BiasReports.Add(report);

        if (breached)
        {
            var (lower, upper) = ModelLifecycle.RatioInterval(request.Ratio, report.SampleSize);
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(BiasThresholdBreachedV1.Descriptor), "Model", (model?.ModelId ?? report.BiasReportId).ToString("D"),
                new BiasThresholdBreachedV1
                {
                    FeatureId = request.Feature,
                    RatingArtefactHash = request.Artefact,
                    Version = model?.Version ?? "UNREGISTERED",
                    ProxyGroup = report.ProxyGroup,
                    Ratio = request.Ratio,
                    ConfidenceInterval = new ConfidenceInterval { Lower = lower, Upper = upper },
                    Band = band,
                    SampleSize = report.SampleSize,
                    BreachId = report.BiasReportId,
                },
                BusinessKeys.Empty.With("breachId", report.BiasReportId.ToString("D"))) { OccurredAt = now });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new BiasTestRunResponse
        {
            BiasReportId = report.BiasReportId,
            Ratio = request.Ratio,
            Band = band,
            Breached = breached,
            ModelStatus = breached ? "SUSPENDED" : model?.Status ?? "NONE",
        };
    }
}

/// <summary>Reads a model for <c>dat.Model.get</c>: the row, its status timeline and the latest 50 metric records.</summary>
internal sealed class ModelReader(DataDbContext db)
{
    public async Task<ModelGetResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var m = await db.Models.AsNoTracking().SingleOrDefaultAsync(x => x.ModelId == id, cancellationToken).ConfigureAwait(false);
        if (m is null)
        {
            return null;
        }

        var transitions = await db.ModelTransitions.AsNoTracking().Where(t => t.ModelId == id).OrderBy(t => t.At).ThenBy(t => t.TransitionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var metrics = await db.ModelMetrics.AsNoTracking().Where(t => t.ModelId == id).OrderByDescending(t => t.RecordedAt).ThenByDescending(t => t.MetricId).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ModelGetResponse
        {
            ModelId = m.ModelId,
            FeatureKey = m.FeatureKey,
            Version = m.Version,
            Status = ModelLifecycle.MapStatus<ModelGetResponse.StatusValue>(m.Status),
            SuspendedReason = m.SuspendedReason,
            ValidationOutcome = m.ValidationOutcome,
            DriftFlagged = m.DriftFlagged,
            DriftReason = m.DriftReason,
            EffectiveDate = m.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Transitions = transitions.Select(t => new ModelGetResponse.TransitionItem { Status = t.Status, At = t.At.ToString(), By = t.ActorKey, Note = t.Note }).ToList(),
            Metrics = metrics.Select(t => new ModelGetResponse.MetricItem
            {
                Metric = t.Metric,
                Value = t.Value.ToString(CultureInfo.InvariantCulture),
                Threshold = t.Threshold?.ToString(CultureInfo.InvariantCulture),
                Breached = t.Breached,
                SampleSize = t.SampleSize,
                RecordedAt = t.RecordedAt.ToString(),
            }).ToList(),
        };
    }
}
