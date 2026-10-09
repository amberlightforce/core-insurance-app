using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Events;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Market.Commands;

internal static class ActivationSupport
{
    public const string ApprovalType = "MKT.PackActivation";
    public const string Authority = "MKT_PACK_ACTIVATION";
    public static string Actor(ActorRef actor) => $"{actor.KindCode}:{actor.Id}";
    public static ObjectRef Subject(Guid id) => new(ModuleCode.MKT, "PackActivation", id.ToString("D"));
    public static DomainError Refuse(string code, string detail) => DomainError.Of(ModuleCode.MKT, code, detail);
    public static PackActivationProof Proof(PackActivationRow row, string entity) => new(
        row.PackId, entity, row.FromVersion!, row.Version, row.Kind, row.Reason,
        ConfigurationHash.Parse(row.ParentHash!.Trim()), row.SupersedesId!.Value,
        Sha256Hash.Parse(row.TargetDigest!.Trim()), row.RequestedBy, row.RequestedPrincipal);
    public static PackActivationView View(PackActivationRow row, string entity) => new()
    {
        ActivationId = row.Id, Pack = row.PackId, LegalEntity = entity,
        Kind = row.Kind == "ROLLBACK" ? PackActivationView.KindValue.Rollback : PackActivationView.KindValue.Activate,
        From = row.FromVersion, To = row.Version, Status = Enum.Parse<PackActivationStatus>(row.Status.Replace("_", "", StringComparison.Ordinal), true),
        Reason = row.Reason, RequestedBy = row.RequestedBy, DecidedBy = row.DecidedBy,
        ApprovalRequestId = row.ApprovalRequestId is { } id ? new ApprovalRequestId(id) : null,
        ActivatedAt = row.ActivatedAt, ResultingHash = row.ResultingHash is { } hash ? Sha256Hash.Parse(hash.Trim()) : null,
    };
}

internal sealed record RequestPackRollback(PackRollbackRequest Request) : ICommand<PackRollbackResponse>;
internal sealed record RequestPackActivation(PackScheduleActivationRequest Request) : ICommand<PackScheduleActivationResponse>;
internal sealed record DecidePackActivation(PackActivationDecideRequest Request) : ICommand<PackActivationDecideResponse>;

