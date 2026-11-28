using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Services.Dsar;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using Dapper;
using Microsoft.EntityFrameworkCore;
using SkLegalEntityId = CoreIns.SharedKernel.Identifiers.LegalEntityId;

namespace CoreIns.Modules.Party.Queries;

/// <summary>Why two parties look alike (the reason codes of <c>pty.Party.duplicates</c>).</summary>
internal static class DuplicateReasons
{
    public const string AfmExact = "AFM_EXACT";
    public const string Name = "NAME";
    public const string NameAndBirthDate = "NAME_AND_BIRTH_DATE";
    public const string BirthDateDiffers = "BIRTH_DATE_DIFFERS";
    public const string IdentifierConflict = "IDENTIFIER_CONFLICT";
}

/// <summary>
/// Duplicate candidates of the legal entity (BO-PTY-05, REQ-PTY-007): pairs of live parties sharing a valid AFM (score 100), a whole-name search key
/// and a birth date (90) or only a whole-name key (60; 30 when both birth dates are known and differ). Merged parties, parties under a DSAR
/// restriction and parties with a merge waiting for approval are left out. The candidate list is computed on read: one SQL pass for the pairs, then
/// the P2 birth dates of the pairs are compared after decryption (never shown). TODO(hardening): materialise the queue incrementally on
/// create/update/import and keep steward "not a duplicate" decisions.
/// </summary>
internal sealed class DuplicateFinder(DbSession session, PartyDbContext db, RequestContext context, PartyProtection protection, PartyReader reader)
{
    /// <summary>Maximum pairs examined per call (the best are kept).</summary>
    private const int PairLimit = 1000;

