using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Commands;

/// <summary>Helpers shared by the job commands: loading a job of the caller's legal entity, state transitions, JSON columns, errors.</summary>
internal static class JobSupport
{
    /// <summary>The caller's legal entity id (REQ-POL-035): every query filters by it; another entity's job is "not found".</summary>
    public static LegalEntityId LegalEntity(RequestContext context, ILegalEntityDirectory directory) =>
        directory.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>The job and one of its quote versions, tracked for update; null when not found.</summary>
    public static async Task<(JobRow Job, QuoteVersionRow Version)?> LoadAsync(
        PolicyDbContext db, LegalEntityId legalEntity, JobId jobId, int versionNo, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.JobId == jobId && j.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        var version = await db.QuoteVersions.SingleOrDefaultAsync(v => v.JobId == jobId && v.VersionNo == versionNo, cancellationToken).ConfigureAwait(false);
        return version is null ? null : (job, version);
    }

    /// <summary>Fires a job transition; a forbidden one is POL-ERR-ILLEGAL-TRANSITION (REQ-POL-004, REQ-POL-072).</summary>
    public static Result<JobState> Fire(JobRow job, JobTrigger trigger)
    {
        var from = Codes.Parse<JobState>(job.State);
        var to = JobStateModel.Machine.Fire(from, trigger);
        return to.IsSuccess
            ? to
            : DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {from} job cannot {trigger}.");
    }

    /// <summary>Fires a quote version transition.</summary>
    public static Result<QuoteState> Fire(QuoteVersionRow version, QuoteTrigger trigger)
    {
        var from = Codes.Parse<QuoteState>(version.State);
        var to = QuoteStateModel.Machine.Fire(from, trigger);
        return to.IsSuccess
            ? to
            : DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {from} quote version cannot {trigger}.");
    }

    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.POL, "NOT-FOUND", $"The {what} does not exist.");

    public static DomainError Stale() =>
        DomainError.Of(ModuleCode.POL, "STALE", "The job changed meanwhile. Load the newer version and try again.");

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, SharedKernelJson.Options);

    public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, SharedKernelJson.Options)
                                                ?? throw new InvalidOperationException($"Stored {typeof(T).Name} is null.");

    /// <summary>A stored numeric(19,4) amount without the trailing zeros of the column scale (exact: no rounding).</summary>
    public static decimal Exact(decimal stored) => stored / 1.0000000000000000000000000000m;

    public static RiskTree Tree(QuoteVersionRow version) => FromJson<RiskTree>(version.RiskTree);

    /// <summary>
    /// The Idempotency-Key for an in-process command POL sends on behalf of its own command: derived deterministically
    /// from the caller's key and the operation, so a replay of POL's command replays the callee's too.
    /// </summary>
    public static IdempotencyKey Derived(IdempotencyKey? callerKey, string operation)
    {
        if (callerKey is not { } key)
        {
            return IdempotencyKey.New();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Value:D}|{operation}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return IdempotencyKey.From(new Guid(bytes));
    }
}
