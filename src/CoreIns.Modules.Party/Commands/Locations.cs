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
using Microsoft.Extensions.Options;
using ContactPointTypeCode = CoreIns.Modules.Party.Contracts.Api.ContactPointType;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Location.add</c> (BO-PTY-02): one address or contact point for a party.</summary>
internal sealed record AddLocation(LocationAddRequest Request) : ICommand<LocationAddResponse>;

/// <summary><c>pty.Location.update</c>: a new version of the current address or contact point named by <paramref name="Id"/>.</summary>
internal sealed record UpdateLocation(string Id, LocationUpdateRequest Request) : ICommand<LocationUpdateResponse>;

/// <summary><c>pty.Location.remove</c>: ends an address or contact point (history kept).</summary>
internal sealed record RemoveLocation(LocationRemoveRequest Request) : ICommand<LocationRemoveResponse>;

internal sealed class AddLocationValidator : AbstractValidator<AddLocation>
{
    public AddLocationValidator()
    {
        RuleFor(c => c.Request.PartyId.Value).NotEmpty();
        RuleFor(c => c.Request).Must(r => (r.Address is null) != (r.ContactPoint is null)).WithName("address").WithErrorCode("ONE_OF")
            .WithMessage("Give exactly one of address and contactPoint.");
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request.ExpectedRecordVersion is not null);
        When(c => c.Request.Address is not null, () => RuleFor(c => c.Request.Address!).SetValidator(new AddressInputValidator()));
        When(c => c.Request.ContactPoint is not null, () => RuleFor(c => c.Request.ContactPoint!).SetValidator(new ContactPointInputValidator()));
    }
}

internal sealed class UpdateLocationValidator : AbstractValidator<UpdateLocation>
{
    public UpdateLocationValidator()
    {
        RuleFor(c => c.Id).Must(id => Guid.TryParse(id, out _)).WithErrorCode("ID");
        RuleFor(c => c.Request).Must(r => (r.Address is null) != (r.ContactPoint is null)).WithName("address").WithErrorCode("ONE_OF")
            .WithMessage("Give exactly one of address and contactPoint.");
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request.ExpectedRecordVersion is not null);
        When(c => c.Request.Address is not null, () => RuleFor(c => c.Request.Address!).SetValidator(new AddressInputValidator()));
        When(c => c.Request.ContactPoint is not null, () => RuleFor(c => c.Request.ContactPoint!).SetValidator(new ContactPointInputValidator()));
    }
}

internal sealed class RemoveLocationValidator : AbstractValidator<RemoveLocation>
{
    public RemoveLocationValidator()
    {
        RuleFor(c => c.Request.LocationId).NotEmpty();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request.ExpectedRecordVersion is not null);
    }
}

internal sealed class AddressInputValidator : AbstractValidator<AddressInput>
{
    public AddressInputValidator()
    {
        RuleFor(a => a.Country).NotEmpty().Matches("^[A-Z]{2}$").WithErrorCode("COUNTRY");
        RuleFor(a => a.Types).NotEmpty();
        RuleFor(a => a.FreeLines).Must(lines => lines is null || lines.Count <= 3).WithErrorCode("FREE_LINES");
    }
}

internal sealed class ContactPointInputValidator : AbstractValidator<ContactPointInput>
{
    public ContactPointInputValidator() => RuleFor(c => c.Value).NotEmpty().MaximumLength(254);
}

