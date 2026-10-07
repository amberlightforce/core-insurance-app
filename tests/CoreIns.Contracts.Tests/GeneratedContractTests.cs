using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.Testing.Contracts;

namespace CoreIns.Contracts.Tests;

/// <summary>
/// F-1c part 3: the generated contract types agree with the published contracts. Samples are generated from the schemas
/// by tools/CoreIns.ContractGen and validated against the schemas in CI (contracts/events/validate.py --instance and
/// tools/CoreIns.ContractGen/validate_samples.py), so a payload that round-trips to the identical JSON is schema-valid.
/// </summary>
public sealed partial class GeneratedContractTests
{
    private static readonly IReadOnlyList<Type> AllTypes = [.. ContractAssemblies.All.SelectMany(a => a.GetTypes())];

    private static readonly Dictionary<string, Type> PayloadTypes = AllTypes
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IEventPayload).IsAssignableFrom(t))
        .ToDictionary(t => Contract(t).RoutingKey, StringComparer.Ordinal);

    [Fact]
    public void Every_catalogued_event_has_a_generated_payload_record_and_vice_versa()
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.Root, "contracts", "events", "catalog.json")))!;
        var entries = catalog["events"]!.AsArray().Select(e => e!.AsObject()).ToList();
        entries.Count.ShouldBe(304);

        var keys = entries.Select(e => $"{e["producer"]!.GetValue<string>().ToLowerInvariant()}.{e["name"]}.v{e["version"]!.GetValue<string>().Split('.')[0]}").ToList();
        PayloadTypes.Keys.Order(StringComparer.Ordinal).ShouldBe(keys.Order(StringComparer.Ordinal));

        var problems = new List<string>();
        foreach (var entry in entries)
        {
            var key = $"{entry["producer"]!.GetValue<string>().ToLowerInvariant()}.{entry["name"]}.v{entry["version"]!.GetValue<string>().Split('.')[0]}";
            var c = Contract(PayloadTypes[key]);
            Check(c.SchemaVersion == entry["version"]!.GetValue<string>(), "version");
            Check(c.AggregateTypes.SequenceEqual(entry["aggregateType"]!.AsArray().Select(a => a!.GetValue<string>())), "aggregate types");
            Check(c.DataClassification.ToString() == entry["dataClassification"]!.GetValue<string>(), "classification");
            Check(c.RequiredBusinessKeys.SequenceEqual(entry["x-business-keys"]!.AsArray().Select(a => a!.GetValue<string>())), "business keys");
            Check(c.SetCompleteness == entry["setCompleteness"]!.GetValue<bool>(), "set completeness");
            Check(c.Topic == entry["topic"]!.GetValue<string>(), "topic");
            Check(c.RegistryName == entry["registryName"]!.GetValue<string>(), "registry name");
            Check(c.OrderingKey == entry["orderingKey"]!.GetValue<string>(), "ordering key");
            Check(c.PayloadStatus.ToString().Equals(entry["status"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase), "payload status");
            Check(PayloadTypes[key].Name == $"{entry["name"]}V{c.Major}", "type name");

            void Check(bool ok, string what)
            {
                if (!ok)
                {
                    problems.Add($"{key}: {what} differs from the catalogue");
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Every_event_sample_round_trips_through_its_payload_record_and_publishes_a_valid_envelope()
    {
        var files = Directory.GetFiles(Repo.EventSamples, "*.json");
        files.Length.ShouldBe(17);
        var publisher = new RecordingEventPublisher();
        var problems = new List<string>();
        var count = 0;
        foreach (var file in files)
        {
            foreach (var envelope in JsonNode.Parse(File.ReadAllText(file))!.AsArray().Select(e => e!.AsObject()))
            {
                count++;
                var key = $"{envelope["producer"]!.GetValue<string>().ToLowerInvariant()}.{envelope["eventType"]}.v{envelope["schemaVersion"]!.GetValue<string>().Split('.')[0]}";
                try
                {
                    var type = PayloadTypes[key];
                    var payload = envelope["payload"]!;
                    var typed = payload.Deserialize(type, SharedKernelJson.Options)!;
                    var again = JsonSerializer.SerializeToNode(typed, type, SharedKernelJson.Options);
                    if (!JsonNode.DeepEquals(again, payload))
                    {
                        problems.Add($"{key}: round-trip changed the payload: {again?.ToJsonString()}");
                        continue;
                    }

                    var contract = ((IEventPayload)typed).Contract;
                    var keys = BusinessKeys.From(envelope["businessKeys"]!.AsObject().Select(p => KeyValuePair.Create(p.Key, p.Value!.GetValue<string>())));
                    EventSet? set = envelope["set_id"] is { } setId
                        ? new EventSet(Guid.Parse(setId.GetValue<string>()), envelope["set_size"]!.GetValue<int>(), envelope["index"]!.GetValue<int>())
                        : null;
                    var published = publisher.Publish(new OutgoingEvent(EventDescriptors.From(contract), envelope["aggregateType"]!.GetValue<string>(),
                        envelope["aggregateId"]!.GetValue<string>(), typed, keys) { Set = set });
                    JsonNode.DeepEquals(published.Payload, payload).ShouldBeTrue();
                }
                catch (Exception ex) when (ex is JsonException or EnvelopeValidationException or KeyNotFoundException or ArgumentException or InvalidOperationException)
                {
                    problems.Add($"{key}: {ex.Message}");
                }
            }
        }

        problems.ShouldBeEmpty();
        count.ShouldBe(608, "a maximal and a minimal sample per event");
    }

    [Fact]
    public void Every_generated_dto_round_trips_its_schema_sample()
    {
        var problems = new List<string>();
        var count = 0;
        foreach (var (code, ns) in Modules)
        {
            foreach (var schema in CannedSamples.SchemaNames(code))
            {
                count++;
                var type = AllTypes.SingleOrDefault(t => t.Namespace == ns + ".Api" && t.Name == schema && !t.IsNested);
                if (type is null)
                {
                    problems.Add($"{code}.{schema}: no generated type");
                    continue;
                }

                var sample = CannedSamples.SchemaJson(code, schema);
                try
                {
                    var again = JsonSerializer.SerializeToNode(sample.Deserialize(type, SharedKernelJson.Options), type, SharedKernelJson.Options);
                    if (!JsonNode.DeepEquals(again, sample))
                    {
                        problems.Add($"{code}.{schema}: round-trip changed the sample: {again?.ToJsonString()}");
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
                {
                    problems.Add($"{code}.{schema}: {ex.Message}");
                }
            }
        }

        problems.ShouldBeEmpty();
        count.ShouldBeGreaterThan(1500);
    }

    [Fact]
    public async Task Every_sandbox_double_implements_its_interface_records_calls_and_answers_with_a_canned_response()
    {
        var fakes = typeof(FakeService).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(FakeService)) && !t.IsAbstract).ToList();
        var interfaces = AllTypes.Where(t => t.IsInterface && t.Name.EndsWith("Service", StringComparison.Ordinal)
                                             && t.GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>() is not null).ToList();
        interfaces.Count.ShouldBeGreaterThan(100);
        fakes.Count.ShouldBe(interfaces.Count);

        var operations = 0;
        foreach (var iface in interfaces)
        {
            var fakeType = fakes.Single(f => iface.IsAssignableFrom(f));
            var fake = (FakeService)Activator.CreateInstance(fakeType)!;
            foreach (var method in iface.GetMethods())
            {
                operations++;
                var args = method.GetParameters().Select(p => p.ParameterType == typeof(CommandOptions) ? CommandOptions.New() : Default(p)).ToArray();
                var task = (Task)method.Invoke(fake, args)!;
                await task;
                if (method.ReturnType.IsGenericType)
                {
                    task.GetType().GetProperty("Result")!.GetValue(task).ShouldNotBeNull($"{fakeType.Name}.{method.Name}");
                }
            }

            fake.Calls.Count.ShouldBe(iface.GetMethods().Length);
            fake.Calls.Select(c => c.OperationId).ShouldBeUnique();
        }

        operations.ShouldBe(425, "every operation with x-in-process or x-consumers");
    }

    [Fact]
    public async Task A_double_can_be_scripted_per_operation()
    {
        var fake = new Testing.Contracts.Fakes.Policy.FakePolicyJobService();
        var canned = fake.Canned<Modules.Policy.Contracts.Api.JobBindResponse>("pol.Job.bind");
        fake.Setup("pol.Job.bind", canned with { CoverNoteDocumentId = null });
        var request = new Modules.Policy.Contracts.Api.JobBindRequest { JobId = JobId.New(), VersionNo = 1, PaymentPlanOption = "PLAN-1" };

        var response = await fake.BindAsync(request, CommandOptions.New(), TestContext.Current.CancellationToken);

        response.CoverNoteDocumentId.ShouldBeNull();
        fake.CallsTo("pol.Job.bind").Single().Arguments[0].ShouldBe(request);
        fake.Fail("pol.Job.bind", new InvalidOperationException("POL-ERR-GATE-FAILED"));
        await Should.ThrowAsync<InvalidOperationException>(() => fake.BindAsync(request, CommandOptions.New(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_recording_publisher_rejects_envelopes_that_break_the_catalogue()
    {
        var publisher = new RecordingEventPublisher();
        var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.EventSamples, "pol.json")))!
            .AsArray().First(e => e!["eventType"]!.GetValue<string>() == "PolicyBound")!["payload"]!
            .Deserialize<Modules.Policy.Contracts.Events.PolicyBoundV1>(SharedKernelJson.Options)!;
        var policyId = PolicyId.New();

        Should.Throw<EnvelopeValidationException>(() => publisher.Publish(payload, policyId.ToString(), BusinessKeys.Empty.With(BusinessKeyNames.PolicyId, policyId)))
            .Message.ShouldContain("quoteId");

        var keys = BusinessKeys.Empty.With(BusinessKeyNames.PolicyId, policyId).With(BusinessKeyNames.PolicyTermId, payload.TermId)
            .With(BusinessKeyNames.TransactionId, payload.TransactionId).With(BusinessKeyNames.JobId, JobId.New()).With(BusinessKeyNames.QuoteId, QuoteId.New());
        publisher.Publish(payload, policyId.ToString(), keys).AggregateSequence.ShouldBe(1);
        publisher.Publish(payload, policyId.ToString(), keys).AggregateSequence.ShouldBe(2);
        publisher.PublishedOf<Modules.Policy.Contracts.Events.PolicyBoundV1>().Count.ShouldBe(2);

        var wrongDescriptor = EventDescriptors.For<Modules.Policy.Contracts.Events.PolicyCancelledV1>();
        Should.Throw<EnvelopeValidationException>(() => publisher.Publish(new OutgoingEvent(wrongDescriptor, "Policy", policyId.ToString(), payload, keys)));
    }

    [Fact]
    public void Error_code_catalogues_follow_the_contract_format()
    {
        var catalogues = AllTypes.Where(t => t is { IsAbstract: true, IsSealed: true } && t.Name.EndsWith("ErrorCodes", StringComparison.Ordinal)
                                             && t.GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>() is not null).ToList();
        catalogues.Count.ShouldBe(17);
        var all = new List<string>();
        foreach (var catalogue in catalogues)
        {
            var prefix = (string)catalogue.GetField("Prefix")!.GetValue(null)!;
            var codes = (IReadOnlyList<string>)catalogue.GetProperty("All")!.GetValue(null)!;
            codes.ShouldNotBeEmpty();
            codes.ShouldAllBe(code => code.StartsWith(prefix, StringComparison.Ordinal) && CodePattern().IsMatch(code));
            all.AddRange(codes);
        }

        all.ShouldBeUnique();
        Platform.Contracts.PlatformErrorCodes.All.ShouldContain("PLT-ERR-IDEMPOTENCY-KEY-REQUIRED");
    }

    private static readonly (string Code, string Namespace)[] Modules =
    [
        ("bil", "CoreIns.Modules.Billing.Contracts"), ("chn", "CoreIns.Modules.Channels.Contracts"), ("clm", "CoreIns.Modules.Claims.Contracts"),
        ("cmp", "CoreIns.Modules.Compliance.Contracts"), ("dat", "CoreIns.Modules.Data.Contracts"), ("doc", "CoreIns.Modules.Documents.Contracts"),
        ("fin", "CoreIns.Modules.Finance.Contracts"), ("mig", "CoreIns.Modules.Migration.Contracts"), ("mkt", "CoreIns.Modules.Market.Contracts"),
        ("pfc", "CoreIns.Modules.Product.Contracts"), ("plt", "CoreIns.Platform.Contracts"), ("pol", "CoreIns.Modules.Policy.Contracts"),
        ("pty", "CoreIns.Modules.Party.Contracts"), ("rat", "CoreIns.Modules.Rating.Contracts"), ("ri", "CoreIns.Modules.Reinsurance.Contracts"),
        ("uw", "CoreIns.Modules.Underwriting.Contracts"), ("wrk", "CoreIns.Modules.Work.Contracts"),
    ];

    private static EventContract Contract(Type type) =>
        (EventContract)type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private static object? Default(ParameterInfo parameter) =>
        parameter.HasDefaultValue ? parameter.DefaultValue
        : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
        : null;

    [GeneratedRegex("^(PTY|PFC|RAT|UW|POL|BIL|CLM|RI|FIN|DOC|CMP|CHN|WRK|PLT|DAT|MIG|MKT)-ERR-[A-Z0-9]+(-[A-Z0-9]+)*$")]
    private static partial Regex CodePattern();
}

/// <summary>Repository paths.</summary>
internal static class Repo
{
    public static string Root { get; } = Find();

    public static string EventSamples => Path.Combine(Root, "tests", "CoreIns.Contracts.Tests", "Generated", "Samples", "events");

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CoreIns.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("CoreIns.sln not found.");
    }
}
