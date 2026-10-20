using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Modules.Data.Contracts;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1848 // Logging is rare here (participant faults); LoggerMessage adds nothing.

namespace CoreIns.Modules.Compliance.Dsar;

/// <summary>One row of the DSAR map (PRD-18 section 9.2.2, CR-CMP-03): which module takes part and whether only export applies.</summary>
internal sealed record DsarMapRow(string Module, bool ExportOnly = false);

/// <summary>The configured map: 15 rows. PFC and MKT hold none and are absent (D-SL6-07). RAT is export only.</summary>
internal sealed class DsarOptions
{
    public IReadOnlyList<DsarMapRow> Map { get; set; } =
    [
        new("PTY"), new("POL"), new("BIL"), new("CLM"), new("UW"), new("RI"), new("DOC"), new("CHN"), new("WRK"),
        new("PLT"), new("FIN"), new("RAT", ExportOnly: true), new("DAT"), new("MIG"), new("CMP"),
    ];
}

/// <summary>Result of export, restrict or erase: the case and its per-item outcomes.</summary>
internal sealed record DsarAction(DsarCase Case, List<DsarOutcomeView> Items);

/// <summary>A case with its tasks and (once compiled) its package.</summary>
internal sealed record DsarCase(DsarRow Row, IReadOnlyList<DsarTaskRow> Tasks);

/// <summary>Views and JSON shared by the handlers and the reader.</summary>
internal static class DsarViews
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string? json) => string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, Json);

    public static string KindCode(DsarOutcomeKind kind) => kind switch
    {
        DsarOutcomeKind.Erased => "ERASED",
        DsarOutcomeKind.Anonymised => "ANONYMISED",
        DsarOutcomeKind.Restricted => "RESTRICTED",
        DsarOutcomeKind.NotHeld => "NOT_HELD",
        _ => "NOT_BUILT",
    };

    /// <summary>
    /// Normalises a participant outcome. The SPI has no Exported outcome: PART-A reports exported classes as NotHeld with Detail
    /// "EXPORTED", PART-B and PART-C as Restricted with Detail "exported". Detail equal to "exported" (any case) means exported data.
    /// </summary>
    public static DsarOutcomeView Outcome(string module, DsarRecordOutcome o) => new()
    {
        Module = module,
        RecordClass = o.RecordClass,
        Kind = KindCode(o.Kind),
        RecordCount = o.RecordCount,
        LegalBasis = o.LegalBasis,
        RetentionEnd = o.RetentionEnd,
        Exported = string.Equals(o.Detail, "exported", StringComparison.OrdinalIgnoreCase),
        Detail = o.Detail,
    };

    public static DsarTaskView Task(DsarTaskRow t) => new()
    {
        TaskId = t.TaskId,
        Module = t.Module,
        Status = t.Status,
        Phase = t.Phase,
        Outcomes = Deserialize<List<DsarOutcomeView>>(t.Outcomes) ?? [],
        RedactThirdParty = t.RedactThirdParty,
        Attempts = t.Attempts,
        Error = t.Error,
    };

    public static string ApprovalFor(string requestType) => requestType == DsarRequestTypes.Erasure ? "ERASE" : "RESTRICT";

    /// <summary>What still stands between the case and completion (empty when it can complete).</summary>
    public static List<string> Blockers(DsarRow c, IReadOnlyList<DsarTaskRow> tasks)
    {
        var blockers = new List<string>();
        if (c.Status is DsarStatuses.Completed or DsarStatuses.Refused)
        {
            return blockers;
        }

        if (tasks.Count == 0)
        {
            blockers.Add("FAN_OUT_NOT_STARTED");
            return blockers;
        }

        blockers.AddRange(tasks.Where(t => t.Status == DsarTaskStatuses.NotBuilt).Select(t => "NOT_BUILT:" + t.Module));
        blockers.AddRange(tasks.Where(t => t.Status == DsarTaskStatuses.Failed).Select(t => "FAILED:" + t.Module));
        blockers.AddRange(tasks.Where(t => t.Status == DsarTaskStatuses.Pending).Select(t => "PENDING:" + t.Module));
        if (c.RequestType is DsarRequestTypes.Access or DsarRequestTypes.Portability)
        {
            if (c.Package is null)
            {
                blockers.Add("PACKAGE_NOT_COMPILED");
            }
        }
        else if (c.ApprovalAction != ApprovalFor(c.RequestType))
        {
            blockers.Add("APPROVAL_REQUIRED");
        }
        else
        {
            var phase = c.ApprovalAction == "ERASE" ? DsarPhases.Erase : DsarPhases.Restrict;
            blockers.AddRange(tasks.Where(t => !t.ExportOnly && t.Phase != phase).Select(t => "ACTION_PENDING:" + t.Module));
        }

        return blockers;
    }

    public static DsarRequestView Request(DsarRow c, IReadOnlyList<DsarTaskRow> tasks) => new()
    {
        DsarId = c.DsarId,
        RequestType = c.RequestType,
        Status = c.Status,
        SubjectPartyId = c.SubjectPartyId,
        CreatedBy = c.CreatedBy,
        ReceivedAt = c.ReceivedAt,
        Deadline = c.Deadline,
        Extended = c.Extended,
        IdentityVerified = c.IdentityVerified,
        LegalHold = c.LegalHold,
        ApprovedBy = c.ApprovedBy,
        ApprovalAction = c.ApprovalAction,
        CompletedAt = c.CompletedAt,
        RefusalReason = c.RefusalReason,
        Blockers = Blockers(c, tasks),
    };

    public static DsarPackageView? Package(DsarRow c) => Deserialize<DsarPackageView>(c.Package);

    /// <summary>Request, tasks and package in the three response shapes (all share these properties).</summary>
    public static (DsarRequestView Request, List<DsarTaskView> Tasks, DsarPackageView? Package) Of(DsarCase c) =>
        (Request(c.Row, c.Tasks), [.. c.Tasks.Select(Task)], Package(c.Row));
}

