using System.Globalization;
using System.Text;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.DataProtection;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Dapper;
using SkLegalEntityId = CoreIns.SharedKernel.Identifiers.LegalEntityId;

namespace CoreIns.Modules.Party.Queries;

/// <summary>Criteria of <c>pty.Party.search</c> (REQ-PTY-068 subset).</summary>
internal sealed record SearchCriteria(string? Criteria, string? Name, string? IdentifierScheme, string? IdentifierValue, string? PartyNumber, int Limit, int Offset);

/// <summary>
/// <c>pty.Party.search</c> (REQ-PTY-001, REQ-PTY-060, REQ-PTY-065..069): names in any script, accent-, case- and
/// script-insensitive through stored keys (language rules + transliteration variants) and pg_trgm; exact identifier
/// matches through the blind index; party numbers exactly. Exact and prefix matches rank before fuzzy ones; ties sort
/// by the Greek collation <c>el_gr_ci_ai</c> (infra/database/greek-search.sql, NFR-PTY-013), then party id.
/// </summary>
internal sealed class PartySearch(
    DbSession session,
    RequestContext context,
    PartyProtection protection,
    PartyReader reader,
    NameForms names,
    IIdValidator idValidator)
{
    /// <summary>Minimum query length (PTY-ERR-QUERY-TOO-SHORT).</summary>
    public const int MinimumQueryLength = 2;

    public async Task<Result<PartySearchPage>> SearchAsync(SearchCriteria criteria, CancellationToken cancellationToken)
    {
        var text = (criteria.Name ?? criteria.Criteria)?.Trim();
        var hasIdentifier = !string.IsNullOrWhiteSpace(criteria.IdentifierScheme) && !string.IsNullOrWhiteSpace(criteria.IdentifierValue);
        if (!hasIdentifier && string.IsNullOrWhiteSpace(criteria.PartyNumber) && (text is null || text.Length < MinimumQueryLength))
        {
            return DomainError.Of(ModuleCode.PTY, "QUERY-TOO-SHORT", $"Give at least {MinimumQueryLength} characters, a party number or an identifier.");
        }

        var legalEntity = protection.Current(context);
        var hits = new Dictionary<Guid, SearchHit>();
        void Add(IEnumerable<SearchHit> found)
        {
            foreach (var hit in found)
            {
                if (!hits.TryGetValue(hit.PartyId, out var existing) || (hit.Tier, hit.Similarity).CompareTo((existing.Tier, existing.Similarity)) > 0)
                {
                    hits[hit.PartyId] = hit;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.PartyNumber))
        {
            Add(await ByNumberAsync(legalEntity, criteria.PartyNumber.Trim(), cancellationToken).ConfigureAwait(false));
        }

        if (hasIdentifier)
        {
            Add(await ByIdentifierAsync(legalEntity, criteria.IdentifierScheme!.Trim(), criteria.IdentifierValue!, cancellationToken).ConfigureAwait(false));
        }

        if (criteria.Criteria is { } box && !string.IsNullOrWhiteSpace(box) && criteria.Name is null && IsNumberLike(box))
        {
            // Single search box: a party number or any identifier of the pack's catalogue (SCR-PTY-01 number-format detection).
            Add(await ByNumberAsync(legalEntity, box.Trim(), cancellationToken).ConfigureAwait(false));
            foreach (var scheme in idValidator.Schemes.Where(s => s != IdScheme.VehiclePlate))
            {
                Add(await ByIdentifierAsync(legalEntity, scheme.Code, box, cancellationToken).ConfigureAwait(false));
            }
        }

        if (text is { Length: >= MinimumQueryLength } && (criteria.Name is not null || !IsNumberLike(text) || hits.Count == 0))
        {
            Add(await ByNameAsync(legalEntity, text, cancellationToken).ConfigureAwait(false));
        }

        var ordered = await OrderAsync(legalEntity, hits.Values, cancellationToken).ConfigureAwait(false);
        var page = ordered.Skip(criteria.Offset).Take(criteria.Limit + 1).ToList();
        var items = await reader.HydrateAsync(legalEntity, page.Take(criteria.Limit).ToList(), cancellationToken).ConfigureAwait(false);
        return new PartySearchPage
        {
            Items = items,
            NextCursor = page.Count > criteria.Limit ? EncodeCursor(criteria.Offset + criteria.Limit) : null,
            Limit = criteria.Limit,
        };
    }

    /// <summary>Opaque offset cursor (ranked results have no stable key range).</summary>
    public static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes("o:" + offset.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Decodes a cursor; 0 when absent, null when malformed.</summary>
    public static int? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                ? offset
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsNumberLike(string text) => text.Any(char.IsAsciiDigit) && !text.Trim().Contains(' ', StringComparison.Ordinal);

    private async Task<IEnumerable<SearchHit>> ByNumberAsync(SkLegalEntityId legalEntity, string number, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT party_id FROM pty.party WHERE legal_entity_id = @le AND party_number = @number",
            new { le = legalEntity.Value, number }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return ids.Select(id => new SearchHit(id, 3, 1m));
    }

    /// <summary>Exact identifier match through the blind index, under every readable key version (REQ-PTY-060).</summary>
    public async Task<IReadOnlyList<SearchHit>> ByIdentifierAsync(SkLegalEntityId legalEntity, string scheme, string value, CancellationToken cancellationToken)
    {
        string normalised;
        try
        {
            var validation = await idValidator.ValidateAsync(new IdScheme(scheme), value, IdValidationContext.None, cancellationToken).ConfigureAwait(false);
            if (!validation.IsAccepted || validation.Normalised is null)
            {
                return [];
            }

            normalised = validation.Normalised;
        }
        catch (SpiException ex) when (ex.Category == SpiErrorCategory.NotApplicable)
        {
            normalised = BlindIndexNormalisers.Identifier(value);
        }

        var candidates = await protection.IndexCandidatesAsync(legalEntity, scheme, normalised, cancellationToken).ConfigureAwait(false);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT DISTINCT party_id FROM pty.party_identifier
             WHERE legal_entity_id = @le AND scheme = @scheme AND value_blind_index = ANY(@candidates) AND valid_to IS NULL AND recorded_to IS NULL
            """,
            new { le = legalEntity.Value, scheme, candidates = candidates.ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. ids.Select(id => new SearchHit(id, 3, 1m))];
    }

    /// <summary>
    /// Every query part must match one stored part key of the party (exact, prefix or trigram-similar to one of the
    /// part's keys or transliteration variants). The party's tier is its weakest part; its similarity the mean.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> ByNameAsync(SkLegalEntityId legalEntity, string query, CancellationToken cancellationToken)
    {
        var parts = names.QueryParts(query);
        if (parts.Count == 0)
        {
            return [];
        }

        var args = new DynamicParameters(new { le = legalEntity.Value });
        var ctes = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var keys = (await names.QueryPartKeysAsync(parts[i], cancellationToken).ConfigureAwait(false)).ToArray();
            args.Add($"k{i}", keys);
            args.Add($"l{i}", keys.Select(key => NameForms.EscapeLike(key) + "%").ToArray());
            ctes.Add($"""
                p{i} AS (
                  SELECT k.party_id,
                         max(CASE WHEN k.search_key = q.key THEN 3 WHEN k.search_key LIKE q.pattern THEN 2 ELSE 1 END) AS tier,
                         max(similarity(k.search_key, q.key))::numeric AS sim
                    FROM pty.party_search_key k
                    JOIN unnest(@k{i}::text[], @l{i}::text[]) AS q(key, pattern) ON (k.search_key LIKE q.pattern OR k.search_key % q.key)
                   WHERE k.legal_entity_id = @le AND k.recorded_to IS NULL AND k.key_kind = 'PART'
                   GROUP BY k.party_id)
                """);
        }

        var join = string.Join(" ", Enumerable.Range(1, parts.Count - 1).Select(i => $"JOIN p{i} USING (party_id)"));
        var tier = parts.Count == 1 ? "p0.tier" : $"least({string.Join(", ", Enumerable.Range(0, parts.Count).Select(i => $"p{i}.tier"))})";
        var sim = $"round(({string.Join(" + ", Enumerable.Range(0, parts.Count).Select(i => $"p{i}.sim"))}) / {parts.Count}, 4)";
        var sql = $"WITH {string.Join(",\n", ctes)}\nSELECT p0.party_id AS PartyId, {tier} AS Tier, {sim} AS Similarity FROM p0 {join} LIMIT 500";

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<HitRecord>(new CommandDefinition(sql, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new SearchHit(r.PartyId, r.Tier, r.Similarity))];
    }

    /// <summary>Ranks: tier, similarity, then the native display name in Greek collation, then id (stable paging).</summary>
    private async Task<IReadOnlyList<SearchHit>> OrderAsync(SkLegalEntityId legalEntity, IEnumerable<SearchHit> hits, CancellationToken cancellationToken)
    {
        var list = hits.ToList();
        if (list.Count <= 1)
        {
            return list;
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var ordered = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT h.party_id
              FROM unnest(@ids, @tiers, @sims) AS h(party_id, tier, sim)
              LEFT JOIN LATERAL (
                   SELECT coalesce(organisation_name, concat_ws(' ', family_name, given_names)) AS display
                     FROM pty.party_name n WHERE n.party_id = h.party_id AND n.legal_entity_id = @le AND n.form = 'NATIVE' AND n.recorded_to IS NULL
                    LIMIT 1) n ON true
             ORDER BY h.tier DESC, h.sim DESC, n.display COLLATE public.el_gr_ci_ai, h.party_id
            """,
            new
            {
                le = legalEntity.Value,
                ids = list.Select(h => h.PartyId).ToArray(),
                tiers = list.Select(h => h.Tier).ToArray(),
                sims = list.Select(h => h.Similarity).ToArray(),
            },
            session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var byId = list.ToDictionary(h => h.PartyId);
        return [.. ordered.Select(id => byId[id])];
    }

    private sealed record HitRecord(Guid PartyId, int Tier, decimal Similarity);
}
