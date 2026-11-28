using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Work.Commands;

/// <summary><c>wrk.Pattern.upsert</c> (BO-WRK-03): creates or replaces queues and activity patterns by code.</summary>
internal sealed record UpsertPatterns(PatternUpsertRequest Request) : ICommand<PatternCatalogue>;

internal sealed partial class UpsertPatternsValidator : AbstractValidator<UpsertPatterns>
{
    private const int MaxItems = 100;
    private const int MaxMinutes = 365 * 24 * 60;

    public UpsertPatternsValidator()
    {
        RuleFor(c => c.Request).Must(r => (r.Queues?.Count ?? 0) + (r.Patterns?.Count ?? 0) is > 0 and <= MaxItems)
            .WithErrorCode("ITEMS_REQUIRED").WithMessage($"Send 1..{MaxItems} queues and patterns.");
        RuleForEach(c => c.Request.Queues).ChildRules(q =>
        {
            q.RuleFor(x => x.Code).Matches(CodePattern()).WithErrorCode("QUEUE_CODE_INVALID");
            q.RuleFor(x => x.Title).NotEmpty().MaximumLength(200).WithErrorCode("QUEUE_TITLE_INVALID");
            q.RuleFor(x => x.Roles).Must(roles => roles.Count is >= 1 and <= 20 && roles.All(r => RolePattern().IsMatch(r)))
                .WithErrorCode("QUEUE_ROLES_INVALID");
        });
        RuleForEach(c => c.Request.Patterns).ChildRules(p =>
        {
            p.RuleFor(x => x.Code).Matches(CodePattern()).WithErrorCode("PATTERN_CODE_INVALID");
            p.RuleFor(x => x.Title).NotEmpty().MaximumLength(300).WithErrorCode("PATTERN_TITLE_INVALID");
            p.RuleFor(x => x.Category).NotEmpty().MaximumLength(64).WithErrorCode("PATTERN_CATEGORY_INVALID");
            p.RuleFor(x => x.Priority).Must(v => v is "NORMAL" or "HIGH").WithErrorCode("PATTERN_PRIORITY_INVALID");
            p.RuleFor(x => x.Queue).Matches(CodePattern()).WithErrorCode("PATTERN_QUEUE_INVALID");
            p.RuleFor(x => x.SlaPolicy).NotEmpty().MaximumLength(100).WithErrorCode("PATTERN_SLA_INVALID");
            p.RuleFor(x => x.TargetMinutes).InclusiveBetween(1, MaxMinutes).WithErrorCode("PATTERN_TARGET_INVALID");
            p.RuleFor(x => x.EscalationMinutes).InclusiveBetween(1, MaxMinutes).WithErrorCode("PATTERN_ESCALATION_INVALID");
            p.RuleFor(x => x).Must(x => x.EscalationMinutes >= x.TargetMinutes).WithErrorCode("PATTERN_ESCALATION_BEFORE_TARGET");
            p.RuleFor(x => x.RaisingModules).Must(m => m.Count is >= 1 and <= 40).WithErrorCode("PATTERN_RAISERS_INVALID");
            p.RuleFor(x => x.SuggestedAssignee).Must(v => v is null || (v.Length is >= 1 and <= 200 && !v.Any(char.IsControl))).WithErrorCode("PATTERN_ASSIGNEE_INVALID");
            p.RuleFor(x => x.AmountBandMultipliers).Must(v => v is null || BandsAreValid(v.Value)).WithErrorCode("PATTERN_BANDS_INVALID");
        });
    }

    private static bool BandsAreValid(JsonElement bands) =>
        bands.ValueKind == JsonValueKind.Object && bands.EnumerateObject().Count() <= 20
        && bands.EnumerateObject().All(b => CodePattern().IsMatch(b.Name) && b.Value.TryGetInt32(out var percent) && percent is >= 1 and <= 1000);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,99}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex RolePattern();
}

