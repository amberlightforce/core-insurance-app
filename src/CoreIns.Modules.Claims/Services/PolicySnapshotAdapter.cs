using System.Text.Json;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Claims.Services;

/// <summary>
/// <see cref="ICoverageSource"/> over the generated POL contract <see cref="IPolicySnapshotService"/>
/// (<c>pol.Snapshot.get(policyId, validAt = lossAt, knownAt = now)</c>, REQ-CLM-002, REQ-POL-007).
/// <para>
/// SL2-POL-SNAP types <c>SnapshotGetResponse</c> in parallel with this work package (pre-release, D-API-06). To compile
/// against either version of the generated record, the adapter reads the response through its JSON form, by the member
/// names SL2-POL-SNAP announced: <c>snapshotRef</c>, <c>validAt</c>, <c>knownAt</c>, <c>inForce</c>, <c>status</c>,
/// <c>notInForceReason</c>, <c>policy{policyId, policyNumber, productCode, insuredPartyId}</c>,
/// <c>content{productVersion, term{productVersion}, segment{segmentId}, coverages[]{coverageCode, selected}}</c>.
/// A member missing at the root is also looked for inside <c>content</c> (the untyped record keeps only <c>snapshotRef</c>
/// and <c>content</c>). Once the typed record is merged this file can read the typed members directly.
/// </para>
/// </summary>
internal sealed partial class PolicySnapshotAdapter(IServiceProvider services, ILogger<PolicySnapshotAdapter> logger) : ICoverageSource
{
    public async Task<SnapshotRead> ReadAsync(Guid policyId, Instant lossAt, Instant knownAt, CancellationToken cancellationToken)
    {
        // Resolved per call: until POL registers its implementation (S0 wiring) the Host still starts and FNOL answers 503.
        var snapshots = services.GetService<IPolicySnapshotService>();
        if (snapshots is null)
        {
            return SnapshotRead.Unavailable("IPolicySnapshotService is not registered in this deployment yet.");
        }

        object? response;
        try
        {
            response = await snapshots.GetAsync(ValidAt.From(lossAt), knownAt, new PolicyId(policyId), null, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.POL && ex.Error.Code.Name is "NOT-FOUND" or "VALIDATION")
        {
            return SnapshotRead.Unverified($"POL answered {ex.Error.Code}.");
        }
        catch (DomainException ex)
        {
            LogFailed(logger, ex.Error.Code.Value);
            return SnapshotRead.Unavailable($"POL answered {ex.Error.Code}.");
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(logger, ex);
            return SnapshotRead.Unavailable("POL did not answer.");
        }

        return Parse(JsonSerializer.SerializeToElement(response, SharedKernelJson.Options), lossAt, knownAt);
    }

    /// <summary>Reads the announced members; a response without snapshot reference or policy facts is unverified.</summary>
    internal static SnapshotRead Parse(JsonElement root, Instant lossAt, Instant knownAt)
    {
        var content = Member(root, "content");
        JsonElement? Find(string name) => Member(root, name) ?? (content is { } c ? Member(c, name) : null);

        var snapshotRef = Text(Find("snapshotRef"));
        var policy = Find("policy");
        var policyId = Guid.TryParse(Text(policy is { } p1 ? Member(p1, "policyId") : null), out var pid) ? pid : (Guid?)null;
        var policyNumber = Text(policy is { } p2 ? Member(p2, "policyNumber") : null);
        var productCode = Text(policy is { } p3 ? Member(p3, "productCode") : null);
        var insured = Guid.TryParse(Text(policy is { } p4 ? Member(p4, "insuredPartyId") : null), out var iid) ? iid : (Guid?)null;
        if (snapshotRef is null || policyId is null || policyNumber is null || productCode is null || insured is null)
        {
            return SnapshotRead.Unverified("The POL snapshot has no reference or no policy facts.");
        }

        var inForce = Find("inForce") is { ValueKind: JsonValueKind.True };
        var segment = content is { } c1 ? Member(c1, "segment") : null;
        var term = content is { } c2 ? Member(c2, "term") : null;
        var coverages = new List<string>();
        if (inForce && content is { } c3 && Member(c3, "coverages") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var coverage in list.EnumerateArray())
            {
                var selected = Member(coverage, "selected") is not { ValueKind: JsonValueKind.False };
                if (selected && Text(Member(coverage, "coverageCode")) is { } code)
                {
                    coverages.Add(code);
                }
            }
        }

        return SnapshotRead.Found(new PolicySnapshotFacts(
            snapshotRef,
            Instant.TryParse(Text(Find("validAt")), out var validAt) ? validAt : lossAt,
            Instant.TryParse(Text(Find("knownAt")), out var known) ? known : knownAt,
            inForce,
            Text(Find("status")),
            Text(Find("notInForceReason")),
            policyId.Value,
            policyNumber,
            productCode,
            Text(content is { } c4 ? Member(c4, "productVersion") : null) ?? Text(term is { } t ? Member(t, "productVersion") : null),
            insured.Value,
            Guid.TryParse(Text(segment is { } s ? Member(s, "segmentId") : null), out var segmentId) ? segmentId : null,
            [.. coverages.Distinct(StringComparer.Ordinal)]));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "pol.Snapshot.get failed with {Code}")]
    private static partial void LogFailed(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "pol.Snapshot.get is unavailable")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);

    private static JsonElement? Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? Text(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } e => e.GetString() is { Length: > 0 } s ? s : null,
        { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        _ => null,
    };
}
