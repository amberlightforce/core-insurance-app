using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ContactPointTypeCode = CoreIns.Modules.Party.Contracts.Api.ContactPointType;

namespace CoreIns.Modules.Party.MigrationImport;

/// <summary>Object types the PTY importer takes (BO-MIG-06).</summary>
internal static class PartyImportTypes
{
    public const string Person = "Person";
    public const string Organisation = "Organisation";
    public const string Contact = "Contact";
}

/// <summary>How a migrated party and contact are marked (so the reverser finds exactly the batch's objects, and a contact can name its party by legacy id).</summary>
internal static class PartyImportMarks
{
    /// <summary><c>party.source_channel</c> prefix of every party of the batch.</summary>
    public static string PartyPrefix(Guid batchId) => $"MIGRATION:{batchId}:";

    /// <summary><c>party.source_channel</c> of one migrated party.</summary>
    public static string PartyChannel(Guid batchId, string legacyId) => PartyPrefix(batchId) + legacyId;

    /// <summary><c>party_contact_point.source</c> of a migrated contact.</summary>
    public static string ContactSource(Guid batchId) => $"MIGRATION:{batchId}";
}

/// <summary>A legacy contact point: the party is named by core id or by the legacy id it was imported under.</summary>
internal sealed record ContactPayload(
    Guid? PartyId, string? PartyLegacyId, ContactPointTypeCode Type, string Value, ContactPointPurpose? Purpose, bool? Primary);

