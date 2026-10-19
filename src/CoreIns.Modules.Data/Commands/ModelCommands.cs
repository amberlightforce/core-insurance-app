using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Contracts.Events;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Common;
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

internal sealed class RegisterModelHandler(DataDbContext db, IClock clock) : ICommandHandler<RegisterModel, ModelRegisterResponse>
{
    public async Task<Result<ModelRegisterResponse>> HandleAsync(RegisterModel command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var existing = await db.Models.AsNoTracking()
            .SingleOrDefaultAsync(m => m.FeatureKey == request.FeatureKey && m.Version == request.Version, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ModelRegisterResponse { ModelId = existing.ModelId, Status = Parse(existing.Status) };
        }

        var row = new ModelRow
        {
            ModelId = Guid.CreateVersion7(),
            FeatureKey = request.FeatureKey,
            Version = request.Version,
            Card = request.ModelCard?.GetRawText() ?? "{}",
            Status = "ACTIVE",
            CreatedAt = clock.Now,
        };
        db.Models.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ModelRegisterResponse { ModelId = row.ModelId, Status = ModelRegisterResponse.StatusValue.Active };
    }

    private static ModelRegisterResponse.StatusValue Parse(string status) =>
        status == "SUSPENDED" ? ModelRegisterResponse.StatusValue.Suspended : ModelRegisterResponse.StatusValue.Active;
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
            foreach (var m in models.Where(m => m.Status != "SUSPENDED"))
            {
                m.Status = "SUSPENDED";
                m.SuspendedReason = $"BIAS_RATIO_{request.Ratio.ToString(CultureInfo.InvariantCulture)}_OUTSIDE_{band}";
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
            // TODO(hardening): the confidence interval is a point (lower = upper = ratio) until sampling exists.
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(BiasThresholdBreachedV1.Descriptor), "Model", (model?.ModelId ?? report.BiasReportId).ToString("D"),
                new BiasThresholdBreachedV1
                {
                    FeatureId = request.Feature,
                    RatingArtefactHash = request.Artefact,
                    Version = model?.Version ?? "UNREGISTERED",
                    ProxyGroup = report.ProxyGroup,
                    Ratio = request.Ratio,
                    ConfidenceInterval = new ConfidenceInterval { Lower = request.Ratio, Upper = request.Ratio },
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

/// <summary>Reads a model for <c>dat.Model.get</c>.</summary>
internal sealed class ModelReader(DataDbContext db)
{
    public async Task<ModelGetResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var m = await db.Models.AsNoTracking().SingleOrDefaultAsync(x => x.ModelId == id, cancellationToken).ConfigureAwait(false);
        return m is null
            ? null
            : new ModelGetResponse
            {
                ModelId = m.ModelId,
                FeatureKey = m.FeatureKey,
                Version = m.Version,
                Status = m.Status == "SUSPENDED" ? ModelGetResponse.StatusValue.Suspended : ModelGetResponse.StatusValue.Active,
                SuspendedReason = m.SuspendedReason,
            };
    }
}
