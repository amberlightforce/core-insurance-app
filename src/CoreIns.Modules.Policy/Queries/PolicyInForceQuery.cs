using CoreIns.Modules.Policy.Contracts;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>
/// <see cref="IPolicyInForceQuery"/> over the current rows: a policy counts when its current term is Scheduled-and-started, InForce or
/// PendingCancellation and the term period covers now, and a current segment's risk matches the postcodes. Read only.
/// <para>TODO(hardening): hazard zones are not stored on the risk; only postcodes match. Index the postcode (generated column) when volumes need it.</para>
/// </summary>
internal sealed class PolicyInForceQuery(DbSession session, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock) : IPolicyInForceQuery
{
    public async Task<InForceCount> CountAsync(InForceCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var patterns = criteria.Postcodes.Select(Pattern).ToArray();
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Product, int Count)>(new CommandDefinition(
            """
            SELECT p.product_code AS Product, count(DISTINCT p.policy_id)::int AS Count
              FROM pol.policy p
              JOIN pol.policy_term t ON t.policy_id = p.policy_id AND t.recorded_to IS NULL
               AND t.state IN ('SCHEDULED', 'IN_FORCE', 'PENDING_CANCELLATION') AND t.valid_from <= @now AND t.valid_to > @now
              JOIN pol.segment s ON s.term_id = t.term_id AND s.recorded_to IS NULL AND s.valid_from <= @now AND s.valid_to > @now
             WHERE p.legal_entity_id = @legalEntity
               AND (cardinality(@products) = 0 OR p.product_code = ANY(@products))
               AND (cardinality(@patterns) = 0 OR EXISTS (
                     SELECT 1 FROM jsonb_path_query(s.snapshot, '$.vehicles[*].fields.garagingPostcode') AS pc(v)
                      WHERE (pc.v #>> '{}') LIKE ANY(@patterns)))
             GROUP BY p.product_code
            """,
            new { legalEntity, now = clock.Now.ToUtcDateTime(), products = criteria.ProductCodes.ToArray(), patterns },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var byProduct = rows.ToDictionary(r => r.Product, r => r.Count, StringComparer.Ordinal);
        return new InForceCount(byProduct.Values.Sum(), byProduct);
    }

    /// <summary>A LIKE pattern: the value with LIKE metacharacters escaped, and a trailing <c>*</c> turned into a prefix match.</summary>
    internal static string Pattern(string postcode)
    {
        var prefix = postcode.EndsWith('*');
        var text = (prefix ? postcode[..^1] : postcode).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return prefix ? text + "%" : text;
    }
}