/// <summary>Every operation joins the command transaction and serialises the global manifest before loading entity facts.</summary>
internal sealed class PackActivationEngine(MarketDbContext db, DbSession session, RequestContext context, IClock clock,
    IPlatformApprovalService approvals, IEventPublisher events)
{
    private async Task LockAsync(CancellationToken ct) => await ConfigStateWriter.LockAsync(session.Connection,
        session.Transaction ?? throw new InvalidOperationException("Activation requires a command transaction."), ct).ConfigureAwait(false);

    private async Task<(LegalEntityRow Entity, ConfigStateRow State, ConfigStateManifest Manifest, PackActivationRow Active, PackVersionRow Target)> FactsAsync(
        string pack, string entityCode, string version, CancellationToken ct)
    {
        if (context.LegalEntity?.Value != entityCode)
            throw new DomainException(ActivationSupport.Refuse("PACK-NOT-FOUND", "The pack is not available for this legal entity."));
        var entity = await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Code == entityCode && e.PackId == pack && e.Status == "ACTIVE", ct).ConfigureAwait(false)
            ?? throw new DomainException(ActivationSupport.Refuse("PACK-NOT-FOUND", "The active legal entity does not own this pack."));
        var state = await db.ConfigStates.AsNoTracking().OrderByDescending(s => s.Seq).FirstAsync(ct).ConfigureAwait(false);
        var manifest = ConfigStateManifest.FromJson(JsonNode.Parse(state.Manifest));
        if (manifest.Hash.ToString() != state.Hash.Trim()) throw new DomainException(ActivationSupport.Refuse("STALE", "The current manifest failed its integrity check."));
        var current = manifest.Packs.SingleOrDefault(p => p.PackId == pack)
            ?? throw new DomainException(ActivationSupport.Refuse("STALE", "The current manifest does not name this pack."));
        var activeRows = await db.PackActivations.Where(a => a.LegalEntityId == entity.LegalEntityId && a.PackId == pack && a.Status == "ACTIVE").ToListAsync(ct).ConfigureAwait(false);
        if (activeRows.Count != 1 || activeRows[0].Version != current.Version || activeRows[0].ActivatedAt is null || activeRows[0].ResultingHash is null)
            throw new DomainException(ActivationSupport.Refuse("STALE", "The entity activation and current manifest disagree."));
        // One manifest is shared by the stamp. Refuse entity-local mutation if it would silently change another entity's pack.
        if (await db.LegalEntities.AnyAsync(e => e.PackId == pack && e.LegalEntityId != entity.LegalEntityId && e.Status == "ACTIVE", ct).ConfigureAwait(false))
            throw new DomainException(ActivationSupport.Refuse("PACK-VALIDATION", "This slice supports one active legal entity per country pack in a stamp."));
        var target = await db.PackVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PackId == pack && v.Version == version && v.Status == "Published", ct).ConfigureAwait(false)
            ?? throw new DomainException(ActivationSupport.Refuse("PACK-VALIDATION", "The target version is not Published."));
        if (target.Country != current.Country || PackVersionContent.DigestOf(PackVersionContent.FromJson(JsonNode.Parse(target.Values))).ToString() != target.ContentDigest.Trim())
            throw new DomainException(ActivationSupport.Refuse("STALE", "The target pack failed its integrity check."));
        var source = await db.PackVersions.AsNoTracking().SingleAsync(v => v.PackId == pack && v.Version == current.Version, ct).ConfigureAwait(false);
        if (source.ContentDigest.Trim() != current.Digest.ToString())
            throw new DomainException(ActivationSupport.Refuse("STALE", "The manifest source digest disagrees with the registry."));
        return (entity, state, manifest, activeRows[0], target);
    }

    private async Task<PackActivationPreview> PreviewAsync(PackActivationRow active, PackVersionRow target, bool rollback, Instant now, CancellationToken ct)
    {
        if (now < active.ActivatedAt!.Value) throw new DomainException(ActivationSupport.Refuse("PACK-VALIDATION", "Retroactive activation is unsupported."));
        var source = await db.PackVersions.AsNoTracking().SingleAsync(v => v.PackId == active.PackId && v.Version == active.Version, ct).ConfigureAwait(false);
        var states = rollback ? await db.ConfigStates.AsNoTracking().Where(s => s.ActivatedAt >= active.ActivatedAt!.Value && s.ActivatedAt < now).OrderBy(s => s.Seq).ToListAsync(ct).ConfigureAwait(false) : [];
        // Each state is checked against the source pack; no unrelated state is attributed to the affected window.
        var hashes = states.Where(s => ConfigStateManifest.FromJson(JsonNode.Parse(s.Manifest)).Packs.Any(p => p.PackId == active.PackId && p.Version == active.Version))
            .Select(s => Sha256Hash.Parse(s.Hash.Trim())).ToList();
        return new PackActivationPreview
        {
            FromVersion = active.Version, ToVersion = target.Version,
            Window = rollback ? new InstantRange(active.ActivatedAt.Value, now) : new InstantRange(now, null), HashesIssued = hashes,
            KeyDiff = PackActivationProof.KeyDiff(PackVersionContent.FromJson(JsonNode.Parse(source.Values)), PackVersionContent.FromJson(JsonNode.Parse(target.Values))),
        };
    }

    public async Task<(PackActivationPreview Preview, PackActivationRow? Row)> RequestAsync(string pack, string entity, string version, string reason, bool rollback, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pack) || string.IsNullOrWhiteSpace(version) || reason is null || reason.Trim().Length < 20 || reason.Length > 128)
            throw new DomainException(ActivationSupport.Refuse("PACK-VALIDATION", "Pack, version and a reason of 20 to 128 characters are required."));
        if (context.Actor.Kind != ActorKind.User) throw new DomainException(ActivationSupport.Refuse("SOD-VIOLATION", "A human release manager must request activation."));
        await LockAsync(ct).ConfigureAwait(false);
        var facts = await FactsAsync(pack, entity, version, ct).ConfigureAwait(false);
        if (facts.Active.Version == version || (rollback && Version.Parse(version) >= Version.Parse(facts.Active.Version)))
            throw new DomainException(ActivationSupport.Refuse("PACK-VALIDATION", "Rollback requires an earlier version; activation requires a different version."));
        var now = clock.Now;
        var preview = await PreviewAsync(facts.Active, facts.Target, rollback, now, ct).ConfigureAwait(false);
        if (context.DryRun) return (preview, null);
        if (await db.PackActivations.AnyAsync(a => a.LegalEntityId == facts.Entity.LegalEntityId && a.PackId == pack && a.Status == "PENDING_APPROVAL", ct).ConfigureAwait(false))
            throw new DomainException(ActivationSupport.Refuse("STALE", "An activation request is already pending."));
        var row = new PackActivationRow
        {
            Id = Guid.CreateVersion7(), LegalEntityId = facts.Entity.LegalEntityId, PackId = pack, Version = version,
            Kind = rollback ? "ROLLBACK" : "ACTIVATE", Status = "PENDING_APPROVAL", RequestedBy = ActivationSupport.Actor(context.Actor),
            RequestedPrincipal = context.OnBehalfOf is { } principal ? ActivationSupport.Actor(principal) : null,
            CreatedAt = now, Reason = reason, FromVersion = facts.Active.Version, ParentHash = facts.State.Hash.Trim(),
            TargetDigest = facts.Target.ContentDigest.Trim(), SupersedesId = facts.Active.Id,
        };
        var proof = ActivationSupport.Proof(row, entity);
        row.ContentHash = proof.Hash.ToString();
        var approval = await approvals.RequestAsync(new ApprovalRequestRequest
        {
            Type = ActivationSupport.ApprovalType, ObjectRef = ActivationSupport.Subject(row.Id), PayloadHash = proof.Hash,
            Diff = JsonSerializer.SerializeToElement(new { legalEntityId = facts.Entity.LegalEntityId.Value }),
            Editors = row.RequestedPrincipal is { } makerPrincipal ? [row.RequestedBy, makerPrincipal] : [row.RequestedBy],
            Authority = new ApprovalAuthority { Type = ActivationSupport.Authority, Codes = new Dictionary<string, string> { ["pack"] = pack, ["legalEntity"] = entity } },
            ReferralRole = "Platform.DesignAuthority", Reason = reason,
        }, CommandOptions.New(), ct).ConfigureAwait(false);
        row.ApprovalRequestId = approval.Request.RequestId;
        db.PackActivations.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (preview, row);
    }

    public async Task<PackActivationView> DecideAsync(PackActivationDecideRequest request, CancellationToken ct)
    {
        if (request.Reason is null || request.Reason.Trim().Length == 0 || request.Reason.Length > 128)
            throw new DomainException(ActivationSupport.Refuse("PACK-VALIDATION", "A checker reason of 1 to 128 characters is required."));
        await LockAsync(ct).ConfigureAwait(false);
        var entity = context.LegalEntity?.Value ?? throw new InvalidOperationException("No authenticated legal entity.");
        var entityRow = await db.LegalEntities.AsNoTracking().SingleAsync(e => e.Code == entity, ct).ConfigureAwait(false);
        var row = await db.PackActivations.SingleOrDefaultAsync(a => a.Id == request.ActivationId && a.LegalEntityId == entityRow.LegalEntityId, ct).ConfigureAwait(false)
            ?? throw new DomainException(ActivationSupport.Refuse("PACK-NOT-FOUND", "The activation does not exist."));
        if (row.Status != "PENDING_APPROVAL" || row.ContentHash is null || row.ParentHash is null || row.TargetDigest is null || row.FromVersion is null || row.SupersedesId is null || row.ApprovalRequestId is null)
            throw new DomainException(ActivationSupport.Refuse("STALE", "The activation is not pending with complete proof."));
        var actor = ActivationSupport.Actor(context.Actor);
        var principal = context.OnBehalfOf is { } p ? ActivationSupport.Actor(p) : null;
        if (context.Actor.Kind != ActorKind.User || actor == row.RequestedBy || actor == row.RequestedPrincipal || (principal is not null && (principal == row.RequestedBy || principal == row.RequestedPrincipal)))
            throw new DomainException(ActivationSupport.Refuse("SOD-VIOLATION", "The checker must be a different human from the maker and maker principal."));
        var proof = ActivationSupport.Proof(row, entity);
        if (proof.Hash.ToString() != row.ContentHash.Trim()) throw new DomainException(ActivationSupport.Refuse("SOD-VIOLATION", "Frozen content no longer matches its approval hash."));
        var approve = request.Decision == PackActivationDecideRequest.DecisionValue.Approve;
        var facts = await FactsAsync(row.PackId, entity, row.Version, ct).ConfigureAwait(false);
        if (approve && (facts.State.Hash.Trim() != row.ParentHash.Trim() || facts.Active.Id != row.SupersedesId || facts.Active.Version != row.FromVersion || facts.Target.ContentDigest.Trim() != row.TargetDigest.Trim()))
            throw new DomainException(ActivationSupport.Refuse("STALE", "The parent state or activation changed; reject and request again."));
        var now = clock.Now;
        var preview = await PreviewAsync(facts.Active, facts.Target, row.Kind == "ROLLBACK", now, ct).ConfigureAwait(false);
        await approvals.DecideAsync(new ApprovalDecideRequest
        {
            RequestId = row.ApprovalRequestId.Value, PayloadHash = proof.Hash,
            Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject, Comment = request.Reason,
        }, CommandOptions.New(), ct).ConfigureAwait(false);
        if (approve)
        {
            var verified = await approvals.VerifyForExecutionAsync(new ApprovalVerifyForExecutionRequest
            { RequestId = row.ApprovalRequestId.Value, Hash = proof.Hash, Type = ActivationSupport.ApprovalType, ObjectRef = ActivationSupport.Subject(row.Id) }, ct).ConfigureAwait(false);
            if (!verified.Ok || verified.Authority.Type != ActivationSupport.Authority || verified.Authority.Codes?.GetValueOrDefault("pack") != row.PackId || verified.Authority.Codes?.GetValueOrDefault("legalEntity") != entity)
                throw new DomainException(ActivationSupport.Refuse("SOD-VIOLATION", "Native PLT proof does not bind the required activation authority."));
            var packs = facts.Manifest.Packs.Where(p => p.PackId != row.PackId).Append(new ManifestPack(row.PackId, row.Version, proof.TargetDigest, facts.Target.Country!)).ToList();
            var manifest = new ConfigStateManifest(packs, facts.Manifest.CoreDigest, proof.Parent, row.Kind == "ROLLBACK" ? StateCauses.PackRollback : StateCauses.PackActivation);
            var hash = await ConfigStateWriter.AppendAsync(session.Connection, session.Transaction!, manifest, now, row.Id, ct).ConfigureAwait(false);
            facts.Active.Status = "SUPERSEDED";
            facts.Active.RecordVersion++;
            row.Status = "ACTIVE"; row.ActivatedAt = now; row.ResultingHash = hash.ToString();
            row.WindowFrom = preview.Window.Start; row.WindowTo = preview.Window.End; row.HashesIssued = preview.HashesIssued.Select(h => h.ToString()).ToArray();
            var keys = BusinessKeys.Empty.With("stampId", entity).With("packId", PackRegistryService.IdOf(row.PackId).ToString("D"));
            if (row.Kind == "ROLLBACK")
                events.Publish(new OutgoingEvent(EventDescriptor.From(PackRolledBackV1.Descriptor), "Stamp", entity, new PackRolledBackV1
                {
                    PackId = PackRegistryService.IdOf(row.PackId), Pack = row.PackId, LegalEntity = entity, ActivationId = row.Id,
                    FromVersion = row.FromVersion, ToVersion = row.Version, Reason = row.Reason, Window = preview.Window, AffectedWindow = preview.Window,
                    HashesIssued = preview.HashesIssued, HashesInWindow = preview.HashesIssued, ResultingHash = Sha256Hash.Parse(hash.ToString()),
                }, keys));
            else
                events.Publish(new OutgoingEvent(EventDescriptor.From(PackActivatedV1.Descriptor), "Stamp", entity, new PackActivatedV1
                {
                    PackId = PackRegistryService.IdOf(row.PackId), Pack = row.PackId, Version = row.Version, LegalEntity = entity, ActivationId = row.Id,
                    ActivatedAt = now, ActivationInstant = now, NewHash = Sha256Hash.Parse(hash.ToString()), ResultingHash = Sha256Hash.Parse(hash.ToString()), SpisRebound = [],
                }, keys));
        }
        else row.Status = "REJECTED";
        row.DecidedBy = actor; row.DecisionReason = request.Reason; row.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ActivationSupport.View(row, entity);
    }
}

