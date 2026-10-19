using System.Text.Json;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Compliance.Clocks;

/// <summary>Configuration section <c>Compliance:Clocks</c>: the zone clocks are due in and how often the sweeper runs.</summary>
internal sealed class ClockOptions
{
    public const string Section = "Compliance:Clocks";

    /// <summary>IANA zone of the stamp's legal entity; a due date is the end of the local day (D-SL6-04).</summary>
    public string TimeZone { get; set; } = "Europe/Athens";

    /// <summary>Seconds between sweeper runs.</summary>
    public int SweepIntervalSeconds { get; set; } = 10;

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

/// <summary>
/// The clock state machine and its events (PRD-11 section 8.1, REQ-CMP-095..104). Every method works on a row the caller
/// has locked (<see cref="LockAsync"/>) inside the unit of work, changes it, writes the history row and stages the event
/// in the outbox. Expiry is decided by the platform clock only, so a dev-clock advance and the sweeper agree.
/// </summary>
internal sealed class ClockEngine(
    ComplianceDbContext db,
    RequestContext context,
    IClock clock,
    IEventPublisher events,
    IOptions<ClockOptions> options)
{
    private static readonly TimeSpan LateEmissionAfter = TimeSpan.FromMinutes(5);

    public TimeZoneInfo Zone => options.Value.Zone;

    public Instant Now => clock.Now;

    /// <summary>Row lock by id (FOR UPDATE), in the caller's transaction.</summary>
    public Task<ClockRow?> LockAsync(LegalEntityId legalEntity, Guid clockId, CancellationToken cancellationToken) =>
        db.Clocks.FromSqlInterpolated($"SELECT * FROM cmp.clock WHERE clock_id = {clockId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Row lock by definition, subject and key.</summary>
    public Task<ClockRow?> LockAsync(LegalEntityId legalEntity, string code, ObjectRef subject, string key, CancellationToken cancellationToken) =>
        db.Clocks.FromSqlInterpolated(
                $"""
                 SELECT * FROM cmp.clock WHERE legal_entity_id = {legalEntity.Value} AND clock_code = {code}
                   AND subject_module = {subject.Module.ToString()} AND subject_type = {subject.Type} AND subject_id = {subject.Id} AND key = {key}
                 FOR UPDATE
                 """)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Serialises concurrent starts of one instance (the unique index would otherwise abort the transaction).</summary>
    public Task LockKeyAsync(string code, ObjectRef subject, string key, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({$"cmp.clock|{code}|{subject}|{key}"}, 0))", cancellationToken);

    public static ObjectRef SubjectOf(ClockRow row) => new(Enum.Parse<ModuleCode>(row.SubjectModule), row.SubjectType, row.SubjectId);

    public static DomainError State(ClockRow row, string action) =>
        DomainError.Of(ModuleCode.CMP, "CLOCK-STATE", $"Clock {row.ClockCode} ({row.ClockId.Value}) is {row.Status}; it cannot be {action}.");

    // ---- transitions ----

    public static Result<StatutoryClockState> Fire(ClockRow row, StatutoryClockTrigger trigger, string action)
    {
        var kind = Kind(row);
        var model = StatutoryClockStateModel.For(kind);
        var from = ClockStatus.Parse(row.Status);
        return model.CanFire(from, trigger) ? model.Fire(from, trigger) : State(row, action);
    }

    public static StatutoryClockKind Kind(ClockRow row) => row.Kind == "WAITING_PERIOD" ? StatutoryClockKind.WaitingPeriod : StatutoryClockKind.Deadline;

    /// <summary>Gives the warning once the warning instant has passed and the deadline has not (DEADLINE clocks).</summary>
    public bool Warn(ClockRow row, Instant now)
    {
        if (row.Status != ClockStatus.Running || row.WarnAt is not { } warnAt || warnAt > now || row.Deadline < now)
        {
            return false;
        }

        row.Status = ClockStatus.Warned;
        row.WarnAt = null;
        Touch(row);
        var threshold = ClockDefinitions.Find(row.ClockCode)?.WarnBefore ?? "P0D";
        Record(row, "WARNED", now, new { threshold, deadline = row.Deadline.ToString() });
        Publish(row, ClockWarnedV1.Descriptor, new ClockWarnedV1
        {
            ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), Deadline = row.Deadline, Threshold = threshold,
        }, now);
        return true;
    }

    /// <summary>Applies the expiry when the due instant has passed: WAITING_PERIOD elapses, DEADLINE breaches.</summary>
    public bool Expire(ClockRow row, Instant now)
    {
        if (!ClockStatus.IsLive(row.Status) || row.Deadline >= now)
        {
            return false;
        }

        var kind = Kind(row);
        var next = StatutoryClockStateModel.For(kind).Fire(ClockStatus.Parse(row.Status), StatutoryClockTrigger.Expire);
        row.Status = ClockStatus.Code(next.Value);
        row.ClosedAt = now;
        row.WarnAt = null;
        Touch(row);
        var late = now - row.Deadline > LateEmissionAfter ? true : (bool?)null;
        if (kind == StatutoryClockKind.WaitingPeriod)
        {
            Record(row, "ELAPSED", now, new { deadline = row.Deadline.ToString() });
            Publish(row, ClockElapsedV1.Descriptor, new ClockElapsedV1
            {
                ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), Kind = ClockElapsedV1.KindValue.WaitingPeriod, LateEmission = late,
            }, row.Deadline);
        }
        else
        {
            Record(row, "BREACHED", now, new { deadline = row.Deadline.ToString() });
            Publish(row, ClockBreachedV1.Descriptor, new ClockBreachedV1
            {
                ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), Deadline = row.Deadline, Late = true, LateEmission = late,
            }, row.Deadline);
        }

