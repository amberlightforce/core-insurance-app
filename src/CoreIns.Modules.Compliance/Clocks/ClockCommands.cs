using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Clocks;

internal sealed record StartClock(ClockStartRequest Request) : ICommand<ClockStartResponse>;

internal sealed record PauseClock(ClockPauseRequest Request) : ICommand<ClockPauseResponse>;

internal sealed record ResumeClock(ClockResumeRequest Request) : ICommand<ClockResumeResponse>;

internal sealed record StopClock(ClockStopRequest Request) : ICommand<ClockStopResponse>;

internal sealed record CancelClock(ClockCancelRequest Request) : ICommand<ClockCancelResponse>;

internal sealed record ExtendClock(ClockExtendRequest Request) : ICommand<ClockExtendResponse>;

/// <summary>Internal (no HTTP): the sweeper's per-clock step. Idempotent by state, so it needs no key.</summary>
internal sealed record SweepClock(StatutoryClockId ClockId) : ICommand<string>;

internal static class ClockValidation
{
    public static void Selector<T>(AbstractValidator<T> validator, Func<T, Guid?> id, Func<T, ObjectRef?> subject, Func<T, ClockCode?> code, Func<T, string?> key)
    {
        validator.RuleFor(c => c).Must(c => id(c) is not null || (subject(c) is not null && code(c) is not null))
            .WithMessage("Give instanceId, or subjectRef and clockCode.");
        validator.RuleFor(c => key(c)).MaximumLength(128);
    }
}

internal sealed class StartClockValidator : AbstractValidator<StartClock>
{
    public StartClockValidator() => RuleFor(c => c.Request.Key).MaximumLength(128);
}

internal sealed class PauseClockValidator : AbstractValidator<PauseClock>
{
    public PauseClockValidator() => ClockValidation.Selector(this, c => c.Request.InstanceId, c => c.Request.SubjectRef, c => c.Request.ClockCode, c => c.Request.Key);
}

internal sealed class ResumeClockValidator : AbstractValidator<ResumeClock>
{
    public ResumeClockValidator() => ClockValidation.Selector(this, c => c.Request.InstanceId, c => c.Request.SubjectRef, c => c.Request.ClockCode, c => c.Request.Key);
}

internal sealed class StopClockValidator : AbstractValidator<StopClock>
{
    public StopClockValidator() => ClockValidation.Selector(this, c => c.Request.InstanceId, c => c.Request.SubjectRef, c => c.Request.ClockCode, c => c.Request.Key);
}

internal sealed class CancelClockValidator : AbstractValidator<CancelClock>
{
    public CancelClockValidator() => ClockValidation.Selector(this, c => c.Request.InstanceId, c => c.Request.SubjectRef, c => c.Request.ClockCode, c => c.Request.Key);
}

internal sealed class ExtendClockValidator : AbstractValidator<ExtendClock>
{
    public ExtendClockValidator()
    {
        ClockValidation.Selector(this, c => c.Request.InstanceId, c => c.Request.SubjectRef, c => c.Request.ClockCode, c => c.Request.Key);
        RuleFor(c => c.Request.Extension).Must(e => IsoPeriod.TryParse(e, out _)).WithMessage("extension must be an ISO 8601 period such as P2M or P7D.");
    }
}