    public async Task<(IReadOnlyList<DuplicateCandidate> Page, bool HasMore)> FindAsync(Guid? partyId, int minScore, int offset, int limit, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var pairs = (await connection.QueryAsync<PairRecord>(new CommandDefinition(
            $"""
            WITH live AS (
              SELECT p.party_id FROM pty.party p
               WHERE p.legal_entity_id = @le AND p.status IN ('PROSPECT', 'ACTIVE', 'INACTIVE')
                 AND NOT EXISTS (SELECT 1 FROM pty.dsar_restriction r WHERE r.party_id = p.party_id AND r.record_class = '{PartyDsarWork.PartyRecord}')
                 AND NOT EXISTS (SELECT 1 FROM pty.party_merge m WHERE m.status = 'PENDING_APPROVAL' AND (m.survivor_party_id = p.party_id OR m.merged_party_id = p.party_id))),
            names AS (
              SELECT DISTINCT k.party_id, k.search_key FROM pty.party_search_key k JOIN live USING (party_id)
               WHERE k.legal_entity_id = @le AND k.key_kind = 'FULL' AND k.recorded_to IS NULL),
            afm AS (
              SELECT DISTINCT i.party_id, i.value_blind_index FROM pty.party_identifier i JOIN live USING (party_id)
               WHERE i.legal_entity_id = @le AND i.scheme = 'AFM' AND i.valid_to IS NULL AND i.recorded_to IS NULL AND i.verification_status <> 'VERIFICATION_FAILED'),
            by_name AS (
              SELECT a.party_id AS a, b.party_id AS b FROM names a JOIN names b ON a.search_key = b.search_key AND a.party_id < b.party_id GROUP BY 1, 2),
            by_afm AS (
              SELECT a.party_id AS a, b.party_id AS b FROM afm a JOIN afm b ON a.value_blind_index = b.value_blind_index AND a.party_id < b.party_id GROUP BY 1, 2)
            SELECT coalesce(n.a, f.a) AS A, coalesce(n.b, f.b) AS B, (n.a IS NOT NULL) AS SameName, (f.a IS NOT NULL) AS SameAfm
              FROM by_name n FULL JOIN by_afm f ON n.a = f.a AND n.b = f.b
             WHERE (@party::uuid IS NULL OR coalesce(n.a, f.a) = @party OR coalesce(n.b, f.b) = @party)
             ORDER BY (f.a IS NOT NULL) DESC, coalesce(n.a, f.a), coalesce(n.b, f.b)
             LIMIT {PairLimit}
            """,
            new { le = legalEntity.Value, party = partyId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var birthDates = new Dictionary<Guid, BusinessDate?>();
        var scored = new List<(Guid A, Guid B, int Score, List<string> Reasons, string? Blocked)>();
        foreach (var pair in pairs)
        {
            var reasons = new List<string>();
            var score = 0;
            if (pair.SameAfm)
            {
                reasons.Add(DuplicateReasons.AfmExact);
                score = 100;
            }

            if (pair.SameName)
            {
                reasons.Add(DuplicateReasons.Name);
                var birthA = await BirthDateAsync(legalEntity, pair.A, birthDates, cancellationToken).ConfigureAwait(false);
                var birthB = await BirthDateAsync(legalEntity, pair.B, birthDates, cancellationToken).ConfigureAwait(false);
                if (birthA is { } a && birthB is { } b)
                {
                    if (a == b)
                    {
                        reasons.Add(DuplicateReasons.NameAndBirthDate);
                        score = Math.Max(score, 90);
                    }
                    else
                    {
                        reasons.Add(DuplicateReasons.BirthDateDiffers);
                        score = Math.Max(score, 30);
                    }
                }
                else
                {
                    score = Math.Max(score, 60);
                }
            }

            if (score < minScore)
            {
                continue;
            }

            var conflict = await HasIdentifierConflictAsync(legalEntity, pair.A, pair.B, cancellationToken).ConfigureAwait(false);
            scored.Add((pair.A, pair.B, score, reasons, conflict ? DuplicateReasons.IdentifierConflict : null));
        }

        var ordered = scored.OrderByDescending(s => s.Score).ThenBy(s => s.A).ThenBy(s => s.B).Skip(offset).Take(limit + 1).ToList();
        var pageItems = ordered.Take(limit).ToList();
        var ids = pageItems.SelectMany(s => new[] { s.A, s.B }).Distinct().ToList();
        var items = (await reader.HydrateAsync(legalEntity, [.. ids.Select(id => new SearchHit(id, 3, 1m))], cancellationToken).ConfigureAwait(false))
            .ToDictionary(i => i.PartyId.Value);
        var page = new List<DuplicateCandidate>();
        foreach (var s in pageItems)
        {
            if (!items.TryGetValue(s.A, out var itemA) || !items.TryGetValue(s.B, out var itemB))
            {
                continue;
            }

            page.Add(new DuplicateCandidate
            {
                CandidateKey = $"{s.A:N}-{s.B:N}", Score = s.Score, Reasons = s.Reasons, PartyA = itemA, PartyB = itemB, MergeBlockedReason = s.Blocked,
            });
        }

        return (page, ordered.Count > limit);
    }

    /// <summary>
    /// True when both parties hold a valid AFM and no AFM is shared: two parties with different valid AFMs are never the same legal person
    /// (REQ-PTY-007). The plain values are compared in memory; they are not kept.
    /// </summary>
    public async Task<bool> HasIdentifierConflictAsync(SkLegalEntityId legalEntity, Guid partyA, Guid partyB, CancellationToken cancellationToken)
    {
        var a = await AfmValuesAsync(legalEntity, partyA, cancellationToken).ConfigureAwait(false);
        var b = await AfmValuesAsync(legalEntity, partyB, cancellationToken).ConfigureAwait(false);
        return a.Count > 0 && b.Count > 0 && !a.Overlaps(b);
    }

    private async Task<HashSet<string>> AfmValuesAsync(SkLegalEntityId legalEntity, Guid partyId, CancellationToken cancellationToken)
    {
        var id = new CoreIns.SharedKernel.Identifiers.PartyId(partyId);
        var rows = await db.Identifiers.AsNoTracking()
            .Where(i => i.PartyId == id && i.Scheme == "AFM" && i.ValidTo == null && i.RecordedTo == null && i.VerificationStatus != "VERIFICATION_FAILED")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            values.Add((await protection.DecryptIdentifierAsync(legalEntity, row.IdentifierId, row.ValueEncrypted, cancellationToken).ConfigureAwait(false)).Trim());
        }

        return values;
    }

    private async Task<BusinessDate?> BirthDateAsync(SkLegalEntityId legalEntity, Guid partyId, Dictionary<Guid, BusinessDate?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(partyId, out var cached))
        {
            return cached;
        }

        var envelope = await db.Parties.AsNoTracking().Where(p => p.PartyId == new CoreIns.SharedKernel.Identifiers.PartyId(partyId)).Select(p => p.BirthDateEncrypted)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        BusinessDate? date = envelope is null ? null : await protection.DecryptBirthDateAsync(legalEntity, partyId, envelope, cancellationToken).ConfigureAwait(false);
        cache[partyId] = date;
        return date;
    }

    private sealed record PairRecord(Guid A, Guid B, bool SameName, bool SameAfm);
}
