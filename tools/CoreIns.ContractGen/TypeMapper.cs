using System.Text.Json.Nodes;

namespace CoreIns.ContractGen;

/// <summary>Where a schema is being mapped: the owning module scope and the namespaces of local component types.</summary>
/// <param name="Scope">Module code of the document (e.g. <c>POL</c>), or <c>common</c>.</param>
internal sealed record MapContext(string Scope);

/// <summary>Maps JSON Schema nodes to C# types, generating nested records and enums into their owner.</summary>
internal sealed class TypeMapper(Documents docs)
{
    private const string Sk = "global::CoreIns.SharedKernel.";
    private const string SkIds = "global::CoreIns.SharedKernel.Identifiers.";
    public const string CommonNs = "CoreIns.Platform.Contracts.Common";

    /// <summary>Common event definitions that map to SharedKernel or built-in types (everything else is generated).</summary>
    public static readonly HashSet<string> MappedCommon = new(StringComparer.Ordinal)
    {
        "Uuid", "Code", "Text", "VersionLabel", "Duration", "DataContractId", "LanguageCode", "CountryCode",
        "DisbursementSourceType", "BusinessNumber", "Sha256", "LocalDate", "Instant", "Decimal", "NonNegativeInt",
        "CurrencyCode", "Money", "ModuleCode", "ObjectRef", "DatePeriod", "TimeWindow", "ClockCode", "EventTypeName",
        "ProductVersionNumber", "AiFeatureId", "RetentionClassCode", "ObligationCode", "OpenObject", "OpenValue",
        "MoneyTotals", "NamedAmounts", "Counts", "RepointMap",
    };

    public static TypeRef Decimal { get; } = new("decimal", true, "global::CoreIns.Platform.Contracts.DecimalStringJsonConverter");

    /// <summary>Maps a schema. <paramref name="nullable"/> is true when the schema itself admits null.</summary>
    public TypeRef Map(Located schema, string? prop, RecordDef owner, MapContext ctx, out bool nullable)
    {
        nullable = false;
        var node = schema.Node;

        if (node["$ref"] is JsonValue refValue)
        {
            return MapRef(schema.File, refValue.GetValue<string>(), prop, owner, ctx, out nullable);
        }

        if (node["oneOf"] is JsonArray oneOf || node["anyOf"] is JsonArray)
        {
            var alternatives = (node["oneOf"] as JsonArray ?? (JsonArray)node["anyOf"]!).OfType<JsonObject>().ToList();
            var nonNull = alternatives.Where(a => !IsNullType(a)).ToList();
            nullable = nonNull.Count < alternatives.Count;
            if (nonNull.Count == 1)
            {
                var inner = Map(new Located(nonNull[0], schema.File), prop, owner, ctx, out var innerNullable);
                nullable |= innerNullable;
                return inner;
            }

            var names = nonNull.Select(a => a["$ref"]?.GetValue<string>().Split('/')[^1]).ToList();
            if (names is ["Money", "Decimal"] or ["Decimal", "Money"])
            {
                return new TypeRef("global::CoreIns.Platform.Contracts.MoneyOrDecimal", true);
            }

            if (names is ["LocalDate", "Instant"])
            {
                return new TypeRef("global::CoreIns.Platform.Contracts.ValidAt", true);
            }

            if (nonNull.All(a => a["type"] is null && a["$ref"] is null) && node["properties"] is JsonObject)
            {
                // oneOf of required-groups: a constraint on an object, not a union.
            }
            else
            {
                return TypeRef.Json with { Note = "Union of several shapes in the contract; read as JSON." };
            }
        }

        if (node["allOf"] is JsonArray allOf)
        {
            if (allOf.Count == 1 && node["properties"] is null)
            {
                return Map(new Located((JsonObject)allOf[0]!, schema.File), prop, owner, ctx, out nullable);
            }

            var merged = Merge(schema);
            return Nested(merged, prop, owner, ctx, "Detail");
        }

        if (node["const"] is JsonValue constant)
        {
            return constant.GetValueKind() == System.Text.Json.JsonValueKind.String ? TypeRef.String : TypeRef.Json;
        }

        if (node["enum"] is JsonArray values)
        {
            nullable = values.Any(v => v is null);
            var wanted = values.Where(v => v is not null).Select(v => v!.ToString()).ToList();
            foreach (var (commonName, commonDef) in (JsonObject)docs.LoadObject(docs.EventsCommon)["$defs"]!)
            {
                if (commonDef?["enum"] is JsonArray commonValues && commonValues.Select(v => v!.ToString()).SequenceEqual(wanted))
                {
                    return new TypeRef($"global::{CommonNs}.{commonName}", true);
                }
            }

            var def =BuildEnum(Names.Pascal(prop ?? owner.Name), values, node);
            var name = owner.Scope.Take(def.Name + "Value", "Kind");
            var named = new EnumDef { Name = name, Summary = def.Summary };
            named.Members.AddRange(def.Members);
            owner.Nested.Add(named);
            return new TypeRef(name, true);
        }

        var type = node["type"];
        if (type is JsonArray typeList)
        {
            var kinds = typeList.Select(t => t!.GetValue<string>()).ToList();
            nullable = kinds.Remove("null");
            if (kinds.Count != 1)
            {
                return TypeRef.Json with { Note = $"Contract type is one of {string.Join(", ", kinds)}; read as JSON." };
            }

            var single = node.DeepClone().AsObject();
            single["type"] = kinds[0];
            return Map(new Located(single, schema.File), prop, owner, ctx, out _);
        }

        switch (type?.GetValue<string>())
        {
            case "string":
                return TypeRef.String;
            case "boolean":
                return TypeRef.Bool;
            case "integer":
                return TypeRef.Int;
            case "null":
                nullable = true;
                return TypeRef.Json;
            case "array":
                if (node["items"] is not JsonObject items || items.Count == 0)
                {
                    return TypeRef.ListOf(TypeRef.Json);
                }

                var item = Map(new Located(items, schema.File), prop is null ? null : Names.Singular(prop), owner, ctx, out _, "Item");
                return TypeRef.ListOf(item);
            case "object":
            case null when node["properties"] is JsonObject:
                if (node["properties"] is JsonObject { Count: > 0 })
                {
                    return Nested(schema, prop, owner, ctx, "Detail");
                }

                if (node["additionalProperties"] is JsonObject ap && ap.Count > 0)
                {
                    return TypeRef.MapOf(Map(new Located(ap, schema.File), prop, owner, ctx, out _, "Entry"));
                }

                return TypeRef.Json with { Note = "Open object: members not defined by the contract yet." };
            default:
                return TypeRef.Json with { Note = "Shape not defined by the contract yet (Unspecified)." };
        }
    }

