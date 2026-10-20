using System.Text;
using CoreIns.Platform.Events;
using CoreIns.Testing.Contracts;
using Npgsql;

namespace CoreIns.IntegrationTests;

/// <summary>Events emitted by a test class break their schemas.</summary>
internal sealed class EventSchemaDriftException(string message) : Exception(message);

/// <summary>
/// NX-TST-05: every event an integration test class actually emitted (plt.outbox_message plus plt.event_archive) must
/// validate against its schema in contracts/events. <see cref="PostgresFixture"/> runs this when it is disposed, so a
/// drifting event fails the test class that produced it. Known drift is listed in <c>EventSchemaAllowList.txt</c>
/// (one <c>EventType key</c> per line, keys as printed in the failure message); new drift fails.
/// </summary>
internal static class EventSchemaDriftCheck
{
    private static readonly Lazy<HashSet<string>> AllowList = new(LoadAllowList);

    /// <summary>Violations (not on the allow-list) among the events in the database; empty when clean.</summary>
    public static async Task<IReadOnlyList<string>> FindAsync(string connectionString, CancellationToken cancellationToken)
    {
        var validator = EventSchemaValidator.ForRepository;
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<Guid>();

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        foreach (var table in new[] { "plt.outbox_message", "plt.event_archive" })
        {
            await using var command = dataSource.CreateCommand($"SELECT {EnvelopeColumnsReader.Columns} FROM {table}");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var envelope = EnvelopeColumnsReader.Read(reader);
                if (!seen.Add(envelope.EventId.Value))
                {
                    continue;
                }

                var type = envelope.EventType.Value;
                foreach (var violation in validator.Validate(envelope))
                {
                    if (!AllowList.Value.Contains($"{type} {violation.Key}"))
                    {
                        problems.Add($"{type} {violation.Key}    <- {violation.Detail}");
                    }
                }
            }
        }

        return [.. problems];
    }

    /// <summary>Throws when the database holds events that break their schema.</summary>
    public static async Task AssertCleanAsync(string connectionString)
    {
        IReadOnlyList<string> problems;
        try
        {
            problems = await FindAsync(connectionString, CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            return; // TODO(hardening): database already dropped/locked by the test; nothing to check
        }

        if (problems.Count == 0)
        {
            return;
        }

        // Discovery aid: COREINS_EVENT_DRIFT_REPORT=<file> also appends the findings there (used to build the allow-list).
        if (Environment.GetEnvironmentVariable("COREINS_EVENT_DRIFT_REPORT") is { Length: > 0 } report)
        {
            File.AppendAllLines(report, problems);
        }

        var message = new StringBuilder()
            .AppendLine("Events emitted by these integration tests do not match their contracts/events schemas (NX-TST-05).")
            .AppendLine("Fix the producer or the schema; only if the drift is accepted, add the left-hand part of each line to")
            .AppendLine("tests/CoreIns.IntegrationTests/EventSchemaAllowList.txt with a TODO(hardening) comment.");
        foreach (var problem in problems)
        {
            message.Append("  ").AppendLine(problem);
        }

        throw new EventSchemaDriftException(message.ToString());
    }

    private static HashSet<string> LoadAllowList()
    {
        var path = Path.Combine(RepositoryPaths.Root, "tests", "CoreIns.IntegrationTests", "EventSchemaAllowList.txt");
        return [.. File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#')];
    }
}
