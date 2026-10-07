using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Platform.Commands;

/// <summary>A state-changing request handled by exactly one <see cref="ICommandHandler{TCommand,TResult}"/>.</summary>
/// <typeparam name="TResult">Result type.</typeparam>
#pragma warning disable CA1040 // Marker interface: ties a command to its result type for compile-time handler matching.
public interface ICommand<TResult>;
#pragma warning restore CA1040

/// <summary>
/// Handles one command type (plain C# handler; no MediatR, ADR §1). Expected failures are returned as
/// <see cref="Result{T}"/> failures; the pipeline rolls the unit of work back. The registered
/// <see cref="ICommandHandler{TCommand,TResult}"/> service is the handler wrapped in the platform decorators.
/// </summary>
/// <typeparam name="TCommand">Command type.</typeparam>
/// <typeparam name="TResult">Result type.</typeparam>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Handles the command.</summary>
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Registration metadata of a command.</summary>
/// <param name="Operation">Operation name (<c>&lt;mod&gt;.&lt;Resource&gt;.&lt;operation&gt;</c>), used in audit and idempotency scopes.</param>
/// <param name="Module">Owning module (prefix of the pipeline's error codes).</param>
public sealed record CommandDescriptor(OperationName Operation, ModuleCode Module)
{
    /// <summary>When true (default), the command needs an Idempotency-Key in the request context (ADR §2 rule 6).</summary>
    public bool RequiresIdempotencyKey { get; init; } = true;

    /// <summary>When true (default), every execution is audited (ADR §2 rule 9).</summary>
    public bool Audited { get; init; } = true;

    /// <summary>When true, <see cref="RequestContext.DryRun"/> is honoured (computed, then rolled back); otherwise a dry run is rejected.</summary>
    public bool SupportsDryRun { get; init; }

    /// <summary>Creates a descriptor from an operation name; the module is its prefix.</summary>
    public static CommandDescriptor For(string operation)
    {
        var name = OperationName.Parse(operation);
        return new CommandDescriptor(name, Enum.Parse<ModuleCode>(name.Value[..name.Value.IndexOf('.', StringComparison.Ordinal)].ToUpperInvariant()));
    }
}

/// <summary>An authority the actor needs for a command (input to <see cref="IAuthorityService.CheckAsync"/>).</summary>
/// <param name="Type">Authority type.</param>
/// <param name="Dimensions">Values of the action.</param>
/// <param name="ObjectRef">Object acted on.</param>
/// <param name="ValidAt">Business instant (default: now).</param>
public sealed record AuthorityRequirement(
    AuthorityTypeCode Type, IReadOnlyDictionary<string, DimensionValue> Dimensions, ObjectRef? ObjectRef = null, Instant? ValidAt = null);

/// <summary>Optional hook: the authorities a command needs. Registered per command type.</summary>
/// <typeparam name="TCommand">Command type.</typeparam>
public interface ICommandAuthorization<in TCommand>
{
    /// <summary>The requirements (empty when none).</summary>
    IEnumerable<AuthorityRequirement> Requirements(TCommand command);
}

/// <summary>Optional hook: what the audit record says about a command (object, number, changes, lineage keys).</summary>
/// <typeparam name="TCommand">Command type.</typeparam>
/// <typeparam name="TResult">Result type.</typeparam>
public interface ICommandAuditor<in TCommand, TResult>
{
    /// <summary>Describes a command's audit facts; <paramref name="result"/> is null when the command did not succeed.</summary>
    CommandAuditFacts Describe(TCommand command, Result<TResult>? result);
}

/// <summary>Facts a command contributes to its audit record.</summary>
public sealed record CommandAuditFacts
{
    /// <summary>The object acted on.</summary>
    public ObjectRef? ObjectRef { get; init; }

    /// <summary>The object's business number.</summary>
    public string? ObjectNumber { get; init; }

    /// <summary>Field-level changes.</summary>
    public IReadOnlyList<Audit.AuditChange> Changes { get; init; } = [];

    /// <summary>Lineage keys of the object.</summary>
    public BusinessKeys BusinessKeys { get; init; } = BusinessKeys.Empty;
}
