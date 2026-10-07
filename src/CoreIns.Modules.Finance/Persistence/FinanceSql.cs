using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Modules.Finance.Domain;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Finance.Persistence;

/// <summary>
/// Database objects EF Core cannot express, applied by the Finance migrations: the deferred double-entry constraint
/// (REQ-FIN-068), the append-only triggers on journals and rule rows (REQ-FIN-070), the rule-key uniqueness index
/// (REQ-FIN-049) and the versioned reference-data seed (chart, derivations, catalogue, rule set).
/// </summary>
internal static class FinanceSql
{
    /// <summary>Triggers, functions and indexes of the first Finance migration.</summary>
    public const string Objects = """
        -- REQ-FIN-068: debits = credits per journal, per transaction currency and in functional currency, checked at commit.
        CREATE FUNCTION fin.check_journal_balanced() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            jid uuid := NEW.journal_id;
            line_count int;
            unbalanced int;
        BEGIN
            SELECT count(*) INTO line_count FROM fin.journal_line WHERE journal_id = jid;
            IF line_count < 2 THEN
                RAISE EXCEPTION 'FIN-ERR-UNBALANCED: journal % has % line(s); a journal needs at least two', jid, line_count
                    USING ERRCODE = '23514';
            END IF;
            SELECT count(*) INTO unbalanced FROM (
                SELECT currency FROM fin.journal_line WHERE journal_id = jid GROUP BY currency
                HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0) t;
            IF unbalanced > 0 THEN
                RAISE EXCEPTION 'FIN-ERR-UNBALANCED: journal % debits differ from credits in a transaction currency', jid
                    USING ERRCODE = '23514';
            END IF;
            SELECT count(*) INTO unbalanced FROM (
                SELECT 1 FROM fin.journal_line WHERE journal_id = jid
                HAVING count(DISTINCT functional_currency) > 1
                    OR sum(CASE side WHEN 'DEBIT' THEN amount_functional ELSE -amount_functional END) <> 0) t;
            IF unbalanced > 0 THEN
                RAISE EXCEPTION 'FIN-ERR-UNBALANCED: journal % functional debits differ from credits', jid
                    USING ERRCODE = '23514';
            END IF;
            RETURN NULL;
        END $$;

        CREATE CONSTRAINT TRIGGER trg_journal_line_balanced AFTER INSERT ON fin.journal_line
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.check_journal_balanced();
        CREATE CONSTRAINT TRIGGER trg_journal_entry_balanced AFTER INSERT ON fin.journal_entry
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.check_journal_balanced();

        -- REQ-FIN-070: journals and rule rows are append-only for every role; a correction is a reversal journal or a
        -- new rule-set version. The app role also has no UPDATE/DELETE grant on these tables.
        CREATE FUNCTION fin.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'fin.% is append-only (REQ-FIN-070): % refused; post a reversal or a new version', TG_TABLE_NAME, TG_OP
                USING ERRCODE = '55000';
        END $$;

        CREATE TRIGGER trg_journal_entry_append_only BEFORE UPDATE OR DELETE ON fin.journal_entry
            FOR EACH ROW EXECUTE FUNCTION fin.forbid_change();
        CREATE TRIGGER trg_journal_entry_no_truncate BEFORE TRUNCATE ON fin.journal_entry
            FOR EACH STATEMENT EXECUTE FUNCTION fin.forbid_change();
        CREATE TRIGGER trg_journal_line_append_only BEFORE UPDATE OR DELETE ON fin.journal_line
            FOR EACH ROW EXECUTE FUNCTION fin.forbid_change();
        CREATE TRIGGER trg_journal_line_no_truncate BEFORE TRUNCATE ON fin.journal_line
            FOR EACH STATEMENT EXECUTE FUNCTION fin.forbid_change();
        CREATE TRIGGER trg_posting_rule_append_only BEFORE UPDATE OR DELETE ON fin.posting_rule
            FOR EACH ROW EXECUTE FUNCTION fin.forbid_change();

        -- REQ-FIN-002, -070: a line may only join a journal whose header this transaction (or one of its
        -- subtransactions) inserted, so a posted journal can never gain lines. A visible header whose inserting
        -- transaction is still in progress can only be our own (other transactions' uncommitted rows are invisible).
        CREATE FUNCTION fin.check_line_joins_open_journal() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            header_xmin bigint;
            top_xid bigint := pg_current_xact_id()::text::bigint;
            full_xid bigint;
            xact_status text;
        BEGIN
            SELECT xmin::text::bigint INTO header_xmin FROM fin.journal_entry WHERE journal_id = NEW.journal_id;
            IF header_xmin IS NOT NULL THEN
                -- Widen the 32-bit xmin to the 64-bit id nearest our own (live xids are within 2^31 of each other;
                -- our subtransactions' xids are just above our top-level id).
                full_xid := ((top_xid >> 32) << 32) + header_xmin;
                IF full_xid > top_xid + 2147483648 THEN
                    full_xid := full_xid - 4294967296;
                ELSIF full_xid < top_xid - 2147483648 THEN
                    full_xid := full_xid + 4294967296;
                END IF;
                IF full_xid >= 3 THEN
                    xact_status := pg_xact_status(full_xid::text::xid8);
                END IF;
            END IF;
            IF xact_status IS DISTINCT FROM 'in progress' THEN
                RAISE EXCEPTION 'FIN: journal % is posted or unknown; lines can only be added in the transaction that posts it (REQ-FIN-070)', NEW.journal_id
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_journal_line_open_journal BEFORE INSERT ON fin.journal_line
            FOR EACH ROW EXECUTE FUNCTION fin.check_line_joins_open_journal();

        -- REQ-FIN-073: only an original journal can be reversed; a reversal is never reversed.
        CREATE FUNCTION fin.check_reversal_target() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF EXISTS (SELECT 1 FROM fin.journal_entry WHERE journal_id = NEW.reverses_journal_id AND source_type = 'REVERSAL') THEN
                RAISE EXCEPTION 'FIN-ERR-ALREADY-REVERSED: journal % is itself a reversal and cannot be reversed', NEW.reverses_journal_id
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_journal_entry_reversal_target BEFORE INSERT ON fin.journal_entry
            FOR EACH ROW WHEN (NEW.reverses_journal_id IS NOT NULL) EXECUTE FUNCTION fin.check_reversal_target();

        -- REQ-FIN-069: journal numbers follow the PLT JOURNAL series (Platform:Numbering, technical default D-SLC-08:
        -- prefix JNL, yearly reset on the accounting date, 10 digits). Changing that series needs a new migration.
        ALTER TABLE fin.journal_entry ADD CONSTRAINT ck_journal_entry_number
            CHECK (journal_number ~ '^JNL[0-9]{14}$' AND substr(journal_number, 4, 4) = extract(year FROM accounting_date)::int::text);

        -- REQ-FIN-049: one rule per key and qualifier combination within a rule-set version.
        CREATE UNIQUE INDEX ux_posting_rule_key ON fin.posting_rule
            (rule_set_id, source_event, entry_type, source_account, coalesce(charge_category, ''), coalesce(charge_type, ''));
        """;

