using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>Views, events and rules of the CAT event register (BO-CLM-08).</summary>
internal static class CatEventSupport
{
    public const string CodePattern = "^[A-Z][A-Z0-9_]{0,63}$";

    public static async Task<CatEventView> ViewAsync(ClaimsDbContext db, CatEventRow row, CancellationToken cancellationToken)
    {
        var count = await db.Claims.AsNoTracking().CountAsync(c => c.LegalEntityId == row.LegalEntityId && c.CatEventCode == row.CatCode, cancellationToken).ConfigureAwait(false);
        return View(row, count);
    }

    public static CatEventView View(CatEventRow row, int taggedClaimCount) => new()
    {
        CatEventId = row.CatEventId,
        CatCode = row.CatCode,
        Name = row.Name,
        Perils = row.Perils,
        Region = row.Region,
        WindowFrom = row.WindowFrom,
        WindowTo = row.WindowTo,
        Status = row.Status == CatEventRow.Open ? CatEventView.StatusValue.Open : CatEventView.StatusValue.Closed,
        TaggedClaimCount = taggedClaimCount,
        RecordVersion = row.RecordVersion,
    };

    public static OutgoingEvent Declared(CatEventRow row, string eventKind)
    {
        var payload = new CatEventDeclaredV1
        {
            CatEventId = row.CatEventId,
            CatCode = row.CatCode,
            Perils = row.Perils,
            Period = new InstantRange(row.WindowFrom, row.WindowTo),
            Area = JsonSerializer.SerializeToElement(new { region = row.Region }),
            Status = row.Status,
        };
        var keys = BusinessKeys.Empty.With("catEventId", row.CatEventId.ToString());
        return eventKind == "declared"
            ? new OutgoingEvent(EventDescriptor.From(CatEventDeclaredV1.Descriptor), "CatEvent", row.CatEventId.ToString(), payload, keys)
            : new OutgoingEvent(
                EventDescriptor.From(CatEventChangedV1.Descriptor), "CatEvent", row.CatEventId.ToString(),
                new CatEventChangedV1 { CatEventId = payload.CatEventId, CatCode = payload.CatCode, Perils = payload.Perils, Period = payload.Period, Area = payload.Area, Status = payload.Status },
                keys);
    }

    public static DomainError Error(string message) => DomainError.Of(ModuleCode.CLM, "CAT-EVENT", message);
}

/// <summary><c>clm.CatEvent.declare</c>.</summary>
internal sealed record DeclareCatEvent(CatEventDeclareRequest Request) : ICommand<CatEventDeclareResponse>;

internal sealed class DeclareCatEventValidator : AbstractValidator<DeclareCatEvent>
{
    public DeclareCatEventValidator()
    {
        RuleFor(c => c.Request.CatCode).NotEmpty().Matches(CatEventSupport.CodePattern).WithErrorCode("CODE");
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Region).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Perils).NotEmpty();
        RuleForEach(c => c.Request.Perils).Matches(CatEventSupport.CodePattern).WithErrorCode("CODE");
    }
}

/// <summary>
/// Declares a CAT event: a unique code per legal entity, at least one peril, a region and a date window ([from, to), to may be open).
/// Publishes <c>CatEventDeclared</c>. TODO(hardening): maker-checker (PLT approval) for opening and closing an event.
/// </summary>
internal sealed class DeclareCatEventHandler(ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, Domain.ClaimProtection protection)
    : ICommandHandler<DeclareCatEvent, CatEventDeclareResponse>
{
    public async Task<Result<CatEventDeclareResponse>> HandleAsync(DeclareCatEvent command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.WindowTo is { } to && to <= request.WindowFrom)
        {
            return CatEventSupport.Error("The event window must end after it starts.");
        }

        var legalEntity = protection.Current(context);
        var now = clock.Now;
        var row = new CatEventRow
        {
            CatEventId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            CatCode = request.CatCode,
            Name = request.Name.Trim(),
            Perils = [.. request.Perils.Distinct(StringComparer.Ordinal)],
            Region = request.Region.Trim(),
            WindowFrom = request.WindowFrom,
            WindowTo = request.WindowTo,
            Status = CatEventRow.Open,
            UpdatedAt = now,
        };
        db.CatEvents.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, "ux_cat_event_code"))
        {
            return CatEventSupport.Error($"A CAT event with code {request.CatCode} already exists.");
        }

        events.Publish(CatEventSupport.Declared(row, "declared"));
        return new CatEventDeclareResponse { CatEvent = CatEventSupport.View(row, 0) };
    }
}

