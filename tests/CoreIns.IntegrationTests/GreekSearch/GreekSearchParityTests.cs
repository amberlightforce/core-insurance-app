using CoreIns.CountryPacks.GR.Language;
using CoreIns.CountryPacks.GR.Transliteration;
using Npgsql;

namespace CoreIns.IntegrationTests.GreekSearch;

/// <summary>
/// infra/database/greek-search.sql against PostgreSQL 17: the SQL search key equals the C# key of the Greece pack
/// (REQ-MKT-178, REQ-PTY-065) on a corpus of more than 200 Greek and Latin names, the Greek collation ignores case and
/// accents and orders digits numerically (NFR-PTY-013), and the text-search configuration folds accents.
/// </summary>
public sealed class GreekSearchParityTests(SearchDatabaseFixture database) : IClassFixture<SearchDatabaseFixture>
{
    [Fact]
    public void Corpus_has_at_least_200_names() => NameCorpus.All.Count.ShouldBeGreaterThanOrEqualTo(200);

    [Fact]
    public async Task Sql_search_key_equals_the_csharp_search_key_for_the_whole_corpus()
    {
        var sqlKeys = await SqlKeysAsync(NameCorpus.All);

        var mismatches = NameCorpus.All
            .Select((name, index) => (name, csharp: GreekSearchNormalizer.SearchKey(name), sql: sqlKeys[index]))
            .Where(entry => !string.Equals(entry.csharp, entry.sql, StringComparison.Ordinal))
            .Select(entry => $"'{entry.name}': C# '{entry.csharp}' vs SQL '{entry.sql}'")
            .ToList();

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sql_search_key_equals_the_csharp_key_for_transliteration_variants()
    {
        var variants = NameCorpus.All.SelectMany(ElotTransliterator.SearchVariants).Distinct(StringComparer.Ordinal).ToList();

        var sqlKeys = await SqlKeysAsync(variants);

        // Variants are already keys: the SQL function must leave them unchanged (idempotence across the two sides).
        variants.Where((variant, index) => !string.Equals(variant, sqlKeys[index], StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Παπαδόπουλος", "ΠΑΠΑΔΟΠΟΥΛΟΣ")]
    [InlineData("Σωτηρόπουλος", "σωτηροπουλος")]
    [InlineData("Ευθυμίου", "ευθυμιου")]
    [InlineData("ΑΪΣΕ", "Αϊσέ")]
    public async Task Greek_collation_ignores_case_accents_and_final_sigma(string left, string right)
    {
        var equal = await ScalarAsync<bool>("SELECT @a = @b COLLATE public.el_gr_ci_ai", ("a", left), ("b", right));
        equal.ShouldBeTrue();
    }

    [Fact]
    public async Task Greek_collation_sorts_accented_letters_with_their_base_and_digits_numerically()
    {
        string[] input = ["ΑΣΦ-10", "Ωμέγα", "άλφα", "ΑΣΦ-2", "Βήτα", "αλφάβητο"];
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT v FROM unnest(@values) AS t(v) ORDER BY v COLLATE public.el_gr_ci_ai", connection);
        command.Parameters.AddWithValue("values", input);
        var sorted = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                sorted.Add(reader.GetString(0));
            }
        }

        sorted.ShouldBe(["άλφα", "αλφάβητο", "ΑΣΦ-2", "ΑΣΦ-10", "Βήτα", "Ωμέγα"]);

        // The application comparer of the Greece pack agrees with the database collation on this input.
        input.Order(new GreekLanguageRules().SortComparer).ShouldBe(sorted);
    }

    [Fact]
    public async Task Text_search_matches_across_accents_and_case()
    {
        var matches = await ScalarAsync<bool>(
            "SELECT public.coreins_search_tsvector(@stored) @@ plainto_tsquery('public.greek_unaccent_v1', public.coreins_search_key(@query))",
            ("stored", "Λεωφ. Κηφισίας 124, 11526 Αθήνα"),
            ("query", "ΚΗΦΙΣΙΑΣ αθηνα"));
        matches.ShouldBeTrue();
    }

    [Fact]
    public async Task Search_key_function_is_usable_in_an_expression_index()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE TEMP TABLE party_name_probe (name text);
            CREATE INDEX party_name_probe_key_idx ON party_name_probe (public.coreins_search_key(name));
            INSERT INTO party_name_probe VALUES ('Παπαδόπουλος'), ('Παπαδάκης');
            """,
            connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await using var query = new NpgsqlCommand(
            "SELECT count(*) FROM party_name_probe WHERE public.coreins_search_key(name) = public.coreins_search_key(@q)", connection);
        query.Parameters.AddWithValue("q", "ΠΑΠΑΔΟΠΟΥΛΟΣ");
        (await query.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1L);

        // Review F-1e m3: the planner can use the expression index (sequential scans disabled to make the choice
        // independent of the tiny table size).
        await using (var settings = new NpgsqlCommand("ANALYZE party_name_probe; SET enable_seqscan = off;", connection))
        {
            await settings.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var explain = new NpgsqlCommand(
            "EXPLAIN SELECT * FROM party_name_probe WHERE public.coreins_search_key(name) = public.coreins_search_key('ΠΑΠΑΔΟΠΟΥΛΟΣ')",
            connection);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                plan.Add(reader.GetString(0));
            }
        }

        string.Join('\n', plan).ShouldContain("party_name_probe_key_idx");
    }

    [Fact]
    public async Task Text_search_configuration_name_is_versioned()
    {
        var exists = await ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_ts_config WHERE cfgname = @name AND cfgnamespace = 'public'::regnamespace)",
            ("name", "greek_unaccent_v1"));
        exists.ShouldBeTrue();
    }

    private async Task<List<string>> SqlKeysAsync(IReadOnlyList<string> values)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT public.coreins_search_key(v) FROM unnest(@values) WITH ORDINALITY AS t(v, n) ORDER BY n", connection);
        command.Parameters.AddWithValue("values", values.ToArray());
        var keys = new List<string>(values.Count);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        keys.Count.ShouldBe(values.Count);
        return keys;
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, string Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