/// <summary>Shared lookups of the clock commands.</summary>
internal static class ClockLookup
{
    public static LegalEntityId LegalEntity(RequestContext context, ILegalEntityDirectory directory) =>
        directory.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>Finds and locks the addressed instance.</summary>
    public static async Task<Result<ClockRow>> FindAsync(
        ClockEngine engine, LegalEntityId legalEntity, Guid? instanceId, ObjectRef? subject, ClockCode? code, string? key, CancellationToken cancellationToken)
    {
        var row = instanceId is { } id
            ? await engine.LockAsync(legalEntity, id, cancellationToken).ConfigureAwait(false)
            : await engine.LockAsync(legalEntity, code!.Value.Value, subject!, key ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return row is null
            ? DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The clock does not exist in your legal entity.")
            : row;
    }
}

internal sealed class StartClockHandler(ClockEngine engine, ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<StartClock, ClockStartResponse>
{
    public async Task<Result<ClockStartResponse>> HandleAsync(StartClock command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var definition = ClockDefinitions.Find(request.ClockCode.Value);
        if (definition is null)
        {
            return DomainError.Of(ModuleCode.CMP, "CLOCK-UNKNOWN", $"Clock definition '{request.ClockCode.Value}' does not exist.");
        }

        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");
        if (!string.Equals(definition.Jurisdiction, jurisdiction.Value, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.CMP, "CLOCK-NO-VALUE", $"Clock {definition.Code} has no value for jurisdiction {jurisdiction.Value}.");
        }

        var legalEntity = ClockLookup.LegalEntity(context, legalEntities);
        var key = request.Key ?? string.Empty;
        await engine.LockKeyAsync(definition.Code, request.SubjectRef, key, cancellationToken).ConfigureAwait(false);

        // Idempotent on (definition, subject, key): a second start returns the instance that exists.
        var existing = await engine.LockAsync(legalEntity, definition.Code, request.SubjectRef, key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Respond(existing);
        }

        var now = engine.Now;
        var start = request.StartTime ?? now;
        var deadline = ClockMath.Deadline(start, definition.Period, engine.Zone);
        var warnAt = ClockMath.WarnAt(deadline, definition.WarnBefore, engine.Zone);
        var row = new ClockRow
        {
            ClockId = StatutoryClockId.New(),
            LegalEntityId = legalEntity,
            Jurisdiction = jurisdiction.Value,
            ClockCode = definition.Code,
            SubjectModule = request.SubjectRef.Module.ToString(),
            SubjectType = request.SubjectRef.Type,
            SubjectId = request.SubjectRef.Id,
            Key = key,
            Variant = request.Variant,
            Kind = definition.KindCode,
            Status = ClockStatus.Running,
            StartedAt = start,
            Deadline = deadline,
            WarnAt = warnAt is { } w && w > start ? w : null,
            ValueVersion = 1,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.Clocks.Add(row);
        engine.Created(row, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Respond(row);
    }

    internal static ClockStartResponse Respond(ClockRow row) => new()
    {
        InstanceId = row.ClockId.Value,
        Status = Enum.Parse<ClockStartResponse.StatusValue>(row.Status, ignoreCase: true),
        Deadline = row.Deadline,
        ValueVersion = row.ValueVersion,
    };
}

internal sealed class PauseClockHandler(ClockEngine engine, ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<PauseClock, ClockPauseResponse>
{
    public async Task<Result<ClockPauseResponse>> HandleAsync(PauseClock command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var found = await ClockLookup.FindAsync(engine, ClockLookup.LegalEntity(context, legalEntities), r.InstanceId, r.SubjectRef, r.ClockCode, r.Key, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var row = found.Value;
        if (row.Status == ClockStatus.Paused)
        {
            return Respond(row); // replay
        }

        var done = engine.Pause(row, engine.Now, r.Reason);
        if (done.IsFailure)
        {
            return done.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Respond(row);
    }

    private static ClockPauseResponse Respond(ClockRow row) => new()
    {
        InstanceId = row.ClockId.Value,
        Status = Enum.Parse<ClockPauseResponse.StatusValue>(row.Status, ignoreCase: true),
        Deadline = row.Deadline,
        ValueVersion = row.ValueVersion,
    };
}

internal sealed class ResumeClockHandler(ClockEngine engine, ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<ResumeClock, ClockResumeResponse>
{
    public async Task<Result<ClockResumeResponse>> HandleAsync(ResumeClock command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var found = await ClockLookup.FindAsync(engine, ClockLookup.LegalEntity(context, legalEntities), r.InstanceId, r.SubjectRef, r.ClockCode, r.Key, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var row = found.Value;
        var done = engine.Resume(row, engine.Now, r.Reason);
        if (done.IsFailure)
        {
            return done.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Respond(row);
    }

    private static ClockResumeResponse Respond(ClockRow row) => new()
    {
        InstanceId = row.ClockId.Value,
        Status = Enum.Parse<ClockResumeResponse.StatusValue>(row.Status, ignoreCase: true),
        Deadline = row.Deadline,
        ValueVersion = row.ValueVersion,
    };
}

internal sealed class StopClockHandler(ClockEngine engine, ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<StopClock, ClockStopResponse>
{
    public async Task<Result<ClockStopResponse>> HandleAsync(StopClock command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var found = await ClockLookup.FindAsync(engine, ClockLookup.LegalEntity(context, legalEntities), r.InstanceId, r.SubjectRef, r.ClockCode, r.Key, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var stopped = engine.Stop(found.Value, r.Outcome.ToString(), r.StopTime, engine.Now);
        if (stopped.IsFailure)
        {
            return stopped.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ClockStopResponse
        {
            InstanceState = Enum.Parse<ClockStopResponse.InstanceStateValue>(stopped.Value.Status, ignoreCase: true),
            MetLateFlag = stopped.Value.MetLate,
        };
    }
}

internal sealed class CancelClockHandler(ClockEngine engine, ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<CancelClock, ClockCancelResponse>
{
    public async Task<Result<ClockCancelResponse>> HandleAsync(CancelClock command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        if (string.IsNullOrWhiteSpace(r.Reason))
        {
            return DomainError.Of(ModuleCode.CMP, "CLOCK-CANCEL-REASON", "Cancelling a clock needs a reason.");
        }

        var found = await ClockLookup.FindAsync(engine, ClockLookup.LegalEntity(context, legalEntities), r.InstanceId, r.SubjectRef, r.ClockCode, r.Key, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var row = found.Value;
        if (row.Status != ClockStatus.Cancelled)
        {
            var done = engine.Cancel(row, engine.Now, r.Reason);
            if (done.IsFailure)
            {
                return done.Error!;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ClockCancelResponse
        {
            InstanceId = row.ClockId.Value,
            Status = Enum.Parse<ClockCancelResponse.StatusValue>(row.Status, ignoreCase: true),
            Deadline = row.Deadline,
            ValueVersion = row.ValueVersion,
        };
    }
}

internal sealed class ExtendClockHandler(ClockEngine engine, ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<ExtendClock, ClockExtendResponse>
{
    public async Task<Result<ClockExtendResponse>> HandleAsync(ExtendClock command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var found = await ClockLookup.FindAsync(engine, ClockLookup.LegalEntity(context, legalEntities), r.InstanceId, r.SubjectRef, r.ClockCode, r.Key, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var row = found.Value;
        var definition = ClockDefinitions.Find(row.ClockCode)!;
        var done = engine.Extend(row, definition, IsoPeriod.Parse(r.Extension), engine.Now, r.Reason);
        if (done.IsFailure)
        {
            return done.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ClockExtendResponse
        {
            InstanceId = row.ClockId.Value,
            Status = Enum.Parse<ClockExtendResponse.StatusValue>(row.Status, ignoreCase: true),
            Deadline = row.Deadline,
            ValueVersion = row.ValueVersion,
        };
    }
}

/// <summary>
/// One sweeper step: locks the clock, then applies the warning or the expiry if the platform clock says it is due. Running
/// twice, or two sweepers at once, changes nothing the second time because the decision is made on the locked row.
/// </summary>
internal sealed class SweepClockHandler(ClockEngine engine, ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<SweepClock, string>
{
    public const string Warned = "WARNED";
    public const string Expired = "EXPIRED";
    public const string NotDue = "NOT_DUE";

    public async Task<Result<string>> HandleAsync(SweepClock command, CancellationToken cancellationToken)
    {
        var row = await engine.LockAsync(ClockLookup.LegalEntity(context, legalEntities), command.ClockId.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The clock does not exist in your legal entity.");
        }

        var now = engine.Now;
        var outcome = NotDue;
        if (engine.Expire(row, now))
        {
            outcome = Expired;
        }
        else if (engine.Warn(row, now))
        {
            outcome = Warned;
        }

        if (outcome != NotDue)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }
}
