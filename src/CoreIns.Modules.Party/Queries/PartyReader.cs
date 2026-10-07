using System.Data;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;
using Npgsql;
using SkLegalEntityId = CoreIns.SharedKernel.Identifiers.LegalEntityId;

namespace CoreIns.Modules.Party.Queries;

/// <summary>A point in valid and record time (REQ-PTY-036): <c>validAt</c> as a business date, <c>knownAt</c> as an instant.</summary>
internal readonly record struct TimePoint(BusinessDate ValidAt, Instant KnownAt);

/// <summary>
/// The read side of the party master: Dapper over the scope's <see cref="DbSession"/> connection (inside the open
/// transaction when there is one, so a command reads its own writes). Every query is filtered by the caller's legal
/// entity (REQ-PTY-035): another entity's party is "not found", never an error that reveals it.
/// </summary>
internal sealed class PartyReader(DbSession session, PartyProtection protection)
{
    private const string Current = "valid_from <= @validAt AND (valid_to IS NULL OR valid_to > @validAt) AND recorded_from <= @knownAt AND (recorded_to IS NULL OR recorded_to > @knownAt)";

    /// <summary>The party as valid at <paramref name="at"/>.ValidAt and known at .KnownAt; null when not found.</summary>
    public async Task<PartyView?> GetAsync(
        SkLegalEntityId legalEntity, LegalEntityCode legalEntityCode, Guid? partyId, string? partyNumber, TimePoint at, bool revealP2, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { le = legalEntity.Value, id = partyId, number = partyNumber, knownAt = at.KnownAt.ToUtcDateTime() });
        args.Add("validAt", at.ValidAt.Value, DbType.Date);

