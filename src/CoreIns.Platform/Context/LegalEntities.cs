using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Options;

namespace CoreIns.Platform.Context;

/// <summary>
/// Maps a legal entity code (envelope, configuration) to its <see cref="LegalEntityId"/> (rows, keys, SPIs), D-CON-33.
/// The registry belongs to MKT (W1-MKT); until it lands, <see cref="StampLegalEntityDirectory"/> knows the stamp's one
/// legal entity (a stamp is one legal entity, INFRASTRUCTURE §8).
/// </summary>
public interface ILegalEntityDirectory
{
    /// <summary>The id of <paramref name="code"/>; throws when the legal entity is unknown.</summary>
    LegalEntityId Resolve(LegalEntityCode code);

    /// <summary>Every legal entity served by this deployment (key-ring warm-up).</summary>
    IReadOnlyList<LegalEntityId> All { get; }
}

/// <summary>The stamp's legal entity from <c>Stamp:LegalEntity</c> and <c>Stamp:LegalEntityId</c>.</summary>
internal sealed class StampLegalEntityDirectory(IOptions<StampOptions> stamp) : ILegalEntityDirectory
{
    public LegalEntityId Resolve(LegalEntityCode code)
    {
        if (!string.Equals(stamp.Value.LegalEntity, code.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Legal entity {code} is not served by this stamp.");
        }

        return StampId();
    }

    public IReadOnlyList<LegalEntityId> All => [StampId()];

    private LegalEntityId StampId() =>
        Guid.TryParse(stamp.Value.LegalEntityId, out var id) && id != Guid.Empty
            ? new LegalEntityId(id)
            : throw new InvalidOperationException("Stamp:LegalEntityId (a UUID) is not configured.");
}

/// <summary>Runs an in-process command call (generated <c>I&lt;Module&gt;…Service</c> methods) with the caller's <c>CommandOptions</c>.</summary>
public static class InProcessCommands
{
    /// <summary>
    /// Sets the scope's idempotency key and dry-run flag from the caller's options for the duration of the call, then
    /// restores them, so the callee's pipeline applies the contract's idempotency (contract §3.5.3) to the caller's key.
    /// </summary>
    public static IDisposable Use(this RequestContext context, IdempotencyKey key, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(context);
        var restore = (context.IdempotencyKey, context.DryRun);
        context.IdempotencyKey = key;
        context.DryRun = dryRun;
        return new Restore(context, restore.IdempotencyKey, restore.DryRun);
    }

    private sealed class Restore(RequestContext context, IdempotencyKey? key, bool dryRun) : IDisposable
    {
        public void Dispose()
        {
            context.IdempotencyKey = key;
            context.DryRun = dryRun;
        }
    }
}
