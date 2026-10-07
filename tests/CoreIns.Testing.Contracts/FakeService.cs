using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Testing.Contracts;

/// <summary>One call a sandbox double received.</summary>
/// <param name="OperationId">The contract operation (e.g. <c>pol.Job.bind</c>).</param>
/// <param name="Arguments">The arguments in signature order (without the cancellation token).</param>
public sealed record RecordedCall(string OperationId, IReadOnlyList<object?> Arguments);

/// <summary>
/// Base of the generated sandbox doubles (D-PRG-07): records every call in order and answers each operation with, in
/// this order, a failure set by <see cref="Fail"/>, a response set by <see cref="Setup{TResponse}"/>, or the canned
/// response generated from the operation's response schema (contracts/openapi, validated in CI).
/// </summary>
public abstract class FakeService
{
    private readonly string _module;
    private readonly ConcurrentQueue<RecordedCall> _calls = new();
    private readonly ConcurrentDictionary<string, Func<RecordedCall, object?>> _responses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Exception> _failures = new(StringComparer.Ordinal);

    /// <summary>Creates the double for a module (lower-case code, e.g. <c>pol</c>).</summary>
    protected FakeService(string module) => _module = module;

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<RecordedCall> Calls => [.. _calls];

    /// <summary>The calls of one operation.</summary>
    public IReadOnlyList<RecordedCall> CallsTo(string operationId) => [.. _calls.Where(c => c.OperationId == operationId)];

    /// <summary>Answers <paramref name="operationId"/> with the result of <paramref name="respond"/>.</summary>
    public void Setup<TResponse>(string operationId, Func<RecordedCall, TResponse> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _responses[operationId] = call => respond(call);
    }

    /// <summary>Answers <paramref name="operationId"/> with <paramref name="response"/>.</summary>
    public void Setup<TResponse>(string operationId, TResponse response) => _responses[operationId] = _ => response;

    /// <summary>Makes <paramref name="operationId"/> throw <paramref name="exception"/>.</summary>
    public void Fail(string operationId, Exception exception) => _failures[operationId] = exception;

    /// <summary>The canned response of an operation (a fresh instance per call).</summary>
    public TResponse Canned<TResponse>(string operationId) => CannedSamples.Response<TResponse>(_module, operationId);

    /// <summary>Records the call and answers it.</summary>
    protected Task<TResponse> RespondAsync<TResponse>(string operationId, object?[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Record(operationId, arguments);
        if (_failures.TryGetValue(operationId, out var failure))
        {
            return Task.FromException<TResponse>(failure);
        }

        return Task.FromResult(_responses.TryGetValue(operationId, out var respond)
            ? (TResponse)respond(call)!
            : Canned<TResponse>(operationId));
    }

    /// <summary>Records the call of an operation without a response body.</summary>
    protected Task RespondAsync(string operationId, object?[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Record(operationId, arguments);
        if (_failures.TryGetValue(operationId, out var failure))
        {
            return Task.FromException(failure);
        }

        if (_responses.TryGetValue(operationId, out var respond))
        {
            respond(call);
        }

        return Task.CompletedTask;
    }

    private RecordedCall Record(string operationId, object?[] arguments)
    {
        var call = new RecordedCall(operationId, arguments ?? []);
        _calls.Enqueue(call);
        return call;
    }
}

/// <summary>The generated schema samples (tests/CoreIns.Testing.Contracts/Generated/Samples, embedded).</summary>
public static class CannedSamples
{
    private static readonly ConcurrentDictionary<string, JsonObject> Modules = new(StringComparer.Ordinal);

    /// <summary>The canned response JSON of an operation.</summary>
    public static JsonNode ResponseJson(string module, string operationId) =>
        Load(module)["responses"]?[operationId]?.DeepClone()
        ?? throw new KeyNotFoundException($"No canned response for {operationId}.");

    /// <summary>The sample JSON of a component schema of a module document.</summary>
    public static JsonNode SchemaJson(string module, string schema) =>
        Load(module)["schemas"]?[schema]?.DeepClone()
        ?? throw new KeyNotFoundException($"No sample for {module}.{schema}.");

    /// <summary>Names of the component schemas with samples.</summary>
    public static IReadOnlyList<string> SchemaNames(string module) => [.. ((JsonObject)Load(module)["schemas"]!).Select(p => p.Key)];

    /// <summary>Operation ids with canned responses.</summary>
    public static IReadOnlyList<string> ResponseOperations(string module) => [.. ((JsonObject)Load(module)["responses"]!).Select(p => p.Key)];

    /// <summary>The canned response of an operation, deserialised with the platform serializer settings.</summary>
    public static TResponse Response<TResponse>(string module, string operationId) =>
        ResponseJson(module, operationId).Deserialize<TResponse>(SharedKernelJson.Options)
        ?? throw new JsonException($"The canned response of {operationId} is null.");

    private static JsonObject Load(string module) => Modules.GetOrAdd(module, static m =>
    {
        var name = $"CoreIns.Testing.Contracts.Samples.{m}.json";
        using var stream = typeof(CannedSamples).Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"Embedded samples {name} not found.");
        return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException(name);
    });
}