    /// <summary>Runs the deferred balance checks of the journals written so far now (inside the caller's savepoint), then defers them again.</summary>
    public const string CheckBalanceNow = """
        SET CONSTRAINTS fin.trg_journal_line_balanced, fin.trg_journal_entry_balanced IMMEDIATE;
        SET CONSTRAINTS fin.trg_journal_line_balanced, fin.trg_journal_entry_balanced DEFERRED;
        """;

    /// <summary>Drops what <see cref="Objects"/> created (migration Down).</summary>
    public const string DropObjects = """
        DROP INDEX IF EXISTS fin.ux_posting_rule_key;
        ALTER TABLE fin.journal_entry DROP CONSTRAINT IF EXISTS ck_journal_entry_number;
        DROP TRIGGER IF EXISTS trg_journal_entry_reversal_target ON fin.journal_entry;
        DROP TRIGGER IF EXISTS trg_journal_line_open_journal ON fin.journal_line;
        DROP FUNCTION IF EXISTS fin.check_reversal_target();
        DROP FUNCTION IF EXISTS fin.check_line_joins_open_journal();
        DROP TRIGGER IF EXISTS trg_posting_rule_append_only ON fin.posting_rule;
        DROP TRIGGER IF EXISTS trg_journal_line_no_truncate ON fin.journal_line;
        DROP TRIGGER IF EXISTS trg_journal_line_append_only ON fin.journal_line;
        DROP TRIGGER IF EXISTS trg_journal_entry_no_truncate ON fin.journal_entry;
        DROP TRIGGER IF EXISTS trg_journal_entry_append_only ON fin.journal_entry;
        DROP TRIGGER IF EXISTS trg_journal_entry_balanced ON fin.journal_entry;
        DROP TRIGGER IF EXISTS trg_journal_line_balanced ON fin.journal_line;
        DROP FUNCTION IF EXISTS fin.forbid_change();
        DROP FUNCTION IF EXISTS fin.check_journal_balanced();
        """;

