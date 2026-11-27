using System.Data;
using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Rating.Authority;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Events;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Dapper;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Rating.Commands;

/// <summary>A release row as read through Dapper.</summary>
internal sealed class ReleaseRecord
{
    public Guid ReleaseId { get; set; }

    public Guid ChangeSetId { get; set; }

    public string ArtefactHash { get; set; } = string.Empty;

    public string ProductCode { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;

    public string Jurisdiction { get; set; } = string.Empty;

    public string DataStatus { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? PayloadHash { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string BuiltBy { get; set; } = string.Empty;

    public string? SubmittedBy { get; set; }

    public string? SubmittedByPrincipal { get; set; }

    public Guid? ActivationId { get; set; }

    public DateTime? EffectiveFrom { get; set; }
}

/// <summary>An activation row as read through Dapper.</summary>
internal sealed class ActivationRecord
{
    public Guid ActivationId { get; set; }

    public string ArtefactHash { get; set; } = string.Empty;

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }
}

/// <summary>
/// Shared pieces of the artefact lifecycle (BO-RAT-06, REQ-RAT-213..226): build an immutable content-hashed artefact from a change set,
/// submit it for maker-checker (PLT approval RAT.Release, authority RAT.RELEASE), the checker returns or schedules it, activation is
/// effective-dated and never edits an artefact, rollback re-activates the prior artefact and keeps every row.
/// </summary>
internal static class LifecycleSupport
{
    public const string Built = "BUILT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Returned = "RETURNED";
    public const string Scheduled = "SCHEDULED";
    public const string RolledBack = "ROLLED_BACK";

    public const string ReleaseColumns =
        """
        release_id AS ReleaseId, change_set_id AS ChangeSetId, artefact_hash AS ArtefactHash, product_code AS ProductCode, product_version AS ProductVersion,
        jurisdiction AS Jurisdiction, data_status AS DataStatus, status AS Status, payload_hash AS PayloadHash, approval_request_id AS ApprovalRequestId,
        built_by AS BuiltBy, submitted_by AS SubmittedBy, submitted_by_principal AS SubmittedByPrincipal, activation_id AS ActivationId, effective_from AS EffectiveFrom
        """;

    public static string ActorKey(ActorRef actor) => $"{actor.KindCode}:{actor.Id}";

    public static ObjectRef Subject(Guid releaseId) => new(ModuleCode.RAT, RatingAuthorityTypes.ReleaseSubjectType, releaseId.ToString("D"));

    public static DomainError Sod(string detail) => DomainError.Of(ModuleCode.RAT, "SOD-VIOLATION", detail);

    public static DomainError State(string detail) => DomainError.Of(ModuleCode.RAT, "RELEASE-STATE", detail);

    public static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.RAT, PlatformErrors.Validation, detail);

    public static DomainError NotFound(string detail) => DomainError.Of(ModuleCode.RAT, PlatformErrors.NotFound, detail);

    public static DateOnly Today(IClock clock) => DateOnly.FromDateTime(clock.Now.ToUtcDateTime());

    public static DateOnly Date(DateTime value) => DateOnly.FromDateTime(value);

    /// <summary>The frozen content PLT approves: who owns it, which artefact, which data status.</summary>
    public static Sha256Hash ContentHash(string legalEntity, string jurisdiction, ReleaseRecord release) =>
        CanonicalJson.HashOf(
            new
            {
                type = RatingAuthorityTypes.ReleaseApprovalType,
                legalEntity,
                jurisdiction,
                product = release.ProductCode,
                productVersion = release.ProductVersion,
                artefactHash = release.ArtefactHash,
                dataStatus = release.DataStatus,
            },
            SharedKernelJson.Options);

    public static async Task<ReleaseRecord?> LoadReleaseAsync(DbSession session, Sha256Hash artefactHash, Guid legalEntity, CancellationToken ct)
    {
        var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<ReleaseRecord>(new CommandDefinition(
            $"SELECT {ReleaseColumns} FROM rat.artifact_release WHERE artefact_hash = @hash AND legal_entity_id = @legalEntity FOR UPDATE",
            new { hash = artefactHash.Value, legalEntity }, session.Transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    public static Guid LegalEntityOf(RequestContext context, ILegalEntityDirectory legalEntities) =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public static string[] CallerKeys(RequestContext context) =>
        context.OnBehalfOf is { } principal ? [ActorKey(context.Actor), ActorKey(principal)] : [ActorKey(context.Actor)];

    /// <summary>Four eyes: the checker is neither the builder, the submitter nor anyone they act for, and is a person.</summary>
    public static DomainError? CheckChecker(RequestContext context, ReleaseRecord release)
    {
        var makers = new[] { release.BuiltBy, release.SubmittedBy, release.SubmittedByPrincipal }.Where(m => m is not null).Select(m => m!).ToArray();
        if (CallerKeys(context).Any(k => makers.Contains(k, StringComparer.Ordinal)))
        {
            return DomainError.Of(ModuleCode.PLT, "SELF-APPROVAL", "The builder or submitter of a release cannot approve it.");
        }

        return context.Actor.Kind == ActorKind.User ? null : DomainError.Of(ModuleCode.PLT, "CHECKER-MUST-BE-HUMAN", "Only a person can decide a rating release.");
    }

    public static CommandAuditFacts Facts(string artefactHash, object? before, object? after) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.RAT, "RatingArtifact", artefactHash),
        BusinessKeys = BusinessKeys.Empty.With("artefactHash", artefactHash),
        Changes = after is null ? [] : AuditDiff.Compute(before, after),
    };

    public static TEnum Lifecycle<TEnum>(string status) where TEnum : struct, Enum => Enum.Parse<TEnum>(status, ignoreCase: true);

    public static string LifecycleOf(string releaseStatus) => releaseStatus switch
    {
        Submitted => "SUBMITTED",
        Approved => "APPROVED",
        Scheduled => "SCHEDULED",
        _ => "DRAFT",
    };

    public static string RatingSlotKey(string productCode, string productVersion) => productCode + "/" + productVersion;

    public static BusinessKeys SlotKeys(string productCode, string productVersion) =>
        BusinessKeys.Empty.With("ratingSlotId", RatingSlotKey(productCode, productVersion));
}

// ---------------------------------------------------------------------------------------------------------------------------------
// rat.RatingChangeSet.create

internal sealed record CreateRatingChangeSet(RatingChangeSetCreateRequest Request) : ICommand<RatingChangeSetCreateResponse>;

internal sealed class CreateRatingChangeSetValidator : AbstractValidator<CreateRatingChangeSet>
{
    public CreateRatingChangeSetValidator()
    {
        RuleFor(c => c.Request.ProductCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.ProductVersion).NotEmpty().MaximumLength(16);
        RuleFor(c => c.Request.Reason).MaximumLength(500);
    }
}

internal sealed class CreateRatingChangeSetHandler(
    DbSession session, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, RatingStore store) : ICommandHandler<CreateRatingChangeSet, RatingChangeSetCreateResponse>
{
    public async Task<Result<RatingChangeSetCreateResponse>> HandleAsync(CreateRatingChangeSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = LifecycleSupport.LegalEntityOf(context, legalEntities);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        string? baseHash = request.BaseArtefactHash;
        if (baseHash is null)
        {
            await store.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
            baseHash = (await store.ResolveAsync(request.ProductCode, request.ProductVersion, LifecycleSupport.Today(clock), cancellationToken).ConfigureAwait(false))?.ArtefactHash
                ?? await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT artefact_hash FROM rat.rating_artifact WHERE product_code = @p ORDER BY created_at DESC LIMIT 1",
                    new { p = request.ProductCode }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (baseHash is null)
        {
            return DomainError.Of(ModuleCode.RAT, "EVIDENCE-INCOMPLETE", "There is no base artefact for the product; name baseArtefactHash.");
        }

        var baseProduct = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT product_code FROM rat.rating_artifact WHERE artefact_hash = @h", new { h = baseHash }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (baseProduct != request.ProductCode)
        {
            return DomainError.Of(ModuleCode.RAT, "EVIDENCE-INCOMPLETE", "The base artefact does not exist or belongs to another product.");
        }

        var tables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (slot, hash) in request.TableHashes ?? new Dictionary<string, string>())
        {
            var code = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT table_code FROM rat.rate_table_version WHERE table_hash = @h", new { h = hash }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (code is null || code != slot)
            {
                return DomainError.Of(ModuleCode.RAT, "EVIDENCE-INCOMPLETE", $"Table {slot} is not an imported rate table of that code (hash {hash}).");
            }

            tables[slot] = hash;
        }

        var id = EntityIds.NewGuid();
        var now = clock.Now.ToUtcDateTime();
        var actor = context.Actor.ToString();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO rat.change_set (change_set_id, legal_entity_id, jurisdiction, product_code, product_version, base_artefact_hash, tables, reason, status,
                record_version, created_at, created_by, updated_at, updated_by)
            VALUES (@id, @legalEntity, @jurisdiction, @product, @version, @baseHash, @tables::jsonb, @reason, 'OPEN', 1, @now, @actor, @now, @actor)
            """,
            new
            {
                id, legalEntity, jurisdiction = context.Jurisdiction?.Value ?? "GR", product = request.ProductCode, version = request.ProductVersion, baseHash,
                tables = JsonSerializer.Serialize(tables), reason = request.Reason, now, actor,
            }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new RatingChangeSetCreateResponse
        {
            ChangeSetId = id,
            ProductCode = request.ProductCode,
            ProductVersion = request.ProductVersion,
            BaseArtefactHash = baseHash,
            Status = RatingChangeSetCreateResponse.StatusValue.Open,
            Tables = [.. tables.Select(t => new RatingChangeSetCreateResponse.TableItem { Slot = t.Key, Hash = t.Value })],
        };
    }
}

internal sealed class CreateRatingChangeSetAuditor : ICommandAuditor<CreateRatingChangeSet, RatingChangeSetCreateResponse>
{
    public CommandAuditFacts Describe(CreateRatingChangeSet command, Result<RatingChangeSetCreateResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.RAT, "RatingChangeSet", result is { IsSuccess: true } ok ? ok.Value.ChangeSetId.ToString("D") : "new"),
        Changes = result is { IsSuccess: true } done
            ? AuditDiff.Compute(null, new { status = "OPEN", product = done.Value.ProductCode, version = done.Value.ProductVersion })
            : [],
    };
}

// ---------------------------------------------------------------------------------------------------------------------------------
// rat.RatingArtifact.build

internal sealed record BuildRatingArtifact(RatingArtifactBuildRequest Request) : ICommand<RatingArtifactBuildResponse>;

internal sealed class BuildRatingArtifactValidator : AbstractValidator<BuildRatingArtifact>
{
    public BuildRatingArtifactValidator()
    {
        RuleFor(c => c.Request.ChangeSetId).NotEmpty();
        RuleFor(c => c.Request.Reason).MaximumLength(500);
    }
}

/// <summary>
/// Compiles the change set into an artefact: the base artefact's steps and tax plan, the pinned table hashes of the change set over the
/// base's. The compile checks every table and step; the same inputs always give the same hash, so a second build returns the first.
/// </summary>
internal sealed class BuildRatingArtifactHandler(
    DbSession session, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, RatingStore store) : ICommandHandler<BuildRatingArtifact, RatingArtifactBuildResponse>
{
    public async Task<Result<RatingArtifactBuildResponse>> HandleAsync(BuildRatingArtifact command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = LifecycleSupport.LegalEntityOf(context, legalEntities);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var changeSet = await connection.QueryFirstOrDefaultAsync<ChangeSetRecord>(new CommandDefinition(
            """
            SELECT change_set_id AS ChangeSetId, product_code AS ProductCode, product_version AS ProductVersion, base_artefact_hash AS BaseArtefactHash,
                   tables::text AS Tables, status AS Status, jurisdiction AS Jurisdiction
              FROM rat.change_set WHERE change_set_id = @id AND legal_entity_id = @legalEntity FOR UPDATE
            """,
            new { id = request.ChangeSetId, legalEntity }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (changeSet is null)
        {
            return LifecycleSupport.NotFound("The change set does not exist.");
        }

        var wantApproved = request.DataStatus == RatingArtifactBuildRequest.DataStatusValue.Approved;
        if (changeSet.Status == "BUILT")
        {
            var built = await connection.QueryFirstAsync<ReleaseRecord>(new CommandDefinition(
                $"SELECT {LifecycleSupport.ReleaseColumns} FROM rat.artifact_release WHERE change_set_id = @id", new { id = changeSet.ChangeSetId }, session.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return Response(built, created: false);
        }

        try
        {
            var baseCompiled = await store.LoadAsync(changeSet.BaseArtefactHash ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (baseCompiled is null)
            {
                return DomainError.Of(ModuleCode.RAT, "EVIDENCE-INCOMPLETE", "The base artefact of the change set no longer exists.");
            }

            var pins = new Dictionary<string, string>(baseCompiled.Definition.Tables, StringComparer.Ordinal);
            foreach (var (slot, hash) in JsonSerializer.Deserialize<Dictionary<string, string>>(changeSet.Tables) ?? [])
            {
                pins[slot] = hash;
            }

            var dtos = await LoadTablesAsync(connection, pins.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            if (dtos.Count != pins.Values.Distinct(StringComparer.Ordinal).Count())
            {
                return DomainError.Of(ModuleCode.RAT, "EVIDENCE-INCOMPLETE", "A pinned rate table does not exist.");
            }

            var metadata = baseCompiled.Definition.Metadata;
            if (wantApproved)
            {
                // The data status is part of the artefact (and so of the hash PLT approves), not of the table hash: approving data promotes the artefact, the stored table rows are not rewritten.
                // TODO(hardening): table-level data status is not content-addressed, so an APPROVED artefact can pin tables whose own rows are still ILLUSTRATIVE_TEST_DATA.
                metadata = new ArtefactMetadata(DataStatus.Approved, NotATariff: false, "Approved rating data.");
            }
            else
            {
                metadata = metadata with { DataStatus = DataStatus.Illustrative };
            }

            var definition = baseCompiled.Definition with
            {
                ProductCode = changeSet.ProductCode,
                ProductVersion = changeSet.ProductVersion,
                Tables = pins,
                Metadata = metadata,
            };
            var compiled = CompiledArtefact.Compile(definition, h => dtos[h]);
            var hashValue = compiled.Hash.Value;
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO rat.rating_artifact (artefact_hash, artefact_code, label, product_code, product_version, engine_version, data_status, definition, created_at, created_by)
                VALUES (@hash, @code, @label, @product, @version, @engine, @status, @definition::jsonb, @now, @actor) ON CONFLICT DO NOTHING
                """,
                new
                {
                    hash = hashValue, code = definition.Code, label = definition.Label, product = definition.ProductCode, version = definition.ProductVersion,
                    engine = definition.EngineVersion, status = definition.Metadata.DataStatus, definition = ArtefactJson.Serialize(definition),
                    now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString(),
                }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            var existing = await connection.QueryFirstOrDefaultAsync<ReleaseRecord>(new CommandDefinition(
                $"SELECT {LifecycleSupport.ReleaseColumns} FROM rat.artifact_release WHERE artefact_hash = @hash", new { hash = hashValue }, session.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            ReleaseRecord release;
            if (existing is not null)
            {
                release = existing; // same inputs, same hash: the earlier release stands
            }
            else
            {
                release = new ReleaseRecord
                {
                    ReleaseId = EntityIds.NewGuid(), ChangeSetId = changeSet.ChangeSetId, ArtefactHash = hashValue, ProductCode = changeSet.ProductCode,
                    ProductVersion = changeSet.ProductVersion, Jurisdiction = changeSet.Jurisdiction.Trim(), DataStatus = definition.Metadata.DataStatus,
                    Status = LifecycleSupport.Built, BuiltBy = LifecycleSupport.ActorKey(context.Actor),
                };
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO rat.artifact_release (release_id, change_set_id, artefact_hash, legal_entity_id, jurisdiction, product_code, product_version, data_status,
                        status, built_by, built_at, record_version)
                    VALUES (@id, @changeSet, @hash, @legalEntity, @jurisdiction, @product, @version, @dataStatus, 'BUILT', @builtBy, @now, 1)
                    """,
                    new
                    {
                        id = release.ReleaseId, changeSet = release.ChangeSetId, hash = hashValue, legalEntity, jurisdiction = release.Jurisdiction, product = release.ProductCode,
                        version = release.ProductVersion, dataStatus = release.DataStatus, builtBy = release.BuiltBy, now = clock.Now.ToUtcDateTime(),
                    }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE rat.change_set SET status = 'BUILT', record_version = record_version + 1, updated_at = @now, updated_by = @actor WHERE change_set_id = @id",
                new { id = changeSet.ChangeSetId, now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString() }, session.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return Response(release, created: inserted == 1);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    private static RatingArtifactBuildResponse Response(ReleaseRecord release, bool created) => new()
    {
        ArtefactHash = Sha256Hash.Parse(release.ArtefactHash),
        ArtefactStatus = LifecycleSupport.Lifecycle<RatingArtifactBuildResponse.ArtefactStatusValue>(LifecycleSupport.LifecycleOf(release.Status)),
        GovernanceBundleId = release.ReleaseId,
        DataStatus = release.DataStatus,
        Created = created,
    };

    private static async Task<Dictionary<string, TableDto>> LoadTablesAsync(IDbConnection connection, string[] hashes, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<TableRecord>(new CommandDefinition(
            "SELECT table_hash AS Hash, definition::text AS Definition FROM rat.rate_table_version WHERE table_hash = ANY(@hashes)", new { hashes }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Hash, r => ArtefactJson.Deserialize<TableDto>(r.Definition), StringComparer.Ordinal);
    }

    private sealed class ChangeSetRecord
    {
        public Guid ChangeSetId { get; set; }

        public string ProductCode { get; set; } = string.Empty;

        public string ProductVersion { get; set; } = string.Empty;

        public string? BaseArtefactHash { get; set; }

        public string Tables { get; set; } = "{}";

        public string Status { get; set; } = string.Empty;

        public string Jurisdiction { get; set; } = string.Empty;
    }
}

internal sealed class BuildRatingArtifactAuditor : ICommandAuditor<BuildRatingArtifact, RatingArtifactBuildResponse>
{
    public CommandAuditFacts Describe(BuildRatingArtifact command, Result<RatingArtifactBuildResponse>? result) =>
        LifecycleSupport.Facts(
            result is { IsSuccess: true } ok ? ok.Value.ArtefactHash.Value : command.Request.ChangeSetId.ToString("D"),
            null,
            result is { IsSuccess: true } done ? new { status = done.Value.ArtefactStatus.ToString(), dataStatus = done.Value.DataStatus, created = done.Value.Created } : null);
}

// ---------------------------------------------------------------------------------------------------------------------------------
// rat.RatingArtifact.submit

internal sealed record SubmitRatingArtifact(RatingArtifactSubmitRequest Request) : ICommand<RatingArtifactSubmitResponse>;

internal sealed class SubmitRatingArtifactValidator : AbstractValidator<SubmitRatingArtifact>
{
    public SubmitRatingArtifactValidator()
    {
        RuleFor(c => c.Request.Reason).MaximumLength(500);
    }
}

internal sealed class SubmitRatingArtifactHandler(
    DbSession session, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IPlatformApprovalService approvals) : ICommandHandler<SubmitRatingArtifact, RatingArtifactSubmitResponse>
{
    public async Task<Result<RatingArtifactSubmitResponse>> HandleAsync(SubmitRatingArtifact command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityId = LifecycleSupport.LegalEntityOf(context, legalEntities);
        var legalEntity = context.LegalEntity!.Value.Value;
        var release = await LifecycleSupport.LoadReleaseAsync(session, request.ArtefactHash, legalEntityId, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return LifecycleSupport.NotFound("The rating artefact does not exist (build it first).");
        }

        if (release.Status is not (LifecycleSupport.Built or LifecycleSupport.Returned))
        {
            return LifecycleSupport.State($"The artefact is {release.Status}; only a built or returned artefact can be submitted.");
        }

        // TODO(hardening): a failing golden suite blocks submit (BO-RAT-07, D-REG-03: RAT-ERR-EVIDENCE-INCOMPLETE).
        var hash = LifecycleSupport.ContentHash(legalEntity, release.Jurisdiction, release);
        var maker = LifecycleSupport.ActorKey(context.Actor);
        var editors = new List<string> { maker };
        if (context.OnBehalfOf is { } principal)
        {
            editors.Add(LifecycleSupport.ActorKey(principal));
        }

        if (!editors.Contains(release.BuiltBy, StringComparer.Ordinal))
        {
            editors.Add(release.BuiltBy);
        }

        ApprovalRequestResponse approval;
        try
        {
            approval = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = RatingAuthorityTypes.ReleaseApprovalType,
                    ObjectRef = LifecycleSupport.Subject(release.ReleaseId),
                    PayloadHash = hash,
                    Diff = JsonSerializer.SerializeToElement(new { legalEntityId, artefactHash = release.ArtefactHash }, SharedKernelJson.Options),
                    Editors = editors,
                    Authority = new ApprovalAuthority
                    {
                        Type = RatingAuthorityTypes.Release.Value,
                        Codes = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [RatingAuthorityTypes.ProductDimension] = release.ProductCode,
                            [RatingAuthorityTypes.JurisdictionDimension] = release.Jurisdiction,
                        },
                    },
                    ReferralRole = RatingAuthorityTypes.CheckerRole,
                    Reason = request.Reason ?? $"Release of rating artefact {release.ArtefactHash[..12]} for {release.ProductCode} {release.ProductVersion}.",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE rat.artifact_release SET status = 'SUBMITTED', payload_hash = @hash, approval_request_id = @approval, submitted_by = @maker, submitted_by_principal = @principal,
                   submitted_at = @now, decided_by = NULL, decided_at = NULL, decision_comment = NULL, record_version = record_version + 1
             WHERE release_id = @id
            """,
            new
            {
                id = release.ReleaseId, hash = hash.Value, approval = approval.Request.RequestId, maker,
                principal = context.OnBehalfOf is { } p ? LifecycleSupport.ActorKey(p) : null, now = clock.Now.ToUtcDateTime(),
            }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new RatingArtifactSubmitResponse
        {
            ArtefactHash = request.ArtefactHash,
            ArtefactStatus = RatingArtifactSubmitResponse.ArtefactStatusValue.Submitted,
            GovernanceBundleId = release.ReleaseId,
            ApprovalRequestId = approval.Request.RequestId.ToString("D"),
        };
    }
}

internal sealed class SubmitRatingArtifactAuditor : ICommandAuditor<SubmitRatingArtifact, RatingArtifactSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitRatingArtifact command, Result<RatingArtifactSubmitResponse>? result) =>
        LifecycleSupport.Facts(command.Request.ArtefactHash.Value, new { status = LifecycleSupport.Built }, result is { IsSuccess: true } ? new { status = LifecycleSupport.Submitted } : null);
}

// ---------------------------------------------------------------------------------------------------------------------------------
// The checker's decision, shared by return (reject) and schedule (approve)

internal sealed partial class ReleaseDecider(RequestContext context, IPlatformApprovalService approvals, ILogger<ReleaseDecider> logger)
{
    /// <summary>Order: four eyes and a person; the PLT request is this release's (type, subject, hash); PLT decides (authority, SoD, hash); an approval verifies for exactly this subject, type and hash.</summary>
    public async Task<Result<(string DecidedBy, Instant DecidedAt, string? Comment)>> DecideAsync(
        ReleaseRecord release, bool approve, string? comment, CancellationToken cancellationToken)
    {
        if (LifecycleSupport.CheckChecker(context, release) is { } notChecker)
        {
            return notChecker;
        }

        if (release.ApprovalRequestId is not { } requestId || release.PayloadHash is null)
        {
            return LifecycleSupport.State("The artefact has no approval request; submit it first.");
        }

        var subject = LifecycleSupport.Subject(release.ReleaseId);
        var hash = Sha256Hash.Parse(release.PayloadHash);
        try
        {
            var plt = await approvals.GetAsync(requestId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (plt.Request.Type != RatingAuthorityTypes.ReleaseApprovalType || plt.Request.ObjectRef != subject || plt.Request.PayloadHash != hash)
            {
                LogRefused(logger, release.ReleaseId, "the PLT request is not bound to this release");
                return LifecycleSupport.Sod("The approval request is not bound to this release.");
            }

            if (plt.Request.Status != ApprovalStatus.PendingApproval)
            {
                return LifecycleSupport.State($"The approval request is {plt.Request.Status}; resubmit the artefact.");
            }

            var decided = await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId,
                    Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                    PayloadHash = hash,
                    Comment = comment,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            if (approve)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = RatingAuthorityTypes.ReleaseApprovalType, ObjectRef = subject },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok
                    || verified.Authority.Type != RatingAuthorityTypes.Release.Value
                    || verified.Authority.Codes?.GetValueOrDefault(RatingAuthorityTypes.ProductDimension) != release.ProductCode
                    || verified.Authority.Codes?.GetValueOrDefault(RatingAuthorityTypes.JurisdictionDimension) != release.Jurisdiction)
                {
                    LogRefused(logger, release.ReleaseId, "the decided authority does not match");
                    return LifecycleSupport.Sod("The approval is not decided under the release authority for this product and jurisdiction.");
                }
            }

            return (LifecycleSupport.ActorKey(context.Actor), decided.Decision.DecidedAt, comment);
        }
        catch (DomainException ex)
        {
            LogRefused(logger, release.ReleaseId, ex.Error.Code.Value);
            return ex.Error;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: rating release {ReleaseId} not decided: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid releaseId, string reason);
}

// ---------------------------------------------------------------------------------------------------------------------------------
// rat.RatingArtifact.return

internal sealed record ReturnRatingArtifact(RatingArtifactReturnRequest Request) : ICommand<RatingArtifactReturnResponse>;

internal sealed class ReturnRatingArtifactValidator : AbstractValidator<ReturnRatingArtifact>
{
    public ReturnRatingArtifactValidator()
    {
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class ReturnRatingArtifactHandler(
    DbSession session, RequestContext context, ILegalEntityDirectory legalEntities, ReleaseDecider decider) : ICommandHandler<ReturnRatingArtifact, RatingArtifactReturnResponse>
{
    public async Task<Result<RatingArtifactReturnResponse>> HandleAsync(ReturnRatingArtifact command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var release = await LifecycleSupport.LoadReleaseAsync(session, request.ArtefactHash, LifecycleSupport.LegalEntityOf(context, legalEntities), cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return LifecycleSupport.NotFound("The rating artefact does not exist.");
        }

        if (release.Status != LifecycleSupport.Submitted)
        {
            return LifecycleSupport.State($"The artefact is {release.Status}; only a submitted artefact can be returned.");
        }

        var decision = await decider.DecideAsync(release, approve: false, request.Reason, cancellationToken).ConfigureAwait(false);
        if (decision.IsFailure)
        {
            return decision.Error!;
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE rat.artifact_release SET status = 'RETURNED', decided_by = @by, decided_at = @at, decision_comment = @comment, record_version = record_version + 1 WHERE release_id = @id",
            new { id = release.ReleaseId, by = decision.Value.DecidedBy, at = decision.Value.DecidedAt.ToUtcDateTime(), comment = request.Reason }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new RatingArtifactReturnResponse
        {
            ArtefactHash = request.ArtefactHash,
            ArtefactStatus = RatingArtifactReturnResponse.ArtefactStatusValue.Draft,
            GovernanceBundleId = release.ReleaseId,
        };
    }
}

internal sealed class ReturnRatingArtifactAuditor : ICommandAuditor<ReturnRatingArtifact, RatingArtifactReturnResponse>
{
    public CommandAuditFacts Describe(ReturnRatingArtifact command, Result<RatingArtifactReturnResponse>? result) =>
        LifecycleSupport.Facts(command.Request.ArtefactHash.Value, new { status = LifecycleSupport.Submitted }, result is { IsSuccess: true } ? new { status = LifecycleSupport.Returned } : null);
}

// ---------------------------------------------------------------------------------------------------------------------------------
// rat.RatingArtifact.schedule (the checker approves and sets the effective date; today activates at once)

internal sealed record ScheduleRatingArtifact(RatingArtifactScheduleRequest Request) : ICommand<RatingArtifactScheduleResponse>;

internal sealed class ScheduleRatingArtifactValidator : AbstractValidator<ScheduleRatingArtifact>
{
    public ScheduleRatingArtifactValidator()
    {
        RuleFor(c => c.Request.Reason).MaximumLength(500);
        RuleFor(c => c.Request.EffectiveFrom).Must(d => d is null || DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .WithMessage("effectiveFrom must be a date, yyyy-MM-dd.");
    }
}

internal sealed class ScheduleRatingArtifactHandler(
    DbSession session, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, ReleaseDecider decider, IEventPublisher events) : ICommandHandler<ScheduleRatingArtifact, RatingArtifactScheduleResponse>
{
    public async Task<Result<RatingArtifactScheduleResponse>> HandleAsync(ScheduleRatingArtifact command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = context.LegalEntity!.Value;
        var release = await LifecycleSupport.LoadReleaseAsync(session, request.ArtefactHash, LifecycleSupport.LegalEntityOf(context, legalEntities), cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return LifecycleSupport.NotFound("The rating artefact does not exist.");
        }

        if (release.Status is not (LifecycleSupport.Submitted or LifecycleSupport.Approved))
        {
            return LifecycleSupport.State($"The artefact is {release.Status}; only a submitted or approved artefact can be scheduled.");
        }

        var today = LifecycleSupport.Today(clock);
        var from = request.EffectiveFrom is null ? today : DateOnly.ParseExact(request.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (from < today)
        {
            return LifecycleSupport.Invalid("effectiveFrom cannot be in the past; an activation never reaches back.");
        }

        if (release.DataStatus != DataStatus.Approved && !legalEntity.Value.EndsWith("-TEST", StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.RAT, "DATA-STATUS", "Only artefacts built on APPROVED rating data can be activated outside a test legal entity.");
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product = release.ProductCode, version = release.ProductVersion });
        args.Add("from", from, DbType.Date);
        var later = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM rat.rate_activation WHERE product_code = @product AND product_version = @version AND status = 'Active' AND effective_from >= @from)",
            args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (later)
        {
            return LifecycleSupport.State("Another activation of this product version starts on or after that date; cancel it first, activations are linear.");
        }

        var wasSubmitted = release.Status == LifecycleSupport.Submitted;
        var decidedAt = clock.Now;
        if (wasSubmitted)
        {
            var decision = await decider.DecideAsync(release, approve: true, request.Reason ?? "Approved and scheduled.", cancellationToken).ConfigureAwait(false);
            if (decision.IsFailure)
            {
                return decision.Error!;
            }

            decidedAt = decision.Value.DecidedAt;
        }
        else if (LifecycleSupport.CheckChecker(context, release) is { } notChecker)
        {
            return notChecker;
        }

        var predecessor = await connection.QueryFirstOrDefaultAsync<ActivationRecord>(new CommandDefinition(
            """
            SELECT activation_id AS ActivationId, artefact_hash AS ArtefactHash, effective_from AS EffectiveFrom, effective_to AS EffectiveTo
              FROM rat.rate_activation WHERE product_code = @product AND product_version = @version AND status = 'Active' AND effective_from < @from
               AND (effective_to IS NULL OR effective_to > @from) ORDER BY effective_from DESC LIMIT 1
            """,
            args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (predecessor is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE rat.rate_activation SET effective_to = @from WHERE activation_id = @id",
                CloseArgs(predecessor.ActivationId, from), session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var activationId = Guid.CreateVersion7();
        var insert = new DynamicParameters(new { id = activationId, hash = release.ArtefactHash, product = release.ProductCode, version = release.ProductVersion, now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString() });
        insert.Add("from", from, DbType.Date);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO rat.rate_activation (activation_id, artefact_hash, product_code, product_version, effective_from, effective_to, status, created_at, created_by)
            VALUES (@id, @hash, @product, @version, @from, NULL, 'Active', @now, @actor)
            """,
            insert, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var update = new DynamicParameters(new { id = release.ReleaseId, activation = activationId, at = decidedAt.ToUtcDateTime(), by = LifecycleSupport.ActorKey(context.Actor), comment = request.Reason ?? "Approved and scheduled." });
        update.Add("from", from, DbType.Date);
        var decidedSql = wasSubmitted ? ", decided_by = @by, decided_at = @at, decision_comment = @comment" : string.Empty;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE rat.artifact_release SET status = 'SCHEDULED', activation_id = @activation, effective_from = @from, record_version = record_version + 1" + decidedSql + " WHERE release_id = @id",
            update, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var definition = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT definition::text FROM rat.rating_artifact WHERE artefact_hash = @hash", new { hash = release.ArtefactHash }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var artefact = ArtefactJson.Deserialize<ArtefactDefinition>(definition!);
        var version = ProductVersionNumber.Parse(release.ProductVersion);
        var hash = Sha256Hash.Parse(release.ArtefactHash);
        var keys = LifecycleSupport.SlotKeys(release.ProductCode, release.ProductVersion);
        var slotId = LifecycleSupport.RatingSlotKey(release.ProductCode, release.ProductVersion);
        var effective = Instant.FromUtc(from.Year, from.Month, from.Day);
        if (wasSubmitted)
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(RatingArtifactPublishedV1.Descriptor), "RatingSlot", slotId,
                new RatingArtifactPublishedV1
                {
                    ArtefactHash = hash,
                    Label = artefact.Label,
                    Slots = [.. artefact.Tables.Keys.OrderBy(k => k, StringComparer.Ordinal)],
                    CompatibleProductVersions = [new ProductVersionRef { ProductCode = release.ProductCode, Version = version, ArtefactHash = hash }],
                    InputSchemaVersion = artefact.Schema,
                    ApprovalRefs = [release.ApprovalRequestId!.Value],
                    State = "APPROVED",
                },
                keys));
        }

        if (from > today)
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(RateVersionScheduledV1.Descriptor), "RatingSlot", slotId,
                new RateVersionScheduledV1
                {
                    ArtefactHash = hash, Slot = artefact.Code, TransactionType = "NEW_BUSINESS", EffectiveFrom = effective, ChannelScope = [],
                    Action = RateVersionScheduledV1.ActionValue.Scheduled, Reason = request.Reason,
                },
                keys));
        }
        else
        {
            // TODO(hardening): a future-dated activation emits RateVersionActivatedV1 only when its date arrives (a due-date sweeper, like PFC's ReleaseSweeper); this slice emits it when the date is today.
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(RateVersionActivatedV1.Descriptor), "RatingSlot", slotId,
                new RateVersionActivatedV1
                {
                    ArtefactHash = hash, Slot = artefact.Code, TransactionType = "NEW_BUSINESS", EffectiveFrom = effective,
                    PredecessorHash = predecessor is null ? null : Sha256Hash.Parse(predecessor.ArtefactHash),
                },
                keys));
        }

        return new RatingArtifactScheduleResponse
        {
            ArtefactHash = request.ArtefactHash,
            ArtefactStatus = from > today ? RatingArtifactScheduleResponse.ArtefactStatusValue.Scheduled : RatingArtifactScheduleResponse.ArtefactStatusValue.Active,
            GovernanceBundleId = release.ReleaseId,
            ActivationId = activationId.ToString("D"),
            EffectiveFrom = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    internal static DynamicParameters CloseArgs(Guid id, DateOnly from)
    {
        var args = new DynamicParameters(new { id });
        args.Add("from", from, DbType.Date);
        return args;
    }
}

internal sealed class ScheduleRatingArtifactAuditor : ICommandAuditor<ScheduleRatingArtifact, RatingArtifactScheduleResponse>
{
    public CommandAuditFacts Describe(ScheduleRatingArtifact command, Result<RatingArtifactScheduleResponse>? result) =>
        LifecycleSupport.Facts(
            command.Request.ArtefactHash.Value, new { status = LifecycleSupport.Submitted },
            result is { IsSuccess: true } ok ? new { status = ok.Value.ArtefactStatus.ToString(), effectiveFrom = ok.Value.EffectiveFrom } : null);
}

// ---------------------------------------------------------------------------------------------------------------------------------
// rat.RatingArtifact.cancelSchedule

internal sealed record CancelRatingSchedule(RatingArtifactCancelScheduleRequest Request) : ICommand<RatingArtifactCancelScheduleResponse>;

internal sealed class CancelRatingScheduleValidator : AbstractValidator<CancelRatingSchedule>
{
    public CancelRatingScheduleValidator()
    {
        RuleFor(c => c.Request.Reason).MaximumLength(500);
    }
}

internal sealed class CancelRatingScheduleHandler(
    DbSession session, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IEventPublisher events) : ICommandHandler<CancelRatingSchedule, RatingArtifactCancelScheduleResponse>
{
    public async Task<Result<RatingArtifactCancelScheduleResponse>> HandleAsync(CancelRatingSchedule command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var release = await LifecycleSupport.LoadReleaseAsync(session, request.ArtefactHash, LifecycleSupport.LegalEntityOf(context, legalEntities), cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return LifecycleSupport.NotFound("The rating artefact does not exist.");
        }

        var today = LifecycleSupport.Today(clock);
        if (release.Status != LifecycleSupport.Scheduled || release.ActivationId is not { } activationId || release.EffectiveFrom is not { } fromDate || LifecycleSupport.Date(fromDate) <= today)
        {
            return LifecycleSupport.State("Only an artefact whose activation date is still ahead can be cancelled.");
        }

        var from = LifecycleSupport.Date(fromDate);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE rat.rate_activation SET status = 'Cancelled' WHERE activation_id = @id", new { id = activationId }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Re-open the activation this one closed.
        var reopen = new DynamicParameters(new { product = release.ProductCode, version = release.ProductVersion });
        reopen.Add("from", from, DbType.Date);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE rat.rate_activation SET effective_to = NULL WHERE product_code = @product AND product_version = @version AND status = 'Active' AND effective_to = @from",
            reopen, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE rat.artifact_release SET status = 'APPROVED', activation_id = NULL, effective_from = NULL, record_version = record_version + 1 WHERE release_id = @id",
            new { id = release.ReleaseId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var definition = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT definition::text FROM rat.rating_artifact WHERE artefact_hash = @hash", new { hash = release.ArtefactHash }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var artefact = ArtefactJson.Deserialize<ArtefactDefinition>(definition!);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RateVersionScheduledV1.Descriptor), "RatingSlot", LifecycleSupport.RatingSlotKey(release.ProductCode, release.ProductVersion),
            new RateVersionScheduledV1
            {
                ArtefactHash = request.ArtefactHash, Slot = artefact.Code, TransactionType = "NEW_BUSINESS", EffectiveFrom = Instant.FromUtc(from.Year, from.Month, from.Day),
                ChannelScope = [], Action = RateVersionScheduledV1.ActionValue.Cancelled, Reason = request.Reason,
            },
            LifecycleSupport.SlotKeys(release.ProductCode, release.ProductVersion)));
        return new RatingArtifactCancelScheduleResponse
        {
            ArtefactHash = request.ArtefactHash,
            ArtefactStatus = RatingArtifactCancelScheduleResponse.ArtefactStatusValue.Approved,
            GovernanceBundleId = release.ReleaseId,
        };
    }
}

internal sealed class CancelRatingScheduleAuditor : ICommandAuditor<CancelRatingSchedule, RatingArtifactCancelScheduleResponse>
{
    public CommandAuditFacts Describe(CancelRatingSchedule command, Result<RatingArtifactCancelScheduleResponse>? result) =>
        LifecycleSupport.Facts(command.Request.ArtefactHash.Value, new { status = LifecycleSupport.Scheduled }, result is { IsSuccess: true } ? new { status = LifecycleSupport.Approved } : null);
}

// ---------------------------------------------------------------------------------------------------------------------------------
// rat.RatingArtifact.rollback

internal sealed record RollbackRatingArtifact(RatingArtifactRollbackRequest Request) : ICommand<RatingArtifactRollbackResponse>;

internal sealed class RollbackRatingArtifactValidator : AbstractValidator<RollbackRatingArtifact>
{
    public RollbackRatingArtifactValidator()
    {
        RuleFor(c => c.Request.ProductCode).NotEmpty();
        RuleFor(c => c.Request.ProductVersion).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>
/// Withdraws the artefact in force for a product version and re-activates the one before it. Nothing is deleted or edited in an artefact: the
/// withdrawn activation becomes RolledBack, the prior one is re-opened, so the history stays and worksheets pinned to either artefact are untouched (REQ-POL-093).
/// </summary>
internal sealed class RollbackRatingArtifactHandler(
    DbSession session, IClock clock, IEventPublisher events) : ICommandHandler<RollbackRatingArtifact, RatingArtifactRollbackResponse>
{
    public async Task<Result<RatingArtifactRollbackResponse>> HandleAsync(RollbackRatingArtifact command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var today = LifecycleSupport.Today(clock);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product = request.ProductCode, version = request.ProductVersion });
        args.Add("today", today, DbType.Date);
        var current = await connection.QueryFirstOrDefaultAsync<ActivationRecord>(new CommandDefinition(
            """
            SELECT activation_id AS ActivationId, artefact_hash AS ArtefactHash, effective_from AS EffectiveFrom, effective_to AS EffectiveTo
              FROM rat.rate_activation WHERE product_code = @product AND product_version = @version AND status = 'Active'
               AND effective_from <= @today AND (effective_to IS NULL OR effective_to > @today) ORDER BY effective_from DESC LIMIT 1 FOR UPDATE
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (current is null)
        {
            return LifecycleSupport.State("The product version has no artefact in force to roll back.");
        }

        var prior = await connection.QueryFirstOrDefaultAsync<ActivationRecord>(new CommandDefinition(
            """
            SELECT activation_id AS ActivationId, artefact_hash AS ArtefactHash, effective_from AS EffectiveFrom, effective_to AS EffectiveTo
              FROM rat.rate_activation WHERE product_code = @product AND product_version = @version AND status = 'Active'
               AND effective_from < @currentFrom ORDER BY effective_from DESC LIMIT 1 FOR UPDATE
            """,
            CurrentFrom(args, current), session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (prior is null)
        {
            return LifecycleSupport.State("There is no earlier activation to roll back to.");
        }

        // A scheduled (future) activation after the current one stays; only what is in force is withdrawn.
        await connection.ExecuteAsync(new CommandDefinition("UPDATE rat.rate_activation SET status = 'RolledBack' WHERE activation_id = @id", new { id = current.ActivationId }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE rat.rate_activation SET effective_to = NULL WHERE activation_id = @id", new { id = prior.ActivationId }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var release = await connection.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            "UPDATE rat.artifact_release SET status = 'ROLLED_BACK', decision_comment = COALESCE(@reason, decision_comment), record_version = record_version + 1 WHERE artefact_hash = @hash RETURNING release_id",
            new { hash = current.ArtefactHash, reason = request.Reason }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var bundle = release ?? EntityIds.NewGuid();

        var definition = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT definition::text FROM rat.rating_artifact WHERE artefact_hash = @hash", new { hash = current.ArtefactHash }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var artefact = ArtefactJson.Deserialize<ArtefactDefinition>(definition!);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RateVersionWithdrawnV1.Descriptor), "RatingSlot", LifecycleSupport.RatingSlotKey(request.ProductCode, request.ProductVersion),
            new RateVersionWithdrawnV1
            {
                WithdrawnHash = Sha256Hash.Parse(current.ArtefactHash),
                ReactivatedHash = Sha256Hash.Parse(prior.ArtefactHash),
                Slots = [.. artefact.Tables.Keys.OrderBy(k => k, StringComparer.Ordinal)],
                Reason = request.Reason,
                // TODO(hardening): count the quotes and transactions rated under the withdrawn artefact (pinned worksheets stay valid; the counts feed the impact notice).
                AffectedQuoteCount = 0,
                AffectedTransactionCount = 0,
            },
            LifecycleSupport.SlotKeys(request.ProductCode, request.ProductVersion)));
        // TODO(hardening): rollback is a single permissioned step here; the PRD asks for maker-checker (RAT.EmergencyRateChange approval) on it.
        return new RatingArtifactRollbackResponse
        {
            WithdrawnHash = Sha256Hash.Parse(current.ArtefactHash),
            ReactivatedHash = Sha256Hash.Parse(prior.ArtefactHash),
            ArtefactStatus = RatingArtifactRollbackResponse.ArtefactStatusValue.Active,
            GovernanceBundleId = bundle,
        };
    }

    private static DynamicParameters CurrentFrom(DynamicParameters template, ActivationRecord current)
    {
        var args = new DynamicParameters(template);
        args.Add("currentFrom", DateOnly.FromDateTime(current.EffectiveFrom), DbType.Date);
        return args;
    }
}

internal sealed class RollbackRatingArtifactAuditor : ICommandAuditor<RollbackRatingArtifact, RatingArtifactRollbackResponse>
{
    public CommandAuditFacts Describe(RollbackRatingArtifact command, Result<RatingArtifactRollbackResponse>? result) =>
        LifecycleSupport.Facts(
            result is { IsSuccess: true } ok ? ok.Value.WithdrawnHash.Value : command.Request.ProductCode + "/" + command.Request.ProductVersion,
            new { status = "ACTIVE" },
            result is { IsSuccess: true } done ? new { status = "ROLLED_BACK", reactivated = done.Value.ReactivatedHash.Value, reason = command.Request.Reason } : null);
}