internal sealed class DeclareCatEventAuditor : ICommandAuditor<DeclareCatEvent, CatEventDeclareResponse>
{
    public CommandAuditFacts Describe(DeclareCatEvent command, Result<CatEventDeclareResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.CLM, "CatEvent", ok.Value.CatEvent.CatEventId.ToString("D")),
                ObjectNumber = ok.Value.CatEvent.CatCode,
                BusinessKeys = BusinessKeys.Empty.With("catEventId", ok.Value.CatEvent.CatEventId.ToString()),
                Changes = AuditDiff.Compute(null, new { catCode = command.Request.CatCode, name = command.Request.Name, perils = command.Request.Perils, region = command.Request.Region, windowFrom = command.Request.WindowFrom, windowTo = command.Request.WindowTo }),
            }
            : new CommandAuditFacts();
}

/// <summary><c>clm.CatEvent.close</c>.</summary>
internal sealed record CloseCatEvent(CatEventCloseRequest Request) : ICommand<CatEventCloseResponse>;

internal sealed class CloseCatEventValidator : AbstractValidator<CloseCatEvent>
{
    public CloseCatEventValidator()
    {
        RuleFor(c => c.Request.CatCode).NotEmpty().Matches(CatEventSupport.CodePattern).WithErrorCode("CODE");
        RuleFor(c => c.Request.Reason).Matches(CatEventSupport.CodePattern).When(c => c.Request.Reason is not null).WithErrorCode("CODE");
    }
}

/// <summary>Closes an open event: no further claim can be tagged, existing tags stay. Publishes <c>CatEventChanged</c>.</summary>
internal sealed class CloseCatEventHandler(ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, Domain.ClaimProtection protection)
    : ICommandHandler<CloseCatEvent, CatEventCloseResponse>
{
    public async Task<Result<CatEventCloseResponse>> HandleAsync(CloseCatEvent command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var row = await db.CatEvents.SingleOrDefaultAsync(e => e.LegalEntityId == legalEntity && e.CatCode == command.Request.CatCode, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ClaimSupport.NotFound("CAT event");
        }

        if (row.Status == CatEventRow.Closed)
        {
            return CatEventSupport.Error($"CAT event {row.CatCode} is already closed.");
        }

        var now = clock.Now;
        row.Status = CatEventRow.Closed;
        row.CloseReason = command.Request.Reason;
        row.ClosedAt = now;
        row.UpdatedAt = now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CatEventSupport.Error($"CAT event {row.CatCode} changed meanwhile; reload it.");
        }

        events.Publish(CatEventSupport.Declared(row, "changed"));
        return new CatEventCloseResponse { CatEvent = await CatEventSupport.ViewAsync(db, row, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class CloseCatEventAuditor : ICommandAuditor<CloseCatEvent, CatEventCloseResponse>
{
    public CommandAuditFacts Describe(CloseCatEvent command, Result<CatEventCloseResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.CLM, "CatEvent", ok.Value.CatEvent.CatEventId.ToString("D")),
                ObjectNumber = ok.Value.CatEvent.CatCode,
                BusinessKeys = BusinessKeys.Empty.With("catEventId", ok.Value.CatEvent.CatEventId.ToString()),
                Changes = AuditDiff.Compute(new { status = "OPEN" }, new { status = "CLOSED", reason = command.Request.Reason }),
            }
            : new CommandAuditFacts();
}