        return true;
    }

    public Result<bool> Pause(ClockRow row, Instant now, string? reason)
    {
        if (Fire(row, StatutoryClockTrigger.Pause, "paused") is { IsFailure: true } refused)
        {
            return refused.Error!;
        }

        row.Status = ClockStatus.Paused;
        row.PausedAt = now;
        row.ValueVersion++;
        Touch(row);
        Record(row, "PAUSED", now, new { reason });
        Publish(row, ClockPausedV1.Descriptor, new ClockPausedV1 { ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), NewDeadline = null }, now);
        return true;
    }

    public Result<bool> Resume(ClockRow row, Instant now, string? reason)
    {
        if (Fire(row, StatutoryClockTrigger.Resume, "resumed") is { IsFailure: true } refused)
        {
            return refused.Error!;
        }

        var pausedAt = row.PausedAt ?? now;
        var shifted = ClockMath.Shift(row.Deadline, pausedAt, now);
        row.WarnAt = row.WarnAt is { } warn ? ClockMath.Shift(warn, pausedAt, now) : null;
        row.Deadline = shifted;
        row.Status = ClockStatus.Running;
        row.PausedAt = null;
        row.ValueVersion++;
        Touch(row);
        Record(row, "RESUMED", now, new { reason, newDeadline = shifted.ToString() });
        Publish(row, ClockResumedV1.Descriptor, new ClockResumedV1 { ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), NewDeadline = shifted }, now);
        return true;
    }

    public Result<bool> Cancel(ClockRow row, Instant now, string reason)
    {
        if (Fire(row, StatutoryClockTrigger.Cancel, "cancelled") is { IsFailure: true } refused)
        {
            return refused.Error!;
        }

        row.Status = ClockStatus.Cancelled;
        row.ClosedAt = now;
        row.PausedAt = null;
        row.WarnAt = null;
        Touch(row);
        Record(row, "CANCELLED", now, new { reason });
        Publish(row, ClockCancelledV1.Descriptor, new ClockCancelledV1 { ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), NewDeadline = null }, now);
        return true;
    }

    public Result<bool> Extend(ClockRow row, ClockDefinition definition, IsoPeriod period, Instant now, string? reason)
    {
        if (!definition.Extendable)
        {
            return DomainError.Of(ModuleCode.CMP, "EXTENSION-NOT-ALLOWED", $"Clock {definition.Code} cannot be extended.");
        }

        if (row.Status is not (ClockStatus.Running or ClockStatus.Warned or ClockStatus.Paused))
        {
            return State(row, "extended");
        }

        var extended = ClockMath.Extend(row.Deadline, period, Zone);
        row.Deadline = extended;
        row.WarnAt = row.Status == ClockStatus.Running ? ClockMath.WarnAt(extended, definition.WarnBefore, Zone) : row.WarnAt;
        row.ValueVersion++;
        Touch(row);
        Record(row, "EXTENDED", now, new { reason, newDeadline = extended.ToString() });
        return true;
    }

    /// <summary>
    /// Stops the clock with an outcome at <paramref name="stopTime"/>. A stop at or before the due instant is Met even when the
    /// sweeper has not noticed that time has passed; a later stop first applies the expiry (a deadline then stays Breached and is
    /// marked MetLate, a waiting period has already elapsed and cannot be stopped). A paused clock may be stopped (cure during an
    /// arrangement), which the state table alone would refuse.
    /// </summary>
    public Result<StopResult> Stop(ClockRow row, string outcome, Instant stopTime, Instant now)
    {
        if (row.Status == ClockStatus.Met && row.Outcome == outcome)
        {
            return new StopResult(row.Status, row.MetLate); // replay
        }

        if (ClockStatus.IsLive(row.Status) && stopTime > row.Deadline)
        {
            Expire(row, Instant.Max(now, stopTime));
        }

        if (row.Status == ClockStatus.Breached && Kind(row) == StatutoryClockKind.Deadline)
        {
            // Late completion: the clock stays Breached, marked MetLate (R-78).
            row.MetLate = true;
            row.Outcome = outcome;
            Touch(row);
            Record(row, "COMPLETED_LATE", now, new { outcome, stopTime = stopTime.ToString() });
            return new StopResult(row.Status, true);
        }

        if (row.Status is not (ClockStatus.Running or ClockStatus.Warned or ClockStatus.Paused))
        {
            return State(row, "stopped");
        }

        row.Status = ClockStatus.Met;
        row.Outcome = outcome;
        row.ClosedAt = stopTime < row.StartedAt ? now : stopTime;
        row.PausedAt = null;
        row.WarnAt = null;
        Touch(row);
        Record(row, "MET", now, new { outcome, stopTime = stopTime.ToString() });
        Publish(row, ClockMetV1.Descriptor, new ClockMetV1 { ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), Outcome = outcome }, now);
        return new StopResult(row.Status, false);
    }

    public void Created(ClockRow row, Instant now)
    {
        Record(row, "STARTED", now, new { deadline = row.Deadline.ToString(), variant = row.Variant });
        Publish(row, ClockStartedV1.Descriptor, new ClockStartedV1 { ClockCode = ClockCode.Parse(row.ClockCode), Subject = SubjectOf(row), Deadline = row.Deadline }, now);
    }

    private static void Touch(ClockRow row) => row.RecordVersion++;

    private void Record(ClockRow row, string type, Instant at, object detail) =>
        db.ClockEvents.Add(new ClockEventRow
        {
            ClockEventId = Guid.CreateVersion7(),
            ClockId = row.ClockId,
            Type = type,
            OccurredAt = at,
            Actor = context.Actor.ToString(),
            Detail = JsonSerializer.Serialize(detail, SharedKernelJson.Options),
        });

    private void Publish<TPayload>(ClockRow row, CoreIns.Platform.Contracts.Events.EventContract descriptor, TPayload payload, Instant occurredAt)
        where TPayload : notnull =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(descriptor), "ClockInstance", row.ClockId.Value.ToString(), payload,
            BusinessKeys.Empty.With("clockInstanceId", row.ClockId.Value.ToString()))
        { OccurredAt = occurredAt });
}

/// <summary>Result of a stop: the state afterwards and whether completion was late.</summary>
internal readonly record struct StopResult(string Status, bool MetLate);