/// <summary>
/// <c>IMigrationImporter</c> for module PTY (BO-MIG-06). <c>Person</c> and <c>Organisation</c> take the <c>pty.Party.create</c> request
/// shape (names, birthDate, identifiers such as the AFM, addresses, contact points) and create through that command, so AFM and identity
/// validation, encryption of P2 values with the blind index, search keys and the party number are exactly those of a live create. A party
/// whose identifier already exists is NOT merged: the item fails with <c>MIG-ERR-DUPLICATE</c> naming the existing party (or
/// <c>MIG-ERR-DSAR-RESTRICTED</c> when that party is under a DSAR restriction). Name-only duplicate candidates are reported in the
/// message and never merged. <c>Contact</c> adds one contact point to a party. Xref is registered by the batch framework.
/// </summary>
internal sealed class PartyMigrationImporter(
    IPartyPartyService parties,
    IIdValidator idValidator,
    PartySearch search,
    PartyProtection protection,
    PartyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IOptions<PartyOptions> options) : IMigrationImporter
{
    private const string Validation = "MIG-ERR-VALIDATION";

    public string ModuleName => "PTY";

    public Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(importContext);
        return item.ObjectType switch
        {
            PartyImportTypes.Person => ImportPartyAsync(item, importContext, PartyType.Person, cancellationToken),
            PartyImportTypes.Organisation => ImportPartyAsync(item, importContext, PartyType.Organisation, cancellationToken),
            PartyImportTypes.Contact => ImportContactAsync(item, importContext, cancellationToken),
            _ => Task.FromResult(Failed(Validation, $"PTY imports Person, Organisation or Contact, not '{item.ObjectType}'.")),
        };
    }

    private async Task<ImportOutcome> ImportPartyAsync(MigrationImportItem item, MigrationImportContext importContext, PartyType type, CancellationToken cancellationToken)
    {
        PartyCreateRequest? request;
        try
        {
            if (item.Payload.ValueKind != JsonValueKind.Object || JsonNode.Parse(item.Payload.GetRawText()) is not JsonObject node)
            {
                return Failed(Validation, "The payload must be an object.");
            }

            // Convenience: a top-level "afm" is the AFM identifier.
            if (node.Remove("afm", out var afm) && afm is JsonValue afmValue && afmValue.TryGetValue<string>(out var afmText) && !string.IsNullOrWhiteSpace(afmText))
            {
                var identifiers = node["identifiers"] as JsonArray ?? [];
                identifiers.Add(new JsonObject { ["scheme"] = "AFM", ["value"] = afmText });
                node["identifiers"] = identifiers;
            }

            node["partyType"] = type == PartyType.Person ? "PERSON" : "ORGANISATION";
            request = node.Deserialize<PartyCreateRequest>(SharedKernelJson.Options);
        }
        catch (JsonException ex)
        {
            return Failed(Validation, "The payload is not a valid party: " + ex.Message);
        }

        if (request is null)
        {
            return Failed(Validation, "The payload is empty.");
        }

        var legalEntity = protection.Current(context);
        if (importContext.DryRun)
        {
            // The create command is not rolled back inside the batch's transaction, so a dry run validates without writing.
            return await DryRunAsync(request, legalEntity, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var response = await parties.CreateAsync(request, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            var partyId = response.Party.PartyId;
            var candidates = response.DuplicateSuggestions is { Count: > 0 } suggestions
                ? " Possible duplicates (not merged, for the PTY review queue): " + string.Join(", ", suggestions.Select(s => s.PartyNumber.Value)) + "."
                : string.Empty;
            // Mark the party as migrated (origin and batch) for the reverser and for contacts that name it by legacy id.
            var row = await db.Parties.SingleAsync(p => p.PartyId == partyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
            row.SourceChannel = PartyImportMarks.PartyChannel(importContext.BatchId, item.LegacyId);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ImportOutcome(ImportStatus.Imported, partyId.Value, null, "Party " + response.Party.PartyNumber.Value + " created." + candidates);
        }
        catch (DomainException ex) when (ex.Error.Code.Name == "DUPLICATE-IDENTIFIER")
        {
            ex.Error.Metadata.TryGetValue("existingPartyId", out var existing);
            ex.Error.Metadata.TryGetValue("existingPartyNumber", out var existingNumber);
            return await DuplicateFailureAsync(existing, existingNumber, cancellationToken).ConfigureAwait(false);
        }

        catch (DomainException ex)
        {
            var field = ex.Error.FieldErrors is { Count: > 0 } fe ? fe[0] : null;
            return Failed(Validation, $"{ex.Error.Code.Value}: {ex.Error.Detail}{(field is null ? string.Empty : $" ({field.Field} {field.Code})")}");
        }
    }

    private async Task<ImportOutcome> DuplicateFailureAsync(string? existing, string? existingNumber, CancellationToken cancellationToken)
    {
        var restricted = Guid.TryParse(existing, out var existingId)
            && await db.DsarRestrictions.AnyAsync(r => r.PartyId == new PartyId(existingId), cancellationToken).ConfigureAwait(false);

        // TODO(hardening): queue the pair in the PTY duplicate review queue (BO-PTY-01) instead of only reporting it.
        return restricted
            ? Failed("MIG-ERR-DSAR-RESTRICTED", $"The identifier belongs to party {existingNumber}, which is under a DSAR restriction: it is not touched; review manually.")
            : Failed("MIG-ERR-DUPLICATE", $"The identifier already belongs to party {existingNumber} ({existing}); nothing merged, review it as a duplicate candidate.");
    }

    private async Task<ImportOutcome> DryRunAsync(PartyCreateRequest request, LegalEntityId legalEntity, CancellationToken cancellationToken)
    {
        if ((request.PartyType == PartyType.Person && request.Person is null) || (request.PartyType == PartyType.Organisation && request.Organisation is null))
        {
            return Failed(Validation, "The payload needs the person or organisation block of its object type.");
        }

        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");
        foreach (var input in request.Identifiers ?? [])
        {
            IdValidationResult result;
            try
            {
                result = await idValidator.ValidateAsync(
                    new IdScheme(input.Scheme), input.Value,
                    new IdValidationContext(input.IssuingCountry ?? jurisdiction.Value, request.PartyType == PartyType.Person ? "Person" : "Organisation"), cancellationToken).ConfigureAwait(false);
            }
            catch (SpiException ex) when (ex.Category == SpiErrorCategory.NotApplicable)
            {
                return Failed(Validation, $"SCHEME_NOT_SUPPORTED: identifier scheme {input.Scheme} is not in the identifier catalogue.");
            }

            if (!result.IsAccepted || result.Normalised is null)
            {
                return Failed(Validation, $"{input.Scheme} {string.Join(",", result.Errors.Select(e => e.Code))}: the identifier is not valid.");
            }

            var hits = await search.ByIdentifierAsync(legalEntity, input.Scheme, result.Normalised, cancellationToken).ConfigureAwait(false);
            if (hits.Count > 0)
            {
                var number = await db.Parties.Where(p => p.PartyId == new PartyId(hits[0].PartyId)).Select(p => p.PartyNumber).FirstAsync(cancellationToken).ConfigureAwait(false);
                return await DuplicateFailureAsync(hits[0].PartyId.ToString(), number.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ImportOutcome(ImportStatus.Imported, null, null, "Would create the party.");
    }

    private async Task<ImportOutcome> ImportContactAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        ContactPayload? payload;
        try
        {
            payload = item.Payload.Deserialize<ContactPayload>(SharedKernelJson.Options);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Value) || (payload.PartyId is null && string.IsNullOrWhiteSpace(payload.PartyLegacyId)))
        {
            return Failed(Validation, "A contact needs type, value and the party (partyId or partyLegacyId).");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        PartyId? partyId = null;
        if (payload.PartyId is { } id)
        {
            partyId = await db.Parties.Where(p => p.PartyId == new PartyId(id) && p.LegalEntityId == legalEntity).Select(p => (PartyId?)p.PartyId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The party was imported (in this or an earlier batch) under that legacy id: source_channel MIGRATION:{batch}:{legacyId}.
            var suffix = ":" + payload.PartyLegacyId;
            var matches = await db.Parties.Where(p => p.LegalEntityId == legalEntity && p.SourceChannel != null && p.SourceChannel.StartsWith("MIGRATION:") && p.SourceChannel.EndsWith(suffix))
                .Select(p => p.PartyId).Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (matches.Count > 1)
            {
                return Failed("MIG-ERR-DUPLICATE", $"The legacy party id {payload.PartyLegacyId} is ambiguous.");
            }

            partyId = matches.Count == 1 ? matches[0] : null;
        }

        if (partyId is null)
        {
            return Failed("MIG-ERR-DEPENDENCY", "The party of the contact does not exist (import the Person or Organisation first).");
        }

        var value = payload.Type == ContactPointTypeCode.Email ? ContactPoints.NormaliseEmail(payload.Value)
            : payload.Type == ContactPointTypeCode.SecureInbox ? payload.Value.Trim()
            : ContactPoints.NormalisePhone(payload.Value, options.Value.DefaultCallingCode);
        if (value is null)
        {
            return Failed(Validation, payload.Type == ContactPointTypeCode.Email ? "EMAIL_SYNTAX: the e-mail address is not valid." : "PHONE_E164: the phone number is not valid.");
        }

        var type = Codes.Of(payload.Type);
        if (await db.ContactPoints.AnyAsync(c => c.PartyId == partyId && c.Type == type && c.Value == value && c.RecordedTo == null, cancellationToken).ConfigureAwait(false))
        {
            return Failed("MIG-ERR-DUPLICATE", "The party already has this contact point.");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, "Would add the contact point.");
        }

        var now = clock.Now;
        var row = new PartyContactPointRow
        {
            ContactPointId = Guid.CreateVersion7(), PartyId = partyId.Value, LegalEntityId = legalEntity, Type = type, Value = value,
            Purpose = Codes.Of(payload.Purpose ?? ContactPointPurpose.Personal), IsPrimary = false, VerificationStatus = "UNVERIFIED",
            Source = PartyImportMarks.ContactSource(importContext.BatchId), ValidFrom = new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime())),
            RecordedFrom = now, CreatedBy = context.Actor.ToString(),
        };

        // The first contact point of its type becomes the primary one.
        row.IsPrimary = payload.Primary == true
            || !await db.ContactPoints.AnyAsync(c => c.PartyId == partyId && c.Type == type && c.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        db.ContactPoints.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ImportOutcome(ImportStatus.Imported, row.ContactPointId, null, "Contact point added.");
    }

    private static ImportOutcome Failed(string code, string message) => new(ImportStatus.Failed, null, code, message);
}
