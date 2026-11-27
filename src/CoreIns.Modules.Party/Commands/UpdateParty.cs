using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Modules.Party.Services.Dsar;
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

/// <summary><c>pty.Party.update</c> (BO-PTY-01): name, birth date and language of a party.</summary>
/// <param name="Id">Party id (GUID) or party number from the route.</param>
/// <param name="Request">The contract request.</param>
internal sealed record UpdateParty(string Id, PartyUpdateRequest Request) : ICommand<PartyUpdateResponse>;

internal sealed class UpdatePartyValidator : AbstractValidator<UpdateParty>
{
    public UpdatePartyValidator()
    {
        RuleFor(c => c.Request.Person).Null().When(c => c.Request.Organisation is not null).WithErrorCode("PERSON_OR_ORGANISATION");
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.PreferredLanguage).Must(l => l is null or "el" or "en").WithErrorCode("LANGUAGE");
        RuleFor(c => c.Request.Reason).MaximumLength(64);
        When(c => c.Request.Person is not null, () =>
        {
            RuleFor(c => c.Request.Person!).Must(p => string.IsNullOrWhiteSpace(p.GivenNames) == string.IsNullOrWhiteSpace(p.FamilyName))
                .WithErrorCode("NAME_PARTS").WithMessage("Give the given names and the family name together.");
            RuleFor(c => c.Request.Person!.GivenNames).MaximumLength(200);
            RuleFor(c => c.Request.Person!.FamilyName).MaximumLength(200);
            RuleFor(c => c.Request.Person!.FatherName).MaximumLength(200);
            RuleFor(c => c.Request.Person!.MotherName).MaximumLength(200);
        });
        When(c => c.Request.Organisation is not null, () => RuleFor(c => c.Request.Organisation!.LegalName).NotEmpty().MaximumLength(300));
    }
}