    private TypeRef Map(Located schema, string? prop, RecordDef owner, MapContext ctx, out bool nullable, string nestedSuffix)
    {
        // Item/entry objects are named <Prop>Item / <Prop>Entry.
        _nestedSuffix = nestedSuffix;
        try
        {
            return Map(schema, prop, owner, ctx, out nullable);
        }
        finally
        {
            _nestedSuffix = null;
        }
    }

    private string? _nestedSuffix;

    private TypeRef Nested(Located schema, string? prop, RecordDef owner, MapContext ctx, string suffix)
    {
        var actual = _nestedSuffix ?? suffix;
        _nestedSuffix = null;
        var name = owner.Scope.Take(Names.Pascal(prop ?? "Part") + actual, "Type");
        var record = BuildRecord(schema, name, ctx, Names.FirstLine(schema.Node["description"]?.ToString()));
        owner.Nested.Add(record);
        return new TypeRef(name, false);
    }

    private TypeRef MapRef(string baseFile, string reference, string? prop, RecordDef owner, MapContext ctx, out bool nullable)
    {
        nullable = false;
        var target = docs.Resolve(baseFile, reference);
        var defName = reference.Split('/')[^1];
        if (PathEquals(target.File, docs.EventsCommon))
        {
            return CommonEvent(defName, prop, ctx);
        }

        if (PathEquals(target.File, docs.OpenApiCommon))
        {
            if (target.Node["$ref"] is not null)
            {
                return Map(target, prop, owner, ctx, out nullable);
            }

            return defName switch
            {
                "Unspecified" => TypeRef.Json with { Note = "Unspecified: the PRD row gives only a name; the owning work package types it (D-API-04/06)." },
                "PageEnvelope" => throw new InvalidDataException("PageEnvelope is only used inside allOf."),
                _ => new TypeRef($"global::{CommonNs}.{defName}", false),
            };
        }

        if (target.File.EndsWith(".schema.json", StringComparison.Ordinal))
        {
            // A reference inside an event schema to its own $defs (not used today beyond Payload).
            return Map(target, prop, owner, ctx, out nullable);
        }

        // A component of a module OpenAPI document.
        var module = Layout.ModuleOfOpenApiFile(target.File);
        switch (ComponentKind(target))
        {
            case ComponentShape.Record:
                return new TypeRef($"global::{Layout.ApiNamespace(module)}.{defName}", false);
            case ComponentShape.Enum:
                return new TypeRef($"global::{Layout.ApiNamespace(module)}.{defName}", true);
            default:
                return Map(target, prop, owner, ctx with { Scope = module }, out nullable);
        }
    }

