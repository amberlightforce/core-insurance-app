using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoreIns.ContractGen;

/// <summary>Counts reported after a run.</summary>
internal sealed class Stats
{
    public int Events { get; set; }

    public int CommonTypes { get; set; }

    public int Dtos { get; set; }

    public int Interfaces { get; set; }

    public int Operations { get; set; }

    public int InProcessOperations { get; set; }

    public int ErrorConstants { get; set; }

    public int Fakes { get; set; }

    public List<string> Issues { get; } = [];
}

/// <summary>Produces every generated file (repo-relative path → content) from the contracts.</summary>
internal sealed class Generator
{
    private const string Pc = "global::CoreIns.Platform.Contracts";
    private const string SkIds = "global::CoreIns.SharedKernel.Identifiers";

    private static readonly JsonSerializerOptions SampleJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Platform error codes registered by D-API-14 (contract §3.5.4 error model).</summary>
    private static readonly string[] RegisteredPlatformCodes =
    [
        "PLT-ERR-IDEMPOTENCY-KEY-REQUIRED", "PLT-ERR-IDEMPOTENCY-KEY-INVALID", "PLT-ERR-IDEMPOTENCY-MISMATCH",
        "PLT-ERR-IDEMPOTENCY-IN-PROGRESS", "PLT-ERR-VALIDATION", "PLT-ERR-INVALID-STATE-TRANSITION",
        "PLT-ERR-AUTHORITY-DENIED", "PLT-ERR-AUTHORITY-REFERRAL-REQUIRED", "PLT-ERR-INTERNAL",
    ];

    private readonly Documents _docs;
    private readonly TypeMapper _mapper;
    private readonly SampleGenerator _samples;
    private readonly SortedDictionary<string, string> _files = new(StringComparer.Ordinal);

    public Generator(string repoRoot)
    {
        _docs = new Documents(Path.Combine(repoRoot, "contracts"));
        _mapper = new TypeMapper(_docs);
        _samples = new SampleGenerator(_docs);
    }

    public Stats Stats { get; } = new();

    public SortedDictionary<string, string> Run()
    {
        Common();
        Events();
        Apis();
        return _files;
    }

    private void Add(string path, string content)
    {
        if (!_files.TryAdd(path, content))
        {
            throw new InvalidOperationException($"Two generated files at {path}.");
        }
    }

    private static string Json(JsonNode node) => JsonSerializer.Serialize(node, SampleJson).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    // ------------------------------------------------------------------------------------------------------- common

    private void Common()
    {
        var defs = (JsonObject)_docs.LoadObject(_docs.EventsCommon)["$defs"]!;
        var ctx = new MapContext("common");
        foreach (var (name, value) in defs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (TypeMapper.MappedCommon.Contains(name) && name != "RepointMap")
            {
                continue;
            }

            var node = (JsonObject)value!;
            TypeDef def;
            if (name == "RepointMap")
            {
                def = _mapper.BuildRecord(new Located((JsonObject)node["items"]!, _docs.EventsCommon), "RepointMapItem", ctx,
                    Names.FirstLine(((JsonObject)node["items"]!)["description"]?.ToString()));
            }
            else if (node["enum"] is JsonArray values)
            {
                def = TypeMapper.BuildEnum(name, values, node);
            }
            else if (node["properties"] is JsonObject)
            {
                def = _mapper.BuildRecord(new Located(node, _docs.EventsCommon), name, ctx, Names.FirstLine(node["description"]?.ToString()));
            }
            else
            {
                Stats.Issues.Add($"events common $defs/{name}: no C# mapping (not an object or enum); add it to TypeMapper.MappedCommon.");
                continue;
            }

            def.Remarks.Add($"contracts/events/common.schema.json#/$defs/{name}.");
            Emit(Layout.Generated("PLT") + "/Common", TypeMapper.CommonNs, def);
            Stats.CommonTypes++;
        }

        var schemas = (JsonObject)_docs.LoadObject(_docs.OpenApiCommon)["components"]!["schemas"]!;
        foreach (var (name, value) in schemas.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var located = new Located((JsonObject)value!, _docs.OpenApiCommon);
            if (name == "PageEnvelope" || TypeMapper.ComponentKind(located) != TypeMapper.ComponentShape.Record)
            {
                continue;
            }

            var def = _mapper.BuildRecord(located, name, new MapContext("PLT"), Names.FirstLine(located.Node["description"]?.ToString()));
            def.Remarks.Add($"contracts/openapi/common.yaml#/components/schemas/{name}.");
            Emit(Layout.Generated("PLT") + "/Common", TypeMapper.CommonNs, def);
            Stats.CommonTypes++;
        }
    }