        var party = await connection.QuerySingleOrDefaultAsync<PartyRecord>(new CommandDefinition(
            """
            SELECT p.party_id AS PartyId, p.party_number AS PartyNumber, p.party_type AS PartyType, p.status AS Status,
                   p.jurisdiction AS Jurisdiction, p.preferred_language AS PreferredLanguage, p.record_version AS RecordVersion,
                   p.birth_date_encrypted AS BirthDateEncrypted, p.created_at AS CreatedAt
              FROM pty.party p
             WHERE p.legal_entity_id = @le AND p.created_at <= @knownAt
               AND (@id::uuid IS NULL OR p.party_id = @id) AND (@number::text IS NULL OR p.party_number = @number)
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (party is null || (partyId is null && partyNumber is null))
        {
            return null;
        }

        args.Add("party", party.PartyId);
        var names = await QueryAsync<NameRecord>(connection,
            $"""
             SELECT form AS Form, script AS Script, given_names AS GivenNames, family_name AS FamilyName, father_name AS FatherName,
                    mother_name AS MotherName, organisation_name AS OrganisationName, trade_name AS TradeName,
                    transliterator_version AS TransliteratorVersion, source_document_ref AS SourceDocumentRef
               FROM pty.party_name WHERE party_id = @party AND {Current}
              ORDER BY CASE form WHEN 'NATIVE' THEN 0 WHEN 'LATIN_GENERATED' THEN 1 ELSE 2 END
             """, args, cancellationToken).ConfigureAwait(false);
        var identifiers = await QueryAsync<IdentifierRecord>(connection,
            $"""
             SELECT identifier_id AS IdentifierId, scheme AS Scheme, value_encrypted AS ValueEncrypted, display_suffix AS DisplaySuffix,
                    issuing_country AS IssuingCountry, verification_status AS VerificationStatus, verification_source AS VerificationSource,
                    validator_version AS ValidatorVersion, valid_from::text AS ValidFrom
               FROM pty.party_identifier WHERE party_id = @party AND {Current} ORDER BY recorded_from, identifier_id
             """, args, cancellationToken).ConfigureAwait(false);
        var addresses = await QueryAsync<AddressRecord>(connection,
            $"""
             SELECT address_id AS AddressId, types AS Types, is_primary AS IsPrimary, country AS Country, street AS Street, number AS Number,
                    building AS Building, floor AS Floor, unit AS Unit, postcode AS Postcode, locality AS Locality, municipality AS Municipality,
                    regional_unit AS RegionalUnit, region AS Region, free_lines AS FreeLines, latin_street AS LatinStreet,
                    latin_building AS LatinBuilding, latin_locality AS LatinLocality, latin_municipality AS LatinMunicipality,
                    latin_regional_unit AS LatinRegionalUnit, latin_region AS LatinRegion, latin_free_lines AS LatinFreeLines,
                    formatted_lines AS FormattedLines, formatted_lines_latin AS FormattedLinesLatin, validation_state AS ValidationState,
                    description AS Description, valid_from::text AS ValidFrom, valid_to::text AS ValidTo
               FROM pty.party_address WHERE party_id = @party AND {Current} ORDER BY is_primary DESC, recorded_from, address_id
             """, args, cancellationToken).ConfigureAwait(false);
        var contacts = await QueryAsync<ContactRecord>(connection,
            $"""
             SELECT contact_point_id AS ContactPointId, type AS Type, value AS Value, purpose AS Purpose, is_primary AS IsPrimary,
                    verification_status AS VerificationStatus
               FROM pty.party_contact_point WHERE party_id = @party AND {Current} ORDER BY type, is_primary DESC, contact_point_id
             """, args, cancellationToken).ConfigureAwait(false);

        BusinessDate? birthDate = null;
        if (revealP2 && party.BirthDateEncrypted is { } envelope)
        {
            birthDate = await protection.DecryptBirthDateAsync(legalEntity, party.PartyId, envelope, cancellationToken).ConfigureAwait(false);
        }

        var identifierViews = new List<PartyIdentifierView>(identifiers.Count);
        foreach (var identifier in identifiers)
        {
            identifierViews.Add(new PartyIdentifierView
            {
                IdentifierId = identifier.IdentifierId,
                Scheme = identifier.Scheme,
                Value = revealP2
                    ? await protection.DecryptIdentifierAsync(legalEntity, identifier.IdentifierId, identifier.ValueEncrypted, cancellationToken).ConfigureAwait(false)
                    : NameForms.Mask(identifier.DisplaySuffix),
                Masked = !revealP2,
                IssuingCountry = identifier.IssuingCountry,
                VerificationStatus = Codes.Parse<VerificationStatus>(identifier.VerificationStatus) switch
                {
                    VerificationStatus.SelfDeclared => PartyIdentifierView.VerificationStatusValue.SelfDeclared,
                    VerificationStatus.DocumentVerified => PartyIdentifierView.VerificationStatusValue.DocumentVerified,
                    VerificationStatus.RegistryVerified => PartyIdentifierView.VerificationStatusValue.RegistryVerified,
                    VerificationStatus.VerificationFailed => PartyIdentifierView.VerificationStatusValue.VerificationFailed,
                    _ => PartyIdentifierView.VerificationStatusValue.Expired,
                },
                VerificationSource = identifier.VerificationSource,
                ValidatorVersion = identifier.ValidatorVersion,
                ValidFrom = BusinessDate.Parse(identifier.ValidFrom),
            });
        }

        return new PartyView
        {
            PartyId = new PartyId(party.PartyId),
            PartyNumber = PartyNumber.Parse(party.PartyNumber),
            PartyType = party.PartyType == "PERSON" ? PartyType.Person : PartyType.Organisation,
            Status = party.Status,
            LegalEntity = legalEntityCode.Value,
            Jurisdiction = party.Jurisdiction,
            PreferredLanguage = party.PreferredLanguage,
            RecordVersion = party.RecordVersion,
            BirthDate = birthDate,
            P2Revealed = revealP2,
            Names = [.. names.Select(ToView)],
            Identifiers = identifierViews,
            Addresses = [.. addresses.Select(ToView)],
            ContactPoints = [.. contacts.Select(ToView)],
            CreatedAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(party.CreatedAt, DateTimeKind.Utc)),
        };
    }

    /// <summary>Ranked search rows → result items (display names, masked identifier, primary address).</summary>
    public async Task<IReadOnlyList<PartySearchItem>> HydrateAsync(
        SkLegalEntityId legalEntity, IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
        {
            return [];
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<SearchItemRecord>(new CommandDefinition(
            """
            SELECT p.party_id AS PartyId, p.party_number AS PartyNumber, p.party_type AS PartyType, p.status AS Status,
                   coalesce(n.organisation_name, nullif(concat_ws(' ', n.given_names, n.family_name), '')) AS DisplayName,
                   coalesce(d.organisation_name, nullif(concat_ws(' ', d.given_names, d.family_name), ''),
                            g.organisation_name, nullif(concat_ws(' ', g.given_names, g.family_name), '')) AS DisplayNameLatin,
                   i.scheme AS IdentifierScheme, i.display_suffix AS IdentifierSuffix,
                   a.postcode AS Postcode, a.locality AS Locality
              FROM pty.party p
              LEFT JOIN LATERAL (SELECT * FROM pty.party_name x WHERE x.party_id = p.party_id AND x.form = 'NATIVE' AND x.recorded_to IS NULL AND x.valid_to IS NULL LIMIT 1) n ON true
              LEFT JOIN LATERAL (SELECT * FROM pty.party_name x WHERE x.party_id = p.party_id AND x.form = 'LATIN_AS_ON_DOCUMENT' AND x.recorded_to IS NULL AND x.valid_to IS NULL LIMIT 1) d ON true
              LEFT JOIN LATERAL (SELECT * FROM pty.party_name x WHERE x.party_id = p.party_id AND x.form = 'LATIN_GENERATED' AND x.recorded_to IS NULL AND x.valid_to IS NULL LIMIT 1) g ON true
              LEFT JOIN LATERAL (SELECT scheme, display_suffix FROM pty.party_identifier x WHERE x.party_id = p.party_id AND x.recorded_to IS NULL AND x.valid_to IS NULL ORDER BY x.recorded_from, x.identifier_id LIMIT 1) i ON true
              LEFT JOIN LATERAL (SELECT postcode, locality FROM pty.party_address x WHERE x.party_id = p.party_id AND x.recorded_to IS NULL AND x.valid_to IS NULL ORDER BY x.is_primary DESC, x.recorded_from LIMIT 1) a ON true
             WHERE p.legal_entity_id = @le AND p.party_id = ANY(@ids)
            """,
            new { le = legalEntity.Value, ids = hits.Select(h => h.PartyId).ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(r => r.PartyId);

        var items = new List<PartySearchItem>(hits.Count);
        foreach (var hit in hits)
        {
            if (!rows.TryGetValue(hit.PartyId, out var row))
            {
                continue;
            }

            items.Add(new PartySearchItem
            {
                PartyId = new PartyId(row.PartyId),
                PartyNumber = PartyNumber.Parse(row.PartyNumber),
                PartyType = row.PartyType == "PERSON" ? PartyType.Person : PartyType.Organisation,
                Status = row.Status,
                DisplayName = row.DisplayName ?? string.Empty,
                DisplayNameLatin = string.IsNullOrEmpty(row.DisplayNameLatin) || row.DisplayNameLatin == row.DisplayName ? null : row.DisplayNameLatin,
                MaskedIdentifier = row.IdentifierScheme is null ? null : new MaskedIdentifier { Scheme = row.IdentifierScheme, MaskedValue = NameForms.Mask(row.IdentifierSuffix ?? string.Empty) },
                PrimaryPostcode = row.Postcode,
                PrimaryLocality = row.Locality,
                MatchQuality = hit.Tier switch
                {
                    3 => PartySearchItem.MatchQualityValue.Exact,
                    2 => PartySearchItem.MatchQualityValue.Prefix,
                    _ => PartySearchItem.MatchQualityValue.Fuzzy,
                },
                Similarity = hit.Similarity,
            });
        }

        return items;
    }

    private async Task<List<T>> QueryAsync<T>(NpgsqlConnection connection, string sql, DynamicParameters args, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<T>(new CommandDefinition(sql, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)];

    private static PartyNameView ToView(NameRecord name) => new()
    {
        Form = name.Form switch
        {
            "NATIVE" => PartyNameView.FormValue.Native,
            "LATIN_GENERATED" => PartyNameView.FormValue.LatinGenerated,
            _ => PartyNameView.FormValue.LatinAsOnDocument,
        },
        Script = name.Script,
        GivenNames = name.GivenNames,
        FamilyName = name.FamilyName,
        FatherName = name.FatherName,
        MotherName = name.MotherName,
        OrganisationName = name.OrganisationName,
        TradeName = name.TradeName,
        TransliteratorVersion = name.TransliteratorVersion,
        SourceDocumentRef = name.SourceDocumentRef,
    };

    private static PartyAddressView ToView(AddressRecord a) => new()
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
            Locality = a.LatinLocality, Municipality = a.LatinMunicipality, RegionalUnit = a.LatinRegionalUnit, Region = a.LatinRegion,
            FreeLines = a.LatinFreeLines,
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
        ValidFrom = BusinessDate.Parse(a.ValidFrom),
        ValidTo = a.ValidTo is null ? null : BusinessDate.Parse(a.ValidTo),
    };

    private static ContactPointView ToView(ContactRecord c) => new()
    {
        ContactPointId = c.ContactPointId,
        Type = Codes.Parse<ContactPointType>(c.Type),
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

    private sealed record PartyRecord(
        Guid PartyId, string PartyNumber, string PartyType, string Status, string Jurisdiction, string PreferredLanguage, int RecordVersion,
        byte[]? BirthDateEncrypted, DateTime CreatedAt);

    private sealed record NameRecord(
        string Form, string Script, string? GivenNames, string? FamilyName, string? FatherName, string? MotherName, string? OrganisationName,
        string? TradeName, string? TransliteratorVersion, string? SourceDocumentRef);

    private sealed record IdentifierRecord(
        Guid IdentifierId, string Scheme, byte[] ValueEncrypted, string DisplaySuffix, string? IssuingCountry, string VerificationStatus,
        string VerificationSource, string ValidatorVersion, string ValidFrom);

/// <summary>A class with setters, not a positional record: Dapper reports text[] columns as System.Array to constructors.</summary>
    private sealed class AddressRecord
    {
        public Guid AddressId { get; init; }
        public string[] Types { get; init; } = [];
        public bool IsPrimary { get; init; }
        public string Country { get; init; } = string.Empty;
        public string? Street { get; init; }
        public string? Number { get; init; }
        public string? Building { get; init; }
        public string? Floor { get; init; }
        public string? Unit { get; init; }
        public string? Postcode { get; init; }
        public string? Locality { get; init; }
        public string? Municipality { get; init; }
        public string? RegionalUnit { get; init; }
        public string? Region { get; init; }
        public string[] FreeLines { get; init; } = [];
        public string? LatinStreet { get; init; }
        public string? LatinBuilding { get; init; }
        public string? LatinLocality { get; init; }
        public string? LatinMunicipality { get; init; }
        public string? LatinRegionalUnit { get; init; }
        public string? LatinRegion { get; init; }
        public string[] LatinFreeLines { get; init; } = [];
        public string[] FormattedLines { get; init; } = [];
        public string[] FormattedLinesLatin { get; init; } = [];
        public string ValidationState { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string ValidFrom { get; init; } = string.Empty;
        public string? ValidTo { get; init; }
    }

    private sealed record ContactRecord(Guid ContactPointId, string Type, string Value, string Purpose, bool IsPrimary, string VerificationStatus);

    private sealed record SearchItemRecord(
        Guid PartyId, string PartyNumber, string PartyType, string Status, string? DisplayName, string? DisplayNameLatin, string? IdentifierScheme,
        string? IdentifierSuffix, string? Postcode, string? Locality);
}

/// <summary>Identifier verification status (REQ-PTY-053).</summary>
internal enum VerificationStatus
{
    SelfDeclared,
    DocumentVerified,
    RegistryVerified,
    VerificationFailed,
    Expired,
}

/// <summary>One ranked search hit: tier 3 exact, 2 prefix, 1 fuzzy (REQ-PTY-066).</summary>
internal sealed record SearchHit(Guid PartyId, int Tier, decimal Similarity);
