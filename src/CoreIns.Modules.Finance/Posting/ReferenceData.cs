using System.Data;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>A legal entity's book profile (REQ-FIN-089).</summary>
internal sealed record BookProfile(string LegalEntityCode, Currency FunctionalCurrency, string StatutoryBook, IReadOnlyList<string> ActiveBooks);

/// <summary>
/// Reads FIN reference data (book profile, rule set in force, chart, GL-key derivations, charge-type view, policy
/// context) with Dapper on the scope's connection and transaction. Intake reads only FIN's own tables (REQ-FIN-045).
/// </summary>
internal sealed class ReferenceData(DbSession session)
{
    public async Task<BookProfile?> BookProfileAsync(string legalEntityCode, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRecord>(new CommandDefinition(
            """
            SELECT legal_entity_code AS LegalEntityCode, functional_currency AS FunctionalCurrency, statutory_book AS StatutoryBook, active_books AS ActiveBooks
              FROM fin.book_profile WHERE legal_entity_code = @le
            """, new { le = legalEntityCode }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : new BookProfile(row.LegalEntityCode, Currency.FromCode(row.FunctionalCurrency.Trim()), row.StatutoryBook, row.ActiveBooks);
    }

    /// <summary>The book's setup on <paramref name="at"/>: the newest Active rule-set version effective on that date, the chart and derivations; null without a rule set.</summary>
    public async Task<BookSetup?> BookSetupAsync(BookProfile profile, string book, BusinessDate at, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { le = profile.LegalEntityCode, book });
        args.Add("at", at.Value, DbType.Date);
        var set = await connection.QuerySingleOrDefaultAsync<RuleSetRecord>(new CommandDefinition(
            """
            SELECT rule_set_id AS RuleSetId, version_no AS VersionNo, effective_from::text AS EffectiveFrom, content_hash AS ContentHash
              FROM fin.posting_rule_set
             WHERE legal_entity_code = @le AND book = @book AND status = 'ACTIVE' AND effective_from <= @at
             ORDER BY effective_from DESC, version_no DESC LIMIT 1
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        args.Add("set", set.RuleSetId);
        var rules = await connection.QueryAsync<RuleRecord>(new CommandDefinition(
            """
            SELECT rule_code AS Code, source_event AS SourceEvent, entry_type AS EntryType, source_account AS SourceAccount,
                   charge_category AS ChargeCategory, charge_type AS ChargeType, account_code AS Account, derive_from AS DeriveFrom,
                   description_el AS DescriptionEl, description_en AS DescriptionEn
              FROM fin.posting_rule WHERE rule_set_id = @set ORDER BY rule_code
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var accounts = await connection.QueryAsync<AccountRecord>(new CommandDefinition(
            """
            SELECT account_code AS Code, name_el AS NameEl, name_en AS NameEn, status AS Status
              FROM fin.gl_account WHERE legal_entity_code = @le AND book = @book
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var derivations = await connection.QueryAsync<(string GlKey, string Account)>(new CommandDefinition(
            """
            SELECT gl_key, account_code FROM fin.account_derivation
             WHERE legal_entity_code = @le AND book = @book AND valid_from <= @at AND (valid_to IS NULL OR valid_to > @at)
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var ruleSet = new RuleSet(set.RuleSetId, profile.LegalEntityCode, book, set.VersionNo, BusinessDate.Parse(set.EffectiveFrom),
            Sha256Hash.Parse(set.ContentHash.Trim()),
            [.. rules.Select(r => new PostingRule(r.Code, r.SourceEvent, r.EntryType, r.SourceAccount, r.ChargeCategory, r.ChargeType, r.Account, r.DeriveFrom, r.DescriptionEl, r.DescriptionEn))]);
        return new BookSetup(
            book,
            profile.FunctionalCurrency,
            ruleSet,
            accounts.ToDictionary(a => a.Code, a => new ChartAccount(a.Code, a.NameEl, a.NameEn, a.Status == "ACTIVE"), StringComparer.Ordinal),
            derivations.ToDictionary(d => d.GlKey, d => d.Account, StringComparer.Ordinal));
    }

    /// <summary>GL keys of the charge types of the given artefacts: (artefact hash, charge type) → GL key.</summary>
    public async Task<IReadOnlyDictionary<(string Hash, string ChargeType), string>> GlKeysAsync(IReadOnlyCollection<string> hashes, CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            return new Dictionary<(string, string), string>();
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Hash, string ChargeType, string GlKey)>(new CommandDefinition(
            "SELECT artefact_hash, charge_type, gl_key FROM fin.charge_type_view WHERE artefact_hash = ANY(@hashes)",
            new { hashes = hashes.ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToDictionary(r => (r.Hash.Trim(), r.ChargeType), r => r.GlKey);
    }

    /// <summary>The policy context of a term (or, without a term, of the policy's latest term).</summary>
    public async Task<PolicyContext?> PolicyContextAsync(LegalEntityId legalEntity, Guid? termId, Guid? policyId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<PolicyContextRecord>(new CommandDefinition(
            """
            SELECT policy_id AS PolicyId, policy_number AS PolicyNumber, policy_term_id AS TermId, transaction_id AS TransactionId,
                   product_code AS ProductCode, product_version AS ProductVersion, trim(artefact_hash) AS ArtefactHash,
                   charge_types_loaded AS ChargeTypesLoaded
              FROM fin.policy_context
             WHERE legal_entity_id = @le AND (@term::uuid IS NULL OR policy_term_id = @term) AND (@policy::uuid IS NULL OR policy_id = @policy)
             ORDER BY recorded_at DESC LIMIT 1
            """, new { le = legalEntity.Value, term = termId, policy = policyId }, session.Transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false) is { } r
            ? new PolicyContext(r.PolicyId, r.PolicyNumber, r.TermId, r.TransactionId, r.ProductCode, r.ProductVersion, r.ArtefactHash, r.ChargeTypesLoaded)
            : null;
    }

    /// <summary>
    /// Serialises intake decisions on one dependency (e.g. <c>term:&lt;id&gt;</c>) until commit, so an event that decides to
    /// wait and the context event that releases waiters cannot miss each other (REQ-FIN-040).
    /// </summary>
    public async Task LockAsync(string dependency, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key = "fin:" + dependency }, session.Transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private sealed class ProfileRecord
    {
        public string LegalEntityCode { get; set; } = string.Empty;

        public string FunctionalCurrency { get; set; } = string.Empty;

        public string StatutoryBook { get; set; } = string.Empty;

        public string[] ActiveBooks { get; set; } = [];
    }

    private sealed class RuleSetRecord
    {
        public Guid RuleSetId { get; set; }

        public int VersionNo { get; set; }

        public string EffectiveFrom { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;
    }

    private sealed class RuleRecord
    {
        public string Code { get; set; } = string.Empty;

        public string SourceEvent { get; set; } = string.Empty;

        public string EntryType { get; set; } = string.Empty;

        public string SourceAccount { get; set; } = string.Empty;

        public string? ChargeCategory { get; set; }

        public string? ChargeType { get; set; }

        public string? Account { get; set; }

        public string? DeriveFrom { get; set; }

        public string DescriptionEl { get; set; } = string.Empty;

        public string DescriptionEn { get; set; } = string.Empty;
    }

    private sealed class AccountRecord
    {
        public string Code { get; set; } = string.Empty;

        public string NameEl { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }

    private sealed class PolicyContextRecord
    {
        public Guid PolicyId { get; set; }

        public string PolicyNumber { get; set; } = string.Empty;

        public Guid TermId { get; set; }

        public Guid TransactionId { get; set; }

        public string ProductCode { get; set; } = string.Empty;

        public string ProductVersion { get; set; } = string.Empty;

        public string ArtefactHash { get; set; } = string.Empty;

        public bool ChargeTypesLoaded { get; set; }
    }
}