internal sealed class RequestPackRollbackHandler(PackActivationEngine engine) : ICommandHandler<RequestPackRollback, PackRollbackResponse>
{
    public async Task<Result<PackRollbackResponse>> HandleAsync(RequestPackRollback c, CancellationToken ct)
    {
        var (preview, row) = await engine.RequestAsync(c.Request.Pack, c.Request.LegalEntity, c.Request.ToVersion, c.Request.Reason, true, ct).ConfigureAwait(false);
        return new PackRollbackResponse { DryRun = row is null, Preview = preview, ActivationId = row?.Id, Status = row is null ? null : PackActivationStatus.PendingApproval, ApprovalRequestId = row?.ApprovalRequestId is { } id ? new ApprovalRequestId(id) : null };
    }
}
internal sealed class RequestPackActivationHandler(PackActivationEngine engine) : ICommandHandler<RequestPackActivation, PackScheduleActivationResponse>
{
    public async Task<Result<PackScheduleActivationResponse>> HandleAsync(RequestPackActivation c, CancellationToken ct)
    {
        var (preview, row) = await engine.RequestAsync(c.Request.Pack, c.Request.LegalEntity, c.Request.Version, c.Request.Reason, false, ct).ConfigureAwait(false);
        return new PackScheduleActivationResponse { DryRun = row is null, Preview = preview, ActivationId = row?.Id, Status = row is null ? null : PackActivationStatus.PendingApproval, ApprovalRequestId = row?.ApprovalRequestId is { } id ? new ApprovalRequestId(id) : null };
    }
}
internal sealed class DecidePackActivationHandler(PackActivationEngine engine) : ICommandHandler<DecidePackActivation, PackActivationDecideResponse>
{
    public async Task<Result<PackActivationDecideResponse>> HandleAsync(DecidePackActivation c, CancellationToken ct) => new PackActivationDecideResponse { Activation = await engine.DecideAsync(c.Request, ct).ConfigureAwait(false) };
}
internal sealed class RequestPackRollbackAuditor : ICommandAuditor<RequestPackRollback, PackRollbackResponse>
{
    public CommandAuditFacts Describe(RequestPackRollback c, Result<PackRollbackResponse>? result) => new()
    { ObjectRef = result is { IsSuccess: true, Value.ActivationId: { } id } ? ActivationSupport.Subject(id) : new ObjectRef(ModuleCode.MKT, "Pack", c.Request.Pack), BusinessKeys = BusinessKeys.Empty.With("pack", c.Request.Pack) };
}
internal sealed class RequestPackActivationAuditor : ICommandAuditor<RequestPackActivation, PackScheduleActivationResponse>
{
    public CommandAuditFacts Describe(RequestPackActivation c, Result<PackScheduleActivationResponse>? result) => new()
    { ObjectRef = result is { IsSuccess: true, Value.ActivationId: { } id } ? ActivationSupport.Subject(id) : new ObjectRef(ModuleCode.MKT, "Pack", c.Request.Pack), BusinessKeys = BusinessKeys.Empty.With("pack", c.Request.Pack) };
}
internal sealed class DecidePackActivationAuditor : ICommandAuditor<DecidePackActivation, PackActivationDecideResponse>
{
    public CommandAuditFacts Describe(DecidePackActivation c, Result<PackActivationDecideResponse>? result) => new()
    { ObjectRef = ActivationSupport.Subject(c.Request.ActivationId), BusinessKeys = BusinessKeys.Empty.With("activationId", c.Request.ActivationId.ToString("D")), Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(new { status = "PENDING_APPROVAL" }, new { status = ok.Value.Activation.Status.ToString() }) : null };
}
