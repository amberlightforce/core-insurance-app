using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Complaints;

/// <summary>A complaint with the clocks it runs under.</summary>
internal sealed record ComplaintCase(ComplaintRow Row, IReadOnlyList<ClockRow> Clocks);

/// <summary>Views and JSON shared by the handlers and the reader.</summary>
internal static partial class ComplaintViews
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record LinkJson(string Module, string Type, string Id);

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Boundary();

    /// <summary>PascalCase enum name to the stored UPPER_SNAKE code.</summary>
    public static string Snake(string name) => Boundary().Replace(name, "_").ToUpperInvariant();

    public static List<ObjectRef> Links(string json) =>
        [.. (JsonSerializer.Deserialize<List<LinkJson>>(json, Json) ?? []).Select(l => new ObjectRef(Enum.Parse<ModuleCode>(l.Module), l.Type, l.Id))];

    public static string SerializeLinks(IEnumerable<ObjectRef> links) =>
        JsonSerializer.Serialize(links.Select(l => new LinkJson(l.Module.ToString(), l.Type, l.Id)), Json);

    public static List<ComplaintActionView> Actions(string json) => JsonSerializer.Deserialize<List<ComplaintActionView>>(json, Json) ?? [];

    public static string SerializeActions(IEnumerable<ComplaintActionView> actions) => JsonSerializer.Serialize(actions, Json);

    public static ComplaintDeadlineView Deadline(ClockRow c) => new()
    {
        ClockCode = c.ClockCode,
        InstanceId = c.ClockId.Value,
        Status = c.Status,
        Deadline = c.Deadline,
    };

    public static ComplaintView View(ComplaintRow r) => new()
    {
        ComplaintId = r.ComplaintId,
        Number = r.Number,
        Status = Enum.Parse<ComplaintView.StatusValue>(r.Status.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
        ReceivedAt = r.ReceivedAt,
        Channel = r.Channel,
        ComplainantPartyId = r.ComplainantPartyId?.Value,
        ComplainantType = r.ComplainantType,
        Description = r.Description,
        Language = r.Language,
        Category = r.Category,
        Cause = r.Cause,
        ProductLine = r.ProductLine,
        Vulnerable = r.Vulnerable,
        Links = Links(r.Links),
        Actions = Actions(r.Actions),
        AcknowledgedAt = r.AcknowledgedAt,
        ReplyStatus = r.ReplyStatus,
        Outcome = r.Outcome,
        ReplyText = r.ReplyText,
        ReplyAuthor = r.ReplyAuthor,
        ReplyApprovedBy = r.ReplyApprovedBy,
        RedressAmount = r.RedressAmount,
        RootCause = r.RootCause,
        AdrBody = r.AdrBody,
        AdrReference = r.AdrReference,
        AnsweredAt = r.AnsweredAt,
        ClosedAt = r.ClosedAt,
        ClosedReason = r.ClosedReason,
        CreatedBy = r.CreatedBy,
        RulesMissing = r.RulesMissing,
    };

    /// <summary>Complaint, number and deadlines: the three properties every complaint response shares.</summary>
    public static (ComplaintView Complaint, string Number, List<ComplaintDeadlineView> Deadlines) Of(ComplaintCase c) =>
        (View(c.Row), c.Row.Number, [.. c.Clocks.Select(Deadline)]);
}

