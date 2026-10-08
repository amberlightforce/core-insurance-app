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
/// double <see cref="FakePolicySnapshotService"/>, answering in POL's typed snapshot shape (in force: content with term,
/// segment and selected coverages; not in force: no content). Every policy here is synthetic test data.
/// </summary>
internal sealed class ClaimsSlice : IAsyncDisposable
{
    public const string Handler = "Staff.ClaimsHandler";
    public const string Manager = "Staff.ClaimsManager";

    private readonly ApiHostFactory _root;
    private readonly ConcurrentDictionary<Guid, ScriptedPolicy> _policies = new();

    public ClaimsSlice(string connectionString, Action<IServiceCollection>? services = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        _root = new ApiHostFactory(connectionString, settings: settings);
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
        var hash = Sha256Hash.Parse(new string('a', 64));
        return new SnapshotGetResponse
        {
            SnapshotRef = $"SNAP-{policy.PolicyId:N}-{validAt.ToDateTimeOffset().ToUnixTimeMilliseconds()}",
            ValidAt = validAt,
            KnownAt = knownAt,
            InForce = inForce,
            Status = inForce ? TermStateCode.InForce : validAt < policy.Start ? null : TermStateCode.Expired,
            NotInForceReason = inForce ? null : SnapshotGetResponse.NotInForceReasonValue.NoTermAtInstant,
            Policy = new SnapshotPolicy
            {
                PolicyId = new PolicyId(policy.PolicyId),
                PolicyNumber = PolicyNumber.Parse(policy.PolicyNumber),
                ProductCode = policy.ProductCode,
                InsuredPartyId = policy.InsuredPartyId,
                LegalEntity = "GR-TEST",
                Jurisdiction = "GR",
            },
            Content = !inForce ? null : new SnapshotContent
            {
                Term = new TermView
                {
                    TermId = PolicyTermId.New(),
                    TermNumber = 1,
                    Period = new InstantRange(policy.Start, policy.End),
                    State = TermStateCode.InForce,
                    ProductVersion = ProductVersionNumber.Parse("1.0"),
                    ArtefactHash = hash,
                    ResolutionHash = new ResolutionHash(hash),
                    ConfigurationHash = new ConfigurationHash(hash),
                    Currency = Currency.FromCode("EUR"),
                    PaymentPlanRef = "ANNUAL",
                    WrittenDate = policy.Start.UtcDate,
                    RecordedAt = policy.Start,
                },
                ProductVersion = ProductVersionNumber.Parse("1.0"),
                Segment = new SegmentView
                {
                    SegmentId = SegmentId.New(),
                    TransactionId = PolicyTransactionId.New(),
                    ValidPeriod = new InstantRange(policy.Start, policy.End),
                    RecordedPeriod = new InstantRange(policy.Start, null),
                    SnapshotHash = hash,
                },
                Vehicles = [],
                Drivers = [],
                Coverages = [.. policy.Coverages.Select(c => new CoverageSelection { CoverageCode = c, Selected = true })],
            },
        };
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