    private void Emit(string dir, string ns, TypeDef def) =>
        Add($"{dir}/{def.Name}.cs", CodeWriter.File(ns, w => w.Type(def)));

    // ------------------------------------------------------------------------------------------------------- events

    private void Events()
    {
        var catalog = _docs.LoadObject(Path.Combine(_docs.EventsRoot, "catalog.json"));
        var samplesByModule = new SortedDictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var entry in ((JsonArray)catalog["events"]!).OfType<JsonObject>()
                     .OrderBy(e => e["producer"]!.GetValue<string>(), StringComparer.Ordinal)
                     .ThenBy(e => e["name"]!.GetValue<string>(), StringComparer.Ordinal)
                     .ThenBy(e => e["version"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            var name = entry["name"]!.GetValue<string>();
            var version = entry["version"]!.GetValue<string>();
            var major = version.Split('.')[0];
            var producer = entry["producer"]!.GetValue<string>();
            var schemaRel = entry["schema"]!.GetValue<string>();
            var schemaFile = Path.Combine(_docs.EventsRoot, schemaRel.Replace('/', Path.DirectorySeparatorChar));
            var schema = _docs.LoadObject(schemaFile);
            var payload = new Located((JsonObject)schema["$defs"]!["Payload"]!, schemaFile);
            var typeName = $"{name}V{major}";
            var aggregates = ((JsonArray)entry["aggregateType"]!).Select(a => a!.GetValue<string>()).ToList();
            var keys = ((JsonArray)entry["x-business-keys"]!).Select(a => a!.GetValue<string>()).ToList();
            var classification = entry["dataClassification"]!.GetValue<string>();
            var status = entry["status"]!.GetValue<string>();
            var consumers = ((JsonArray?)entry["consumers"] ?? []).Select(c => c!["module"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal).ToList();
            var personal = ((JsonArray?)entry["personalDataFields"] ?? []).Select(c => c!.GetValue<string>()).ToList();
            var set = entry["setCompleteness"]?.GetValue<bool>() ?? false;

            var scope = new NameScope(typeName, "EventType", "SchemaVersion", "Producer", "Classification", "AggregateTypes",
                "RequiredBusinessKeys", "Descriptor", "Contract", "EqualityContract");
            var record = _mapper.BuildRecord(payload, typeName, new MapContext(producer),
                $"Payload of {producer.ToLowerInvariant()}.{name} v{version}: {schema["description"]}.", scope,
                $"{Pc}.Events.IEventPayload<{typeName}>");
            record.Remarks.Add($"Schema contracts/events/{schemaRel}; aggregate {string.Join(" | ", aggregates)} (ordering key {entry["orderingKey"]}); trigger: {entry["trigger"]}.");
            record.Remarks.Add($"Payload status {status}{(status == "minimal" ? $" ({schema["x-payload-note"]}); open parts are JsonElement until the producer defines them in a minor version" : string.Empty)}.");
            record.Remarks.Add($"Required business keys (D-CON-28): {string.Join(", ", keys)}. Consumers: {(consumers.Count == 0 ? "none" : string.Join(", ", consumers))}.");
            if (set)
            {
                record.Remarks.Add("Set completeness (D4, D-CON-19): set_id, set_size and index are required on the envelope.");
            }

            static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Names.Literal)) + "]";
            record.Members.Add($"/// <summary>Catalogued event name.</summary>");
            record.Members.Add($"public const string EventType = {Names.Literal(name)};");
            record.Members.Add(string.Empty);
            record.Members.Add($"/// <summary>Schema version (<c>major.minor</c>) this record follows.</summary>");
            record.Members.Add($"public const string SchemaVersion = {Names.Literal(version)};");
            record.Members.Add(string.Empty);
            record.Members.Add($"/// <summary>The only producing module.</summary>");
            record.Members.Add($"public const {SkIds}.ModuleCode Producer = {SkIds}.ModuleCode.{producer};");
            record.Members.Add(string.Empty);
            record.Members.Add($"/// <summary>Pinned highest personal-data class of the payload (envelope <c>dataClassification</c>).</summary>");
            record.Members.Add($"public const {SkIds}.DataClassification Classification = {SkIds}.DataClassification.{classification};");
            record.Members.Add(string.Empty);
            record.Members.Add($"/// <summary>Allowed aggregate types (envelope <c>aggregateType</c>).</summary>");
            record.Members.Add($"public static global::System.Collections.Generic.IReadOnlyList<string> AggregateTypes {{ get; }} = {List(aggregates)};");
            record.Members.Add(string.Empty);
            record.Members.Add($"/// <summary>Lineage keys every envelope must carry (<c>x-business-keys</c>; <c>a|b</c> = at least one).</summary>");
            record.Members.Add($"public static global::System.Collections.Generic.IReadOnlyList<string> RequiredBusinessKeys {{ get; }} = {List(keys)};");
            record.Members.Add(string.Empty);
            record.Members.Add($"/// <summary>The catalogue entry.</summary>");
            record.Members.Add($"public static {Pc}.Events.EventContract Descriptor {{ get; }} = new(Producer, EventType, SchemaVersion, AggregateTypes, Classification)");
            record.Members.Add("{");
            record.Members.Add("    RequiredBusinessKeys = RequiredBusinessKeys,");
            record.Members.Add($"    SetCompleteness = {(set ? "true" : "false")},");
            record.Members.Add($"    OrderingKey = {Names.Literal(entry["orderingKey"]!.GetValue<string>())},");
            record.Members.Add($"    PayloadStatus = {Pc}.Events.PayloadStatus.{(status == "full" ? "Full" : "Minimal")},");
            record.Members.Add($"    PersonalDataFields = {List(personal)},");
            record.Members.Add($"    Consumers = [{string.Join(", ", consumers.Select(c => $"{SkIds}.ModuleCode.{c}"))}],");
            record.Members.Add($"    SchemaPath = {Names.Literal(schemaRel)},");
            record.Members.Add("};");
            record.Members.Add(string.Empty);
            record.Members.Add($"/// <inheritdoc />");
            record.Members.Add($"{Pc}.Events.EventContract {Pc}.Events.IEventPayload.Contract => Descriptor;");

            Emit(Layout.Generated(producer) + "/Events", Layout.EventsNamespace(producer), record);
            Stats.Events++;

            var arr = samplesByModule.TryGetValue(producer, out var a) ? a : samplesByModule[producer] = [];
            foreach (var maximal in new[] { true, false })
            {
                var path = $"{producer}.{name}.v{major}.{(maximal ? "max" : "min")}";
                var envelope = new JsonObject
                {
                    ["eventId"] = SampleGenerator.Uuid(path + ".eventId"),
                    ["eventType"] = name,
                    ["schemaVersion"] = version,
                    ["producer"] = producer,
                    ["aggregateType"] = aggregates[0],
                    ["aggregateId"] = SampleGenerator.Uuid(path + ".aggregateId"),
                    ["aggregateSequence"] = 1,
                    ["occurredAt"] = "2026-10-07T09:00:00Z",
                    ["recordedAt"] = "2026-10-07T09:00:01Z",
                    ["legalEntity"] = "GR-TEST",
                    ["jurisdiction"] = "GR",
                    ["configurationHash"] = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
                    ["businessKeys"] = new JsonObject(keys.Select(k => k.Split('|')[0]).Distinct()
                        .Select(k => new KeyValuePair<string, JsonNode?>(k, SampleGenerator.Uuid(path + ".bk." + k)))),
                    ["correlationId"] = "4bf92f3577b34da6a3ce929d0e0e4736",
                    ["causationId"] = null,
                    ["actor"] = new JsonObject { ["kind"] = "SERVICE", ["id"] = "contract-samples" },
                    ["aiInteractionId"] = null,
                    ["origin"] = "LIVE",
                    ["dataClassification"] = classification,
                    ["payload"] = _samples.Sample(payload, path, maximal),
                };
                if (set)
                {
                    envelope["set_id"] = SampleGenerator.Uuid(path + ".set");
                    envelope["set_size"] = 1;
                    envelope["index"] = 1;
                }

                arr.Add(envelope);
            }
        }

        foreach (var (module, arr) in samplesByModule)
        {
            Add($"{Layout.ContractTestsProject}/Generated/Samples/events/{module.ToLowerInvariant()}.json", Json(arr));
        }
    }