    public enum ComponentShape
    {
        Record,
        Enum,
        Alias,
    }

    /// <summary>Whether a component schema becomes its own record, its own enum, or is mapped inline (alias / open value).</summary>
    public static ComponentShape ComponentKind(Located component)
    {
        var n = component.Node;
        if (n["$ref"] is not null)
        {
            return ComponentShape.Alias;
        }

        if (n["enum"] is JsonArray)
        {
            return ComponentShape.Enum;
        }

        if (n["allOf"] is JsonArray all && all.Count > 1)
        {
            return ComponentShape.Record;
        }

        return n["properties"] is JsonObject { Count: > 0 } ? ComponentShape.Record : ComponentShape.Alias;
    }

    public TypeRef CommonEvent(string name, string? prop, MapContext ctx) => name switch
    {
        "Uuid" => IdTypeMap.ForUuid(ctx.Scope, prop),
        "Code" or "Text" or "VersionLabel" or "Duration" or "DataContractId" or "LanguageCode" or "CountryCode"
            or "DisbursementSourceType" => TypeRef.String,
        "BusinessNumber" => IdTypeMap.ForBusinessNumber(prop),
        "Sha256" => IdTypeMap.ForHash(prop),
        "LocalDate" => new TypeRef(Sk + "BusinessDate", true),
        "Instant" => new TypeRef(Sk + "Instant", true),
        "Decimal" => Decimal,
        "NonNegativeInt" => TypeRef.Int,
        "CurrencyCode" => new TypeRef(Sk + "Currency", true),
        "Money" => new TypeRef(Sk + "Money", true),
        "ModuleCode" => new TypeRef(SkIds + "ModuleCode", true),
        "ObjectRef" => new TypeRef(SkIds + "ObjectRef", false),
        "DatePeriod" => new TypeRef(Sk + "DateRange", true),
        "TimeWindow" => new TypeRef(Sk + "InstantRange", true),
        "ClockCode" or "EventTypeName" or "ProductVersionNumber" or "AiFeatureId" or "RetentionClassCode" or "ObligationCode"
            => new TypeRef(SkIds + name, true),
        "OpenObject" => TypeRef.Json with { Note = "Open structure (OpenObject): consumers must not rely on its members until the producer defines them in a minor version." },
        "OpenValue" => TypeRef.Json,
        "MoneyTotals" => TypeRef.MapOf(new TypeRef(Sk + "Money", true)),
        "NamedAmounts" => TypeRef.MapOf(new TypeRef("global::CoreIns.Platform.Contracts.MoneyOrDecimal", true)),
        "Counts" => TypeRef.MapOf(TypeRef.Int),
        "RepointMap" => TypeRef.ListOf(new TypeRef($"global::{CommonNs}.RepointMapItem", false)),
        _ => new TypeRef($"global::{CommonNs}.{name}", docs.LoadObject(docs.EventsCommon)["$defs"]![name]!["enum"] is not null),
    };

