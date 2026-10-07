using CoreIns.Modules.Market.Contracts.Localisation;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ContactPointTypeCode = CoreIns.Modules.Party.Contracts.Api.ContactPointType;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Party.create</c> as a command of the platform pipeline (validation → transaction → idempotency → audit → authority → handler).</summary>
/// <param name="Request">The contract request (generated from contracts/openapi/pty.yaml).</param>
internal sealed record CreateParty(PartyCreateRequest Request) : ICommand<PartyCreateResponse>;

/// <summary>Shape rules of the request (the pipeline turns failures into PTY-ERR-VALIDATION with field errors).</summary>
internal sealed class CreatePartyValidator : AbstractValidator<CreateParty>
{
    public CreatePartyValidator()
    {
        RuleFor(c => c.Request.Person).NotNull().When(c => c.Request.PartyType == PartyType.Person).WithErrorCode("PERSON_REQUIRED");
        RuleFor(c => c.Request.Organisation).Null().When(c => c.Request.PartyType == PartyType.Person).WithErrorCode("ORGANISATION_NOT_ALLOWED");
        RuleFor(c => c.Request.Organisation).NotNull().When(c => c.Request.PartyType == PartyType.Organisation).WithErrorCode("ORGANISATION_REQUIRED");
        RuleFor(c => c.Request.Person).Null().When(c => c.Request.PartyType == PartyType.Organisation).WithErrorCode("PERSON_NOT_ALLOWED");
        When(c => c.Request.Person is not null, () =>
        {
            RuleFor(c => c.Request.Person!.GivenNames).NotEmpty().MaximumLength(200);
            RuleFor(c => c.Request.Person!.FamilyName).NotEmpty().MaximumLength(200);
            RuleFor(c => c.Request.Person!.FatherName).MaximumLength(200);
            RuleFor(c => c.Request.Person!.MotherName).MaximumLength(200);
        });
        When(c => c.Request.Organisation is not null, () => RuleFor(c => c.Request.Organisation!.LegalName).NotEmpty().MaximumLength(300));
        RuleFor(c => c.Request.PreferredLanguage).Must(l => l is null or "el" or "en").WithErrorCode("LANGUAGE");
        RuleForEach(c => c.Request.Identifiers).ChildRules(identifier =>
        {
            identifier.RuleFor(i => i.Scheme).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("SCHEME");
            identifier.RuleFor(i => i.Value).NotEmpty().MaximumLength(100);
            identifier.RuleFor(i => i.IssuingCountry).Matches("^[A-Z]{2}$").When(i => i.IssuingCountry is not null).WithErrorCode("COUNTRY");
        });
        RuleFor(c => c.Request.Identifiers).Must(ids => ids is null || ids.Select(i => i.Scheme).Distinct(StringComparer.Ordinal).Count() == ids.Count)
            .WithErrorCode("SCHEME_REPEATED").WithMessage("Give each identifier scheme once.");
        RuleForEach(c => c.Request.Addresses).ChildRules(address =>
        {
            address.RuleFor(a => a.Country).NotEmpty().Matches("^[A-Z]{2}$").WithErrorCode("COUNTRY");
            address.RuleFor(a => a.Types).NotEmpty();
            address.RuleFor(a => a.FreeLines).Must(lines => lines is null || lines.Count <= 3).WithErrorCode("FREE_LINES");
        });
        RuleForEach(c => c.Request.ContactPoints).ChildRules(contact => contact.RuleFor(cp => cp.Value).NotEmpty().MaximumLength(254));
        RuleFor(c => c.Request.Reason).MaximumLength(64);
    }
}