/// <summary>
/// The write side of addresses and contact points (BO-PTY-02, REQ-PTY-012, -074, -081..085). Both tables are bitemporal with one
/// row per version (the primary key is the version's id): a change closes the current row in record time (<c>recordedTo</c>), keeps a
/// valid-time copy of the old values up to the new start, and inserts the new row. Per type exactly one current location is primary: the first
/// one becomes primary and a new primary demotes the previous one (itself a new version, so the history shows when). Ending a primary promotes the
/// oldest remaining one. Every change bumps the party's <c>recordVersion</c> (optimistic concurrency, PTY-ERR-STALE) and publishes
/// <c>PartyUpdated</c>. Contact values are stored as for create: normalised, not encrypted (the P2 columns of the party are identifiers and the
/// birth date); events and audit carry no value.
/// </summary>
internal sealed class LocationWriter(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, AddressBuilder addressBuilder,
    IOptions<PartyOptions> options)
{
    private const string UserEntry = "USER_ENTRY";

    public async Task<Result<LocationAddResponse>> AddAsync(LocationAddRequest request, CancellationToken cancellationToken)
    {
        if ((request.Address is null) == (request.ContactPoint is null))
        {
            return Invalid("address", "ONE_OF", "Give exactly one of address and contactPoint.");
        }

        var legalEntity = protection.Current(context);
        var started = await BeginAsync(legalEntity, request.PartyId, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return started.Error;
        }

        var (party, now, today) = started.Value;
        var from = request.ValidFrom ?? today;
        string group;
        PartyAddressView? addressView = null;
        ContactPointView? contactView = null;
        Guid id;
        if (request.Address is { } input)
        {
            var row = await addressBuilder.BuildAsync(input, party.PartyId, legalEntity, from, now, context.Actor.ToString(), cancellationToken).ConfigureAwait(false);
            if (row.IsFailure)
            {
                return row.Error;
            }

            var added = row.Value;
            var others = await CurrentAddressesAsync(party.PartyId, from, cancellationToken).ConfigureAwait(false);
            ApplyAddressPrimary(added, input.Primary, others, now);
            db.Addresses.Add(added);
            (group, id, addressView) = ("ADDRESS", added.AddressId, ToView(added));
        }
        else
        {
            var contact = request.ContactPoint!;
            var value = NormaliseContact(contact);
            if (value is null)
            {
                return Invalid("contactPoint.value", ContactCode(contact.Type), "The contact point is not valid.");
            }

            var type = Codes.Of(contact.Type);
            var others = await CurrentContactsAsync(party.PartyId, from, cancellationToken).ConfigureAwait(false);
            if (others.Any(o => o.Type == type && o.Value == value))
            {
                return Invalid("contactPoint.value", "DUPLICATE", "The party already has this contact point.");
            }

            var added = new PartyContactPointRow
            {
                ContactPointId = Guid.CreateVersion7(), PartyId = party.PartyId, LegalEntityId = legalEntity, Type = type, Value = value,
                Purpose = Codes.Of(contact.Purpose ?? ContactPointPurpose.Personal), VerificationStatus = "UNVERIFIED", Source = UserEntry,
                ValidFrom = from, RecordedFrom = now, CreatedBy = context.Actor.ToString(),
            };
            ApplyContactPrimary(added, contact.Primary, others, now);
            db.ContactPoints.Add(added);
            (group, id, contactView) = ("CONTACT_POINT", added.ContactPointId, ToView(added));
        }

        if (await SaveAsync(party, now, group, from, today, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return stale;
        }

        return new LocationAddResponse
        {
            LocationId = id, Kind = addressView is not null ? LocationKind.Address : LocationKind.ContactPoint, Address = addressView, ContactPoint = contactView,
            RecordVersion = party.RecordVersion,
        };
    }

    public async Task<Result<LocationUpdateResponse>> UpdateAsync(string idText, LocationUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idText, out var id))
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The location does not exist.");
        }

        if ((request.Address is null) == (request.ContactPoint is null))
        {
            return Invalid("address", "ONE_OF", "Give exactly one of address and contactPoint.");
        }

        var legalEntity = protection.Current(context);
        var address = await db.Addresses.SingleOrDefaultAsync(a => a.AddressId == id && a.LegalEntityId == legalEntity && a.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        var contactRow = address is null
            ? await db.ContactPoints.SingleOrDefaultAsync(c => c.ContactPointId == id && c.LegalEntityId == legalEntity && c.RecordedTo == null, cancellationToken).ConfigureAwait(false)
            : null;
        var partyId = address?.PartyId ?? contactRow?.PartyId;
        if (partyId is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The location does not exist or is no longer the current version; reload the party.");
        }

        if ((address is not null) != (request.Address is not null))
        {
            return Invalid(address is not null ? "address" : "contactPoint", "KIND", "The data does not match the location kind.");
        }

        var started = await BeginAsync(legalEntity, partyId.Value, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return started.Error;
        }

        var (party, now, today) = started.Value;
        var from = request.ValidFrom ?? today;
        var actor = context.Actor.ToString();
        if (address is not null)
        {
            if (CheckPeriod(address.ValidFrom, address.ValidTo, from, today) is { } badPeriod)
            {
                return badPeriod;
            }

            var built = await addressBuilder.BuildAsync(request.Address!, party.PartyId, legalEntity, from, now, actor, cancellationToken).ConfigureAwait(false);
            if (built.IsFailure)
            {
                return built.Error;
            }

            var updated = built.Value;
            updated.ValidTo = address.ValidTo;
            address.RecordedTo = now;
            if (address.ValidFrom < from)
            {
                var history = address.Copy();
                (history.AddressId, history.ValidTo, history.RecordedFrom, history.RecordedTo, history.CreatedBy) = (Guid.CreateVersion7(), from, now, null, actor);
                db.Addresses.Add(history);
            }

            var others = (await CurrentAddressesAsync(party.PartyId, from, cancellationToken).ConfigureAwait(false)).Where(o => o.AddressId != address.AddressId).ToList();
            ApplyAddressPrimary(updated, request.Address!.Primary ?? address.IsPrimary, others, now);
            db.Addresses.Add(updated);
            if (await SaveAsync(party, now, "ADDRESS", from, today, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            return new LocationUpdateResponse { LocationId = updated.AddressId, Kind = LocationKind.Address, Address = ToView(updated), RecordVersion = party.RecordVersion };
        }

        var old = contactRow!;
        var contact = request.ContactPoint!;
        if (CheckPeriod(old.ValidFrom, old.ValidTo, from, today) is { } problem)
        {
            return problem;
        }

        var value = NormaliseContact(contact);
        if (value is null)
        {
            return Invalid("contactPoint.value", ContactCode(contact.Type), "The contact point is not valid.");
        }

        var type = Codes.Of(contact.Type);
        var currentOthers = (await CurrentContactsAsync(party.PartyId, from, cancellationToken).ConfigureAwait(false)).Where(o => o.ContactPointId != old.ContactPointId).ToList();
        if (currentOthers.Any(o => o.Type == type && o.Value == value))
        {
            return Invalid("contactPoint.value", "DUPLICATE", "The party already has this contact point.");
        }

        var unchanged = old.Type == type && old.Value == value;
        old.RecordedTo = now;
        if (old.ValidFrom < from)
        {
            var history = old.Copy();
            (history.ContactPointId, history.ValidTo, history.RecordedFrom, history.RecordedTo, history.CreatedBy) = (Guid.CreateVersion7(), from, now, null, actor);
            db.ContactPoints.Add(history);
        }

        var next = new PartyContactPointRow
        {
            ContactPointId = Guid.CreateVersion7(), PartyId = party.PartyId, LegalEntityId = legalEntity, Type = type, Value = value,
            Purpose = Codes.Of(contact.Purpose ?? Codes.Parse<ContactPointPurpose>(old.Purpose)),
            VerificationStatus = unchanged ? old.VerificationStatus : "UNVERIFIED", Source = unchanged ? old.Source : UserEntry,
            ValidFrom = from, ValidTo = old.ValidTo, RecordedFrom = now, CreatedBy = actor,
        };
        ApplyContactPrimary(next, contact.Primary ?? old.IsPrimary, currentOthers, now);
        db.ContactPoints.Add(next);
        if (await SaveAsync(party, now, "CONTACT_POINT", from, today, cancellationToken).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        return new LocationUpdateResponse { LocationId = next.ContactPointId, Kind = LocationKind.ContactPoint, ContactPoint = ToView(next), RecordVersion = party.RecordVersion };
    }

    public async Task<Result<LocationRemoveResponse>> RemoveAsync(LocationRemoveRequest request, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var id = request.LocationId;
        var address = await db.Addresses.SingleOrDefaultAsync(a => a.AddressId == id && a.LegalEntityId == legalEntity && a.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        var contact = address is null
            ? await db.ContactPoints.SingleOrDefaultAsync(c => c.ContactPointId == id && c.LegalEntityId == legalEntity && c.RecordedTo == null, cancellationToken).ConfigureAwait(false)
            : null;
        var partyId = address?.PartyId ?? contact?.PartyId;
        if (partyId is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The location does not exist or is no longer the current version; reload the party.");
        }

        var started = await BeginAsync(legalEntity, partyId.Value, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return started.Error;
        }

        var (party, now, today) = started.Value;
        var to = request.ValidTo ?? today;
        var actor = context.Actor.ToString();
        var validFrom = address?.ValidFrom ?? contact!.ValidFrom;
        var validTo = address is not null ? address.ValidTo : contact!.ValidTo;
        // A location that has not started yet (starts today or later) and is ended on or before its start never took effect: it is voided
        // (its record is closed, no ended copy), so the history still shows it was entered.
        var voided = to <= validFrom && validFrom >= today;
        if ((to <= validFrom && !voided) || (validTo is { } existing && to >= existing))
        {
            return Invalid("validTo", "PERIOD", "The location must end after it starts and before it already ends.");
        }

        string group;
        if (address is not null)
        {
            address.RecordedTo = now;
            if (!voided)
            {
                var ended = address.Copy();
                (ended.AddressId, ended.ValidTo, ended.RecordedFrom, ended.RecordedTo, ended.CreatedBy) = (Guid.CreateVersion7(), to, now, null, actor);
                db.Addresses.Add(ended);
            }

            if (address.IsPrimary && (to <= today || voided))
            {
                // TODO(hardening): a multi-type address promotes at most one successor, picked among those sharing a type, not one per type.
                var others = (await CurrentAddressesAsync(party.PartyId, today, cancellationToken).ConfigureAwait(false)).Where(o => o.AddressId != address.AddressId).ToList();
                var sharing = others.Where(o => o.Types.Intersect(address.Types).Any()).ToList();
                if (!sharing.Any(o => o.IsPrimary) && sharing.OrderBy(o => o.ValidFrom).ThenBy(o => o.RecordedFrom).FirstOrDefault() is { } next)
                {
                    Replace(next, p => p.IsPrimary = true, now);
                }
            }

            group = "ADDRESS";
        }
        else
        {
            contact!.RecordedTo = now;
            if (!voided)
            {
                var ended = contact.Copy();
                (ended.ContactPointId, ended.ValidTo, ended.RecordedFrom, ended.RecordedTo, ended.CreatedBy) = (Guid.CreateVersion7(), to, now, null, actor);
                db.ContactPoints.Add(ended);
            }

            if (contact.IsPrimary && (to <= today || voided))
            {
                var others = (await CurrentContactsAsync(party.PartyId, today, cancellationToken).ConfigureAwait(false)).Where(o => o.ContactPointId != contact.ContactPointId).ToList();
                var sharing = others.Where(o => o.Type == contact.Type).ToList();
                if (!sharing.Any(o => o.IsPrimary) && sharing.OrderBy(o => o.ValidFrom).ThenBy(o => o.RecordedFrom).FirstOrDefault() is { } next)
                {
                    Replace(next, p => p.IsPrimary = true, now);
                }
            }

            group = "CONTACT_POINT";
        }

        if (await SaveAsync(party, now, group, to, today, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return stale;
        }

        return new LocationRemoveResponse { LocationId = id, Kind = address is not null ? LocationKind.Address : LocationKind.ContactPoint, ValidTo = to, RecordVersion = party.RecordVersion };
    }

    // ---- shared steps ----

    private sealed record Begun(PartyRow Party, Instant Now, BusinessDate Today)
    {
        public void Deconstruct(out PartyRow party, out Instant now, out BusinessDate today) => (party, now, today) = (Party, Now, Today);
    }

    private async Task<Result<Begun>> BeginAsync(LegalEntityId legalEntity, PartyId partyId, int? expectedVersion, CancellationToken cancellationToken)
    {
        if (await RoleSupport.GuardPartyAsync(db, legalEntity, partyId, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var party = await db.Parties.SingleAsync(p => p.LegalEntityId == legalEntity && p.PartyId == partyId, cancellationToken).ConfigureAwait(false);
        if (expectedVersion is { } expected && party.RecordVersion != expected)
        {
            return Stale(party.RecordVersion);
        }

        return new Begun(party, clock.Now, RoleSupport.Today(clock));
    }

    /// <summary>Bumps the party version, saves, and publishes <c>PartyUpdated</c>; a lost version race becomes PTY-ERR-STALE.</summary>
    private async Task<DomainError?> SaveAsync(PartyRow party, Instant now, string group, BusinessDate effectiveFrom, BusinessDate today, CancellationToken cancellationToken)
    {
        party.RecordVersion++;
        party.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(party.RecordVersion);
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PartyUpdatedV1.Descriptor),
            "Party",
            party.PartyId.Value.ToString(),
            new PartyUpdatedV1 { PartyId = party.PartyId, PartyNumber = party.PartyNumber, ChangedAttributeGroups = [group], Backdated = effectiveFrom < today, EffectiveFrom = effectiveFrom },
            BusinessKeys.Empty.With("partyId", party.PartyId.Value.ToString())));
        return null;
    }

    private Task<List<PartyAddressRow>> CurrentAddressesAsync(PartyId partyId, BusinessDate at, CancellationToken cancellationToken) =>
        db.Addresses.Where(a => a.PartyId == partyId && a.RecordedTo == null && (a.ValidTo == null || a.ValidTo > at)).ToListAsync(cancellationToken);

    private Task<List<PartyContactPointRow>> CurrentContactsAsync(PartyId partyId, BusinessDate at, CancellationToken cancellationToken) =>
        db.ContactPoints.Where(c => c.PartyId == partyId && c.RecordedTo == null && (c.ValidTo == null || c.ValidTo > at)).ToListAsync(cancellationToken);

    /// <summary>The new row is primary when asked or when no current location shares one of its types; being primary demotes the others' flag.</summary>
    private void ApplyAddressPrimary(PartyAddressRow row, bool? wanted, IReadOnlyList<PartyAddressRow> others, Instant now)
    {
        var sharing = others.Where(o => o.Types.Intersect(row.Types).Any()).ToList();
        row.IsPrimary = wanted == true || !sharing.Any(o => o.IsPrimary);
        if (row.IsPrimary)
        {
            foreach (var other in sharing.Where(o => o.IsPrimary))
            {
                Replace(other, p => p.IsPrimary = false, now);
            }
        }
    }

    private void ApplyContactPrimary(PartyContactPointRow row, bool? wanted, IReadOnlyList<PartyContactPointRow> others, Instant now)
    {
        var sharing = others.Where(o => o.Type == row.Type).ToList();
        row.IsPrimary = wanted == true || !sharing.Any(o => o.IsPrimary);
        if (row.IsPrimary)
        {
            foreach (var other in sharing.Where(o => o.IsPrimary))
            {
                Replace(other, p => p.IsPrimary = false, now);
            }
        }
    }

    /// <summary>A new record version of an existing row with one change; the old version is closed at <paramref name="now"/> (append-only).</summary>
    private void Replace(PartyAddressRow old, Action<PartyAddressRow> change, Instant now)
    {
        old.RecordedTo = now;
        var copy = old.Copy();
        (copy.AddressId, copy.RecordedFrom, copy.RecordedTo, copy.CreatedBy) = (Guid.CreateVersion7(), now, null, context.Actor.ToString());
        change(copy);
        db.Addresses.Add(copy);
    }

    private void Replace(PartyContactPointRow old, Action<PartyContactPointRow> change, Instant now)
    {
        old.RecordedTo = now;
        var copy = old.Copy();
        (copy.ContactPointId, copy.RecordedFrom, copy.RecordedTo, copy.CreatedBy) = (Guid.CreateVersion7(), now, null, context.Actor.ToString());
        change(copy);
        db.ContactPoints.Add(copy);
    }

    private static DomainError? CheckPeriod(BusinessDate currentFrom, BusinessDate? currentTo, BusinessDate from, BusinessDate today)
    {
        if (currentTo is { } to && to <= today)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The location has already ended.");
        }

        if (from < currentFrom || (currentTo is { } end && from >= end))
        {
            return Invalid("validFrom", "PERIOD", "The new values must start within the period of the current version.");
        }

        return null;
    }

    private string? NormaliseContact(ContactPointInput input) =>
        input.Type == ContactPointTypeCode.Email
            ? ContactPoints.NormaliseEmail(input.Value)
            : input.Type == ContactPointTypeCode.SecureInbox ? NullIfEmpty(input.Value.Trim()) : ContactPoints.NormalisePhone(input.Value, options.Value.DefaultCallingCode);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string ContactCode(ContactPointTypeCode type) => type == ContactPointTypeCode.Email ? "EMAIL_SYNTAX" : "PHONE_E164";

    private static DomainError Stale(int currentVersion) =>
        new(ErrorCode.For(ModuleCode.PTY, "STALE"), "The party changed meanwhile; reload it.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["currentRecordVersion"] = currentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };

    private static DomainError Invalid(string field, string code, string message) => AddressBuilder.Invalid(field, code, message);

    internal static PartyAddressView ToView(PartyAddressRow a) => new()
    {
        AddressId = a.AddressId,
        Types = [.. a.Types.Select(Codes.Parse<AddressType>)],
        Primary = a.IsPrimary,
        Country = a.Country,
        Native = new AddressFields
        {
            Street = a.Street, Number = a.Number, Building = a.Building, Floor = a.Floor, Unit = a.Unit, Postcode = a.Postcode,
            Locality = a.Locality, Municipality = a.Municipality, RegionalUnit = a.RegionalUnit, Region = a.Region, FreeLines = a.FreeLines,
        },
        Latin = new AddressFields
        {
            Street = a.LatinStreet, Number = a.Number, Building = a.LatinBuilding, Floor = a.Floor, Unit = a.Unit, Postcode = a.Postcode,
            Locality = a.LatinLocality, Municipality = a.LatinMunicipality, RegionalUnit = a.LatinRegionalUnit, Region = a.LatinRegion, FreeLines = a.LatinFreeLines,
        },
        FormattedLines = a.FormattedLines,
        FormattedLinesLatin = a.FormattedLinesLatin,
        ValidationState = a.ValidationState switch
        {
            "VALIDATED" => PartyAddressView.ValidationStateValue.Validated,
            "INVALID" => PartyAddressView.ValidationStateValue.Invalid,
            _ => PartyAddressView.ValidationStateValue.Unvalidated,
        },
        Description = a.Description,
        ValidFrom = a.ValidFrom,
        ValidTo = a.ValidTo,
    };

    internal static ContactPointView ToView(PartyContactPointRow c) => new()
    {
        ContactPointId = c.ContactPointId,
        Type = Codes.Parse<ContactPointTypeCode>(c.Type),
        Value = c.Value,
        Purpose = Codes.Parse<ContactPointPurpose>(c.Purpose),
        Primary = c.IsPrimary,
        VerificationStatus = c.VerificationStatus switch
        {
            "VERIFIED" => ContactPointView.VerificationStatusValue.Verified,
            "BOUNCING" => ContactPointView.VerificationStatusValue.Bouncing,
            _ => ContactPointView.VerificationStatusValue.Unverified,
        },
    };
}

internal sealed class AddLocationHandler(LocationWriter writer) : ICommandHandler<AddLocation, LocationAddResponse>
{
    public Task<Result<LocationAddResponse>> HandleAsync(AddLocation command, CancellationToken cancellationToken) => writer.AddAsync(command.Request, cancellationToken);
}

internal sealed class UpdateLocationHandler(LocationWriter writer) : ICommandHandler<UpdateLocation, LocationUpdateResponse>
{
    public Task<Result<LocationUpdateResponse>> HandleAsync(UpdateLocation command, CancellationToken cancellationToken) =>
        writer.UpdateAsync(command.Id, command.Request, cancellationToken);
}

internal sealed class RemoveLocationHandler(LocationWriter writer) : ICommandHandler<RemoveLocation, LocationRemoveResponse>
{
    public Task<Result<LocationRemoveResponse>> HandleAsync(RemoveLocation command, CancellationToken cancellationToken) => writer.RemoveAsync(command.Request, cancellationToken);
}

/// <summary>Audit facts of <c>pty.Location.*</c>: the location id and kind; never an address or contact value (NFR-PTY-011).</summary>
internal sealed class LocationAuditor :
    ICommandAuditor<AddLocation, LocationAddResponse>,
    ICommandAuditor<UpdateLocation, LocationUpdateResponse>,
    ICommandAuditor<RemoveLocation, LocationRemoveResponse>
{
    public CommandAuditFacts Describe(AddLocation command, Result<LocationAddResponse>? result) =>
        Facts(command.Request.PartyId.Value.ToString(), result is { IsSuccess: true } ok ? (ok.Value.Kind, ok.Value.LocationId, ok.Value.RecordVersion) : null, "add");

    public CommandAuditFacts Describe(UpdateLocation command, Result<LocationUpdateResponse>? result) =>
        Facts(null, result is { IsSuccess: true } ok ? (ok.Value.Kind, ok.Value.LocationId, ok.Value.RecordVersion) : null, "update");

    public CommandAuditFacts Describe(RemoveLocation command, Result<LocationRemoveResponse>? result) =>
        Facts(null, result is { IsSuccess: true } ok ? (ok.Value.Kind, ok.Value.LocationId, ok.Value.RecordVersion) : null, "remove");

    private static CommandAuditFacts Facts(string? partyId, (LocationKind Kind, Guid Id, int Version)? done, string action)
    {
        if (done is not { } d)
        {
            return new CommandAuditFacts();
        }

        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, d.Kind == LocationKind.Address ? "PartyAddress" : "PartyContactPoint", d.Id.ToString("D")),
            BusinessKeys = partyId is null ? BusinessKeys.Empty : BusinessKeys.Empty.With("partyId", partyId),
            Changes = AuditDiff.Compute(null, new { action, kind = d.Kind.ToString(), recordVersion = d.Version }),
        };
    }
}
