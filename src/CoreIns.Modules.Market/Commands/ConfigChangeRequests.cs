using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Market.Commands;

internal static class ConfigChangeSupport
{
    public const string ApprovalType = "MKT.ConfigChange";
    public const string Authority = "MKT_CONFIG_CHANGE";
    public const string Draft = "DRAFT";
    public const string Pending = "PENDING_APPROVAL";
    public const string Applied = "APPLIED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";

    public static ObjectRef Subject(Guid id) => new(ModuleCode.MKT, "ConfigChangeRequest", id.ToString("D"));

    public static DomainException Refuse(string code, string detail) => new(DomainError.Of(ModuleCode.MKT, code, detail));

    /// <summary>The content the approval is bound to: everything the checker decides on, frozen at submit.</summary>
    public static Sha256Hash Proof(ConfigChangeRequestRow row, string entity)
    {
        var items = new JsonArray();
        foreach (var i in row.Items.OrderBy(i => i.LineNo))
        {
            items.Add(new JsonObject
            {
                ["key"] = i.Key, ["value"] = i.Value, ["legalStatus"] = i.LegalStatus, ["sourceRef"] = i.SourceRef,
                ["motorPath"] = i.MotorPath, ["validFrom"] = i.ValidFrom?.ToString(), ["validTo"] = i.ValidTo?.ToString(), ["note"] = i.Note,
            });
        }

        return CanonicalJson.Hash(new JsonObject
        {
            ["requestId"] = row.Id.ToString("D"), ["legalEntity"] = entity, ["country"] = row.Country, ["baseHash"] = row.BaseHash.Trim(),
            ["reason"] = row.Reason, ["maker"] = row.CreatedBy, ["items"] = items,
        });
    }

    public static ConfigChangeRequestView View(ConfigChangeRequestRow row) => new()
    {
        Id = row.Id,
        Status = row.Status switch
        {
            Draft => ConfigChangeRequestView.StatusValue.Draft,
            Pending => ConfigChangeRequestView.StatusValue.PendingApproval,
            Applied => ConfigChangeRequestView.StatusValue.Applied,
            Rejected => ConfigChangeRequestView.StatusValue.Rejected,
            _ => ConfigChangeRequestView.StatusValue.Withdrawn,
        },
        Reason = row.Reason, Country = row.Country.Trim(), BaseHash = Sha256Hash.Parse(row.BaseHash.Trim()),
        Items = [.. row.Items.OrderBy(i => i.LineNo).Select(i => new ConfigChangeItemView
        {
            Key = i.Key, Value = i.Value, LegalStatus = i.LegalStatus, SourceRef = i.SourceRef, MotorPath = i.MotorPath,
            ValidFrom = i.ValidFrom, ValidTo = i.ValidTo, Note = i.Note,
        })],
        CreatedBy = row.CreatedBy, DecidedBy = row.DecidedBy, DecisionReason = row.DecisionReason,
        ApprovalRequestId = row.ApprovalRequestId is { } a ? new ApprovalRequestId(a) : null,
        ResultingHash = row.ResultingHash is { } h ? Sha256Hash.Parse(h.Trim()) : null,
        AppliedAt = row.AppliedAt, CreatedAt = row.CreatedAt, RecordVersion = row.RecordVersion,
    };
}

internal sealed record CreateConfigChange(ConfigChangeRequestCreateRequest Request) : ICommand<ConfigChangeRequestCreateResponse>;
internal sealed record UpdateConfigChange(Guid Id, ConfigChangeRequestUpdateRequest Request) : ICommand<ConfigChangeRequestUpdateResponse>;
internal sealed record SubmitConfigChange(ConfigChangeRequestSubmitRequest Request) : ICommand<ConfigChangeRequestSubmitResponse>;
internal sealed record ApproveConfigChange(ConfigChangeRequestApproveRequest Request) : ICommand<ConfigChangeRequestApproveResponse>;
internal sealed record RejectConfigChange(ConfigChangeRequestRejectRequest Request) : ICommand<ConfigChangeRequestRejectResponse>;
internal sealed record WithdrawConfigChange(ConfigChangeRequestWithdrawRequest Request) : ICommand<ConfigChangeRequestWithdrawResponse>;

