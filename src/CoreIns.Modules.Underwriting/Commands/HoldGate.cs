using System.Text.Json;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary>
/// Policy holds inside <c>uw.Rules.evaluate</c> (BO-UW-09, REQ-UW-007). The Active holds that match the job facts become one
/// <c>UW_POLICY_HOLD</c> issue per hold (rule id <c>HOLD:{code}</c>, blocking PRE_BIND), so POL bind gate and
/// <c>uw.Issue.blockingStatus</c> block the job like any other open issue. A hold that is released or no longer matches closes its Open issue;
/// an approved hold issue is an override and stays. Only POL in-process evaluation changes issues; an HTTP evaluation previews them.
/// </summary>
internal sealed class HoldGate(PolicyHoldQueries holds, UnderwritingStore store, IEventPublisher events)
{
    /// <summary>The facts of the quote the holds are matched against, from the POL risk snapshot (vehicle postcode, coverages).</summary>
    public static HoldFacts Facts(JsonElement snapshot, string product, bool renewal, DateOnly effective)
    {
        var postcodes = new List<string>();
        if (snapshot.TryGetProperty("vehicle", out var vehicle) && vehicle.ValueKind == JsonValueKind.Object)
        {
            AddPostcode(vehicle, postcodes);
            if (vehicle.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            {
                AddPostcode(fields, postcodes);
            }
        }

        var coverages = new List<string>();
        if (snapshot.TryGetProperty("coverages", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var code = item.ValueKind == JsonValueKind.String ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (code is not null)
                {
                    coverages.Add(code);
                }
            }
        }

        // TODO(hardening): the line of business and the job type of change jobs are not on the evaluation request; only product, new business and renewal match.
        return new HoldFacts(renewal ? "RENEWAL" : "NEW_BUSINESS", product, null, coverages, postcodes, [], effective);
    }

    private static void AddPostcode(JsonElement source, List<string> postcodes)
    {
        if (source.TryGetProperty("garagingPostcode", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
        {
            postcodes.Add(p.GetString()!);
        }
    }

    public Task<IReadOnlyList<HoldDefinition>> ApplicableAsync(HoldFacts facts, CancellationToken cancellationToken) => holds.ApplicableAsync(facts, cancellationToken);

    /// <summary>Raises, keeps or closes the hold issues of the job and returns one response item per applicable hold.</summary>
    public async Task<IReadOnlyList<RulesEvaluateResponse.IssueItem>> ReconcileAsync(
        IReadOnlyList<HoldDefinition> applicable, Guid legalEntity, Guid jobId, Guid evaluationId, bool reconcile, CancellationToken cancellationToken)
    {
        var active = (await store.ActiveIssuesAsync(legalEntity, jobId, cancellationToken).ConfigureAwait(false))
            .Where(i => i.RuleId.StartsWith(HoldIssue.RulePrefix, StringComparison.Ordinal)).ToList();
        var items = new List<RulesEvaluateResponse.IssueItem>();
        foreach (var hold in applicable)
        {
            var key = HoldIssue.Key(hold.HoldId);
            var existing = active.FirstOrDefault(i => i.IssueKey == key);
            var issueId = existing?.IssueId ?? Guid.CreateVersion7();
            var status = existing?.Status ?? IssueStatus.Open;
            if (existing is null && reconcile)
            {
                var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
                await store.InsertIssueAsync(
                    issueId, legalEntity, jobId, HoldIssue.IssueType, key, "PRE_BIND", "REFER", "EXPERT", HoldIssue.RuleId(hold.Code), hold.Description,
                    hold.Description, evaluationId, fingerprint, null, cancellationToken).ConfigureAwait(false);
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(UWIssueRaisedV1.Descriptor), "Job", jobId.ToString("D"),
                    new UWIssueRaisedV1
                    {
                        JobId = JobId.From(jobId),
                        IssueId = new UwIssueId(issueId),
                        IssueType = HoldIssue.IssueType,
                        IssueKeyHash = Sha256Hash.ComputeUtf8(key),
                        BlockingPoint = BlockingPoint.PreBind,
                        Severity = "REFER",
                        Lane = "EXPERT",
                        Reopened = false,
                    },
                    BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", issueId.ToString("D"))));
            }

            items.Add(new RulesEvaluateResponse.IssueItem
            {
                IssueId = new UwIssueId(issueId),
                IssueType = HoldIssue.IssueType,
                Severity = "REFER",
                BlockingPoint = BlockingPoint.PreBind,
                IssueKey = key,
                Lane = "EXPERT",
                ExplanationKeys = [HoldIssue.RuleId(hold.Code)],
                ApprovalStatus = status,
            });
        }

        if (reconcile)
        {
            var keys = applicable.Select(h => HoldIssue.Key(h.HoldId)).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in active.Where(i => !keys.Contains(i.IssueKey) && (i.Status == IssueStatus.Open || i.Status == IssueStatus.Rejected)))
            {
                await store.CloseIssueAsync(gone.IssueId, evaluationId, "HOLD_NO_LONGER_APPLIES", cancellationToken).ConfigureAwait(false);
                events.Publish(new OutgoingEvent(
                    EventDescriptor.From(UWIssueClosedV1.Descriptor), "Job", jobId.ToString("D"),
                    new UWIssueClosedV1 { JobId = JobId.From(jobId), IssueId = new UwIssueId(gone.IssueId), Reason = "HOLD_NO_LONGER_APPLIES" },
                    BusinessKeys.Empty.With("jobId", jobId.ToString("D")).With("issueId", gone.IssueId.ToString("D"))));
            }
        }

        return items;
    }
}
