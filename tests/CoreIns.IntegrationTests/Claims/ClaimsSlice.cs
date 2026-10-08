using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>A scripted policy the fake POL snapshot answers for: one annual term [Start, End) with selected coverages.</summary>
internal sealed record ScriptedPolicy(Guid PolicyId, string PolicyNumber, Guid InsuredPartyId, string ProductCode, Instant Start, Instant End, IReadOnlyList<string> Coverages);

/// <summary>
/// The CLM slice host: the real Host and database with POL's <c>pol.Snapshot.get</c> played by the generated sandbox
/// double <see cref="FakePolicySnapshotService"/> (SL2-POL-SNAP builds the real one in parallel). The double answers in
/// the shape SL2-POL-SNAP announced; the members are repeated inside <c>content</c> so the untyped pre-release record
/// carries them too. Every policy here is synthetic test data.
/// </summary>
internal sealed class ClaimsSlice : IAsyncDisposable
{
    public const string Handler = "Staff.ClaimsHandler";
    public const string Manager = "Staff.ClaimsManager";

    private readonly ApiHostFactory _root;
    private readonly ConcurrentDictionary<Guid, ScriptedPolicy> _policies = new();

    public ClaimsSlice(string connectionString, Action<IServiceCollection>? services = null)
    {
        _root = new ApiHostFactory(connectionString);
        Factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IPolicySnapshotService>(Snapshots);
            services?.Invoke(s);
        }));
        Client = Factory.CreateClient();
        Snapshots.Setup("pol.Snapshot.get", Answer);
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public FakePolicySnapshotService Snapshots { get; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Now minus <paramref name="days"/> days.</summary>
    public static Instant DaysAgo(double days) => Instant.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-days));

    /// <summary>A policy in force from 100 days ago to 265 days ahead with own damage (OD) and MTPL cover.</summary>
    public ScriptedPolicy Policy(params string[] coverages)
    {
        var policy = new ScriptedPolicy(
            Guid.CreateVersion7(), "POL" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture), Guid.CreateVersion7(),
            "MOTOR-GR", DaysAgo(100), DaysAgo(-265), coverages.Length == 0 ? ["OD", "MTPL"] : coverages);
        _policies[policy.PolicyId] = policy;
        return policy;
    }

    /// <summary>An FNOL body for <paramref name="policy"/>; <paramref name="change"/> edits the JSON (remove or replace members).</summary>
    public static JsonObject Fnol(ScriptedPolicy policy, Instant? lossAt = null, string cause = "COLLISION", bool exposure = true, Action<JsonObject>? change = null)
    {
        var body = new JsonObject
        {
            ["lineOfBusiness"] = "MOTOR",
            ["policyId"] = policy.PolicyId.ToString(),
            ["policyNumber"] = policy.PolicyNumber,
            ["lossAt"] = (lossAt ?? DaysAgo(2)).ToString(),
            ["lossCause"] = cause,
            ["lossLocation"] = "Λεωφ. Συγγρού 120, Αθήνα (synthetic)",
            ["description"] = "Rear-end collision; Γιώργος Νικολάου was driving (synthetic SECRET-DESC)",
            ["channel"] = "STAFF",
            ["receiptMedium"] = "TELEPHONE",
            ["reporter"] = new JsonObject { ["partyId"] = policy.InsuredPartyId.ToString(), ["relationship"] = "INSURED" },
            ["incidents"] = new JsonArray(new JsonObject { ["incidentType"] = "VEHICLE", ["vehicleRef"] = "YXA-1234", ["drivable"] = true, ["damageAreas"] = new JsonArray("REAR") }),
        };
        if (exposure)
        {
            body["exposures"] = new JsonArray(new JsonObject { ["kind"] = "OWN_DAMAGE", ["coverageCode"] = "OD" });
        }

        change?.Invoke(body);
        return body;
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> SubmitAsync(JsonObject body, string roles = Handler, Guid? key = null, bool dryRun = false) =>
        SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/submit" + (dryRun ? "?dryRun=true" : string.Empty), body, roles, key);

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, object? body = null, string? roles = Handler, Guid? key = null, bool withKey = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        request.Headers.AcceptLanguage.ParseAdd("en");
        if (withKey && method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        if (body is not null)
        {
            request.Content = new StringContent(body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        }

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _root.DisposeAsync();
    }

    private SnapshotGetResponse Answer(CoreIns.Testing.Contracts.RecordedCall call)
    {
        var validAt = ((ValidAt?)call.Arguments[0])?.Instant ?? throw new InvalidOperationException("CLM must read the snapshot at the loss instant.");
        var knownAt = (Instant?)call.Arguments[1] ?? throw new InvalidOperationException("CLM must give knownAt.");
        var policyId = ((PolicyId?)call.Arguments[2])?.Value ?? Guid.Empty;
        if (!_policies.TryGetValue(policyId, out var policy))
        {
            throw new DomainException(DomainError.Of(ModuleCode.POL, "NOT-FOUND", "The policy does not exist."));
        }

        var inForce = validAt >= policy.Start && validAt < policy.End;
        var facts = new JsonObject
        {
            ["validAt"] = validAt.ToString(),
            ["knownAt"] = knownAt.ToString(),
            ["inForce"] = inForce,
            ["status"] = inForce ? "IN_FORCE" : validAt < policy.Start ? null : "EXPIRED",
            ["notInForceReason"] = inForce ? null : "NO_TERM_AT_INSTANT",
            ["policy"] = new JsonObject
            {
                ["policyId"] = policy.PolicyId.ToString(),
                ["policyNumber"] = policy.PolicyNumber,
                ["productCode"] = policy.ProductCode,
                ["insuredPartyId"] = policy.InsuredPartyId.ToString(),
            },
        };
        var content = (JsonObject)facts.DeepClone();
        content["productVersion"] = "1.0";
        content["segment"] = new JsonObject { ["segmentId"] = Guid.CreateVersion7().ToString() };
        content["coverages"] = new JsonArray([.. (inForce ? policy.Coverages : []).Select(c => (JsonNode)new JsonObject { ["coverageCode"] = c, ["selected"] = true })]);
        var root = (JsonObject)facts.DeepClone();
        root["snapshotRef"] = $"SNAP-{policy.PolicyId:N}-{validAt.ToDateTimeOffset().ToUnixTimeMilliseconds()}";
        root["content"] = content;
        return JsonSerializer.Deserialize<SnapshotGetResponse>(root.ToJsonString(), SharedKernelJson.Options)!;
    }
}

/// <summary>JSON and SQL helpers for the CLM tests.</summary>
internal static class ClaimsTestExtensions
{
    public static string Text(this JsonNode? node, string path)
    {
        var current = node;
        foreach (var part in path.Split('.'))
        {
            current = int.TryParse(part, out var index) ? current![index] : current![part];
        }

        return current is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : current?.ToJsonString() ?? "null";
    }

    public static async Task<T> ScalarAsync<T>(this PostgresFixture database, string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }
}