/// <summary>
/// Creates a person or organisation (REQ-PTY-001, -003, -012, -030..035, -046, -048, -052, -060..063, -065, -074, -075,
/// -081..083): identifiers validated by the bound <c>IdValidator</c> and stored encrypted with a blind index; names with
/// generated Latin forms and search keys; addresses validated and formatted by <c>AddressFormatter</c>; a party number
/// from the PLT numbering service; <c>PartyCreated</c> through the outbox in the same transaction.
/// </summary>
internal sealed class CreatePartyHandler(
    PartyDbContext db,
    RequestContext context,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    PartyProtection protection,
    NameForms nameForms,
    IIdValidator idValidator,
    IEnumerable<IAddressFormatter> addressFormatters,
    ILanguageRuleSet languageRules,
    PartySearch search,
    PartyReader reader,
    IOptions<PartyOptions> options) : ICommandHandler<CreateParty, PartyCreateResponse>
{
    private const string UserEntry = "USER_ENTRY";

    public async Task<Result<PartyCreateResponse>> HandleAsync(CreateParty command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");
        var now = clock.Now;
        var today = new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime()));
        var actor = context.Actor.ToString();
        var partyId = PartyId.New();

        // 1. Identifiers: validate and normalise through the bound IdValidator (REQ-PTY-003, -048), refuse duplicates (REQ-PTY-052).
        var identifiers = new List<PartyIdentifierRow>();
        foreach (var input in request.Identifiers ?? [])
        {
            var validated = await ValidateIdentifierAsync(input, jurisdiction, request.PartyType, cancellationToken).ConfigureAwait(false);
            if (validated.IsFailure)
            {
                return validated.Error;
            }

            var (normalised, validatorVersion) = validated.Value;
            var duplicate = await search.ByIdentifierAsync(legalEntity, input.Scheme, normalised, cancellationToken).ConfigureAwait(false);
            if (duplicate.Count > 0)
            {
                return await DuplicateAsync(input.Scheme, duplicate[0].PartyId, cancellationToken).ConfigureAwait(false);
            }

            var identifierId = Guid.CreateVersion7();
            identifiers.Add(new PartyIdentifierRow
            {
                IdentifierId = identifierId,
                PartyId = partyId,
                LegalEntityId = legalEntity,
                Scheme = input.Scheme,
                ValueEncrypted = await protection.EncryptIdentifierAsync(legalEntity, identifierId, normalised, cancellationToken).ConfigureAwait(false),
                ValueBlindIndex = await protection.IndexAsync(legalEntity, input.Scheme, normalised, cancellationToken).ConfigureAwait(false),
                DisplaySuffix = normalised.Length <= 3 ? normalised : normalised[^3..],
                IssuingCountry = input.IssuingCountry,
                VerificationStatus = Codes.Of(VerificationStatus.SelfDeclared),
                VerificationSource = UserEntry,
                ValidatorVersion = validatorVersion,
                ValidFrom = today,
                RecordedFrom = now,
                CreatedBy = actor,
            });
        }

        // 2. Names (REQ-PTY-061..063) and search keys (REQ-PTY-065..067).
        var names = await NamesAsync(request, partyId, legalEntity, today, now, actor, cancellationToken).ConfigureAwait(false);
        var keys = new List<PartySearchKeyRow>();
        foreach (var name in names)
        {
            var full = name.OrganisationName ?? NameForms.PersonDisplay(name.GivenNames, name.FamilyName);
            foreach (var (kind, key) in await nameForms.StoredKeysAsync(full, cancellationToken).ConfigureAwait(false))
            {
                keys.Add(new PartySearchKeyRow
                {
                    PartyId = partyId, LegalEntityId = legalEntity, NameId = name.NameId, KeyKind = kind, SearchKey = key,
                    RuleVersion = nameForms.KeyRuleVersion, RecordedFrom = now,
                });
            }
        }

        // 3. Addresses (REQ-PTY-012, -074, -075, -076).
        var addresses = new List<PartyAddressRow>();
        foreach (var input in request.Addresses ?? [])
        {
            var address = await AddressAsync(input, partyId, legalEntity, today, now, actor, cancellationToken).ConfigureAwait(false);
            if (address.IsFailure)
            {
                return address.Error;
            }

            addresses.Add(address.Value);
        }

        MarkPrimary(addresses, a => string.Join(",", a.Types), a => a.IsPrimary, (a, p) => a.IsPrimary = p);

        // 4. Contact points (REQ-PTY-081..083).
        var contacts = new List<PartyContactPointRow>();
        foreach (var (input, index) in (request.ContactPoints ?? []).Select((c, i) => (c, i)))
        {
            var value = input.Type == ContactPointTypeCode.Email
                ? ContactPoints.NormaliseEmail(input.Value)
                : input.Type == ContactPointTypeCode.SecureInbox ? input.Value.Trim() : ContactPoints.NormalisePhone(input.Value, options.Value.DefaultCallingCode);
            if (value is null)
            {
                return Invalid($"contactPoints[{index}].value", input.Type == ContactPointTypeCode.Email ? "EMAIL_SYNTAX" : "PHONE_E164", "The contact point is not valid.");
            }

            contacts.Add(new PartyContactPointRow
            {
                ContactPointId = Guid.CreateVersion7(), PartyId = partyId, LegalEntityId = legalEntity, Type = Codes.Of(input.Type), Value = value,
                Purpose = Codes.Of(input.Purpose ?? ContactPointPurpose.Personal), IsPrimary = input.Primary == true,
                VerificationStatus = "UNVERIFIED", Source = UserEntry, ValidFrom = today, RecordedFrom = now, CreatedBy = actor,
            });
        }

        MarkPrimary(contacts, c => c.Type, c => c.IsPrimary, (c, p) => c.IsPrimary = p);

        // 5. The party itself: number from the PLT numbering service (REQ-PTY-034), initial status (REQ-PTY-033).
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Party, today), cancellationToken).ConfigureAwait(false);
        var status = PartyStateModel.Machine.Start(PartyStatus.Prospect);
        var party = new PartyRow
        {
            PartyId = partyId,
            LegalEntityId = legalEntity,
            Jurisdiction = jurisdiction.Value,
            PartyNumber = PartyNumber.Parse(number.Value),
            PartyType = request.PartyType == PartyType.Person ? "PERSON" : "ORGANISATION",
            Status = Codes.Of(status.Value),
            PreferredLanguage = request.PreferredLanguage ?? languageRules.Language,
            BirthDateEncrypted = request.Person is { } person
                ? await protection.EncryptBirthDateAsync(legalEntity, partyId.Value, person.BirthDate, cancellationToken).ConfigureAwait(false)
                : null,
            SourceChannel = context.Channel,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        };

        db.Parties.Add(party);
        db.Names.AddRange(names);
        db.SearchKeys.AddRange(keys);
        db.Identifiers.AddRange(identifiers);
        db.Addresses.AddRange(addresses);
        db.ContactPoints.AddRange(contacts);

        // Save inside the handler so a unique-index race (another party took the identifier meanwhile) becomes a typed error.
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_party_identifier_current" })
        {
            return DomainError.Of(ModuleCode.PTY, "DUPLICATE-IDENTIFIER", "Another party took this identifier meanwhile; search for it and use the existing party.");
        }

        // 6. The event, staged in the outbox of this transaction (no P2 value in the payload, REQ-PTY-042).
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PartyCreatedV1.Descriptor),
            "Party",
            partyId.Value.ToString(),
            new PartyCreatedV1 { PartyId = partyId, PartyNumber = party.PartyNumber, PartyType = party.PartyType, Status = party.Status, RolesAtCreation = [] },
            BusinessKeys.Empty.With("partyId", partyId.Value.ToString())));

        var view = await reader.GetAsync(legalEntity, context.LegalEntity!.Value, partyId.Value, null, new TimePoint(today, now), revealP2: false, cancellationToken)
            .ConfigureAwait(false);
        var duplicates = await PossibleDuplicatesAsync(legalEntity, partyId.Value, names, cancellationToken).ConfigureAwait(false);
        return new PartyCreateResponse { Party = view!, DuplicateSuggestions = duplicates, MissingData = [] };
    }

    private async Task<Result<(string Normalised, string ValidatorVersion)>> ValidateIdentifierAsync(
        IdentifierInput input, Jurisdiction jurisdiction, PartyType partyType, CancellationToken cancellationToken)
    {
        IdValidationResult result;
        try
        {
            result = await idValidator.ValidateAsync(
                new IdScheme(input.Scheme), input.Value, new IdValidationContext(input.IssuingCountry ?? jurisdiction.Value, partyType == PartyType.Person ? "Person" : "Organisation"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SpiException ex) when (ex.Category == SpiErrorCategory.NotApplicable)
        {
            return Invalid("identifiers.scheme", "SCHEME_NOT_SUPPORTED", $"Identifier scheme {input.Scheme} is not in the identifier catalogue.");
        }

        if (result.IsAccepted && result.Normalised is not null)
        {
            return (result.Normalised, result.ValidatorVersion);
        }

        if (result.Errors.Any(e => e.Code == IdValidationErrorCodes.CheckDigit))
        {
            return DomainError.Of(ModuleCode.PTY, "ID-CHECKDIGIT", $"The {input.Scheme} check digit is wrong.");
        }

        return new DomainError(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), $"The {input.Scheme} value is not valid.")
        {
            FieldErrors = [.. result.Errors.Select(e => new FieldError("identifiers.value", e.Code, "pty.identifier." + e.Code.ToLowerInvariant()))],
        };
    }

    private async Task<DomainError> DuplicateAsync(string scheme, Guid existing, CancellationToken cancellationToken)
    {
        var number = await db.Parties.Where(p => p.PartyId == new PartyId(existing)).Select(p => p.PartyNumber).FirstAsync(cancellationToken).ConfigureAwait(false);
        return new DomainError(ErrorCode.For(ModuleCode.PTY, "DUPLICATE-IDENTIFIER"), $"Another party already holds this {scheme}. Use the existing party.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["existingPartyId"] = existing.ToString(), ["existingPartyNumber"] = number.Value },
        };
    }

    private async Task<List<PartyNameRow>> NamesAsync(
        PartyCreateRequest request, PartyId partyId, LegalEntityId legalEntity, BusinessDate today, Instant now, string actor, CancellationToken cancellationToken)
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
            var native = Row("NATIVE", script);
            native.GivenNames = person.GivenNames.Trim();
            native.FamilyName = person.FamilyName.Trim();
            native.FatherName = person.FatherName?.Trim();
            native.MotherName = person.MotherName?.Trim();
            rows.Add(native);
            if (script != ScriptCodes.Latin)
            {
                var latin = Row("LATIN_GENERATED", ScriptCodes.Latin);
                latin.GivenNames = await nameForms.LatinAsync(native.GivenNames, cancellationToken).ConfigureAwait(false);
                latin.FamilyName = await nameForms.LatinAsync(native.FamilyName, cancellationToken).ConfigureAwait(false);
                latin.FatherName = await nameForms.LatinAsync(native.FatherName, cancellationToken).ConfigureAwait(false);
                latin.MotherName = await nameForms.LatinAsync(native.MotherName, cancellationToken).ConfigureAwait(false);
                latin.TransliteratorVersion = nameForms.TransliteratorVersion;
                rows.Add(latin);
            }

            if (person.LatinNameAsOnDocument is { } document)
            {
                var asOnDocument = Row("LATIN_AS_ON_DOCUMENT", ScriptCodes.Latin);
                asOnDocument.GivenNames = document.GivenNames.Trim();
                asOnDocument.FamilyName = document.FamilyName.Trim();
                asOnDocument.SourceDocumentRef = document.SourceDocumentRef;
                rows.Add(asOnDocument);
            }
        }
        else if (request.Organisation is { } organisation)
        {
            var script = NameForms.ScriptOf(organisation.LegalName);
            var native = Row("NATIVE", script);
            native.OrganisationName = organisation.LegalName.Trim();
            native.TradeName = organisation.TradeName?.Trim();
            rows.Add(native);
            if (script != ScriptCodes.Latin)
            {
                var latin = Row("LATIN_GENERATED", ScriptCodes.Latin);
                latin.OrganisationName = await nameForms.LatinAsync(native.OrganisationName, cancellationToken).ConfigureAwait(false);
                latin.TradeName = await nameForms.LatinAsync(native.TradeName, cancellationToken).ConfigureAwait(false);
                latin.TransliteratorVersion = nameForms.TransliteratorVersion;
                rows.Add(latin);
            }
        }

        return rows;
    }

    private async Task<Result<PartyAddressRow>> AddressAsync(
        AddressInput input, PartyId partyId, LegalEntityId legalEntity, BusinessDate today, Instant now, string actor, CancellationToken cancellationToken)
    {
        var address = new PostalAddress
        {
            Country = input.Country,
            Street = Clean(input.Street),
            Number = Clean(input.Number),
            Building = Clean(input.Building),
            Floor = Clean(input.Floor),
            Unit = Clean(input.Unit),
            Postcode = Clean(input.Postcode),
            Locality = Clean(input.Locality),
            Municipality = Clean(input.Municipality),
            RegionalUnit = Clean(input.RegionalUnit),
            Region = Clean(input.Region),
            FreeLines = [.. (input.FreeLines ?? []).Select(l => l.Trim()).Where(l => l.Length > 0)],
        };

        var formatter = addressFormatters.FirstOrDefault(f => f.Countries.Contains(input.Country, StringComparer.Ordinal));
        var state = "UNVALIDATED";
        string? ruleSet = null;
        IReadOnlyList<string> lines = [.. new[] { Join(" ", address.Street, address.Number), Join(" ", address.Postcode, address.Locality) }.OfType<string>(), .. address.FreeLines];
        IReadOnlyList<string> latinLines = lines;
        if (formatter is not null)
        {
            var validation = await formatter.ValidateAsync(address, cancellationToken).ConfigureAwait(false);
            if (validation.Errors.Any(e => e.Code == AddressErrorCodes.PostcodeFormat))
            {
                return DomainError.Of(ModuleCode.PTY, "POSTCODE-FORMAT", "The postcode does not have the country's format.");
            }

            if (validation.Status == AddressValidationStatus.Invalid)
            {
                return new DomainError(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), "The address is not valid.")
                {
                    FieldErrors = [.. validation.Errors.Select(e => new FieldError("addresses." + (e.Field ?? "address"), e.Code, "pty.address." + e.Code.ToLowerInvariant()))],
                };
            }

            address = validation.Normalised;
            state = validation.Status == AddressValidationStatus.Valid ? "VALIDATED" : "UNVALIDATED";
            var nativeScript = NameForms.ScriptOf(string.Concat(address.Street, address.Locality));
            var native = await formatter.FormatAsync(address, AddressPurpose.Postal, nativeScript, cancellationToken).ConfigureAwait(false);
            var latin = await formatter.FormatAsync(address, AddressPurpose.Postal, ScriptCodes.Latin, cancellationToken).ConfigureAwait(false);
            lines = native.Lines;
            latinLines = latin.Lines;
            ruleSet = latin.RuleSetId;
        }

        var latinFreeLines = new List<string>();
        foreach (var line in address.FreeLines)
        {
            latinFreeLines.Add(await nameForms.LatinAsync(line, cancellationToken).ConfigureAwait(false) ?? line);
        }

        return new PartyAddressRow
        {
            AddressId = Guid.CreateVersion7(), PartyId = partyId, LegalEntityId = legalEntity,
            Types = [.. input.Types.Distinct().Select(t => Codes.Of(t))], IsPrimary = input.Primary == true, Country = input.Country,
            Street = address.Street, Number = address.Number, Building = address.Building, Floor = address.Floor, Unit = address.Unit,
            Postcode = address.Postcode, Locality = address.Locality, Municipality = address.Municipality, RegionalUnit = address.RegionalUnit,
            Region = address.Region, FreeLines = [.. address.FreeLines],
            LatinStreet = await nameForms.LatinAsync(address.Street, cancellationToken).ConfigureAwait(false),
            LatinBuilding = await nameForms.LatinAsync(address.Building, cancellationToken).ConfigureAwait(false),
            LatinLocality = await nameForms.LatinAsync(address.Locality, cancellationToken).ConfigureAwait(false),
            LatinMunicipality = await nameForms.LatinAsync(address.Municipality, cancellationToken).ConfigureAwait(false),
            LatinRegionalUnit = await nameForms.LatinAsync(address.RegionalUnit, cancellationToken).ConfigureAwait(false),
            LatinRegion = await nameForms.LatinAsync(address.Region, cancellationToken).ConfigureAwait(false),
            LatinFreeLines = [.. latinFreeLines],
            FormattedLines = [.. lines], FormattedLinesLatin = [.. latinLines], ValidationState = state, RuleSetId = ruleSet,
            Description = Clean(input.Description), ValidFrom = today, RecordedFrom = now, CreatedBy = actor,
        };
    }

    /// <summary>Possible duplicates: other parties whose whole-name key equals one of the new party's (warning, not a refusal).</summary>
    private async Task<IReadOnlyList<PartySearchItem>> PossibleDuplicatesAsync(
        LegalEntityId legalEntity, Guid partyId, IReadOnlyList<PartyNameRow> names, CancellationToken cancellationToken)
    {
        var fullKeys = names.Select(n => nameForms.Key(n.OrganisationName ?? NameForms.PersonDisplay(n.GivenNames, n.FamilyName))).Distinct().ToList();
        var others = await db.SearchKeys
            .Where(k => k.LegalEntityId == legalEntity && k.KeyKind == "FULL" && k.RecordedTo == null && fullKeys.Contains(k.SearchKey) && k.PartyId != new PartyId(partyId))
            .Select(k => k.PartyId).Distinct().Take(5).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await reader.HydrateAsync(legalEntity, [.. others.Select(id => new SearchHit(id.Value, 3, 1m))], cancellationToken).ConfigureAwait(false);
    }

    private static void MarkPrimary<T>(List<T> rows, Func<T, string> group, Func<T, bool> isPrimary, Action<T, bool> setPrimary)
    {
        foreach (var rowsOfGroup in rows.GroupBy(group))
        {
            var first = rowsOfGroup.FirstOrDefault(isPrimary) ?? rowsOfGroup.First();
            foreach (var row in rowsOfGroup)
            {
                setPrimary(row, ReferenceEquals(row, first));
            }
        }
    }

    private static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), message) { FieldErrors = [new FieldError(field, code, "pty." + code.ToLowerInvariant(), message)] };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}

/// <summary>Audit facts of <c>pty.Party.create</c>: object, number and lineage; no P2 value (REQ-PTY-044, NFR-PTY-011).</summary>
internal sealed class CreatePartyAuditor : ICommandAuditor<CreateParty, PartyCreateResponse>
{
    public CommandAuditFacts Describe(CreateParty command, Result<PartyCreateResponse>? result)
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
            Changes = AuditDiff.Compute(null, new { partyType = party.PartyType.ToString(), status = party.Status, identifierSchemes = party.Identifiers.Select(i => i.Scheme).ToArray() }),
        };
    }
}