    /// <summary>Builds a record from an object schema (allOf parts merged).</summary>
    public RecordDef BuildRecord(Located schema, string name, MapContext ctx, string? summary, NameScope? scope = null, string? bases = null)
    {
        var merged = schema.Node["allOf"] is JsonArray ? Merge(schema) : schema;
        var node = merged.Node;
        var record = new RecordDef { Name = name, Summary = summary, Scope = scope ?? new NameScope(name), Bases = bases };
        var required = (node["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        if (node["oneOf"] is JsonArray groups && groups.All(g => g?["required"] is JsonArray && g["type"] is null))
        {
            record.Remarks.Add("Exactly one of: " + string.Join(" | ", groups.Select(g => string.Join(" + ", ((JsonArray)g!["required"]!).Select(x => x!.GetValue<string>())))) + ".");
        }

        if (node["minProperties"] is JsonValue min)
        {
            record.Remarks.Add($"At least {min} member(s) must be present.");
        }

        foreach (var (jsonName, value) in (node["properties"] as JsonObject) ?? [])
        {
            var propSchema = value as JsonObject ?? [];
            var csName = record.Scope.Take(Names.Pascal(jsonName));
            var type = Map(new Located(propSchema, merged.File), jsonName, record, ctx, out var schemaNullable);
            var isRequired = required.Contains(jsonName);
            var prop = new PropDef
            {
                JsonName = jsonName,
                Name = csName,
                Type = type,
                Required = isRequired,
                Nullable = schemaNullable || !isRequired,
                OmitWhenNull = !isRequired && !schemaNullable,
                Doc = Names.FirstLine(propSchema["description"]?.ToString()) is { Length: > 0 } d ? d : null,
            };
            if (type.Note is not null)
            {
                prop.Remarks.Add(type.Note);
            }

            if (propSchema["x-classification"] is JsonValue cls && cls.GetValue<string>() != "P0")
            {
                prop.Remarks.Add($"Personal data, class {cls.GetValue<string>()} (x-classification).");
            }

            if (propSchema["x-todo-owner"] is JsonValue todo)
            {
                prop.Remarks.Add($"Shape to be typed by {todo} (D-API-13).");
            }

            record.Props.Add(prop);
        }

        return record;
    }

    /// <summary>Merges allOf parts (resolving refs) into one object schema; later parts override properties.</summary>
    public Located Merge(Located schema)
    {
        var props = new JsonObject();
        var required = new List<string>();
        void Add(Located part)
        {
            var p = docs.Deref(part);
            if (p.Node["allOf"] is JsonArray inner)
            {
                foreach (var x in inner.OfType<JsonObject>())
                {
                    Add(new Located(x, p.File));
                }
            }

            foreach (var (k, v) in (p.Node["properties"] as JsonObject) ?? [])
            {
                props[k] = Rebase(v!.DeepClone(), p.File, schema.File);
            }

            foreach (var r in (p.Node["required"] as JsonArray) ?? [])
            {
                var s = r!.GetValue<string>();
                if (!required.Contains(s))
                {
                    required.Add(s);
                }
            }
        }

        foreach (var part in ((JsonArray)schema.Node["allOf"]!).OfType<JsonObject>())
        {
            Add(new Located(part, schema.File));
        }

        foreach (var (k, v) in (schema.Node["properties"] as JsonObject) ?? [])
        {
            props[k] = v!.DeepClone();
        }

        foreach (var r in (schema.Node["required"] as JsonArray) ?? [])
        {
            required.Add(r!.GetValue<string>());
        }

        var merged = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray([.. required.Distinct().Select(x => (JsonNode)x)]) };
        if (schema.Node["description"] is { } d)
        {
            merged["description"] = d.DeepClone();
        }

        return new Located(merged, schema.File);
    }

    /// <summary>Rewrites relative <c>$ref</c>s of a node copied from <paramref name="from"/> so they resolve from <paramref name="to"/>.</summary>
    private JsonNode Rebase(JsonNode node, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return node;
        }

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                if (key == "$ref" && obj[key] is JsonValue v)
                {
                    var target = docs.Resolve(from, v.GetValue<string>());
                    var hash = v.GetValue<string>().IndexOf('#', StringComparison.Ordinal);
                    var fragment = hash < 0 ? string.Empty : v.GetValue<string>()[hash..];
                    var rel = Path.GetRelativePath(Path.GetDirectoryName(to)!, target.File).Replace('\\', '/');
                    obj[key] = rel + fragment;
                }
                else if (obj[key] is { } child)
                {
                    obj[key] = Rebase(child, from, to);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is { } child)
                {
                    arr[i] = Rebase(child.DeepClone(), from, to);
                }
            }
        }

        return node;
    }

    public static EnumDef BuildEnum(string name, JsonArray values, JsonObject node)
    {
        var def = new EnumDef { Name = name, Summary = Names.FirstLine(node["description"]?.ToString()) };
        var scope = new NameScope();
        foreach (var v in values.Where(v => v is not null))
        {
            var json = v!.ToString();
            def.Members.Add((json, scope.Take(Names.Pascal(json), "Value")));
        }

        return def;
    }

    private static bool IsNullType(JsonObject alternative) => alternative["type"] is JsonValue t && t.GetValue<string>() == "null";

    private static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
