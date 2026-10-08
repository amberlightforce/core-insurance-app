using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Finance;
using CoreIns.Modules.Claims.Events;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Reverify;

/// <summary>
/// SL3-CLM-REVERIFY test host: the real Host and database with POL's <c>pol.Snapshot.get</c> played by the generated sandbox
/// double, scripted with a version history per policy so that the snapshot of an earlier version reports
/// <c>supersession = { superseded, successorRef }</c> exactly as D-SL3-03 (c) specifies. Refs are
/// <c>SNAP-{policy}-{validAt}-v{version}</c> and are registered when issued, so a read by ref echoes the exact valid instant.
/// Policy events (PolicyChanged, PolicyCancelled) are published through the real outbox and dispatched by the real processor.
/// </summary>
internal sealed class ReverifyHarness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, List<Version>> _versions = new();
    private readonly ConcurrentDictionary<string, (Guid Policy, Instant ValidAt, int Version)> _refs = new();
    private readonly ConcurrentDictionary<Guid, ScriptedPolicy> _scripted = new();

    public ReverifyHarness(PostgresFixture database)
    {

        Slice = new ClaimsSlice(database.AppConnectionString);
        Money = new ClaimsMoney(Slice.Factory, Slice.Client, database.SuperuserConnectionString);
        Slice.Snapshots.Setup("pol.Snapshot.get", Answer);
    }

    private sealed record Version(IReadOnlyList<string> Coverages, Instant End);

    public ClaimsSlice Slice { get; }

    public ClaimsMoney Money { get; }

    /// <summary>When true POL does not answer (a dependency outage).</summary>
    public bool PolDown { get; set; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => Slice.DisposeAsync();

    /// <summary>A scripted policy (version 1 = the slice default: OD and MTPL, in force around now).</summary>
    public ScriptedPolicy Policy(params string[] coverages)
    {
        var policy = Slice.Policy(coverages);
        _scripted[policy.PolicyId] = policy;
        _versions[policy.PolicyId] = [new Version(policy.Coverages, policy.End)];
        return policy;
    }

    /// <summary>POL records a new version of the policy: every earlier ref now reports itself superseded.</summary>
    public void Supersede(ScriptedPolicy policy, string[] coverages, Instant? end = null) =>
        _versions[policy.PolicyId].Add(new Version(coverages, end ?? policy.End));

    /// <summary>An open claim with one own-damage (OD) exposure; the loss is two days ago unless given.</summary>
    public async Task<MoneyClaim> OpenClaimAsync(ScriptedPolicy policy, Instant? lossAt = null) =>
        await Money.OpenClaimAsync(ClaimsSlice.Fnol(policy, lossAt));

    public Task DrainAsync() => Slice.Factory.Services.GetRequiredService<OutboxProcessor>().DrainAsync(Ct);

    public static BusinessDate DaysAgo(int days) => BusinessDate.Parse(DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    public Task<EventEnvelope> PublishChangedAsync(ScriptedPolicy policy, BusinessDate effective)
    {
        var payload = FinanceSlice.Sample("pol", "PolicyChanged");
        payload["effectiveDate"] = effective.ToString();
        return PublishAsync(
            PolicyChangedV1.Descriptor, policy, payload,
            BusinessKeys.Empty.With("policyId", policy.PolicyId.ToString()).With("transactionId", payload["transactionId"]!.GetValue<string>()));
    }

    public Task<EventEnvelope> PublishCancelledAsync(ScriptedPolicy policy, BusinessDate effective)
    {
        var payload = FinanceSlice.Sample("pol", "PolicyCancelled");
        payload["effectiveDate"] = effective.ToString();
        return PublishAsync(
            PolicyCancelledV1.Descriptor, policy, payload,
            BusinessKeys.Empty.With("policyId", policy.PolicyId.ToString()).With("transactionId", payload["transactionId"]!.GetValue<string>())
                .With("policyTermId", payload["termId"]!.GetValue<string>()));
    }

    /// <summary>Re-delivers the given events to a CLM handler with origin REPLAY (the handler's markers are overridden).</summary>
    public Task<int> ReplayAsync(string handlerName, params EventEnvelope[] events) =>
        Slice.Factory.Services.GetRequiredService<OutboxReplayService>()
            .ReplayAsync(handlerName, new ReplayFilter { EventIds = [.. events.Select(e => e.EventId.Value)] }, Ct);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> ReverifyAsync(
        MoneyClaim claim, string decision, string? expectedNewRef, string reason = "ADJUSTER_REVIEW", string? comment = null, string roles = Handler) =>
        Money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/coverage/reverify",
            new { claimId = claim.ClaimId, decision, reasonCode = reason, comment, expectedNewSnapshotRef = expectedNewRef }, roles);

    public async Task<JsonNode> ClaimAsync(MoneyClaim claim) => await Money.ClaimAsync(claim);

    /// <summary>The ReverificationRequired payloads published for a claim, in order.</summary>
    public async Task<IReadOnlyList<JsonObject>> RaisedAsync(MoneyClaim claim)
    {
        var json = await Money.ScalarAsync<string?>(
            $"SELECT json_agg(payload ORDER BY aggregate_sequence)::text FROM plt.outbox_message WHERE event_type = 'ReverificationRequired' AND aggregate_id = '{claim.ClaimId}'");
        return json is null ? [] : [.. JsonNode.Parse(json)!.AsArray().Select(n => n!.AsObject())];
    }

    public Task<long> RowsAsync(MoneyClaim claim, string? status = null) => Money.ScalarAsync<long>(
        $"SELECT count(*) FROM clm.reverification WHERE claim_id = '{claim.ClaimId}'" + (status is null ? string.Empty : $" AND status = '{status}'"));

    private async Task<EventEnvelope> PublishAsync(EventContract contract, ScriptedPolicy policy, JsonObject payload, BusinessKeys keys)
    {
        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("pol-test-producer");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('c', 64));
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(Ct);
        var envelope = scope.ServiceProvider.GetRequiredService<IEventPublisher>()
            .Publish(new OutgoingEvent(EventDescriptor.From(contract), "Policy", policy.PolicyId.ToString(), payload, keys));
        await transaction.CommitAsync(Ct);
        return envelope;
    }

    private string Issue(Guid policyId, Instant validAt, int version)
    {
        var snapshotRef = $"SNAP-{policyId:N}-{validAt.ToDateTimeOffset().ToUnixTimeMilliseconds()}-v{version}";
        _refs[snapshotRef] = (policyId, validAt, version);
        return snapshotRef;
    }

    private SnapshotGetResponse Answer(CoreIns.Testing.Contracts.RecordedCall call)
    {
        if (PolDown)
        {
            throw new InvalidOperationException("POL is down (scripted outage).");
        }

        var snapshotRef = (string?)call.Arguments[3];
        Guid policyId;
        Instant validAt;
        int version;
        Instant knownAt;
        if (snapshotRef is not null)
        {
            if (!_refs.TryGetValue(snapshotRef, out var known))
            {
                throw new DomainException(DomainError.Of(ModuleCode.POL, "VALIDATION", "The snapshot reference was not issued."));
            }

            (policyId, validAt, version) = known;
            knownAt = Instant.FromDateTimeOffset(DateTimeOffset.UtcNow);
        }
        else
        {
            validAt = ((ValidAt?)call.Arguments[0])?.Instant ?? throw new InvalidOperationException("CLM must read the snapshot at the loss instant.");
            knownAt = (Instant?)call.Arguments[1] ?? throw new InvalidOperationException("CLM must give knownAt.");
            policyId = ((PolicyId?)call.Arguments[2])?.Value ?? Guid.Empty;
            version = _versions.TryGetValue(policyId, out var all) ? all.Count : 0;
        }

        if (!_versions.TryGetValue(policyId, out var history))
        {
            throw new DomainException(DomainError.Of(ModuleCode.POL, "NOT-FOUND", "The policy does not exist."));
        }

        var current = history.Count;
        var scripted = _scripted[policyId];
        var content = history[version - 1];
        var inForce = validAt >= scripted.Start && validAt < content.End;
        var hash = Sha256Hash.Parse(new string('a', 64));
        return new SnapshotGetResponse
        {
            SnapshotRef = snapshotRef ?? Issue(policyId, validAt, version),
            ValidAt = validAt,
            KnownAt = knownAt,
            InForce = inForce,
            Status = inForce ? TermStateCode.InForce : validAt < scripted.Start ? null : TermStateCode.Cancelled,
            NotInForceReason = inForce ? null : SnapshotGetResponse.NotInForceReasonValue.NoTermAtInstant,
            Policy = new SnapshotPolicy
            {
                PolicyId = new PolicyId(policyId),
                PolicyNumber = PolicyNumber.Parse(scripted.PolicyNumber),
                ProductCode = scripted.ProductCode,
                InsuredPartyId = scripted.InsuredPartyId,
                LegalEntity = "GR-TEST",
                Jurisdiction = "GR",
            },
            Content = !inForce ? null : new SnapshotContent
            {
                Term = new TermView
                {
                    TermId = PolicyTermId.New(),
                    TermNumber = 1,
                    Period = new InstantRange(scripted.Start, content.End),
                    State = TermStateCode.InForce,
                    ProductVersion = ProductVersionNumber.Parse("1.0"),
                    ArtefactHash = hash,
                    ResolutionHash = new ResolutionHash(hash),
                    ConfigurationHash = new ConfigurationHash(hash),
                    Currency = Currency.FromCode("EUR"),
                    PaymentPlanRef = "ANNUAL",
                    WrittenDate = scripted.Start.UtcDate,
                    RecordedAt = scripted.Start,
                },
                ProductVersion = ProductVersionNumber.Parse("1.0"),
                Segment = new SegmentView
                {
                    SegmentId = SegmentId.New(),
                    TransactionId = PolicyTransactionId.New(),
                    ValidPeriod = new InstantRange(scripted.Start, content.End),
                    RecordedPeriod = new InstantRange(scripted.Start, null),
                    SnapshotHash = hash,
                },
                Vehicles = [],
                Drivers = [],
                Coverages = [.. content.Coverages.Select(c => new CoverageSelection { CoverageCode = c, Selected = true })],
            },
            Supersession = version < current
                ? new SnapshotSupersession { Superseded = true, SuccessorRef = Issue(policyId, validAt, current), SupersededAt = knownAt }
                : new SnapshotSupersession { Superseded = false },
        };
    }
}