/// <summary>
/// Changes the name (bitemporal: the old rows are closed in record time and a valid-time copy ends today; new rows start today;
/// search keys are rebuilt through <see cref="NameForms"/>), the birth date (P2, re-encrypted) and the language of a party, with
/// optimistic concurrency on <c>recordVersion</c> (REQ-PTY-043). A DSAR-restricted or merged party is refused.
/// Publishes <c>PartyUpdated</c> through the outbox.
/// </summary>
internal sealed class UpdatePartyHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, NameForms nameForms,
    PartyReader reader) : ICommandHandler<UpdateParty, PartyUpdateResponse>
{
    public async Task<Result<PartyUpdateResponse>> HandleAsync(UpdateParty command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var party = await FindAsync(legalEntity, command.Id, cancellationToken).ConfigureAwait(false);
        if (party is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist.");
        }

        if (party.Status == Codes.Of(PartyStatus.Merged))
        {
            return DomainError.Of(ModuleCode.PTY, "MERGED", "The party was merged; update the surviving party.");
        }

        if (await db.DsarRestrictions.AnyAsync(r => r.PartyId == party.PartyId && r.RecordClass == PartyDsarWork.PartyRecord, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PTY, "RESTRICTED", "The party record is restricted by a data-subject request and cannot be changed.");
        }

        if (party.RecordVersion != request.ExpectedRecordVersion)
        {
            return Stale(party.RecordVersion);
        }

        var isPerson = party.PartyType == "PERSON";
        if ((request.Person is not null && !isPerson) || (request.Organisation is not null && isPerson))
        {
            return Invalid(isPerson ? "organisation" : "person", "PARTY_TYPE", "The data does not match the party type.");
        }

        var now = clock.Now;
        var today = new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime()));
        var actor = context.Actor.ToString();
        var groups = new List<string>();

        var person = request.Person;
        if (person is { GivenNames.Length: > 0 } || request.Organisation is not null)
        {
            await ChangeNameAsync(party, legalEntity, request, today, now, actor, cancellationToken).ConfigureAwait(false);
            groups.Add("NAME");
        }

        if (person?.BirthDate is { } birthDate)
        {
            // TODO(hardening): the birth date is a single encrypted column; its history lives only in the audit trail, not bitemporally.
            party.BirthDateEncrypted = await protection.EncryptBirthDateAsync(legalEntity, party.PartyId.Value, birthDate, cancellationToken).ConfigureAwait(false);
            groups.Add("BIRTH_DATE");
        }

        if (request.PreferredLanguage is { } language && language != party.PreferredLanguage)
        {
            party.PreferredLanguage = language;
            groups.Add("LANGUAGE");
        }

        if (groups.Count == 0)
        {
            return Invalid("person", "NO_CHANGE", "The request changes nothing.");
        }

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
            new PartyUpdatedV1 { PartyId = party.PartyId, PartyNumber = party.PartyNumber, ChangedAttributeGroups = groups, Backdated = false, EffectiveFrom = today },
            BusinessKeys.Empty.With("partyId", party.PartyId.Value.ToString())));

        var view = await reader.GetAsync(legalEntity, context.LegalEntity!.Value, party.PartyId.Value, null, new TimePoint(today, now), revealP2: false, cancellationToken)
            .ConfigureAwait(false);
        var duplicates = groups.Contains("NAME")
            ? await DuplicatesAsync(legalEntity, party.PartyId, cancellationToken).ConfigureAwait(false)
            : [];
        return new PartyUpdateResponse { Party = view!, DuplicateSuggestions = duplicates, MissingData = [] };
    }

    private Task<PartyRow?> FindAsync(LegalEntityId legalEntity, string id, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(id, out var guid))
        {
            var partyId = new PartyId(guid);
            return db.Parties.SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.PartyId == partyId, cancellationToken);
        }

        if (!PartyNumber.TryParse(id, out var number))
        {
            return Task.FromResult<PartyRow?>(null);
        }

        return db.Parties.SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.PartyNumber == number, cancellationToken);
    }

    private async Task ChangeNameAsync(
        PartyRow party, LegalEntityId legalEntity, PartyUpdateRequest request, BusinessDate today, Instant now, string actor, CancellationToken cancellationToken)
    {
        var current = await db.Names.Where(n => n.PartyId == party.PartyId && n.RecordedTo == null && n.ValidTo == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        var native = current.FirstOrDefault(n => n.Form == "NATIVE");
        var rows = await NewNamesAsync(party.PartyId, legalEntity, request, native, today, now, actor, cancellationToken).ConfigureAwait(false);

        var oldKeys = await db.SearchKeys.Where(k => k.PartyId == party.PartyId && k.RecordedTo == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in oldKeys)
        {
            key.RecordedTo = now;
        }

        foreach (var old in current)
        {
            old.RecordedTo = now;
            if (old.ValidFrom < today && old.Form != "LATIN_AS_ON_DOCUMENT")
            {
                // Valid-time history: the old name stays the truth for the period before today (record time starts now).
                db.Names.Add(new PartyNameRow
                {
                    NameId = Guid.CreateVersion7(), PartyId = old.PartyId, LegalEntityId = old.LegalEntityId, Form = old.Form, Script = old.Script,
                    GivenNames = old.GivenNames, FamilyName = old.FamilyName, FatherName = old.FatherName, MotherName = old.MotherName,
                    OrganisationName = old.OrganisationName, TradeName = old.TradeName, TransliteratorVersion = old.TransliteratorVersion,
                    SourceDocumentRef = old.SourceDocumentRef, ValidFrom = old.ValidFrom, ValidTo = today, RecordedFrom = now, CreatedBy = actor,
                });
            }
        }

        db.Names.AddRange(rows);
        foreach (var name in rows)
        {
            var full = name.OrganisationName ?? NameForms.PersonDisplay(name.GivenNames, name.FamilyName);
            foreach (var (kind, key) in await nameForms.StoredKeysAsync(full, cancellationToken).ConfigureAwait(false))
            {
                db.SearchKeys.Add(new PartySearchKeyRow
                {
                    PartyId = party.PartyId, LegalEntityId = legalEntity, NameId = name.NameId, KeyKind = kind, SearchKey = key,
                    RuleVersion = nameForms.KeyRuleVersion, RecordedFrom = now,
                });
            }
        }
    }

    private async Task<List<PartyNameRow>> NewNamesAsync(
        PartyId partyId, LegalEntityId legalEntity, PartyUpdateRequest request, PartyNameRow? native, BusinessDate today, Instant now, string actor, CancellationToken cancellationToken)
    {
        PartyNameRow Row(string form, string script) => new()
        {
            NameId = Guid.CreateVersion7(), PartyId = partyId, LegalEntityId = legalEntity, Form = form, Script = script,
            ValidFrom = today, RecordedFrom = now, CreatedBy = actor,
        };

        var rows = new List<PartyNameRow>();
        if (request.Person is { } person)
        {
            var script = NameForms.ScriptOf(person.FamilyName + person.GivenNames);
            var row = Row("NATIVE", script);
            row.GivenNames = person.GivenNames!.Trim();
            row.FamilyName = person.FamilyName!.Trim();
            row.FatherName = person.FatherName?.Trim() ?? native?.FatherName;
            row.MotherName = person.MotherName?.Trim() ?? native?.MotherName;
            rows.Add(row);
            if (script != ScriptCodes.Latin)
            {
                var latin = Row("LATIN_GENERATED", ScriptCodes.Latin);
                latin.GivenNames = await nameForms.LatinAsync(row.GivenNames, cancellationToken).ConfigureAwait(false);
                latin.FamilyName = await nameForms.LatinAsync(row.FamilyName, cancellationToken).ConfigureAwait(false);
                latin.FatherName = await nameForms.LatinAsync(row.FatherName, cancellationToken).ConfigureAwait(false);
                latin.MotherName = await nameForms.LatinAsync(row.MotherName, cancellationToken).ConfigureAwait(false);
                latin.TransliteratorVersion = nameForms.TransliteratorVersion;
                rows.Add(latin);
            }
        }
        else if (request.Organisation is { } organisation)
        {
            var script = NameForms.ScriptOf(organisation.LegalName);
            var row = Row("NATIVE", script);
            row.OrganisationName = organisation.LegalName.Trim();
            row.TradeName = organisation.TradeName?.Trim() ?? native?.TradeName;
            rows.Add(row);
            if (script != ScriptCodes.Latin)
            {
                var latin = Row("LATIN_GENERATED", ScriptCodes.Latin);
                latin.OrganisationName = await nameForms.LatinAsync(row.OrganisationName, cancellationToken).ConfigureAwait(false);
                latin.TradeName = await nameForms.LatinAsync(row.TradeName, cancellationToken).ConfigureAwait(false);
                latin.TransliteratorVersion = nameForms.TransliteratorVersion;
                rows.Add(latin);
            }
        }

        return rows;
    }

    /// <summary>Other parties sharing a whole-name key of the changed party (warning, not a refusal). The new keys are read from the tracker.</summary>
    private async Task<IReadOnlyList<PartySearchItem>> DuplicatesAsync(LegalEntityId legalEntity, PartyId partyId, CancellationToken cancellationToken)
    {
        var fullKeys = db.SearchKeys.Local.Where(k => k.PartyId == partyId && k.KeyKind == "FULL" && k.RecordedTo == null).Select(k => k.SearchKey).Distinct().ToList();
        var others = await db.SearchKeys
            .Where(k => k.LegalEntityId == legalEntity && k.KeyKind == "FULL" && k.RecordedTo == null && fullKeys.Contains(k.SearchKey) && k.PartyId != partyId)
            .Select(k => k.PartyId).Distinct().Take(5).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await reader.HydrateAsync(legalEntity, [.. others.Select(id => new SearchHit(id.Value, 3, 1m))], cancellationToken).ConfigureAwait(false);
    }

    private static DomainError Stale(int currentVersion) =>
        new(ErrorCode.For(ModuleCode.PTY, "STALE"), "The party changed meanwhile; reload it.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["currentRecordVersion"] = currentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };

    private static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), message) { FieldErrors = [new FieldError(field, code, "pty." + code.ToLowerInvariant(), message)] };
}

/// <summary>Audit facts of <c>pty.Party.update</c>: object and number; no P2 value.</summary>
internal sealed class UpdatePartyAuditor : ICommandAuditor<UpdateParty, PartyUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateParty command, Result<PartyUpdateResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts();
        }

        var party = success.Value.Party;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.PTY, "Party", party.PartyId),
            ObjectNumber = party.PartyNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("partyId", party.PartyId.Value.ToString()),
            Changes = AuditDiff.Compute(new { recordVersion = command.Request.ExpectedRecordVersion }, new { recordVersion = party.RecordVersion, language = party.PreferredLanguage }),
        };
    }
}
