using System.Collections.Concurrent;
using System.Data;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using Dapper;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Underwriting.Queries;

/// <summary>Module options (<c>Underwriting:*</c>).</summary>
internal sealed class UnderwritingOptions
{
    public const string Section = "Underwriting";

    /// <summary>Product codes for which the illustrative motor rule set is published (stands in for rule-set authoring and approval).</summary>
    public List<string> SeedProductCodes { get; set; } = ["MOTOR_PRIVATE_CAR"];
}

/// <summary>A stored rule set version as read.</summary>
internal sealed class RuleSetRecord
{
    public string Hash { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;
}

/// <summary>An issue row as read.</summary>
internal sealed record OpenIssue(Guid IssueId, string IssueType, string IssueKey, string BlockingPoint, string Severity, string Lane, string Status);

/// <summary>Dapper reads and writes of the uw schema on the scope's connection and transaction.</summary>
internal sealed class UnderwritingStore(DbSession session, IClock clock, RequestContext context, IOptions<UnderwritingOptions> options)
{
    private static readonly ConcurrentDictionary<string, CompiledRuleSet> Cache = new(StringComparer.Ordinal);

    /// <summary>Publishes the built-in illustrative rule set when it is not stored yet (idempotent, content-addressed).</summary>
    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var productCode in options.Value.SeedProductCodes)
        {
            var dto = BuiltInRuleSets.Motor(productCode);
            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM uw.rule_set_version WHERE rule_set_code = @code AND version_no = @version AND product_code = @product)",
                new { code = dto.Code, version = dto.Version, product = productCode }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (exists)
            {
                continue;
            }

            var compiled = CompiledRuleSet.Compile(dto);
            var args = new DynamicParameters(new
            {
                code = dto.Code, version = dto.Version, product = productCode, hash = compiled.Hash, status = "Active", dataStatus = dto.DataStatus,
                definition = RuleSetJson.Serialize(dto), now = clock.Now.ToUtcDateTime(), actor = context.Actor.ToString(),
            });
            args.Add("fromDate", DateOnly.ParseExact(dto.EffectiveFrom, "yyyy-MM-dd"), DbType.Date);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO uw.rule_set_version (rule_set_code, version_no, product_code, content_hash, status, effective_from, data_status, definition, created_at, created_by)
                VALUES (@code, @version, @product, @hash, @status, @fromDate, @dataStatus, @definition::jsonb, @now, @actor) ON CONFLICT DO NOTHING
                """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>The active rule set of the product effective on <paramref name="date"/> (REQ-UW-043); null when none.</summary>
    public async Task<CompiledRuleSet?> ResolveAsync(string productCode, DateOnly date, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { product = productCode });
        args.Add("asOf", date, DbType.Date);
        var row = await connection.QueryFirstOrDefaultAsync<RuleSetRecord>(new CommandDefinition(
            """
            SELECT content_hash AS Hash, definition::text AS Definition FROM uw.rule_set_version
             WHERE product_code = @product AND status = 'Active' AND effective_from <= @asOf
             ORDER BY effective_from DESC, version_no DESC LIMIT 1
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is not { } found)
        {
            return null;
        }

        var compiled = Cache.GetOrAdd(found.Hash, _ => CompiledRuleSet.Compile(RuleSetJson.Deserialize(found.Definition)));
        return string.Equals(compiled.Hash, found.Hash, StringComparison.Ordinal) ? compiled : null;
    }

    public async Task InsertEvaluationAsync(
        Guid evaluationId, Guid legalEntity, Guid jobId, string checkpoint, CompiledRuleSet ruleSet, string snapshotRef, string? snapshotHash,
        string outcome, string lane, string trace, string actor, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.evaluation (evaluation_id, legal_entity_id, job_id, checkpoint, rule_set_code, rule_set_version, rule_set_hash, snapshot_ref, snapshot_hash, outcome, lane, trace, created_at, created_by)
            VALUES (@evaluationId, @legalEntity, @jobId, @checkpoint, @code, @version, @hash, @snapshotRef, @snapshotHash, @outcome, @lane, @trace::jsonb, @now, @actor)
            """,
            new
            {
                evaluationId, legalEntity, jobId, checkpoint, code = ruleSet.Dto.Code, version = ruleSet.Dto.Version, hash = ruleSet.Hash, snapshotRef, snapshotHash,
                outcome, lane, trace, now = clock.Now.ToUtcDateTime(), actor,
            }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OpenIssue>> OpenIssuesAsync(Guid legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<OpenIssue>(new CommandDefinition(
            """
            SELECT issue_id AS IssueId, issue_type AS IssueType, issue_key AS IssueKey, blocking_point AS BlockingPoint,
                   severity AS Severity, lane AS Lane, status AS Status
              FROM uw.issue WHERE legal_entity_id = @legalEntity AND job_id = @jobId AND status = 'Open' ORDER BY created_at, issue_id
            """, new { legalEntity, jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
    }

    public async Task InsertIssueAsync(
        Guid issueId, Guid legalEntity, Guid jobId, string issueType, string issueKey, string blockingPoint, string severity, string lane, string ruleId,
        string messageEn, string messageEl, Guid evaluationId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO uw.issue (issue_id, legal_entity_id, job_id, issue_type, issue_key, blocking_point, severity, lane, status, rule_id, message_en, message_el, raised_evaluation_id, record_version, created_at)
            VALUES (@issueId, @legalEntity, @jobId, @issueType, @issueKey, @blockingPoint, @severity, @lane, 'Open', @ruleId, @messageEn, @messageEl, @evaluationId, 1, @now)
            """,
            new { issueId, legalEntity, jobId, issueType, issueKey, blockingPoint, severity, lane, ruleId, messageEn, messageEl, evaluationId, now = clock.Now.ToUtcDateTime() },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CloseIssueAsync(Guid issueId, Guid evaluationId, string reason, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE uw.issue SET status = 'Closed', closed_evaluation_id = @evaluationId, close_reason = @reason, closed_at = @now, record_version = record_version + 1 WHERE issue_id = @issueId AND status = 'Open'",
            new { issueId, evaluationId, reason, now = clock.Now.ToUtcDateTime() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Open issues of the job that block at or before the given point (a PRE_QUOTE issue still blocks PRE_BIND).</summary>
    public async Task<IReadOnlyList<OpenIssue>> BlockingAsync(Guid legalEntity, Guid jobId, string blockingPoint, CancellationToken cancellationToken)
    {
        var rank = blockingPoint switch { "PRE_QUOTE" => 1, "PRE_BIND" => 2, "PRE_ISSUE" => 3, _ => 0 };
        return (await OpenIssuesAsync(legalEntity, jobId, cancellationToken).ConfigureAwait(false))
            .Where(i => i.BlockingPoint switch { "PRE_QUOTE" => 1, "PRE_BIND" => 2, "PRE_ISSUE" => 3, _ => 99 } <= rank)
            .ToList();
    }
}