    // --------------------------------------------------------------------------------------------------------- apis

    private sealed record Operation(string Id, string Method, string Path, JsonObject Node, string File)
    {
        public string Resource => Id.Split('.')[1];

        public string Verb => Id.Split('.')[2];

        public bool InProcess =>
            Node["x-in-process"]?.GetValue<bool>() == true || Node["x-consumers"] is JsonArray { Count: > 0 };

        public bool IsCommand => Node["x-operation-kind"]?.GetValue<string>() == "command";
    }

    private void Apis()
    {
        var errorCodes = new SortedDictionary<string, SortedDictionary<string, List<(int Status, string Op)>>>(StringComparer.Ordinal);
        foreach (var module in Layout.Modules.Keys)
        {
            var file = Path.Combine(_docs.OpenApiRoot, module.ToLowerInvariant() + ".yaml");
            var doc = _docs.LoadObject(file);
            var ctx = new MapContext(module);
            var schemas = (JsonObject?)doc["components"]?["schemas"] ?? [];
            var samples = new JsonObject();
            var schemaSamples = new JsonObject();

            foreach (var (name, value) in schemas.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var located = new Located((JsonObject)value!, file);
                switch (TypeMapper.ComponentKind(located))
                {
                    case TypeMapper.ComponentShape.Record:
                        var record = _mapper.BuildRecord(located, name, ctx, Names.FirstLine(located.Node["description"]?.ToString()));
                        if (located.Node["x-status"] is JsonValue st)
                        {
                            record.Remarks.Add($"x-status: {st}.");
                        }

                        Emit(Layout.Generated(module) + "/Api", Layout.ApiNamespace(module), record);
                        schemaSamples[name] = _samples.Sample(located, $"{module}.{name}", maximal: true);
                        Stats.Dtos++;
                        break;
                    case TypeMapper.ComponentShape.Enum:
                        Emit(Layout.Generated(module) + "/Api", Layout.ApiNamespace(module), TypeMapper.BuildEnum(name, (JsonArray)located.Node["enum"]!, located.Node));
                        Stats.Dtos++;
                        break;
                }
            }

            var operations = new List<Operation>();
            foreach (var (path, item) in (JsonObject)doc["paths"]!)
            {
                foreach (var (method, opNode) in (JsonObject)item!)
                {
                    if (method is "get" or "post" or "put" or "patch" or "delete" && opNode is JsonObject op)
                    {
                        operations.Add(new Operation(op["operationId"]!.GetValue<string>(), method.ToUpperInvariant(), path, op, file));
                    }
                }
            }

            operations.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            Stats.Operations += operations.Count;
            foreach (var op in operations)
            {
                foreach (var code in ((JsonArray?)op.Node["x-error-codes"] ?? []).OfType<JsonObject>())
                {
                    var text = code["code"]!.GetValue<string>();
                    var owner = text.Split("-ERR-")[0];
                    if (!Layout.Modules.ContainsKey(owner))
                    {
                        Stats.Issues.Add($"{op.Id}: error code {text} has no module prefix.");
                        continue;
                    }

                    var byCode = errorCodes.TryGetValue(owner, out var m) ? m : errorCodes[owner] = new(StringComparer.Ordinal);
                    (byCode.TryGetValue(text, out var uses) ? uses : byCode[text] = []).Add(((int)(code["status"]?.GetValue<long>() ?? 0), op.Id));
                }
            }

            foreach (var group in operations.Where(o => o.InProcess).GroupBy(o => o.Resource).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Interface(module, group.ToList(), ctx, samples);
            }

            Add($"{Layout.TestingProject}/Generated/Samples/{module.ToLowerInvariant()}.json",
                Json(new JsonObject { ["responses"] = samples, ["schemas"] = schemaSamples }));
        }

        foreach (var code in RegisteredPlatformCodes)
        {
            var byCode = errorCodes.TryGetValue("PLT", out var m) ? m : errorCodes["PLT"] = new(StringComparer.Ordinal);
            if (!byCode.ContainsKey(code))
            {
                byCode[code] = [];
            }
        }

        foreach (var (module, codes) in errorCodes)
        {
            ErrorCodes(module, codes);
        }
    }

