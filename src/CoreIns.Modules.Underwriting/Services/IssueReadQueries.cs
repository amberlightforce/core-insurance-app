using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary>
/// The issue reads of BO-UW-03: <c>uw.Issue.get</c>, <c>uw.Issue.explain</c>, <c>uw.Issue.listForChannel</c> and
/// <c>uw.Approval.history</c>. Read only, always scoped to the caller's legal entity (another entity's issue is "not found").
/// Nothing here exposes P2: the explanation shows a driver's age as its band, and the channel list shows neither rule ids nor
/// who decided nor why.
/// </summary>
internal sealed class IssueReadQueries(
    IssueReads reads,
    ReferralQueries referrals,
    RequestContext context,
    ILegalEntityDirectory legalEntities)
{
    public async Task<Result<IssueGetResponse>> GetAsync(Guid issueId, CancellationToken cancellationToken)
    {
        var issue = await reads.GetAsync(LegalEntity(), issueId, cancellationToken).ConfigureAwait(false);
        if (issue is null)
        {
            return NotFound(issueId);
        }

        return new IssueGetResponse
        {
            Issue = UnderwritingIssueQueries.Item(issue),
            Explanation = await ExplainAsync(issue, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<Result<IssueExplainResponse>> ExplainAsync(Guid issueId, CancellationToken cancellationToken)
    {
        var issue = await reads.GetAsync(LegalEntity(), issueId, cancellationToken).ConfigureAwait(false);
        if (issue is null)
        {
            return NotFound(issueId);
        }

        return new IssueExplainResponse { Explanation = await ExplainAsync(issue, cancellationToken).ConfigureAwait(false) };
    }

    public async Task<Result<IssueListForChannelPage>> ListForChannelAsync(Guid jobRef, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, 50, out var size) || !UnderwritingIssueQueries.TryDecode(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        var english = context.Language == Language.En;
        var rows = await reads.ChannelIssuesAsync(LegalEntity(), jobRef, after, size + 1, cancellationToken).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, size, UnderwritingIssueQueries.Encode);
        return new IssueListForChannelPage
        {
            Items = [.. page.Select(i => new IssueListForChannelItem
            {
                IssueId = new UwIssueId(i.IssueId),
                IssueType = i.IssueType,
                BlockingPoint = DecideIssuesHandler.Point(i.BlockingPoint),
                ChannelStatus = i.Status switch
                {
                    IssueStatus.Rejected => IssueListForChannelItem.ChannelStatusValue.NotAccepted,
                    IssueStatus.Open => IssueListForChannelItem.ChannelStatusValue.UnderReview,
                    _ => IssueListForChannelItem.ChannelStatusValue.Cleared,
                },
                Message = english ? i.MessageEn : i.MessageEl,
                RaisedAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(i.CreatedAt, DateTimeKind.Utc)),
            })],
            NextCursor = next,
            Limit = size,
        };
    }

    public async Task<Result<ApprovalHistoryPage>> HistoryAsync(Guid? jobRef, string? issueKey, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, 50, out var size) || !TryDecode(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        var rows = await reads.HistoryAsync(LegalEntity(), jobRef, string.IsNullOrWhiteSpace(issueKey) ? null : issueKey, after, size + 1, cancellationToken).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, size, Encode);
        return new ApprovalHistoryPage
        {
            Items = [.. page.Select(r => new ApprovalHistoryItem
            {
                IssueId = new UwIssueId(r.IssueId),
                JobRef = JobId.From(r.JobId),
                IssueKey = r.IssueKey,
                IssueType = r.IssueType,
                Event = r.Kind == 0 ? ApprovalHistoryItem.EventValue.Decided : ApprovalHistoryItem.EventValue.Invalidated,
                Decision = r.Decision switch { "APPROVE" => IssueDecisionCode.Approve, "REJECT" => IssueDecisionCode.Reject, _ => null },
                Approver = r.Actor,
                Reason = r.Reason,
                Fingerprint = r.Fingerprint?.Trim(),
                AuthorityCheckId = r.AuthorityCheckId is { } check ? new AuthorityCheckId(check) : null,
                InvalidatedByEvaluationId = r.InvalidatedByEvaluationId,
                At = Instant.FromUtcDateTime(DateTime.SpecifyKind(r.At, DateTimeKind.Utc)),
            })],
            NextCursor = next,
            Limit = size,
        };
    }

    private async Task<IssueExplanation> ExplainAsync(IssueRecord issue, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity();
        var ruleSet = await reads.RuleSetOfAsync(legalEntity, issue.IssueId, cancellationToken).ConfigureAwait(false);
        var readings = await referrals.ExplainsAsync(legalEntity, [issue.JobId], cancellationToken).ConfigureAwait(false);
        var reading = readings.TryGetValue(issue.JobId, out var byRule) && byRule.TryGetValue(issue.RuleId, out var found) ? found : null;
        return new IssueExplanation
        {
            IssueId = new UwIssueId(issue.IssueId),
            RuleId = issue.RuleId,
            RuleSetCode = ruleSet?.Code ?? string.Empty,
            RuleSetVersion = ruleSet?.Version ?? string.Empty,
            RuleSetHash = ruleSet?.Hash.Trim(),
            Fact = reading?.Fact,
            Observed = reading?.Observed,
            Limit = reading?.Limit,
            LimitUnavailableReason = reading is null ? "RULE_DECLARES_NO_LIMIT" : null,
            MessageEn = issue.MessageEn,
            MessageEl = issue.MessageEl,
            ExplanationKeys = [issue.RuleId],
        };
    }

    private Guid LegalEntity() =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    private static DomainError NotFound(Guid issueId) =>
        DomainError.Of(ModuleCode.UW, "ISSUE-NOT-FOUND", $"No issue {issueId:D} in this legal entity.");

    private static string Encode(ApprovalHistoryRecord last) =>
        KeysetPaging.Encode(KeysetPaging.Part(DateTime.SpecifyKind(last.At, DateTimeKind.Utc).Ticks), KeysetPaging.Part(last.IssueId), KeysetPaging.Part(last.Kind));

    private static bool TryDecode(string? cursor, out (DateTime At, Guid IssueId, int Kind)? after)
    {
        after = null;
        if (!KeysetPaging.TryDecode(cursor, 3, out var parts))
        {
            return false;
        }

        if (parts is null)
        {
            return true;
        }

        if (!KeysetPaging.TryLong(parts[0], out var ticks) || ticks is <= 0 || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[1], "D", out var id) || !KeysetPaging.TryLong(parts[2], out var kind) || kind is < 0 or > 1)
        {
            return false;
        }

        after = (new DateTime(ticks, DateTimeKind.Utc), id, (int)kind);
        return true;
    }
}