/// <summary>
/// DSAR case orchestration (SL6-CMP-DSAR): fans a request out to every participant on the configured map, aggregates the
/// outcomes, and completes the case once, when nothing blocks it. Participants that are not registered are NOT_BUILT.
/// </summary>
internal sealed class DsarEngine(
    ComplianceDbContext db,
    ClockEngine clocks,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    DsarParticipantRegistry registry,
    DsarRetentionPolicy retention,
    IEventPublisher events,
    IServiceProvider services,
    Microsoft.Extensions.Options.IOptions<DsarOptions> options,
    ILogger<DsarEngine> logger)
{
    private const string Clock = ClockDefinitions.DsarResponse;

    private Instant Now => clocks.Now;

    private string Actor => context.Actor.Id;

    private LegalEntityId LegalEntity => ClockLookup.LegalEntity(context, legalEntities);

    private static DomainError Err(string code, string message) => DomainError.Of(ModuleCode.CMP, code, message);

    private static ObjectRef ClockSubject(DsarId id) => new(ModuleCode.CMP, "Dsar", id.Value.ToString("D", CultureInfo.InvariantCulture));

    public async Task<DsarCase?> FindAsync(Guid id, CancellationToken ct)
    {
        var dsarId = new DsarId(id);
        var legalEntity = LegalEntity;
        var row = await db.Set<DsarRow>().SingleOrDefaultAsync(r => r.DsarId == dsarId && r.LegalEntityId == legalEntity, ct).ConfigureAwait(false);
        return row is null ? null : new DsarCase(row, await TasksAsync(row.DsarId, ct).ConfigureAwait(false));
    }

    private async Task<List<DsarTaskRow>> TasksAsync(DsarId id, CancellationToken ct)
    {
        var tasks = await db.Set<DsarTaskRow>().Where(t => t.DsarId == id).ToListAsync(ct).ConfigureAwait(false);
        var order = options.Value.Map.Select((m, i) => (m.Module, i)).ToDictionary(x => x.Module, x => x.i);
        return [.. tasks.OrderBy(t => order.GetValueOrDefault(t.Module, int.MaxValue))];
    }

    private async Task<Result<DsarCase>> LoadAsync(Guid id, CancellationToken ct) =>
        await FindAsync(id, ct).ConfigureAwait(false) is { } found
            ? found
            : Err("NOT-FOUND", "The data-subject request does not exist in your legal entity.");

    public async Task<Result<DsarCase>> CreateAsync(DsarCreateRequest request, CancellationToken ct)
    {
        var id = DsarId.New();
        var now = Now;
        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");
        _ = jurisdiction;
        var started = await new StartClockHandler(clocks, db, context, legalEntities).HandleAsync(
            new StartClock(new ClockStartRequest { ClockCode = ClockCode.Parse(Clock), SubjectRef = ClockSubject(id) }), ct).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return started.Error!;
        }

        var row = new DsarRow
        {
            DsarId = id,
            LegalEntityId = LegalEntity,
            RequestType = request.RequestType.ToString().ToUpperInvariant(),
            Status = DsarStatuses.Received,
            SubjectPartyId = request.SubjectPartyId,
            CreatedBy = Actor,
            ReceivedAt = now,
            Deadline = started.Value.Deadline ?? now,
            LegalHold = request.LegalHold ?? false,
            Notes = request.Notes,
            RecordVersion = 1,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DSARReceivedV1.Descriptor), "Dsar", id.Value.ToString("D", CultureInfo.InvariantCulture),
            new DSARReceivedV1
            {
                DsarId = id,
                RequestType = row.RequestType,
                SubjectPartyIds = [row.SubjectPartyId],
                Scope = [.. options.Value.Map.Select(m => m.Module)],
                Deadline = row.Deadline,
            },
            BusinessKeys.Empty.With("dsarId", id.Value.ToString("D", CultureInfo.InvariantCulture))) { OccurredAt = now });
        return new DsarCase(row, []);
    }

    public async Task<Result<DsarCase>> VerifyIdentityAsync(DsarVerifyIdentityRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        if (c.Row.Status is DsarStatuses.Completed or DsarStatuses.Refused)
        {
            return Err("DSAR-STATE", "The request is closed.");
        }

        if (!c.Row.IdentityVerified)
        {
            c.Row.IdentityVerified = true;
            c.Row.IdentityMethod = request.Method;
            c.Row.VerifiedBy = Actor;
            c.Row.Status = DsarStatuses.IdentityVerified;
            await SaveAsync(c.Row, ct).ConfigureAwait(false);
        }

        return c;
    }

    public async Task<Result<DsarCase>> StartFanOutAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        if (!c.Row.IdentityVerified)
        {
            return Err("IDENTITY-NOT-VERIFIED", "The identity of the data subject must be verified before the fan-out.");
        }

        if (c.Tasks.Count > 0 || c.Row.Status is DsarStatuses.Completed or DsarStatuses.Refused)
        {
            return c; // replay
        }

        var tasks = options.Value.Map.Select(m => new DsarTaskRow
        {
            TaskId = Guid.CreateVersion7(),
            DsarId = c.Row.DsarId,
            Module = m.Module,
            ExportOnly = m.ExportOnly,
        }).ToList();
        db.AddRange(tasks);
        c.Row.Status = DsarStatuses.InReview;
        await RunAsync(c.Row, tasks, DsarPhases.Export, null, ct).ConfigureAwait(false);
        await SaveAsync(c.Row, ct).ConfigureAwait(false);
        return new DsarCase(c.Row, tasks);
    }

    public async Task<Result<DsarCase>> RetryTaskAsync(Guid id, Guid taskId, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        var task = c.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null)
        {
            return Err("NOT-FOUND", "The task does not exist in this request.");
        }

        if (c.Row.Status is DsarStatuses.Completed or DsarStatuses.Refused || task.Status is DsarTaskStatuses.Done or DsarTaskStatuses.Pending)
        {
            return Err("DSAR-STATE", "Only a failed or not built task of an open request can be retried.");
        }

        await RunAsync(c.Row, [task], task.Phase, null, ct).ConfigureAwait(false);
        if (task.Phase == DsarPhases.Export && c.Row.Package is not null)
        {
            c.Row.Package = DsarViews.Serialize(Compile(c, Now));
        }

        await CompleteIfReadyAsync(c, ct).ConfigureAwait(false);
        await SaveAsync(c.Row, ct).ConfigureAwait(false);
        return c;
    }

    public async Task<Result<DsarCase>> DecideItemAsync(DsarDecideItemRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        if (c.Row.Status is DsarStatuses.Completed or DsarStatuses.Refused)
        {
            return Err("DSAR-STATE", "The request is closed.");
        }

        if (request.TaskId is { } taskId)
        {
            var task = c.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task is null)
            {
                return Err("NOT-FOUND", "The task does not exist in this request.");
            }

            task.RedactThirdParty = request.RedactThirdParty ?? task.RedactThirdParty;
            task.DecisionNote = request.Note;
            task.UpdatedAt = Now;
            c.Row.Package = null; // a decision changes the package: recompile
        }

        if (request.LegalHold is { } hold)
        {
            c.Row.LegalHold = hold;
        }

        await SaveAsync(c.Row, ct).ConfigureAwait(false);
        return c;
    }

    public async Task<Result<DsarCase>> ApproveAsync(DsarApproveRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        var action = request.Action.ToString().ToUpperInvariant();
        if (c.Row.Status != DsarStatuses.InReview)
        {
            return Err("DSAR-STATE", "Approval is possible once the fan-out has run and while the request is open.");
        }

        // Four eyes (D-SL6-07): the approving DPO is not the case creator.
        if (string.Equals(Actor, c.Row.CreatedBy, StringComparison.Ordinal))
        {
            return Err("APPROVAL-SAME-USER", "The approver must differ from the case creator.");
        }

        if (c.Row.ApprovalAction is not null && c.Row.ApprovalAction != action)
        {
            return Err("DSAR-STATE", "The request already carries an approval for another action.");
        }

        if (c.Row.ApprovalAction is null)
        {
            c.Row.ApprovalAction = action;
            c.Row.ApprovedBy = Actor;
            c.Row.ApprovalReason = request.Reason;
            await SaveAsync(c.Row, ct).ConfigureAwait(false);
        }

        return c;
    }

    public async Task<Result<DsarCase>> ExtendAsync(DsarExtendRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        if (c.Row.Status is DsarStatuses.Completed or DsarStatuses.Refused)
        {
            return Err("DSAR-STATE", "The request is closed.");
        }

        if (c.Row.Extended)
        {
            return Err("EXTENSION-NOT-ALLOWED", "The deadline can be extended once (GDPR Art 12(3)).");
        }

        var extended = await new ExtendClockHandler(clocks, db, context, legalEntities).HandleAsync(
            new ExtendClock(new ClockExtendRequest
            {
                ClockCode = ClockCode.Parse(Clock),
                SubjectRef = ClockSubject(c.Row.DsarId),
                Extension = string.IsNullOrWhiteSpace(request.Extension) ? "P2M" : request.Extension,
                Reason = request.Reason,
            }), ct).ConfigureAwait(false);
        if (extended.IsFailure)
        {
            return extended.Error!;
        }

        c.Row.Extended = true;
        c.Row.Deadline = extended.Value.Deadline ?? c.Row.Deadline;
        await SaveAsync(c.Row, ct).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DSARExtendedV1.Descriptor), "Dsar", c.Row.DsarId.Value.ToString("D", CultureInfo.InvariantCulture),
            new DSARExtendedV1 { DsarId = c.Row.DsarId, NewDeadline = c.Row.Deadline },
            BusinessKeys.Empty.With("dsarId", c.Row.DsarId.Value.ToString("D", CultureInfo.InvariantCulture))) { OccurredAt = Now });
        return c;
    }

    public async Task<Result<DsarCase>> RefuseAsync(DsarRefuseRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        if (c.Row.Status == DsarStatuses.Refused)
        {
            return c;
        }

        if (c.Row.Status == DsarStatuses.Completed)
        {
            return Err("DSAR-STATE", "The request is already completed.");
        }

        c.Row.RefusalReason = request.Reason;
        await CloseAsync(c, DsarStatuses.Refused, new { refused = true }, ct).ConfigureAwait(false);
        return c;
    }

    public async Task<Result<DsarAction>> ExportAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var c = loaded.Value;
        if (c.Tasks.Count == 0)
        {
            return Err("DSAR-STATE", "Start the fan-out before exporting.");
        }

        if (c.Row.Status != DsarStatuses.Completed && c.Row.Status != DsarStatuses.Refused && c.Row.Package is null)
        {
            c.Row.Package = DsarViews.Serialize(Compile(c, Now));
            await CompleteIfReadyAsync(c, ct).ConfigureAwait(false);
            await SaveAsync(c.Row, ct).ConfigureAwait(false);
        }

        return new DsarAction(c, PerItem(c));
    }

    public async Task<Result<DsarAction>> ActAsync(Guid id, bool erase, string? legalBasis, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var c = loaded.Value;
        var action = erase ? "ERASE" : "RESTRICT";
        if (c.Row.Status is DsarStatuses.Completed or DsarStatuses.Refused || c.Tasks.Count == 0)
        {
            return c.Row.Status == DsarStatuses.Completed ? new DsarAction(c, PerItem(c)) : Err("DSAR-STATE", "The request is not open for this action.");
        }

        if (c.Row.ApprovalAction != action)
        {
            return Err("APPROVAL-REQUIRED", $"{action} needs the approval of a DPO who is not the case creator.");
        }

        if (erase && c.Row.LegalHold)
        {
            return Err("LEGAL-HOLD", "A legal hold blocks the purge; restrict the data instead.");
        }

        var phase = erase ? DsarPhases.Erase : DsarPhases.Restrict;
        var todo = c.Tasks.Where(t => !t.ExportOnly && !(t.Phase == phase && t.Status == DsarTaskStatuses.Done)).ToList();
        await RunAsync(c.Row, todo, phase, legalBasis, ct).ConfigureAwait(false);
        c.Row.ActionResult = DsarViews.Serialize(PerItem(c));
        await CompleteIfReadyAsync(c, ct).ConfigureAwait(false);
        await SaveAsync(c.Row, ct).ConfigureAwait(false);
        return new DsarAction(c, PerItem(c));
    }

    private static List<DsarOutcomeView> PerItem(DsarCase c) =>
        [.. c.Tasks.SelectMany(t => DsarViews.Deserialize<List<DsarOutcomeView>>(t.Outcomes) ?? [])];

    private static DsarPackageView Compile(DsarCase c, Instant? at = null)
    {
        var entries = new List<JsonElement>();
        foreach (var t in c.Tasks)
        {
            if (t.RedactThirdParty)
            {
                entries.Add(JsonSerializer.SerializeToElement(new { module = t.Module, redacted = true }));
            }
            else if (t.ExportPayload is not null)
            {
                using var payload = JsonDocument.Parse(t.ExportPayload);
                entries.Add(JsonSerializer.SerializeToElement(new { module = t.Module, redacted = false, payload = payload.RootElement }));
            }
        }

        return new DsarPackageView { GeneratedAt = at, Index = PerItem(c), Entries = entries };
    }

    /// <summary>Runs one phase for the given tasks. A missing participant is NOT_BUILT, a throwing one FAILED; nothing is skipped silently.</summary>
    private async Task RunAsync(DsarRow c, IEnumerable<DsarTaskRow> tasks, string phase, string? legalBasis, CancellationToken ct)
    {
        var subject = SubjectRef.ForParty(c.SubjectPartyId);
        var dsarContext = new DsarContext(c.DsarId.Value, c.CreatedBy, c.ApprovedBy);
        foreach (var t in tasks)
        {
            t.Attempts++;
            t.Phase = phase;
            t.UpdatedAt = Now;
            t.Error = null;
            var participant = Resolve(t);
            if (participant is null)
            {
                t.Status = DsarTaskStatuses.NotBuilt;
                t.Outcomes = DsarViews.Serialize(new[] { DsarViews.Outcome(t.Module, DsarRecordOutcome.NotBuilt("ALL")) });
                continue;
            }

            // Participants share the scope's transaction (DAT, PTY, POL): a savepoint per task lets a faulting one be undone
            // without poisoning the transaction for the tasks that follow.
            var tx = db.Database.CurrentTransaction;
            var savepoint = tx is not null && tx.SupportsSavepoints ? "dsar_task" : null;
            if (savepoint is not null)
            {
                await tx!.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
            }

            try
            {
                var result = phase switch
                {
                    DsarPhases.Export => await participant.ExportAsync(subject, dsarContext, ct).ConfigureAwait(false),
                    DsarPhases.Erase => await participant.EraseAsync(subject, dsarContext, ct).ConfigureAwait(false),
                    _ => await participant.RestrictAsync(
                        subject, "DSAR " + c.RequestType, legalBasis ?? "GDPR Art 18: restriction of processing", null, ct).ConfigureAwait(false),
                };
                t.Outcomes = DsarViews.Serialize(result.Outcomes.Select(o => DsarViews.Outcome(t.Module, o)).ToList());
                if (phase == DsarPhases.Export)
                {
                    t.ExportPayload = AsJson(result.ExportPayload);
                }

                // Retention classes force restrict with a basis: erasing or anonymising one is a fault, never accepted silently.
                var violation = phase == DsarPhases.Erase
                    ? result.Outcomes.FirstOrDefault(o => o.Kind is DsarOutcomeKind.Erased or DsarOutcomeKind.Anonymised && retention.IsRestrictOnly(o.RecordClass))
                    : null;
                t.Status = violation is not null ? DsarTaskStatuses.Failed : result.HasNotBuilt ? DsarTaskStatuses.NotBuilt : DsarTaskStatuses.Done;
                t.Error = violation is null ? null : "RETENTION-VIOLATION:" + violation.RecordClass;
                if (savepoint is not null)
                {
                    await tx!.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DSAR participant {Module} failed in phase {Phase}", t.Module, phase);
                if (savepoint is not null)
                {
                    try
                    {
                        await tx!.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackEx) when (rollbackEx is not OperationCanceledException)
                    {
                        // TODO(hardening): the connection itself is broken; the case needs a retry in a fresh scope.
                        logger.LogError(rollbackEx, "DSAR savepoint rollback failed after {Module} fault", t.Module);
                    }
                }

                t.Status = DsarTaskStatuses.Failed;
                t.Error = ex.GetType().Name;
            }
        }
    }

    private static string? AsJson(string? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(payload);
        }
    }

    private IDsarParticipant? Resolve(DsarTaskRow task) =>
        registry.Find(task.Module) ?? (task.Module == "DAT" ? DataDsarAdapter.TryCreate(services, task.TaskId) : null);

    /// <summary>Completes the case, once, when no blocker remains (D-SL6-07: NOT_BUILT and FAILED tasks keep it open).</summary>
    private async Task CompleteIfReadyAsync(DsarCase c, CancellationToken ct)
    {
        if (c.Row.Status != DsarStatuses.InReview || DsarViews.Blockers(c.Row, c.Tasks).Count > 0)
        {
            return;
        }

        await CloseAsync(c, DsarStatuses.Completed, new { requestType = c.Row.RequestType, modules = c.Tasks.Select(t => t.Module).ToArray() }, ct).ConfigureAwait(false);
    }

    private async Task CloseAsync(DsarCase c, string status, object summary, CancellationToken ct)
    {
        var now = Now;
        c.Row.Status = status;
        c.Row.CompletedAt = now;
        var stopped = await new StopClockHandler(clocks, db, context, legalEntities).HandleAsync(
            new StopClock(new ClockStopRequest
            {
                ClockCode = ClockCode.Parse(Clock),
                SubjectRef = ClockSubject(c.Row.DsarId),
                Outcome = ClockStopRequest.OutcomeValue.Met,
                StopTime = now,
            }), ct).ConfigureAwait(false);
        if (stopped.IsFailure)
        {
            logger.LogWarning("DSAR clock could not be stopped: {Code}", stopped.Error!.Code);
        }

        await SaveAsync(c.Row, ct).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DSARCompletedV1.Descriptor), "Dsar", c.Row.DsarId.Value.ToString("D", CultureInfo.InvariantCulture),
            new DSARCompletedV1 { DsarId = c.Row.DsarId, OutcomeSummary = JsonSerializer.SerializeToElement(summary) },
            BusinessKeys.Empty.With("dsarId", c.Row.DsarId.Value.ToString("D", CultureInfo.InvariantCulture))) { OccurredAt = now });
    }

    private async Task SaveAsync(DsarRow row, CancellationToken ct)
    {
        row.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// DAT's part in a request, through its in-process contracts (DAT has no module participant). Erase calls dat.Erasure.execute
/// with requestId = case id + task id; the synchronous response completes the task.
/// </summary>
internal sealed class DataDsarAdapter(IServiceProvider services, Guid taskId) : IDsarParticipant
{
    private const string FactClass = "SUBJECT_FACT";

    public string Module => "DAT";

    public static IDsarParticipant? TryCreate(IServiceProvider services, Guid taskId) =>
        services.GetService(typeof(IDataErasureService)) is null ? null : new DataDsarAdapter(services, taskId);

    private static string Token(SubjectRef s) => s.PartyId.Value.ToString("D", CultureInfo.InvariantCulture);

    private static string RequestId(DsarContext c, Guid taskId) =>
        c.CaseId.ToString("N", CultureInfo.InvariantCulture) + taskId.ToString("N", CultureInfo.InvariantCulture);

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        var service = (IDataSubjectService)services.GetService(typeof(IDataSubjectService))!;
        var response = await service.ExportAsync(
            new SubjectExportRequest { RequestId = RequestId(context, taskId) + "-x", SubjectToken = Token(subject), DsarRef = context.CaseId },
            CommandOptions.New(), ct).ConfigureAwait(false);
        var count = response.Facts?.Count ?? 0;
        var outcome = count == 0 ? DsarRecordOutcome.NotHeld(FactClass) : new DsarRecordOutcome(FactClass, DsarOutcomeKind.NotHeld, count, Detail: "EXPORTED");
        return new DsarModuleResult(Module, [outcome], JsonSerializer.Serialize(new { module = Module, facts = response.Facts ?? [] }));
    }

    public async Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        var service = (IDataRestrictionService)services.GetService(typeof(IDataRestrictionService))!;
        var response = await service.ApplyAsync(
            new RestrictionApplyRequest { RequestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), SubjectToken = Token(subject), Scope = reason },
            CommandOptions.New(), ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, [DsarRecordOutcome.Restricted(FactClass, legalBasis, response.FactCount)]);
    }

    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        var service = (IDataErasureService)services.GetService(typeof(IDataErasureService))!;
        var response = await service.ExecuteAsync(
            new ErasureExecuteRequest { RequestId = RequestId(context, taskId), SubjectToken = Token(subject), DsarRef = context.CaseId },
            CommandOptions.New(), ct).ConfigureAwait(false);
        var outcome = response.Status == ErasureExecuteResponse.StatusValue.Erased
            ? new DsarRecordOutcome(FactClass, DsarOutcomeKind.Erased, response.FactCount)
            : DsarRecordOutcome.NotHeld(FactClass);
        return new DsarModuleResult(Module, [outcome]);
    }
}