    /// <summary>Privileges of the application role (REQ-FIN-070, -088): reference data read-only, journals insert-only.</summary>
    public static IReadOnlyList<string> Grants(string appRole) =>
    [
        $"REVOKE ALL ON ALL TABLES IN SCHEMA {FinanceModule.Schema} FROM {appRole}",
        $"GRANT SELECT ON fin.book_profile, fin.gl_account, fin.account_derivation, fin.event_catalogue, fin.posting_rule_set, fin.posting_rule TO {appRole}",
        $"GRANT SELECT, INSERT ON fin.journal_entry, fin.journal_line, fin.financial_period, fin.charge_type_view TO {appRole}",
        $"GRANT SELECT, INSERT, UPDATE ON fin.business_event, fin.policy_context TO {appRole}",
        $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {FinanceModule.Schema} TO {appRole}",
    ];
}

/// <summary>
/// The versioned Finance reference data shipped with the module (<c>Seed/*.json</c>, embedded). A seed file is
/// immutable once a migration applied it: a change is a new file, a new rule-set version and a new migration.
/// </summary>
internal static class FinanceSeed
{
    /// <summary>The seed the first migration applies.</summary>
    public const string GrTestV1 = "gr-test.finance.v1";

    /// <summary>The raw JSON of a seed.</summary>
    public static string Json(string name)
    {
        var resource = $"CoreIns.Modules.Finance.Seed.{name}.json";
        using var stream = typeof(FinanceSeed).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource {resource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The posting rules of a seed's rule set, as the domain sees them.</summary>
    public static IReadOnlyList<PostingRule> Rules(string name) =>
        [.. Root(name)["ruleSet"]!["rules"]!.AsArray().Select(r => Rule(r!))];

    /// <summary>The content hash of a seed's rule set: SHA-256 of the canonical JSON of its rules (REQ-FIN-052).</summary>
    public static string RuleSetHash(string name) => CanonicalJson.Hash(Root(name)["ruleSet"]!["rules"]).Value;

    /// <summary>Chart accounts of a seed (code set).</summary>
    public static IReadOnlySet<string> ChartCodes(string name) =>
        Root(name)["chart"]!["accounts"]!.AsArray().Select(a => a!["code"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    /// <summary>GL key derivations of a seed.</summary>
    public static IReadOnlyDictionary<string, string> Derivations(string name) =>
        Root(name)["derivations"]!["rows"]!.AsArray().ToDictionary(r => r!["glKey"]!.GetValue<string>(), r => r!["account"]!.GetValue<string>(), StringComparer.Ordinal);

    /// <summary>INSERT statements loading a seed (idempotent: ON CONFLICT DO NOTHING).</summary>
    public static string Sql(string name)
    {
        var root = Root(name);
        var le = root["legalEntity"]!.GetValue<string>();
        var sql = new StringBuilder();
        var profile = root["bookProfile"]!;
        sql.AppendLine(Invariant($"""
            INSERT INTO fin.book_profile (legal_entity_code, functional_currency, statutory_book, active_books, source)
            VALUES ({Q(le)}, {Q(Str(profile, "functionalCurrency"))}, {Q(Str(profile, "statutoryBook"))}, ARRAY[{string.Join(", ", profile["activeBooks"]!.AsArray().Select(b => Q(b!.GetValue<string>())))}]::text[], {Q(Str(profile, "source"))})
            ON CONFLICT DO NOTHING;
            """));

        var chartBook = Str(root["chart"]!, "book");
        foreach (var a in root["chart"]!["accounts"]!.AsArray().Select(x => x!))
        {
            sql.AppendLine(Invariant($"""
                INSERT INTO fin.gl_account (legal_entity_code, book, account_code, name_el, name_en, account_type, normal_balance, code_origin, system_only, status, source)
                VALUES ({Q(le)}, {Q(chartBook)}, {Q(Str(a, "code"))}, {Q(Str(a, "el"))}, {Q(Str(a, "en"))}, {Q(Str(a, "type"))}, {Q(Str(a, "normalBalance"))}, {Q(Str(a, "origin"))}, {(a["systemOnly"]?.GetValue<bool>() == true ? "true" : "false")}, 'ACTIVE', {Q(Str(a, "source"))})
                ON CONFLICT DO NOTHING;
                """));
        }

        var derivations = root["derivations"]!;
        foreach (var d in derivations["rows"]!.AsArray().Select(x => x!))
        {
            sql.AppendLine(Invariant($"""
                INSERT INTO fin.account_derivation (legal_entity_code, book, gl_key, account_code, valid_from, valid_to)
                VALUES ({Q(le)}, {Q(Str(derivations, "book"))}, {Q(Str(d, "glKey"))}, {Q(Str(d, "account"))}, DATE {Q(Str(derivations, "validFrom"))}, NULL)
                ON CONFLICT DO NOTHING;
                """));
        }

        foreach (var c in root["catalogue"]!.AsArray().Select(x => x!))
        {
            sql.AppendLine(Invariant($"""
                INSERT INTO fin.event_catalogue (registry_name, schema_major, relevance, amount_fields, note)
                VALUES ({Q(Str(c, "event"))}, {c["major"]!.GetValue<int>()}, {Q(Str(c, "relevance"))}, {Q(Str(c, "amountFields"))}, {Q(Str(c, "note"))})
                ON CONFLICT DO NOTHING;
                """));
        }

        var ruleSet = root["ruleSet"]!;
        var book = Str(ruleSet, "book");
        var version = ruleSet["version"]!.GetValue<int>();
        var ruleSetId = DeterministicId($"{le}/{book}/rule-set/{version}");
        sql.AppendLine(Invariant($"""
            INSERT INTO fin.posting_rule_set (rule_set_id, legal_entity_code, book, version_no, status, effective_from, content_hash, source, created_at)
            VALUES ('{ruleSetId}', {Q(le)}, {Q(book)}, {version}, 'ACTIVE', DATE {Q(Str(ruleSet, "effectiveFrom"))}, {Q(RuleSetHash(name))}, {Q("seed " + name)}, TIMESTAMPTZ '2026-10-07 00:00:00+00')
            ON CONFLICT DO NOTHING;
            """));
        foreach (var rule in Rules(name))
        {
            var ruleId = DeterministicId($"{le}/{book}/rule-set/{version}/{rule.Code}");
            sql.AppendLine(Invariant($"""
                INSERT INTO fin.posting_rule (rule_id, rule_set_id, rule_code, source_event, entry_type, source_account, charge_category, charge_type, specificity, account_code, derive_from, description_el, description_en)
                VALUES ('{ruleId}', '{ruleSetId}', {Q(rule.Code)}, {Q(rule.SourceEvent)}, {Q(rule.EntryType)}, {Q(rule.SourceAccount)}, {QN(rule.ChargeCategory)}, {QN(rule.ChargeType)}, {rule.Specificity}, {QN(rule.Account)}, {QN(rule.DeriveFrom)}, {Q(rule.DescriptionEl)}, {Q(rule.DescriptionEn)})
                ON CONFLICT DO NOTHING;
                """));
        }

        return sql.ToString();
    }

    private static JsonNode Root(string name) => JsonNode.Parse(Json(name)) ?? throw new InvalidOperationException($"Seed {name} is empty.");

    private static PostingRule Rule(JsonNode r) => new(
        Str(r, "code"), Str(r, "sourceEvent"), Str(r, "entryType"), Str(r, "sourceAccount"),
        r["chargeCategory"]?.GetValue<string>(), r["chargeType"]?.GetValue<string>(), r["account"]?.GetValue<string>(),
        r["deriveFrom"]?.GetValue<string>(), Str(r, "el"), Str(r, "en"));

    private static string Str(JsonNode node, string property) =>
        node[property]?.GetValue<string>() ?? throw new InvalidOperationException($"Seed property '{property}' is missing.");

    private static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string QN(string? value) => value is null ? "NULL" : Q(value);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>A stable UUID (version 8, name-based) so the seed inserts the same ids everywhere.</summary>
    internal static Guid DeterministicId(string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("coreins.fin.seed:" + name))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