    private sealed record Param(string Name, string CsName, TypeRef Type, bool Required, string In, string? Doc);

    private void Interface(string module, List<Operation> ops, MapContext ctx, JsonObject samples)
    {
        var moduleName = Layout.Name(module);
        var resource = ops[0].Resource;
        var iface = $"I{moduleName}{Names.Pascal(resource)}Service";
        var ns = Layout.RootNamespace(module);
        var fake = $"Fake{moduleName}{Names.Pascal(resource)}Service";
        var dummy = new RecordDef { Name = "Parameters" };

        var w = new CodeWriter();
        var fw = new CodeWriter();
        w.Doc($"In-process contract of {module.ToLowerInvariant()}.{resource} (D-ARC-16): the operations other modules call, with the request/response schemas, error codes, idempotency and dry-run rules of contracts/openapi/{module.ToLowerInvariant()}.yaml.",
            ["Implemented by the owner module; callers in earlier waves build against the sandbox double " + fake + " (D-PRG-07)."]);
        w.Line("[global::System.CodeDom.Compiler.GeneratedCode(\"CoreIns.ContractGen\", \"1.0\")]");
        w.Open($"public interface {iface}");
        fw.Doc($"Sandbox double of {ns}.{iface} (D-PRG-07): records every call and answers with a schema-valid canned response (override per operation with Setup / Fail).");
        fw.Line("[global::System.CodeDom.Compiler.GeneratedCode(\"CoreIns.ContractGen\", \"1.0\")]");
        fw.Open($"public sealed class {fake} : global::CoreIns.Testing.Contracts.FakeService, global::{ns}.{iface}");
        fw.Doc($"Creates the double with the canned responses of {module}.");
        fw.Line($"public {fake}()");
        fw.Line($"    : base({Names.Literal(module.ToLowerInvariant())})");
        fw.Line("{");
        fw.Line("}");

        var first = true;
        foreach (var op in ops)
        {
            Stats.InProcessOperations++;
            var parameters = new List<Param>();
            var hasDryRun = false;
            foreach (var p in ((JsonArray?)op.Node["parameters"] ?? []).OfType<JsonObject>())
            {
                var param = p["$ref"] is JsonValue r ? _docs.Resolve(op.File, r.GetValue<string>()) : new Located(p, op.File);
                var name = param.Node["name"]!.GetValue<string>();
                var location = param.Node["in"]!.GetValue<string>();
                if (location == "header")
                {
                    continue;
                }

                if (name == "dryRun")
                {
                    hasDryRun = true;
                    continue;
                }

                var type = _mapper.Map(new Located((JsonObject)param.Node["schema"]!, param.File), name, dummy, ctx, out _);
                if (dummy.Nested is [EnumDef inlineEnum])
                {
                    // An inline enum parameter becomes its own Api enum named after the operation.
                    var enumName = Names.Pascal(op.Resource) + Names.Pascal(op.Verb) + Names.Pascal(name);
                    var named = new EnumDef { Name = enumName, Summary = $"Values of the '{name}' parameter of {op.Id}." };
                    named.Members.AddRange(inlineEnum.Members);
                    Emit(Layout.Generated(module) + "/Api", Layout.ApiNamespace(module), named);
                    Stats.Dtos++;
                    type = new TypeRef($"global::{Layout.ApiNamespace(module)}.{enumName}", true);
                    dummy.Nested.Clear();
                }

                parameters.Add(new Param(name, Names.Camel(name), type, param.Node["required"]?.GetValue<bool>() == true, location,
                    Names.FirstLine(param.Node["description"]?.ToString())));
            }

            if (dummy.Nested.Count > 0)
            {
                throw new InvalidDataException($"{op.Id}: a parameter needs a nested type; not supported.");
            }

            TypeRef? body = null;
            var bodyRequired = false;
            if (op.Node["requestBody"] is JsonObject rb)
            {
                var bodySchema = (JsonObject)rb["content"]!["application/json"]!["schema"]!;
                body = _mapper.Map(new Located(bodySchema, op.File), "request", dummy, ctx, out _);
                bodyRequired = rb["required"]?.GetValue<bool>() == true;
            }

            TypeRef? response = null;
            JsonObject? responseSchema = null;
            foreach (var (status, resp) in ((JsonObject)op.Node["responses"]!).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (status.StartsWith('2') && resp?["content"]?["application/json"]?["schema"] is JsonObject s)
                {
                    responseSchema = s;
                    response = _mapper.Map(new Located(s, op.File), "response", dummy, ctx, out _);
                    break;
                }
            }

            if (dummy.Nested.Count > 0)
            {
                throw new InvalidDataException($"{op.Id}: an inline request/response schema needs a nested type; not supported.");
            }

            var sig = new List<string>();
            var args = new List<string>();
            var taken = new HashSet<string>(StringComparer.Ordinal) { "request", "options", "cancellationToken" };
            foreach (var p in parameters.Where(p => p.Required))
            {
                var csName = taken.Add(p.CsName) ? p.CsName : p.CsName + "Parameter";
                sig.Add($"{p.Type.Text} {csName}");
                args.Add(csName);
            }

            if (body is not null && bodyRequired)
            {
                sig.Add($"{body.Text} request");
                args.Add("request");
            }

            if (op.IsCommand)
            {
                sig.Add($"{Pc}.CommandOptions options");
                args.Add("options");
            }

            if (body is not null && !bodyRequired)
            {
                sig.Add($"{body.Text}? request = null");
                args.Add("request");
            }

            foreach (var p in parameters.Where(p => !p.Required))
            {
                var csName = taken.Add(p.CsName) ? p.CsName : p.CsName + "Parameter";
                sig.Add($"{p.Type.Text}? {csName} = null");
                args.Add(csName);
            }

            sig.Add("global::System.Threading.CancellationToken cancellationToken = default");
            var returns = response is null ? "global::System.Threading.Tasks.Task" : $"global::System.Threading.Tasks.Task<{response.Text}>";
            var method = Names.Pascal(op.Verb) + "Async";

            var remarks = new List<string>
            {
                $"Operation {op.Id} ({op.Node["x-operation-kind"]}; HTTP {op.Method} {op.Path}).",
                $"Maturity: {op.Node["x-maturity"]} (D-API-06){(op.Node["x-maturity"]?.GetValue<string>() == "pre-release" ? ": the owner may still tighten inputs and outputs until the first consumer work package that calls it merges" : string.Empty)}.",
                $"Status: {op.Node["x-status"]}{(op.Node["x-typed"] is { } typed ? $"; fully typed from {typed}" : string.Empty)}. Wave {op.Node["x-wave"]}.",
                $"Exposure: {string.Join(", ", ((JsonArray?)op.Node["x-exposure"] ?? []).Select(x => x!.ToString()))}; consumers: {string.Join(", ", ((JsonArray?)op.Node["x-consumers"] ?? []).Select(x => x!.ToString()))}.",
            };
            if (op.IsCommand)
            {
                remarks.Add($"Command: idempotent on options.IdempotencyKey{(hasDryRun ? "; supports dry-run (options.DryRun)" : "; no dry-run")}.");
            }

            var errors = ((JsonArray?)op.Node["x-error-codes"] ?? []).OfType<JsonObject>().Select(c => $"{c["code"]} ({c["status"]})").ToList();
            if (errors.Count > 0)
            {
                remarks.Add("Errors: " + string.Join(", ", errors) + ".");
            }

            if (!first)
            {
                w.Line();
                fw.Line();
            }

            first = false;
            w.Doc(op.Node["summary"]?.ToString() ?? op.Id, remarks);
            foreach (var p in parameters)
            {
                w.Line($"/// <param name=\"{p.CsName.TrimStart('@')}\">{Names.Xml(p.Doc is { Length: > 0 } ? p.Doc : $"{p.In} parameter '{p.Name}'")}</param>");
            }

            w.Line($"{returns} {method}({string.Join(", ", sig)});");

            fw.Line("/// <inheritdoc />");
            var call = $"{Names.Literal(op.Id)}, [{string.Join(", ", args)}], cancellationToken";
            fw.Line(response is null
                ? $"public {returns} {method}({string.Join(", ", sig)}) => RespondAsync({call});"
                : $"public {returns} {method}({string.Join(", ", sig)}) => RespondAsync<{response.Text}>({call});");

            if (responseSchema is not null)
            {
                samples[op.Id] = _samples.Sample(new Located(responseSchema, op.File), $"{module}.{op.Id}.response", maximal: true);
            }
        }

        w.Close();
        fw.Close();
        Add($"{Layout.Generated(module)}/Services/{iface}.cs", CodeWriter.Header + "\n" + $"namespace {ns};\n\n" + w);
        Add($"{Layout.TestingProject}/Generated/Fakes/{Layout.Name(module)}/{fake}.cs",
            CodeWriter.Header + "\n" + $"namespace {Layout.FakesNamespace}.{Layout.Name(module)};\n\n" + fw);
        Stats.Interfaces++;
        Stats.Fakes++;
    }