/// <summary>
/// Complaint handling (W8-CMP-01): register, classify, link, acknowledge, investigate, reply with second-user approval,
/// escalate and close (PRD-11 section 7.3.4). The 50-day reply clock runs on the clock engine.
/// </summary>
internal sealed class ComplaintEngine(
    ComplianceDbContext db,
    ClockEngine clocks,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IEventPublisher events)
{
    private Instant Now => clocks.Now;

    private string Actor => context.Actor.Id;

    private LegalEntityId LegalEntity => ClockLookup.LegalEntity(context, legalEntities);

    private static DomainError Err(string code, string message) => DomainError.Of(ModuleCode.CMP, code, message);

    private static string Key(ComplaintId id) => id.Value.ToString("D", CultureInfo.InvariantCulture);

    public async Task<ComplaintCase?> FindAsync(Guid id, CancellationToken ct)
    {
        var complaintId = new ComplaintId(id);
        var legalEntity = LegalEntity;
        var row = await db.Set<ComplaintRow>().SingleOrDefaultAsync(r => r.ComplaintId == complaintId && r.LegalEntityId == legalEntity, ct).ConfigureAwait(false);
        return row is null ? null : new ComplaintCase(row, await ClocksOfAsync([row], ct).ConfigureAwait(false));
    }

    /// <summary>The reply clocks of the given complaints (live status and deadline).</summary>
    public async Task<List<ClockRow>> ClocksOfAsync(IReadOnlyCollection<ComplaintRow> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.ReplyClockId is not null).Select(r => new StatutoryClockId(r.ReplyClockId!.Value)).ToList();
        return ids.Count == 0 ? [] : await db.Clocks.AsNoTracking().Where(c => ids.Contains(c.ClockId)).ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<Result<ComplaintCase>> LoadAsync(Guid id, CancellationToken ct) =>
        await FindAsync(id, ct).ConfigureAwait(false) is { } found
            ? found
            : Err("NOT-FOUND", "The complaint does not exist in your legal entity.");

    private async Task<Result<ComplaintCase>> LoadOpenAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct).ConfigureAwait(false);
        return loaded.IsSuccess && loaded.Value.Row.Status == ComplaintStatuses.Closed ? Err("COMPLAINT-STATE", "The complaint is closed.") : loaded;
    }

    public async Task<Result<ComplaintCase>> CreateAsync(ComplaintCreateRequest request, CancellationToken ct)
    {
        var id = ComplaintId.New();
        var now = Now;
        var received = request.ReceivedAt ?? now;
        var number = $"CMP-{received.ToBusinessDate(clocks.Zone).Value.Year}-{id.Value.ToString("N", CultureInfo.InvariantCulture)[^10..].ToUpperInvariant()}";

        // Fail closed (REQ-CMP-127): without a clock value for the jurisdiction the complaint is still registered, flagged, and
        // no deadline is invented.
        // TODO(hardening): raise a WRK task for the missing rule and apply the strictest entity-level value.
        ClockRow? clock = null;
        var rulesMissing = false;
        var started = await new StartClockHandler(clocks, db, context, legalEntities).HandleAsync(
            new StartClock(new ClockStartRequest
            {
                ClockCode = ClockCode.Parse(ClockDefinitions.ComplaintReply),
                SubjectRef = new ObjectRef(ModuleCode.CMP, "Complaint", Key(id)),
                StartTime = received,
            }), ct).ConfigureAwait(false);
        if (started.IsSuccess)
        {
            clock = await db.Clocks.AsNoTracking().SingleAsync(c => c.ClockId == new StatutoryClockId(started.Value.InstanceId), ct).ConfigureAwait(false);
        }
        else if (started.Error!.Code.ToString().EndsWith("CLOCK-NO-VALUE", StringComparison.Ordinal))
        {
            rulesMissing = true;
        }
        else
        {
            return started.Error!;
        }

        var links = request.Links ?? [];
        var row = new ComplaintRow
        {
            ComplaintId = id,
            LegalEntityId = LegalEntity,
            Number = number,
            Status = ComplaintStatuses.Received,
            ReceivedAt = received,
            Channel = request.Channel,
            ComplainantPartyId = request.ComplainantPartyId is { } party ? new PartyId(party) : null,
            ComplainantType = request.ComplainantType?.ToString().ToUpperInvariant(),
            Description = request.Description,
            Language = request.Language,
            ProductLine = request.ProductLine,
            Vulnerable = request.Vulnerable ?? false,
            Links = ComplaintViews.SerializeLinks(links),
            CreatedBy = Actor,
            ReplyClockId = clock?.ClockId.Value,
            RulesMissing = rulesMissing,
            RecordVersion = 1,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        events.Publish(Outgoing(
            ComplaintReceivedV1.Descriptor,
            new ComplaintReceivedV1 { ComplaintId = id, Links = [.. links], ClockInstanceIds = clock is null ? [] : [clock.ClockId.Value] }, id, now));
        return new ComplaintCase(row, clock is null ? [] : [clock]);
    }

    public async Task<Result<ComplaintCase>> ClassifyAsync(ComplaintClassifyRequest request, CancellationToken ct)
    {
        var loaded = await LoadOpenAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        // BR-CMP-028: a claim notification is never a complaint. Routing it to CLM is the handler's job.
        if (string.Equals(request.Category, "CLAIM_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
        {
            return Err("NOT-A-COMPLAINT", "A claim notification is not a complaint; register it as a claim.");
        }

        var row = loaded.Value.Row;
        row.Category = request.Category;
        row.Cause = request.Cause ?? row.Cause;
        row.ProductLine = request.ProductLine ?? row.ProductLine;
        row.Vulnerable = request.Vulnerable ?? row.Vulnerable;
        await SaveAsync(row, ct).ConfigureAwait(false);
        return loaded.Value;
    }

    public async Task<Result<ComplaintCase>> LinkAsync(ComplaintLinkRequest request, CancellationToken ct)
    {
        var loaded = await LoadOpenAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var row = loaded.Value.Row;
        var links = ComplaintViews.Links(row.Links);
        foreach (var link in request.Links.Where(l => !links.Contains(l)))
        {
            links.Add(link);
        }

        row.Links = ComplaintViews.SerializeLinks(links);
        await SaveAsync(row, ct).ConfigureAwait(false);
        return loaded.Value;
    }

    public async Task<Result<ComplaintCase>> AcknowledgeAsync(ComplaintAcknowledgeRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var row = loaded.Value.Row;
        if (row.Status != ComplaintStatuses.Received)
        {
            return Err("COMPLAINT-STATE", "Only a received complaint can be acknowledged.");
        }

        row.Status = ComplaintStatuses.Acknowledged;
        row.AcknowledgedAt = Now;
        row.AcknowledgementNote = request.Note;
        await SaveAsync(row, ct).ConfigureAwait(false);
        events.Publish(Outgoing(ComplaintAcknowledgedV1.Descriptor, new ComplaintAcknowledgedV1 { ComplaintId = row.ComplaintId }, row.ComplaintId, Now));
        return loaded.Value;
    }

    public async Task<Result<ComplaintCase>> AddActionAsync(ComplaintAddActionRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var row = loaded.Value.Row;
        if (row.Status is not (ComplaintStatuses.Acknowledged or ComplaintStatuses.UnderInvestigation))
        {
            return Err("COMPLAINT-STATE", "Investigation steps are recorded on an acknowledged complaint that has no reply yet.");
        }

        var actions = ComplaintViews.Actions(row.Actions);
        actions.Add(new ComplaintActionView { Kind = request.Kind, Note = request.Note, By = Actor, At = Now });
        row.Actions = ComplaintViews.SerializeActions(actions);
        row.Status = ComplaintStatuses.UnderInvestigation;
        await SaveAsync(row, ct).ConfigureAwait(false);
        return loaded.Value;
    }

    public async Task<Result<ComplaintCase>> SubmitReplyAsync(ComplaintSubmitReplyRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var row = loaded.Value.Row;
        if (row.Status is not (ComplaintStatuses.Acknowledged or ComplaintStatuses.UnderInvestigation))
        {
            return Err("COMPLAINT-STATE", "A reply is drafted on an acknowledged complaint that is not yet answered.");
        }

        // TODO(hardening): redress above the entity threshold (EUR 500, BR-CMP-031) and rejected outcomes need approval too;
        // here every reply needs the second user.
        row.Outcome = ComplaintViews.Snake(request.Outcome.ToString());
        row.ReplyText = request.ReplyText;
        row.RedressAmount = request.RedressAmount;
        row.RootCause = request.RootCause ?? row.RootCause;
        row.ReplyAuthor = Actor;
        row.ReplyApprovedBy = null;
        row.ReplyStatus = ComplaintReplyStatuses.PendingApproval;
        row.Status = ComplaintStatuses.UnderInvestigation;
        await SaveAsync(row, ct).ConfigureAwait(false);
        return loaded.Value;
    }

    public async Task<Result<ComplaintCase>> ApproveReplyAsync(ComplaintApproveReplyRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var c = loaded.Value;
        var row = c.Row;
        if (row.ReplyStatus != ComplaintReplyStatuses.PendingApproval)
        {
            return Err("COMPLAINT-STATE", "There is no reply waiting for approval.");
        }

        // Segregation of duties: the approver is not the reply's author.
        // Also across delegation: an approver acting for the author (or an author acting through an agent) is the author.
        if (string.Equals(Actor, row.ReplyAuthor, StringComparison.Ordinal)
            || (context.OnBehalfOf is { } principal && string.Equals(principal.Id, row.ReplyAuthor, StringComparison.Ordinal)))
        {
            return Err("SOD", "The approver must differ from the author of the reply.");
        }

        var now = Now;
        if (request.Approved == false)
        {
            row.ReplyStatus = ComplaintReplyStatuses.Returned;
            await SaveAsync(row, ct).ConfigureAwait(false);
            return c;
        }

        row.ReplyStatus = ComplaintReplyStatuses.Approved;
        row.ReplyApprovedBy = Actor;
        row.Status = ComplaintStatuses.Answered;
        row.AnsweredAt = now;
        IReadOnlyList<ClockRow> running = c.Clocks;
        if (row.ReplyClockId is { } clockId)
        {
            // The reply is the thing the clock measures: stop it as Met (late if it already breached).
            var stopped = await new StopClockHandler(clocks, db, context, legalEntities).HandleAsync(
                new StopClock(new ClockStopRequest { InstanceId = clockId, Outcome = ClockStopRequest.OutcomeValue.Met, StopTime = now }), ct).ConfigureAwait(false);
            _ = stopped; // a clock already closed or cancelled does not block the answer
            running = await ClocksOfAsync([row], ct).ConfigureAwait(false);
        }

        await SaveAsync(row, ct).ConfigureAwait(false);
        events.Publish(Outgoing(ComplaintAnsweredV1.Descriptor, new ComplaintAnsweredV1 { ComplaintId = row.ComplaintId, Outcome = row.Outcome! }, row.ComplaintId, now));
        return new ComplaintCase(row, running);
    }

    public async Task<Result<ComplaintCase>> EscalateAsync(ComplaintEscalateRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var row = loaded.Value.Row;
        if (row.Status != ComplaintStatuses.Answered)
        {
            return Err("COMPLAINT-STATE", "Only an answered complaint can be escalated.");
        }

        row.Status = ComplaintStatuses.Escalated;
        row.AdrBody = request.AdrBody ?? row.AdrBody;
        row.AdrReference = request.Reference ?? row.AdrReference;
        await SaveAsync(row, ct).ConfigureAwait(false);
        events.Publish(Outgoing(ComplaintEscalatedV1.Descriptor, new ComplaintEscalatedV1 { ComplaintId = row.ComplaintId, AdrBody = row.AdrBody }, row.ComplaintId, Now));
        return loaded.Value;
    }

    public async Task<Result<ComplaintCase>> CloseAsync(ComplaintCloseRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var row = loaded.Value.Row;
        if (row.Status is not (ComplaintStatuses.Answered or ComplaintStatuses.Escalated))
        {
            return Err("COMPLAINT-STATE", "Only an answered or escalated complaint can be closed.");
        }

        // BR-CMP-032: an upheld complaint needs a root cause.
        // TODO(hardening): redress must be Paid (or a reason recorded) before an awarded complaint closes.
        var rootCause = request.RootCause ?? row.RootCause;
        if (ComplaintOutcomes.IsUpheld(row.Outcome) && string.IsNullOrWhiteSpace(rootCause))
        {
            return Err("ROOT-CAUSE-REQUIRED", "A root cause is required to close an upheld complaint.");
        }

        row.RootCause = rootCause;
        row.Status = ComplaintStatuses.Closed;
        row.ClosedAt = Now;
        row.ClosedReason = request.Reason;
        await SaveAsync(row, ct).ConfigureAwait(false);
        events.Publish(Outgoing(ComplaintClosedV1.Descriptor, new ComplaintClosedV1 { ComplaintId = row.ComplaintId }, row.ComplaintId, Now));
        return loaded.Value;
    }

    private static OutgoingEvent Outgoing<TPayload>(EventContract contract, TPayload payload, ComplaintId id, Instant at)
        where TPayload : notnull =>
        new(EventDescriptor.From(contract), "Complaint", Key(id), payload, BusinessKeys.Empty.With("complaintId", Key(id))) { OccurredAt = at };

    private async Task SaveAsync(ComplaintRow row, CancellationToken ct)
    {
        row.RecordVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
