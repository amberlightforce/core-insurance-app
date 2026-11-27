using System.Globalization;
using System.Text.RegularExpressions;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.RuleSet.create</c>: a new rule set with a first Draft version (BO-UW-01).</summary>
internal sealed record CreateRuleSet(RuleSetCreateRequest Request) : ICommand<RuleSetAuthoringResponse>;

/// <summary><c>uw.RuleSet.draft</c>: a new Draft version copied from an existing one.</summary>
internal sealed record DraftRuleSet(RuleSetDraftRequest Request) : ICommand<RuleSetAuthoringResponse>;

/// <summary><c>uw.RuleSet.edit</c>: patches (decision-table cells, rules, test cases) applied to a Draft version.</summary>
internal sealed record EditRuleSet(RuleSetEditRequest Request) : ICommand<RuleSetAuthoringResponse>;

/// <summary><c>uw.RuleSet.import</c>: a Draft version from an exported document.</summary>
internal sealed record ImportRuleSet(RuleSetImportRequest Request) : ICommand<RuleSetAuthoringResponse>;

internal static partial class RuleSetRules
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,63}$")]
    public static partial Regex CodePattern();

    public static IRuleBuilderOptions<T, string> RuleSetCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Must(c => c is not null && CodePattern().IsMatch(c)).WithMessage("The rule-set code is 2 to 64 characters: capitals, digits, '_' or '-'.");
}

internal sealed class CreateRuleSetValidator : AbstractValidator<CreateRuleSet>
{
    public CreateRuleSetValidator()
    {
        RuleFor(c => c.Request.RuleSetCode).RuleSetCode();
        RuleFor(c => c.Request.ProductCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Checkpoint).Must(c => c is "PRE_QUOTE" or "PRE_BIND").WithMessage("The checkpoint must be PRE_QUOTE or PRE_BIND.");
    }
}

internal sealed class DraftRuleSetValidator : AbstractValidator<DraftRuleSet>
{
    public DraftRuleSetValidator() => RuleFor(c => c.Request.RuleSetCode).NotEmpty().MaximumLength(64);
}

internal sealed class EditRuleSetValidator : AbstractValidator<EditRuleSet>
{
    public EditRuleSetValidator()
    {
        RuleFor(c => c.Request.RuleSetCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Version).NotEmpty().MaximumLength(16);
        RuleFor(c => c.Request.Patches).NotNull().Must(p => p is { Count: >= 1 and <= 200 }).WithMessage("Give 1 to 200 patches.");
    }
}

internal sealed class ImportRuleSetValidator : AbstractValidator<ImportRuleSet>
{
    public ImportRuleSetValidator() => RuleFor(c => c.Request.Document).NotNull();
}