internal sealed class UpsertPatternsHandler(WorkDbContext db, CatalogueCache cache) : ICommandHandler<UpsertPatterns, PatternCatalogue>
{
    public async Task<Result<PatternCatalogue>> HandleAsync(UpsertPatterns command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var queueRows = db.Queues.ToDictionary(q => q.Code, StringComparer.Ordinal);
        foreach (var queue in request.Queues ?? [])
        {
            var roles = string.Join(',', queue.Roles.Distinct(StringComparer.Ordinal));
            if (queueRows.TryGetValue(queue.Code, out var existing))
            {
                existing.Title = queue.Title;
                existing.Roles = roles;
            }
            else
            {
                var row = new QueueRow { Code = queue.Code, Title = queue.Title, Roles = roles };
                db.Queues.Add(row);
                queueRows[queue.Code] = row;
            }
        }

        var patternRows = db.Patterns.ToDictionary(p => p.Code, StringComparer.Ordinal);
        foreach (var pattern in request.Patterns ?? [])
        {
            if (!queueRows.ContainsKey(pattern.Queue))
            {
                return WorkErrors.Invalid($"Pattern {pattern.Code}: queue '{pattern.Queue}' does not exist.");
            }

            var bands = pattern.AmountBandMultipliers is { } element
                ? element.EnumerateObject().ToDictionary(b => b.Name, b => b.Value.GetInt32(), StringComparer.Ordinal)
                : null;
            var bandJson = WorkCatalogue.SerializeBands(bands);
            var raisers = string.Join(',', pattern.RaisingModules.Select(m => m.ToString()).Distinct(StringComparer.Ordinal));
            var gate = pattern.GateBlocking ?? false;
            if (patternRows.TryGetValue(pattern.Code, out var row))
            {
                var changed = row.Title != pattern.Title || row.Category != pattern.Category || row.Priority != pattern.Priority
                    || row.QueueCode != pattern.Queue || row.SlaPolicy != pattern.SlaPolicy || row.TargetMinutes != pattern.TargetMinutes
                    || row.EscalationMinutes != pattern.EscalationMinutes || row.GateBlocking != gate
                    || row.RaisingModules != raisers || row.SuggestedAssignee != pattern.SuggestedAssignee || row.AmountBandPercent != bandJson;
                if (changed && int.TryParse(row.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                {
                    row.Version = (version + 1).ToString(CultureInfo.InvariantCulture);
                }

                row.Title = pattern.Title;
                row.Category = pattern.Category;
                row.Priority = pattern.Priority;
                row.QueueCode = pattern.Queue;
                row.SlaPolicy = pattern.SlaPolicy;
                row.TargetMinutes = pattern.TargetMinutes;
                row.EscalationMinutes = pattern.EscalationMinutes;
                row.GateBlocking = gate;
                row.RaisingModules = raisers;
                row.SuggestedAssignee = pattern.SuggestedAssignee;
                row.AmountBandPercent = bandJson;
                row.Active = pattern.Active ?? true;
            }
            else
            {
                db.Patterns.Add(new PatternRow
                {
                    Code = pattern.Code, Version = "1", Title = pattern.Title, Category = pattern.Category, Priority = pattern.Priority,
                    QueueCode = pattern.Queue, SlaPolicy = pattern.SlaPolicy, TargetMinutes = pattern.TargetMinutes,
                    EscalationMinutes = pattern.EscalationMinutes, GateBlocking = gate, RaisingModules = raisers,
                    SuggestedAssignee = pattern.SuggestedAssignee, AmountBandPercent = bandJson, Active = pattern.Active ?? true,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        cache.Invalidate();
        return PatternCatalogueReader.ToDto(await WorkCatalogue.LoadAsync(db, cancellationToken).ConfigureAwait(false));
    }
}

internal sealed class UpsertPatternsAuditor : ICommandAuditor<UpsertPatterns, PatternCatalogue>
{
    public CommandAuditFacts Describe(UpsertPatterns command, Result<PatternCatalogue>? result) =>
        new() { ObjectRef = new ObjectRef(ModuleCode.WRK, "PatternCatalogue", "default") };
}
