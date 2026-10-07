using System.Text.Json.Serialization;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Kind of actor behind a fact (contract §3.4.1 <c>actor</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActorKind>))]
public enum ActorKind
{
    /// <summary>A signed-in person (staff, customer, broker).</summary>
    [JsonStringEnumMemberName("USER")]
    User,

    /// <summary>A service identity, batch job or event handler.</summary>
    [JsonStringEnumMemberName("SERVICE")]
    Service,

    /// <summary>An AI agent (SYS-01). Never holds authority of its own (REQ-PLT-111).</summary>
    [JsonStringEnumMemberName("AI_AGENT")]
    AiAgent,
}

/// <summary>Who acted: kind plus a stable id (user object id, service name or agent id). Classified P1.</summary>
/// <param name="Kind">Kind of actor.</param>
/// <param name="Id">Stable id, 1–200 characters.</param>
public sealed record ActorRef(ActorKind Kind, string Id)
{
    /// <summary>Validated id.</summary>
    public string Id { get; } = Id is { Length: >= 1 and <= 200 } && !Id.Any(char.IsControl)
        ? Id
        : throw new ArgumentException("An actor id has 1-200 characters.", nameof(Id));

    /// <summary>A user actor.</summary>
    public static ActorRef User(string id) => new(ActorKind.User, id);

    /// <summary>A service actor.</summary>
    public static ActorRef Service(string id) => new(ActorKind.Service, id);

    /// <summary>The wire form of the kind (USER, SERVICE, AI_AGENT).</summary>
    public string KindCode => Kind switch
    {
        ActorKind.User => "USER",
        ActorKind.Service => "SERVICE",
        ActorKind.AiAgent => "AI_AGENT",
        _ => throw new InvalidOperationException($"Unknown actor kind {Kind}."),
    };

    /// <summary>Parses a wire kind.</summary>
    public static ActorKind ParseKind(string code) => code switch
    {
        "USER" => ActorKind.User,
        "SERVICE" => ActorKind.Service,
        "AI_AGENT" => ActorKind.AiAgent,
        _ => throw new FormatException($"'{code}' is not an actor kind (USER, SERVICE, AI_AGENT)."),
    };

    /// <summary><c>KIND:id</c>.</summary>
    public override string ToString() => $"{KindCode}:{Id}";
}

/// <summary>Reference to a business object owned by a module (contracts/events common <c>ObjectRef</c>).</summary>
/// <param name="Module">Owning module.</param>
/// <param name="Type">Object type, e.g. <c>Policy</c>.</param>
/// <param name="Id">Object id (usually a UUID).</param>
public sealed record ObjectRef(ModuleCode Module, string Type, string Id)
{
    /// <summary>Validated type.</summary>
    public string Type { get; } = Type is { Length: >= 1 and <= 128 } ? Type : throw new ArgumentException("An object type is required.", nameof(Type));

    /// <summary>Validated id.</summary>
    public string Id { get; } = Id is { Length: >= 1 and <= 200 } ? Id : throw new ArgumentException("An object id is required.", nameof(Id));

    /// <summary>Reference to an entity by its strongly typed id.</summary>
    public static ObjectRef For<TId>(ModuleCode module, string type, TId id)
        where TId : struct, IEntityId<TId> => new(module, type, id.Value.ToString("D"));

    /// <summary><c>MOD/Type/id</c>.</summary>
    public override string ToString() => $"{Module}/{Type}/{Id}";
}

/// <summary>Personal-data class (contract §3.2.1 rule 7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DataClassification>))]
public enum DataClassification
{
    /// <summary>Not personal.</summary>
    P0 = 0,

    /// <summary>Personal.</summary>
    P1 = 1,

    /// <summary>Personal-sensitive (financial identifiers, national ids, precise location).</summary>
    P2 = 2,

    /// <summary>Special category (health, criminal-offence data).</summary>
    P3 = 3,
}
