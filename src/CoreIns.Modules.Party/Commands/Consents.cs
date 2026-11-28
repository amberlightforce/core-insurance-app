using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Consent.record</c> (BO-PTY-06).</summary>
internal sealed record RecordConsent(ConsentRecordRequest Request) : ICommand<ConsentRecordResponse>;

/// <summary><c>pty.Consent.withdraw</c>: takes effect at once.</summary>
internal sealed record WithdrawConsent(ConsentWithdrawRequest Request) : ICommand<ConsentWithdrawResponse>;

/// <summary><c>pty.CommunicationPreference.set</c>.</summary>
internal sealed record SetCommunicationPreference(CommunicationPreferenceSetRequest Request) : ICommand<CommunicationPreferenceSetResponse>;

internal sealed class RecordConsentValidator : AbstractValidator<RecordConsent>
{
    public RecordConsentValidator()
    {
        RuleFor(c => c.Request.PartyId).NotEmpty();
        RuleFor(c => c.Request.Purpose).Must(ConsentCatalogue.Purposes.Contains).WithMessage("Unknown consent purpose.");
        RuleFor(c => c.Request.Channel).Must(ConsentCatalogue.Channels.Contains).WithMessage("Unknown channel.");
        RuleFor(c => c.Request.Source).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.WordingVersion!).MaximumLength(64).When(c => c.Request.WordingVersion is not null);
        RuleFor(c => c.Request.ProofRef).MaximumLength(500);
    }
}

internal sealed class WithdrawConsentValidator : AbstractValidator<WithdrawConsent>
{
    public WithdrawConsentValidator()
    {
        RuleFor(c => c.Request.PartyId).NotEmpty();
        RuleFor(c => c.Request.Purpose).Must(ConsentCatalogue.Purposes.Contains).WithMessage("Unknown consent purpose.");
        RuleFor(c => c.Request.Channel).Must(ConsentCatalogue.Channels.Contains).WithMessage("Unknown channel.");
        RuleFor(c => c.Request.ProofRef!).MaximumLength(500).When(c => c.Request.ProofRef is not null);
    }
}

internal sealed class SetCommunicationPreferenceValidator : AbstractValidator<SetCommunicationPreference>
{
    public SetCommunicationPreferenceValidator()
    {
        RuleFor(c => c.Request.PartyId).NotEmpty();
        RuleFor(c => c.Request.Purpose).Must(p => p == ConsentCatalogue.Default || ConsentCatalogue.Purposes.Contains(p)).WithMessage("Unknown purpose.");
        RuleFor(c => c.Request.Channel).Must(ConsentCatalogue.Channels.Contains).WithMessage("Unknown channel.");
        RuleFor(c => c.Request.Medium!).Must(ConsentCatalogue.Media.Contains).When(c => c.Request.Medium is not null).WithMessage("Unknown medium.");
    }
}

/// <summary>Shared mapping and state rules of consent and preference.</summary>
internal static class ConsentSupport
{
    public const string Granted = "GRANTED";

    public static ConsentQueryItem ToView(ConsentRow r) => new()
    {
        ConsentId = r.ConsentId, PartyId = r.PartyId, Purpose = r.Purpose, Channel = r.Channel,
        Kind = Enum.Parse<ConsentQueryItem.KindValue>(r.Kind, true), State = Enum.Parse<ConsentQueryItem.StateValue>(r.State, true),
        Source = r.Source, WordingVersion = r.WordingVersion, ProofRef = r.ProofRef, RecordedAt = r.RecordedAt, RecordedBy = r.RecordedBy,
    };

    /// <summary>The newest record of each purpose and channel.</summary>
    public static IReadOnlyList<ConsentRow> Current(IEnumerable<ConsentRow> rows) =>
        [.. rows.GroupBy(r => (r.Purpose, r.Channel)).Select(g => g.OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.ConsentId).First())
            .OrderBy(r => r.Purpose, StringComparer.Ordinal).ThenBy(r => r.Channel, StringComparer.Ordinal)];

    public static void Publish(IEventPublisher events, ConsentRow r) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ConsentChangedV1.Descriptor), "Party", r.PartyId.Value.ToString(),
            new ConsentChangedV1
            {
                PartyId = r.PartyId, Purpose = r.Purpose, Channel = r.Channel, NewState = r.State, ProofRef = r.ProofRef,
                Kind = r.Kind switch
                {
                    "GIVEN" => ConsentChangedV1.KindValue.Given,
                    "WITHDRAWN" => ConsentChangedV1.KindValue.Withdrawn,
                    _ => ConsentChangedV1.KindValue.Objection,
                },
            },
            BusinessKeys.Empty.With("partyId", r.PartyId.Value.ToString())));
}