/// <summary>
/// Create, update, submit, decide and withdraw of configuration change requests (BO-MKT-01, REQ-MKT-052..061). Every operation joins the command
/// transaction. Approval goes through the platform approval service (separation of duties); applying an approved request appends an overlay pack
/// version and a configuration state under the state lock in the same transaction, so the change and its new hash are atomic.
/// </summary>
internal sealed class ConfigChangeEngine(MarketDbContext db, DbSession session, RequestContext context, IClock clock,
    IPlatformApprovalService approvals, IConfigurationStates states, IHostEnvironment environment)
{
    private static string Actor(ActorRef actor) => $"{actor.KindCode}:{actor.Id}";

    private string Maker => Actor(context.Actor);

    private string? MakerPrincipal => context.OnBehalfOf is { } p ? Actor(p) : null;

    private string EntityCode => context.LegalEntity?.Value ?? throw new InvalidOperationException("No authenticated legal entity.");

    private void RequireHuman()
    {
        if (context.Actor.Kind != ActorKind.User)
        {
            throw ConfigChangeSupport.Refuse("SOD-VIOLATION", "A human must make or decide a configuration change.");
        }
    }

    private async Task<LegalEntityRow> EntityAsync(CancellationToken ct) =>
        await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Code == EntityCode && e.Status == "ACTIVE", ct).ConfigureAwait(false)
            ?? throw ConfigChangeSupport.Refuse("CFG-LEGAL-ENTITY-UNKNOWN", "The active legal entity is not in the registry.");

    private async Task<ConfigChangeRequestRow> LoadAsync(Guid id, LegalEntityRow entity, CancellationToken ct) =>
        await db.ConfigChangeRequests.Include(r => r.Items).SingleOrDefaultAsync(r => r.Id == id && r.LegalEntityId == entity.LegalEntityId, ct).ConfigureAwait(false)
            ?? throw ConfigChangeSupport.Refuse("CFG-REQUEST-NOT-FOUND", "The configuration change request does not exist.");

    private async Task<(ConfigStateRow State, ConfigStateManifest Manifest)> NewestAsync(CancellationToken ct)
    {
        var state = await db.ConfigStates.AsNoTracking().OrderByDescending(s => s.Seq).FirstAsync(ct).ConfigureAwait(false);
        var manifest = ConfigStateManifest.FromJson(JsonNode.Parse(state.Manifest));
        if (manifest.Hash.ToString() != state.Hash.Trim())
        {
            throw ConfigChangeSupport.Refuse("STALE", "The current manifest failed its integrity check.");
        }

        return (state, manifest);
    }

    private void RequireMaker(ConfigChangeRequestRow row)
    {
        if (row.CreatedBy != Maker)
        {
            throw ConfigChangeSupport.Refuse("SOD-VIOLATION", "Only the maker may change or withdraw the request.");
        }
    }

    /// <summary>Validates the items against the key registry and builds the values. Unknown key, bad value, bad dates or a non-Settled value in Production are refused.</summary>
    private List<PackConfigValue> Values(IReadOnlyList<ConfigChangeItemInput> items, string country, bool forApply)
    {
        if (items.Count is 0 or > 50)
        {
            throw ConfigChangeSupport.Refuse("CFG-VALIDATION", "A request holds 1 to 50 items.");
        }

        var values = new List<PackConfigValue>();
        foreach (var item in items)
        {
            var descriptor = ConfigKeys.Find(item.Key)
                ?? throw ConfigChangeSupport.Refuse("CFG-UNKNOWN-KEY", $"'{item.Key}' is not a registered configuration key.");
            if (!Enum.TryParse<LegalStatus>(item.LegalStatus.ToString(), out var legal))
            {
                throw ConfigChangeSupport.Refuse("CFG-VALIDATION", $"Unknown legal status for '{item.Key}'.");
            }

            if (environment.IsProduction() && legal is not (LegalStatus.Settled or LegalStatus.NotRegulatory))
            {
                throw ConfigChangeSupport.Refuse("CFG-NOT-SETTLED", $"'{item.Key}' has legal status {legal}; Production refuses values that are not Settled.");
            }

            if (item.ValidFrom is { } from && item.ValidTo is { } to && to <= from)
            {
                throw ConfigChangeSupport.Refuse("CFG-VALIDATION", $"'{item.Key}': validTo must be after validFrom.");
            }

            values.Add(new PackConfigValue(item.Key, descriptor.Type, item.Value, legal, item.SourceRef.Trim(), item.MotorPath ?? false, item.ValidFrom, item.ValidTo, item.Note));
        }

        // Types, tax-treatment rows, source references and overlaps are checked by loading the values as a state of their own.
        _ = Candidate(country, "1.0.0", values, forApply ? "apply" : "request");
        return values;
    }

    private PackVersionContent Candidate(string country, string version, List<PackConfigValue> values, string what)
    {
        var content = PackVersionContent.Of(ConfigOverlay.PackIdFor(country), version, country, values);
        try
        {
            var manifest = new ConfigStateManifest(
                [new ManifestPack(content.PackId, version, content.Digest, country)], CoreDefaults.Content.Digest, null, StateCauses.Genesis);
            _ = new ConfigurationCatalogue(new CatalogueState(manifest, CoreDefaults.Content, [content]), clock.Now);
        }
        catch (InvalidOperationException ex)
        {
            throw ConfigChangeSupport.Refuse("CFG-VALIDATION", $"The values of the {what} are not valid: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            throw ConfigChangeSupport.Refuse("CFG-VALIDATION", $"The values of the {what} are not valid: {ex.Message}");
        }

        return content;
    }

    private static List<ConfigChangeItemRow> Rows(Guid requestId, IReadOnlyList<ConfigChangeItemInput> items) =>
        [.. items.Select((i, n) => new ConfigChangeItemRow
        {
            RequestId = requestId, LineNo = n + 1, Key = i.Key, Value = i.Value, LegalStatus = i.LegalStatus.ToString(), SourceRef = i.SourceRef.Trim(),
            MotorPath = i.MotorPath ?? false, ValidFrom = i.ValidFrom, ValidTo = i.ValidTo, Note = i.Note,
        })];

    private static void CheckReason(string? reason, int min)
    {
        if (reason is null || reason.Trim().Length < min || reason.Length > 128)
        {
            throw ConfigChangeSupport.Refuse("CFG-VALIDATION", $"A reason of {min} to 128 characters is required.");
        }
    }

    public async Task<ConfigChangeRequestRow> CreateAsync(ConfigChangeRequestCreateRequest request, CancellationToken ct)
    {
        RequireHuman();
        CheckReason(request.Reason, 10);
        var entity = await EntityAsync(ct).ConfigureAwait(false);
        var (state, manifest) = await NewestAsync(ct).ConfigureAwait(false);
        var pack = manifest.Packs.SingleOrDefault(p => p.PackId == entity.PackId)
            ?? throw ConfigChangeSupport.Refuse("CFG-VALIDATION", "The current state does not name the legal entity's pack.");
        _ = Values(request.Items, pack.Country, forApply: false);
        var now = clock.Now;
        var id = Guid.CreateVersion7();
        var row = new ConfigChangeRequestRow
        {
            Id = id, LegalEntityId = entity.LegalEntityId, Country = pack.Country, Status = ConfigChangeSupport.Draft, Reason = request.Reason.Trim(),
            BaseHash = state.Hash.Trim(), CreatedBy = Maker, CreatedPrincipal = MakerPrincipal, CreatedAt = now, UpdatedAt = now,
            Items = Rows(id, request.Items),
        };
        if (!context.DryRun)
        {
            db.ConfigChangeRequests.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return row;
    }

    public async Task<ConfigChangeRequestRow> UpdateAsync(Guid id, ConfigChangeRequestUpdateRequest request, CancellationToken ct)
    {
        RequireHuman();
        var entity = await EntityAsync(ct).ConfigureAwait(false);
        var row = await LoadAsync(id, entity, ct).ConfigureAwait(false);
        RequireMaker(row);
        if (row.Status != ConfigChangeSupport.Draft)
        {
            throw ConfigChangeSupport.Refuse("STALE", "Only a DRAFT request can be changed.");
        }

        if (request.Reason is not null)
        {
            CheckReason(request.Reason, 10);
            row.Reason = request.Reason.Trim();
        }

        if (request.Items is not null)
        {
            _ = Values(request.Items, row.Country.Trim(), forApply: false);
            db.ConfigChangeItems.RemoveRange(row.Items);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            row.Items = Rows(row.Id, request.Items);
        }

        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    public async Task<ConfigChangeRequestRow> SubmitAsync(ConfigChangeRequestSubmitRequest request, CancellationToken ct)
    {
        RequireHuman();
        var entity = await EntityAsync(ct).ConfigureAwait(false);
        var row = await LoadAsync(request.RequestId, entity, ct).ConfigureAwait(false);
        RequireMaker(row);
        if (row.Status != ConfigChangeSupport.Draft)
        {
            throw ConfigChangeSupport.Refuse("STALE", "Only a DRAFT request can be submitted.");
        }

        _ = Values([.. row.Items.OrderBy(i => i.LineNo).Select(ToInput)], row.Country.Trim(), forApply: false);
        var hash = ConfigChangeSupport.Proof(row, entity.Code);
        var approval = await approvals.RequestAsync(new ApprovalRequestRequest
        {
            Type = ConfigChangeSupport.ApprovalType, ObjectRef = ConfigChangeSupport.Subject(row.Id), PayloadHash = hash,
            Diff = JsonSerializer.SerializeToElement(new { legalEntityId = entity.LegalEntityId.Value, keys = row.Items.Select(i => i.Key).ToArray() }),
            Editors = row.CreatedPrincipal is { } principal ? [row.CreatedBy, principal] : [row.CreatedBy],
            Authority = new ApprovalAuthority { Type = ConfigChangeSupport.Authority, Codes = new Dictionary<string, string> { ["legalEntity"] = entity.Code } },
            ReferralRole = "Platform.DesignAuthority", Reason = row.Reason,
        }, CommandOptions.New(), ct).ConfigureAwait(false);
        row.ApprovalRequestId = approval.Request.RequestId;
        row.ContentHash = hash.ToString();
        row.Status = ConfigChangeSupport.Pending;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    private static ConfigChangeItemInput ToInput(ConfigChangeItemRow i) => new()
    {
        Key = i.Key, Value = i.Value, SourceRef = i.SourceRef, MotorPath = i.MotorPath, ValidFrom = i.ValidFrom, ValidTo = i.ValidTo, Note = i.Note,
        LegalStatus = Enum.Parse<ConfigChangeItemInput.LegalStatusValue>(i.LegalStatus),
    };

    public async Task<ConfigChangeRequestRow> WithdrawAsync(ConfigChangeRequestWithdrawRequest request, CancellationToken ct)
    {
        RequireHuman();
        CheckReason(request.Reason, 1);
        var entity = await EntityAsync(ct).ConfigureAwait(false);
        var row = await LoadAsync(request.RequestId, entity, ct).ConfigureAwait(false);
        RequireMaker(row);
        if (row.Status is not (ConfigChangeSupport.Draft or ConfigChangeSupport.Pending))
        {
            throw ConfigChangeSupport.Refuse("STALE", "Only a DRAFT or PENDING_APPROVAL request can be withdrawn.");
        }

        if (row.Status == ConfigChangeSupport.Pending && row.ApprovalRequestId is { } approvalId)
        {
            _ = await approvals.WithdrawAsync(new ApprovalWithdrawRequest { ApprovalRequestId = new ApprovalRequestId(approvalId), Reason = request.Reason }, CommandOptions.New(), ct).ConfigureAwait(false);
        }

        row.Status = ConfigChangeSupport.Withdrawn;
        row.DecisionReason = request.Reason;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    public async Task<ConfigChangeRequestRow> DecideAsync(Guid requestId, string reason, bool approve, CancellationToken ct)
    {
        RequireHuman();
        CheckReason(reason, 1);
        var entity = await EntityAsync(ct).ConfigureAwait(false);
        await ConfigStateWriter.LockAsync(session.Connection, session.Transaction
            ?? throw new InvalidOperationException("Deciding requires a command transaction."), ct).ConfigureAwait(false);
        var row = await LoadAsync(requestId, entity, ct).ConfigureAwait(false);
        if (row.Status != ConfigChangeSupport.Pending || row.ContentHash is null || row.ApprovalRequestId is null)
        {
            throw ConfigChangeSupport.Refuse("STALE", "The request is not pending approval.");
        }

        var actor = Maker;
        var principal = MakerPrincipal;
        if (actor == row.CreatedBy || actor == row.CreatedPrincipal
            || (principal is not null && (principal == row.CreatedBy || principal == row.CreatedPrincipal)))
        {
            throw ConfigChangeSupport.Refuse("SOD-VIOLATION", "The checker must be a different human from the maker and maker principal.");
        }

        var proof = ConfigChangeSupport.Proof(row, entity.Code);
        if (proof.ToString() != row.ContentHash.Trim())
        {
            throw ConfigChangeSupport.Refuse("SOD-VIOLATION", "Frozen content no longer matches its approval hash.");
        }

        var decision = await approvals.DecideAsync(new ApprovalDecideRequest
        {
            RequestId = row.ApprovalRequestId.Value, PayloadHash = proof,
            Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject, Comment = reason,
        }, CommandOptions.New(), ct).ConfigureAwait(false);
        var now = decision.Decision.DecidedAt;
        if (approve)
        {
            var verified = await approvals.VerifyForExecutionAsync(new ApprovalVerifyForExecutionRequest
            { RequestId = row.ApprovalRequestId.Value, Hash = proof, Type = ConfigChangeSupport.ApprovalType, ObjectRef = ConfigChangeSupport.Subject(row.Id) }, ct).ConfigureAwait(false);
            if (!verified.Ok || verified.Authority.Type != ConfigChangeSupport.Authority || verified.Authority.Codes?.GetValueOrDefault("legalEntity") != entity.Code)
            {
                throw ConfigChangeSupport.Refuse("SOD-VIOLATION", "Native PLT proof does not bind the required configuration-change authority.");
            }

            await ApplyAsync(row, now, ct).ConfigureAwait(false);
        }
        else
        {
            row.Status = ConfigChangeSupport.Rejected;
        }

        row.DecidedBy = actor;
        row.DecisionReason = reason;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>Records the overlay pack version and the new configuration state for an approved request. Runs under the state lock, in the command transaction.</summary>
    private async Task ApplyAsync(ConfigChangeRequestRow row, Instant decidedAt, CancellationToken ct)
    {
        var country = row.Country.Trim();
        var (newest, manifest) = await NewestAsync(ct).ConfigureAwait(false);
        var items = row.Items.OrderBy(i => i.LineNo).Select(ToInput).ToList();
        var incoming = Values(items, country, forApply: true);

        // A key another change touched since the request was written needs a fresh request: the maker would otherwise approve against values he never saw.
        if (newest.Hash.Trim() != row.BaseHash.Trim())
        {
            var before = await states.ByHashAsync(ConfigurationHash.Parse(row.BaseHash.Trim()), ct).ConfigureAwait(false)
                ?? throw ConfigChangeSupport.Refuse("CFG-HASH-UNKNOWN", "The base configuration state is unknown.");
            var after = await states.ByHashAsync(ConfigurationHash.Parse(newest.Hash.Trim()), ct).ConfigureAwait(false)
                ?? throw ConfigChangeSupport.Refuse("CFG-HASH-UNKNOWN", "The current configuration state is unknown.");
            foreach (var key in incoming.Select(v => v.Key).Distinct(StringComparer.Ordinal))
            {
                if (Signature(before, key) != Signature(after, key))
                {
                    throw ConfigChangeSupport.Refuse("STALE", $"'{key}' changed since the request was written (base {row.BaseHash.Trim()[..12]}); reject it and make a new request.");
                }
            }
        }

        var overlayId = ConfigOverlay.PackIdFor(country);
        var existing = new List<PackConfigValue>();
        if (manifest.Packs.SingleOrDefault(p => p.PackId == overlayId) is { } named)
        {
            var current = await db.PackVersions.AsNoTracking().SingleAsync(v => v.PackId == overlayId && v.Version == named.Version, ct).ConfigureAwait(false);
            existing = PackVersionContent.FromJson(JsonNode.Parse(current.Values));
        }

        var version = $"1.0.{await db.PackVersions.CountAsync(v => v.PackId == overlayId, ct).ConfigureAwait(false) + 1}";
        var content = Candidate(country, version, ConfigOverlay.Apply(existing, incoming), "applied overlay");
        var at = decidedAt < newest.ActivatedAt ? newest.ActivatedAt : decidedAt;
        await PersistedConfigurationStates.RegisterAsync(session.Connection, session.Transaction!, content, at, ct).ConfigureAwait(false);
        var packs = manifest.Packs.Where(p => p.PackId != overlayId).Append(new ManifestPack(overlayId, version, content.Digest, country)).ToList();
        var next = new ConfigStateManifest(packs, manifest.CoreDigest, ConfigurationHash.Parse(newest.Hash.Trim()), StateCauses.ConfigChange);
        var hash = await ConfigStateWriter.AppendAsync(session.Connection, session.Transaction!, next, at, row.Id, ct).ConfigureAwait(false);
        row.Status = ConfigChangeSupport.Applied;
        row.AppliedAt = at;
        row.ResultingHash = hash.ToString();
    }

    private static string Signature(ConfigurationCatalogue catalogue, string key) =>
        string.Join(';', catalogue.Entries.Where(e => e.Key == key)
            .OrderBy(e => e.Node, StringComparer.Ordinal).ThenBy(e => e.ValidFrom?.ToString(), StringComparer.Ordinal)
            .Select(e => $"{e.Node}|{e.Value}|{e.LegalStatus}|{e.ValidFrom}|{e.ValidTo}"));
}

internal sealed class CreateConfigChangeHandler(ConfigChangeEngine engine) : ICommandHandler<CreateConfigChange, ConfigChangeRequestCreateResponse>
{
    public async Task<Result<ConfigChangeRequestCreateResponse>> HandleAsync(CreateConfigChange c, CancellationToken ct)
    {
        try { return new ConfigChangeRequestCreateResponse { Request = ConfigChangeSupport.View(await engine.CreateAsync(c.Request, ct).ConfigureAwait(false)) }; }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class UpdateConfigChangeHandler(ConfigChangeEngine engine) : ICommandHandler<UpdateConfigChange, ConfigChangeRequestUpdateResponse>
{
    public async Task<Result<ConfigChangeRequestUpdateResponse>> HandleAsync(UpdateConfigChange c, CancellationToken ct)
    {
        try { return new ConfigChangeRequestUpdateResponse { Request = ConfigChangeSupport.View(await engine.UpdateAsync(c.Id, c.Request, ct).ConfigureAwait(false)) }; }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class SubmitConfigChangeHandler(ConfigChangeEngine engine) : ICommandHandler<SubmitConfigChange, ConfigChangeRequestSubmitResponse>
{
    public async Task<Result<ConfigChangeRequestSubmitResponse>> HandleAsync(SubmitConfigChange c, CancellationToken ct)
    {
        try { return new ConfigChangeRequestSubmitResponse { Request = ConfigChangeSupport.View(await engine.SubmitAsync(c.Request, ct).ConfigureAwait(false)) }; }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class ApproveConfigChangeHandler(ConfigChangeEngine engine) : ICommandHandler<ApproveConfigChange, ConfigChangeRequestApproveResponse>
{
    public async Task<Result<ConfigChangeRequestApproveResponse>> HandleAsync(ApproveConfigChange c, CancellationToken ct)
    {
        try { return new ConfigChangeRequestApproveResponse { Request = ConfigChangeSupport.View(await engine.DecideAsync(c.Request.RequestId, c.Request.Reason, true, ct).ConfigureAwait(false)) }; }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class RejectConfigChangeHandler(ConfigChangeEngine engine) : ICommandHandler<RejectConfigChange, ConfigChangeRequestRejectResponse>
{
    public async Task<Result<ConfigChangeRequestRejectResponse>> HandleAsync(RejectConfigChange c, CancellationToken ct)
    {
        try { return new ConfigChangeRequestRejectResponse { Request = ConfigChangeSupport.View(await engine.DecideAsync(c.Request.RequestId, c.Request.Reason, false, ct).ConfigureAwait(false)) }; }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class WithdrawConfigChangeHandler(ConfigChangeEngine engine) : ICommandHandler<WithdrawConfigChange, ConfigChangeRequestWithdrawResponse>
{
    public async Task<Result<ConfigChangeRequestWithdrawResponse>> HandleAsync(WithdrawConfigChange c, CancellationToken ct)
    {
        try { return new ConfigChangeRequestWithdrawResponse { Request = ConfigChangeSupport.View(await engine.WithdrawAsync(c.Request, ct).ConfigureAwait(false)) }; }
        catch (DomainException ex) { return ex.Error; }
    }
}

/// <summary>Audit facts shared by the six operations: the request is the object, its keys are the business keys, the status move is the change.</summary>
internal static class ConfigChangeAudit
{
    public static CommandAuditFacts Facts(Guid? id, string fallbackOperation, ConfigChangeRequestView? after, string statusBefore) => new()
    {
        ObjectRef = id is { } known ? ConfigChangeSupport.Subject(known) : new ObjectRef(ModuleCode.MKT, "ConfigChangeRequest", fallbackOperation),
        BusinessKeys = id is { } k ? BusinessKeys.Empty.With("requestId", k.ToString("D")) : BusinessKeys.Empty,
        Changes = after is null ? [] : AuditDiff.Compute(new { status = statusBefore }, new
        {
            status = after.Status.ToString(), keys = string.Join(',', after.Items.Select(i => i.Key)), resultingHash = after.ResultingHash?.ToString(),
        }),
    };
}

internal sealed class CreateConfigChangeAuditor : ICommandAuditor<CreateConfigChange, ConfigChangeRequestCreateResponse>
{
    public CommandAuditFacts Describe(CreateConfigChange c, Result<ConfigChangeRequestCreateResponse>? r) =>
        ConfigChangeAudit.Facts(r is { IsSuccess: true } created ? created.Value.Request.Id : null, "new", r is { IsSuccess: true } shown ? shown.Value.Request : null, "none");
}

internal sealed class UpdateConfigChangeAuditor : ICommandAuditor<UpdateConfigChange, ConfigChangeRequestUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateConfigChange c, Result<ConfigChangeRequestUpdateResponse>? r) =>
        ConfigChangeAudit.Facts(c.Id, "update", r is { IsSuccess: true } ok ? ok.Value.Request : null, "DRAFT");
}

internal sealed class SubmitConfigChangeAuditor : ICommandAuditor<SubmitConfigChange, ConfigChangeRequestSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitConfigChange c, Result<ConfigChangeRequestSubmitResponse>? r) =>
        ConfigChangeAudit.Facts(c.Request.RequestId, "submit", r is { IsSuccess: true } ok ? ok.Value.Request : null, "DRAFT");
}

internal sealed class ApproveConfigChangeAuditor : ICommandAuditor<ApproveConfigChange, ConfigChangeRequestApproveResponse>
{
    public CommandAuditFacts Describe(ApproveConfigChange c, Result<ConfigChangeRequestApproveResponse>? r) =>
        ConfigChangeAudit.Facts(c.Request.RequestId, "approve", r is { IsSuccess: true } ok ? ok.Value.Request : null, "PENDING_APPROVAL");
}

internal sealed class RejectConfigChangeAuditor : ICommandAuditor<RejectConfigChange, ConfigChangeRequestRejectResponse>
{
    public CommandAuditFacts Describe(RejectConfigChange c, Result<ConfigChangeRequestRejectResponse>? r) =>
        ConfigChangeAudit.Facts(c.Request.RequestId, "reject", r is { IsSuccess: true } ok ? ok.Value.Request : null, "PENDING_APPROVAL");
}

internal sealed class WithdrawConfigChangeAuditor : ICommandAuditor<WithdrawConfigChange, ConfigChangeRequestWithdrawResponse>
{
    public CommandAuditFacts Describe(WithdrawConfigChange c, Result<ConfigChangeRequestWithdrawResponse>? r) =>
        ConfigChangeAudit.Facts(c.Request.RequestId, "withdraw", r is { IsSuccess: true } ok ? ok.Value.Request : null, "OPEN");
}
