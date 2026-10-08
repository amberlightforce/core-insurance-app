using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary>
/// <c>uw.Rules.evaluate</c> in-process (D-ARC-16). It runs through the command pipeline with the caller's
/// <see cref="CommandOptions"/> (idempotency, dry-run); a failure is thrown as a <see cref="DomainException"/> with the UW-ERR code.
/// </summary>
internal sealed class UnderwritingRulesService(RequestContext context, ICommandHandler<EvaluateRules, RulesEvaluateResponse> evaluate) : IUnderwritingRulesService
{
    public async Task<RulesEvaluateResponse> EvaluateAsync(RulesEvaluateRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await evaluate.HandleAsync(new EvaluateRules(request), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
        }
    }
}

/// <summary><c>uw.Issue.blockingStatus</c> (REQ-UW-002): the gate POL asks before quote, bind and issue.</summary>
internal sealed class UnderwritingIssueService(UnderwritingStore store, RequestContext context, ILegalEntityDirectory legalEntities) : IUnderwritingIssueService
{
    public async Task<IssueBlockingStatusResponse> BlockingStatusAsync(JobId jobRef, BlockingPoint blockingPoint, CancellationToken cancellationToken = default)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var point = blockingPoint switch
        {
            BlockingPoint.PreQuote => "PRE_QUOTE",
            BlockingPoint.PreBind => "PRE_BIND",
            BlockingPoint.PreIssue => "PRE_ISSUE",
            _ => throw new DomainException(DomainError.Of(ModuleCode.UW, "VALIDATION", "Ask for PRE_QUOTE, PRE_BIND or PRE_ISSUE.")),
        };
        var issues = await store.BlockingAsync(legalEntity.Value, jobRef.Value, point, cancellationToken).ConfigureAwait(false);
        return new IssueBlockingStatusResponse
        {
            Blocked = issues.Count > 0,
            Issues = [.. issues.Select(i => new IssueBlockingStatusResponse.IssueItem
            {
                IssueId = new UwIssueId(i.IssueId),
                IssueType = i.IssueType,
                Severity = i.Severity,
                BlockingPoint = i.BlockingPoint switch
                {
                    "PRE_QUOTE" => BlockingPoint.PreQuote,
                    "PRE_BIND" => BlockingPoint.PreBind,
                    "PRE_ISSUE" => BlockingPoint.PreIssue,
                    _ => BlockingPoint.NonBlocking,
                },
                IssueKey = i.IssueKey,
                Lane = i.Lane,
                ApprovalStatus = i.Status,
            })],
        };
    }

    public Task<IssueListForChannelPage> ListForChannelAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.UW, "NOT-AVAILABLE", "uw.Issue.listForChannel belongs to the referral workbench work package."));
}

/// <summary><c>uw.Issue.list</c> (REQ-UW-075, -077 subset): the issues of a job, or the legal entity's issues in one status (the referral queue).</summary>
internal sealed class UnderwritingIssueQueries(UnderwritingStore store, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<Result<IssueListPage>> ListAsync(Guid? jobRef, IssueStatusCode? status, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200 || !TryDecode(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var size = limit ?? 50;
        var rows = await store.ListIssuesAsync(legalEntity, jobRef, status?.ToString(), after, size + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        return new IssueListPage
        {
            Items = [.. page.Select(Item)],
            NextCursor = rows.Count > size ? Encode(page[^1]) : null,
            Limit = size,
        };
    }

    internal static IssueListItem Item(IssueRecord row) => new()
    {
        Id = row.IssueId,
        JobRef = JobId.From(row.JobId),
        IssueType = row.IssueType,
        IssueKey = row.IssueKey,
        RuleId = row.RuleId,
        Severity = row.Severity,
        BlockingPoint = DecideIssuesHandler.Point(row.BlockingPoint),
        Lane = row.Lane,
        Status = Enum.Parse<IssueStatusCode>(row.Status),
        RecordVersion = row.RecordVersion,
        MessageEn = row.MessageEn,
        MessageEl = row.MessageEl,
        RaisedAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
        RaisedBy = row.RaisedBy,
        CloseReason = row.CloseReason,
        Decision = row is { Decision: { } decision, DecidedBy: { } by, DecidedAt: { } at, DecisionReason: { } reason, AuthorityCheckId: { } check }
            ? new IssueDecisionView
            {
                Decision = decision == "APPROVE" ? IssueDecisionCode.Approve : IssueDecisionCode.Reject,
                DecidedBy = by,
                DecidedAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
                Reason = reason,
                Message = row.DecisionMessage,
                AuthorityCheckId = new AuthorityCheckId(check),
            }
            : null,
    };

    private static string Encode(IssueRecord last) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{last.IssueId:N}"));

    private static bool TryDecode(string? cursor, out (DateTime CreatedAt, Guid IssueId)? after)
    {
        after = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('_');
            if (parts.Length == 2 && long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                && ticks is > 0 && ticks <= DateTime.MaxValue.Ticks && Guid.TryParseExact(parts[1], "N", out var id))
            {
                after = (new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