    private void ErrorCodes(string module, SortedDictionary<string, List<(int Status, string Op)>> codes)
    {
        var name = $"{Layout.Name(module)}ErrorCodes";
        var prefix = module + "-ERR-";
        var w = new CodeWriter();
        w.Doc($"Error codes of {module} (contract §3.5.4, D-API-01 format <MOD>-ERR-<NNN or NAME>) declared in the x-error-codes of contracts/openapi{(module == "PLT" ? " and registered by D-API-14" : string.Empty)}. Problem Details type is /problems/<CODE> (D-API-15).");
        w.Line("[global::System.CodeDom.Compiler.GeneratedCode(\"CoreIns.ContractGen\", \"1.0\")]");
        w.Open($"public static class {name}");
        var scope = new NameScope(name, "All", "HttpStatus", "Prefix");
        var members = new List<(string Code, string Member, int Status)>();
        foreach (var (code, uses) in codes)
        {
            var member = scope.Take(Names.Pascal(code[prefix.Length..]), "Code");
            var statuses = uses.Select(u => u.Status).Where(s => s != 0).Distinct().Order().ToList();
            if (statuses.Count > 1)
            {
                Stats.Issues.Add($"{code}: mapped to several HTTP statuses ({string.Join(", ", statuses)}) by {string.Join(", ", uses.Select(u => u.Op).Distinct())}.");
            }

            var status = uses.Where(u => u.Status != 0).GroupBy(u => u.Status).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
            members.Add((code, member, status));
            var ops = uses.Select(u => u.Op).Distinct().Order(StringComparer.Ordinal).ToList();
            w.Doc($"{code}{(status == 0 ? string.Empty : $" (HTTP {status})")}.",
                [ops.Count == 0 ? "Registered by D-API-14." : $"Declared by {ops.Count} operation(s): {string.Join(", ", ops.Take(12))}{(ops.Count > 12 ? ", …" : string.Empty)}."]);
            w.Line($"public const string {member} = {Names.Literal(code)};");
            w.Line();
            Stats.ErrorConstants++;
        }

        w.Doc("The code prefix of this module.");
        w.Line($"public const string Prefix = {Names.Literal(prefix)};");
        w.Line();
        w.Doc("Every code of this module.");
        w.Line($"public static global::System.Collections.Generic.IReadOnlyList<string> All {{ get; }} = [{string.Join(", ", members.Select(m => m.Member))}];");
        w.Line();
        w.Doc("The HTTP status each code maps to (the most frequent declaration when operations disagree); codes registered without a status are absent.");
        w.Line("public static global::System.Collections.Generic.IReadOnlyDictionary<string, int> HttpStatus { get; } = new global::System.Collections.Generic.Dictionary<string, int>(global::System.StringComparer.Ordinal)");
        w.Line("{");
        foreach (var m in members.Where(m => m.Status != 0))
        {
            w.Line($"    [{m.Member}] = {m.Status.ToString(CultureInfo.InvariantCulture)},");
        }

        w.Line("};");
        w.Close();
        Add($"{Layout.Generated(module)}/{name}.cs", CodeWriter.Header + "\n" + $"namespace {Layout.RootNamespace(module)};\n\n" + w);
    }
}
