using System.Data;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Finance.Queries;

/// <summary><c>fin.PostingRules.list</c>: the rules of the rule-set versions in force on a date (REQ-FIN-048, -049, -052).</summary>
internal sealed class PostingRuleReader(DbSession session)
{
    public async Task<PostingRulesListPage> ListAsync(LegalEntityCode legalEntity, string? book, BusinessDate validAt, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var offset = cursor is null ? 0 : int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var o) && o >= 0
            ? o
            : throw new FormatException("Invalid cursor.");
        var args = new DynamicParameters(new { le = legalEntity.Value, book, offset, take = limit + 1 });
        args.Add("at", validAt.Value, DbType.Date);
        var rows = (await connection.QueryAsync<RuleRecord>(new CommandDefinition(
            """
            WITH in_force AS (
                SELECT DISTINCT ON (s.book) s.* FROM fin.posting_rule_set s
                 WHERE s.legal_entity_code = @le AND s.status = 'ACTIVE' AND s.effective_from <= @at AND (@book::text IS NULL OR s.book = @book)
                 ORDER BY s.book, s.effective_from DESC, s.version_no DESC)
            SELECT s.rule_set_id AS RuleSetId, s.version_no AS VersionNo, s.status AS Status, s.legal_entity_code AS LegalEntityCode, s.book AS Book,
                   s.effective_from::text AS EffectiveFrom, trim(s.content_hash) AS ContentHash, r.rule_code AS RuleCode, r.source_event AS SourceEvent,
                   r.entry_type AS EntryType, r.source_account AS SourceAccount, r.charge_category AS ChargeCategory, r.charge_type AS ChargeType,
                   r.specificity AS Specificity, r.account_code AS Account, r.derive_from AS DeriveFrom, r.description_el AS DescriptionEl, r.description_en AS DescriptionEn
              FROM in_force s JOIN fin.posting_rule r ON r.rule_set_id = s.rule_set_id
             ORDER BY s.book, r.source_event, r.entry_type, r.source_account, r.specificity DESC, r.rule_code
            OFFSET @offset LIMIT @take
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new PostingRulesListPage
        {
            Items = [.. rows.Select(r => new PostingRulesListItem
            {
                RuleSetId = r.RuleSetId,
                RuleSetVersion = r.VersionNo,
                RuleSetStatus = r.Status == "ACTIVE" ? PostingRulesListItem.RuleSetStatusValue.Active : PostingRulesListItem.RuleSetStatusValue.Superseded,
                LegalEntity = r.LegalEntityCode,
                Book = r.Book,
                EffectiveFrom = BusinessDate.Parse(r.EffectiveFrom),
                ContentHash = Sha256Hash.Parse(r.ContentHash),
                RuleCode = r.RuleCode,
                SourceEventType = r.SourceEvent,
                EntryType = r.EntryType,
                SourceAccount = r.SourceAccount,
                ChargeCategory = r.ChargeCategory,
                ChargeType = r.ChargeType,
                Specificity = r.Specificity,
                Account = r.Account,
                DeriveFrom = r.DeriveFrom is null ? null : PostingRulesListItem.DeriveFromValue.GlKey,
                Description = new FinLocalizedText { El = r.DescriptionEl, En = r.DescriptionEn },
            })],
            NextCursor = next,
            Limit = limit,
        };
    }

    private sealed class RuleRecord
    {
        public Guid RuleSetId { get; set; }

        public int VersionNo { get; set; }

        public string Status { get; set; } = string.Empty;

        public string LegalEntityCode { get; set; } = string.Empty;

        public string Book { get; set; } = string.Empty;

        public string EffectiveFrom { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public string RuleCode { get; set; } = string.Empty;

        public string SourceEvent { get; set; } = string.Empty;

        public string EntryType { get; set; } = string.Empty;

        public string SourceAccount { get; set; } = string.Empty;

        public string? ChargeCategory { get; set; }

        public string? ChargeType { get; set; }

        public int Specificity { get; set; }

        public string? Account { get; set; }

        public string? DeriveFrom { get; set; }

        public string DescriptionEl { get; set; } = string.Empty;

        public string DescriptionEn { get; set; } = string.Empty;
    }
}