/// <summary>
/// Records a consent given or an objection. A consent needs its wording version and a proof reference (PTY-ERR-PROOF-REQUIRED); the party must exist,
/// not be merged and not be DSAR-restricted. Rows are never changed; the newest row is the state. Publishes <c>ConsentChanged</c>.
/// </summary>
internal sealed class RecordConsentHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<RecordConsent, ConsentRecordResponse>
{
    public async Task<Result<ConsentRecordResponse>> HandleAsync(RecordConsent command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var given = request.Kind == ConsentRecordRequest.KindValue.Given;
        if (string.IsNullOrWhiteSpace(request.ProofRef) || (given && string.IsNullOrWhiteSpace(request.WordingVersion)))
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "PROOF-REQUIRED"), "A consent needs its wording version and a proof reference.");
        }

        if (await RoleSupport.GuardPartyAsync(db, legalEntity, request.PartyId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var row = new ConsentRow
        {
            ConsentId = Guid.CreateVersion7(), LegalEntityId = legalEntity, PartyId = request.PartyId, Purpose = request.Purpose, Channel = request.Channel,
            Kind = given ? "GIVEN" : "OBJECTION", State = given ? ConsentSupport.Granted : "OBJECTED", Source = request.Source,
            WordingVersion = request.WordingVersion, ProofRef = request.ProofRef, RecordedAt = clock.Now, RecordedBy = context.Actor.ToString(),
        };
        db.Consents.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ConsentSupport.Publish(events, row);
        return new ConsentRecordResponse { Consent = ConsentSupport.ToView(row) };
    }
}

/// <summary>
/// Withdraws a granted consent. Immediate (the next resolve already ignores the consent) and always allowed, also for a DSAR-restricted party.
/// Nothing recorded for the purpose and channel is NOT-FOUND; an already withdrawn or objected consent is returned unchanged.
/// </summary>
internal sealed class WithdrawConsentHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<WithdrawConsent, ConsentWithdrawResponse>
{
    public async Task<Result<ConsentWithdrawResponse>> HandleAsync(WithdrawConsent command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var rows = await db.Consents.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.PartyId == request.PartyId && r.Purpose == request.Purpose && r.Channel == request.Channel)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (ConsentSupport.Current(rows) is not [var current, ..])
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party has no consent for this purpose and channel.");
        }

        if (current.State != ConsentSupport.Granted)
        {
            return new ConsentWithdrawResponse { Consent = ConsentSupport.ToView(current) };
        }

        var row = new ConsentRow
        {
            ConsentId = Guid.CreateVersion7(), LegalEntityId = legalEntity, PartyId = request.PartyId, Purpose = request.Purpose, Channel = request.Channel,
            Kind = "WITHDRAWN", State = "WITHDRAWN", Source = current.Source, WordingVersion = current.WordingVersion,
            ProofRef = string.IsNullOrWhiteSpace(request.ProofRef) ? "WITHDRAWN-BY:" + context.Actor : request.ProofRef, RecordedAt = clock.Now,
            RecordedBy = context.Actor.ToString(),
        };
        db.Consents.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ConsentSupport.Publish(events, row);
        return new ConsentWithdrawResponse { Consent = ConsentSupport.ToView(row) };
    }
}