/// <summary>Shared pieces of the authoring handlers: the business date, the stored-version response.</summary>
internal sealed class RuleSetAuthoringSupport(RuleSetAuthoringStore store, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    public string Today()
    {
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        return clock.Now.ToBusinessDate(legalEntities.TimeZoneOf(caller)).Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static DateOnly Date(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The stored version with its definition and a fresh validation report.</summary>
    public async Task<RuleSetAuthoringResponse> ResponseAsync(string code, string version, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(code, version, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(DomainError.Of(ModuleCode.UW, "RULESET-NOT-FOUND", $"Rule set {code} {version} does not exist."));
        return Describe(record);
    }

    public static RuleSetAuthoringResponse Describe(RuleSetVersionRecord record)
    {
        var dto = RuleSetJson.Deserialize(record.Definition);
        var analysis = RuleSetAnalysis.Analyse(dto);
        return new RuleSetAuthoringResponse { Version = ToDetail(record, dto), Validation = RuleSetAuthoring.ToReport(record.RuleSetCode, record.VersionNo, analysis) };
    }

    public static RuleSetVersionDetail ToDetail(RuleSetVersionRecord record, RuleSetDto dto) => new()
    {
        RuleSetCode = record.RuleSetCode,
        Version = record.VersionNo,
        ProductCode = record.ProductCode,
        Checkpoint = record.Checkpoint,
        Status = Enum.Parse<RuleSetVersionDetail.StatusValue>(record.Status),
        EffectiveFrom = DateOnly.FromDateTime(record.EffectiveFrom).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DataStatus = record.DataStatus,
        ContentHash = record.ContentHash.Trim(),
        DefinitionHash = RuleSetAuthoring.DefinitionHash(dto),
        RuleCount = dto.Rules.Count,
        TestCaseCount = dto.TestCases.Count,
        CreatedAt = record.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        CreatedBy = record.CreatedBy,
        RecordVersion = record.RecordVersion,
        SubmittedBy = record.SubmittedBy,
        SubmittedAt = record.SubmittedAt?.ToString("O", CultureInfo.InvariantCulture),
        ApprovalRequestId = record.ApprovalRequestId?.ToString("D"),
        DecidedBy = record.DecidedBy,
        DecidedAt = record.DecidedAt?.ToString("O", CultureInfo.InvariantCulture),
        DecisionReason = record.DecisionReason,
        Definition = RuleSetAuthoring.ToContract(dto),
    };

    /// <summary>Inserts a new Draft of <paramref name="dto"/> (hash from its analysis) and returns the stored version.</summary>
    public async Task<RuleSetAuthoringResponse> InsertDraftAsync(RuleSetDto dto, CancellationToken cancellationToken)
    {
        var analysis = RuleSetAnalysis.Analyse(dto);
        var hash = analysis.ContentHash ?? RuleSetAuthoring.DefinitionHash(dto);
        var from = DateOnly.TryParseExact(dto.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw RuleSetAuthoring.Invalid("effectiveFrom must be a date, yyyy-MM-dd.");
        if (dto.Checkpoint is not ("PRE_QUOTE" or "PRE_BIND"))
        {
            throw RuleSetAuthoring.Invalid("The checkpoint must be PRE_QUOTE or PRE_BIND.");
        }

        if (!await store.InsertDraftAsync(dto.Code, dto.Version, dto.ProductCode, dto.Checkpoint, hash, from, "ILLUSTRATIVE_TEST_DATA", RuleSetJson.Serialize(dto), cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(DomainError.Of(ModuleCode.UW, "RULESET-EXISTS", $"Rule set {dto.Code} {dto.Version} already exists."));
        }

        return await ResponseAsync(dto.Code, dto.Version, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CreateRuleSetHandler(RuleSetAuthoringStore store, RuleSetAuthoringSupport support) : ICommandHandler<CreateRuleSet, RuleSetAuthoringResponse>
{
    public async Task<Result<RuleSetAuthoringResponse>> HandleAsync(CreateRuleSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        try
        {
            if (await store.LatestAsync(request.RuleSetCode, cancellationToken).ConfigureAwait(false) is not null)
            {
                return DomainError.Of(ModuleCode.UW, "RULESET-EXISTS", $"Rule set {request.RuleSetCode} already exists; draft a new version instead.");
            }

            var from = request.EffectiveFrom ?? request.Definition?.EffectiveFrom ?? support.Today();
            var dto = request.Definition is null
                ? RuleSetAuthoring.Skeleton(request.RuleSetCode, request.ProductCode, request.Checkpoint, "1.0", from, request.Note)
                : RuleSetAuthoring.FromContract(request.Definition) with
                {
                    Code = request.RuleSetCode, ProductCode = request.ProductCode, Checkpoint = request.Checkpoint, Version = "1.0", EffectiveFrom = from,
                    DataStatus = "ILLUSTRATIVE_TEST_DATA", Note = request.Note ?? request.Definition.Note,
                };
            return await support.InsertDraftAsync(dto, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class DraftRuleSetHandler(RuleSetAuthoringStore store, RuleSetAuthoringSupport support) : ICommandHandler<DraftRuleSet, RuleSetAuthoringResponse>
{
    public async Task<Result<RuleSetAuthoringResponse>> HandleAsync(DraftRuleSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        try
        {
            var source = request.FromVersion is null
                ? await store.LatestAsync(request.RuleSetCode, cancellationToken).ConfigureAwait(false)
                : await store.GetAsync(request.RuleSetCode, request.FromVersion, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                return DomainError.Of(ModuleCode.UW, "RULESET-NOT-FOUND", $"Rule set {request.RuleSetCode} {request.FromVersion} does not exist.");
            }

            var version = RuleSetAuthoring.NextVersion(await store.VersionsAsync(request.RuleSetCode, cancellationToken).ConfigureAwait(false));
            var dto = RuleSetJson.Deserialize(source.Definition) with
            {
                Version = version, DataStatus = "ILLUSTRATIVE_TEST_DATA", EffectiveFrom = request.EffectiveFrom ?? support.Today(),
            };
            return await support.InsertDraftAsync(dto, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class EditRuleSetHandler(RuleSetAuthoringStore store, RuleSetAuthoringSupport support) : ICommandHandler<EditRuleSet, RuleSetAuthoringResponse>
{
    public async Task<Result<RuleSetAuthoringResponse>> HandleAsync(EditRuleSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        try
        {
            var record = await store.GetAsync(request.RuleSetCode, request.Version, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return DomainError.Of(ModuleCode.UW, "RULESET-NOT-FOUND", $"Rule set {request.RuleSetCode} {request.Version} does not exist.");
            }

            if (record.Status != "Draft")
            {
                return DomainError.Of(ModuleCode.UW, "RULESET-STATE", $"Rule set {request.RuleSetCode} {request.Version} is {record.Status}; only a Draft can be edited.");
            }

            if (request.RecordVersion is { } expected && expected != record.RecordVersion)
            {
                return DomainError.Of(ModuleCode.UW, "STALE", $"Rule set {request.RuleSetCode} {request.Version} changed since you read it (recordVersion {record.RecordVersion}); reload and edit again.");
            }

            var dto = RuleSetAuthoring.Apply(RuleSetJson.Deserialize(record.Definition), request.Patches);
            var analysis = RuleSetAnalysis.Analyse(dto);
            var from = DateOnly.TryParseExact(dto.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DateOnly.FromDateTime(record.EffectiveFrom);
            if (!await store.UpdateDraftAsync(record.RuleSetCode, record.VersionNo, record.RecordVersion, analysis.ContentHash ?? RuleSetAuthoring.DefinitionHash(dto), from, RuleSetJson.Serialize(dto), cancellationToken).ConfigureAwait(false))
            {
                return DomainError.Of(ModuleCode.UW, "STALE", "The version was changed by someone else meanwhile (or is no longer a Draft); reload and edit again.");
            }

            return await support.ResponseAsync(record.RuleSetCode, record.VersionNo, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class ImportRuleSetHandler(RuleSetAuthoringStore store, RuleSetAuthoringSupport support) : ICommandHandler<ImportRuleSet, RuleSetAuthoringResponse>
{
    public async Task<Result<RuleSetAuthoringResponse>> HandleAsync(ImportRuleSet command, CancellationToken cancellationToken)
    {
        var document = command.Request.Document;
        try
        {
            if (document.Format != RuleSetAuthoring.ExportFormat)
            {
                return DomainError.Of(ModuleCode.UW, "VALIDATION", $"The document format must be {RuleSetAuthoring.ExportFormat}.");
            }

            var dto = RuleSetAuthoring.FromContract(document.Definition);
            if (!string.Equals(RuleSetAuthoring.DefinitionHash(dto), document.DefinitionHash, StringComparison.OrdinalIgnoreCase))
            {
                return DomainError.Of(ModuleCode.UW, "VALIDATION", "The definition does not match its definitionHash (the document was changed after the export).");
            }

            var existing = await store.VersionsAsync(dto.Code, cancellationToken).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                var latest = await store.LatestAsync(dto.Code, cancellationToken).ConfigureAwait(false);
                if (latest is not null && (latest.ProductCode != dto.ProductCode || latest.Checkpoint != dto.Checkpoint))
                {
                    return DomainError.Of(ModuleCode.UW, "VALIDATION", $"Rule set {dto.Code} exists for another product or checkpoint.");
                }
            }

            dto = dto with { Version = existing.Count == 0 ? "1.0" : RuleSetAuthoring.NextVersion(existing), DataStatus = "ILLUSTRATIVE_TEST_DATA" };
            return await support.InsertDraftAsync(dto, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }
}

internal sealed class RuleSetAuthoringAuditor<TCommand> : ICommandAuditor<TCommand, RuleSetAuthoringResponse>
    where TCommand : ICommand<RuleSetAuthoringResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<RuleSetAuthoringResponse>? result)
    {
        var version = result is { IsSuccess: true } done ? done.Value.Version : null;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.UW, "RuleSet", version is null ? "unknown" : version.RuleSetCode + "@" + version.Version),
            BusinessKeys = version is null ? BusinessKeys.Empty : BusinessKeys.Empty.With("ruleSetCode", version.RuleSetCode).With("version", version.Version),
        };
    }
}
