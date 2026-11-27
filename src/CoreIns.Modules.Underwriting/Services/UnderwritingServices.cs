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
using CoreIns.SharedKernel.Paging;
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
            var result = await evaluate.HandleAsync(new EvaluateRules(request, ReconcileIssues: true), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
        }
    }
}

/// <summary><c>uw.Issue.blockingStatus</c> (REQ-UW-002): the gate POL asks before quote, bind and issue.</summary>
internal sealed class UnderwritingIssueService(UnderwritingStore store, RequestContext context, ILegalEntityDirectory legalEntities, IssueReadQueries reads) : IUnderwritingIssueService
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

    public async Task<IssueListForChannelPage> ListForChannelAsync(JobId jobRef, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var result = await reads.ListForChannelAsync(jobRef.Value, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
    }
}

/// <summary><c>uw.Issue.list</c> (REQ-UW-075, -077 subset): the issues of a job, or the legal entity's issues in one status (the referral queue).</summary>
internal sealed class UnderwritingIssueQueries(UnderwritingStore store, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<Result<IssueListPage>> ListAsync(
        Guid? jobRef, IssueStatusCode? status, string? cursor, int? limit, CancellationToken cancellationToken, string? issueType = null, BlockingPoint? blockingPoint = null)
    {
        if (!KeysetPaging.TryTake(limit, 50, out var size) || !TryDecode(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var rows = await store.ListIssuesAsync(
            legalEntity, jobRef, status?.ToString(), after, size + 1, cancellationToken, issueType, blockingPoint is { } point ? PointCode(point) : null).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, size, Encode);
        return new IssueListPage { Items = [.. page.Select(Item)], NextCursor = next, Limit = size };
    }

    internal static string PointCode(BlockingPoint point) => point switch
    {
        BlockingPoint.PreQuote => "PRE_QUOTE",
        BlockingPoint.PreBind => "PRE_BIND",
        BlockingPoint.PreIssue => "PRE_ISSUE",
        _ => "NON_BLOCKING",
    };

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

    internal static string Encode(IssueRecord last) =>
        KeysetPaging.Encode(KeysetPaging.Part(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc).Ticks), KeysetPaging.Part(last.IssueId));

    internal static bool TryDecode(string? cursor, out (DateTime CreatedAt, Guid IssueId)? after)
    {
        after = null;
        if (!KeysetPaging.TryDecode(cursor, 2, out var parts))
        {
            return false;
        }

        if (parts is null)
        {
            return true;
        }

        if (!KeysetPaging.TryLong(parts[0], out var ticks) || ticks is <= 0 || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "D", out var id))
        {
            return false;
        }

        after = (new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}