/// <summary>
/// Sets the preference of a purpose from a date. The superseded rows close in record time; a row that started before the date is re-inserted
/// ending on it, so earlier dates still resolve to the earlier preference. Publishes <c>CommunicationPreferenceChanged</c>.
/// </summary>
internal sealed class SetCommunicationPreferenceHandler(PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<SetCommunicationPreference, CommunicationPreferenceSetResponse>
{
    public async Task<Result<CommunicationPreferenceSetResponse>> HandleAsync(SetCommunicationPreference command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        if (await RoleSupport.GuardPartyAsync(db, legalEntity, request.PartyId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var party = await db.Parties.AsNoTracking().SingleAsync(p => p.PartyId == request.PartyId, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var from = request.ValidFrom ?? RoleSupport.Today(clock);
        var current = await db.CommunicationPreferences
            .Where(r => r.PartyId == request.PartyId && r.Purpose == request.Purpose && r.RecordedTo == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var preferenceId = current.OrderByDescending(r => r.ValidFrom).Select(r => r.PreferenceId).FirstOrDefault();
        if (preferenceId == Guid.Empty)
        {
            preferenceId = Guid.CreateVersion7();
        }

        foreach (var old in current.Where(r => r.ValidTo is null || r.ValidTo.Value > from))
        {
            old.RecordedTo = now;
            if (old.ValidFrom < from)
            {
                db.CommunicationPreferences.Add(new CommunicationPreferenceRow
                {
                    RowId = Guid.CreateVersion7(), PreferenceId = old.PreferenceId, PartyId = old.PartyId, LegalEntityId = old.LegalEntityId, Purpose = old.Purpose,
                    Channel = old.Channel, Language = old.Language, Medium = old.Medium, ValidFrom = old.ValidFrom, ValidTo = from, RecordedFrom = now,
                    CreatedBy = context.Actor.ToString(),
                });
            }
        }

        var row = new CommunicationPreferenceRow
        {
            RowId = Guid.CreateVersion7(), PreferenceId = preferenceId, PartyId = request.PartyId, LegalEntityId = legalEntity, Purpose = request.Purpose,
            Channel = request.Channel, Language = request.Language ?? party.PreferredLanguage, Medium = request.Medium ?? ConsentCatalogue.DefaultMedium(request.Channel),
            ValidFrom = from, ValidTo = null, RecordedFrom = now, CreatedBy = context.Actor.ToString(),
        };
        db.CommunicationPreferences.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(CommunicationPreferenceChangedV1.Descriptor), "Party", row.PartyId.Value.ToString(),
            new CommunicationPreferenceChangedV1
            {
                PartyId = row.PartyId, Purpose = row.Purpose, Channel = row.Channel, Language = row.Language, Medium = row.Medium,
            },
            BusinessKeys.Empty.With("partyId", row.PartyId.Value.ToString())));
        return new CommunicationPreferenceSetResponse
        {
            PreferenceId = preferenceId, Channel = row.Channel, Language = row.Language, Medium = row.Medium, Proof = "PREFERENCE:" + preferenceId.ToString("D"),
        };
    }
}

/// <summary>Audit facts of the consent and preference commands: the object and the party only, never contact values.</summary>
internal sealed class ConsentAuditor :
    ICommandAuditor<RecordConsent, ConsentRecordResponse>,
    ICommandAuditor<WithdrawConsent, ConsentWithdrawResponse>,
    ICommandAuditor<SetCommunicationPreference, CommunicationPreferenceSetResponse>
{
    public CommandAuditFacts Describe(RecordConsent command, Result<ConsentRecordResponse>? result) =>
        Consent(result is { IsSuccess: true } ok ? ok.Value.Consent : null);

    public CommandAuditFacts Describe(WithdrawConsent command, Result<ConsentWithdrawResponse>? result) =>
        Consent(result is { IsSuccess: true } ok ? ok.Value.Consent : null);

    public CommandAuditFacts Describe(SetCommunicationPreference command, Result<CommunicationPreferenceSetResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.PTY, "CommunicationPreference", ok.Value.PreferenceId.ToString("D")),
                BusinessKeys = BusinessKeys.Empty.With("partyId", command.Request.PartyId.Value.ToString()),
            }
            : new CommandAuditFacts();

    private static CommandAuditFacts Consent(ConsentQueryItem? consent) => consent is null
        ? new CommandAuditFacts()
        : new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, "Consent", consent.ConsentId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("partyId", consent.PartyId.Value.ToString()),
        };
}
