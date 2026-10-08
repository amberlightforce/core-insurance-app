using CoreIns.Modules.Claims.Persistence;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>Errors and helpers shared by the CLM commands.</summary>
internal static class ClaimSupport
{
    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.CLM, "NOT-FOUND", $"The {what} does not exist.");

    /// <summary>Racing commands (REQ-CLM racing rule, HANDOVER §4): the claim changed meanwhile; never 500, never ILLEGAL-TRANSITION.</summary>
    public static DomainError Stale(int current) =>
        new(ErrorCode.For(ModuleCode.CLM, "STALE"), "The claim changed meanwhile. Load the newer version and try again.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["currentRecordVersion"] = current.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };

    public static DomainError Stale() => DomainError.Of(ModuleCode.CLM, "STALE", "The claim changed meanwhile. Load the newer version and try again.");

    /// <summary>
    /// The claim of the caller's legal entity, tracked for update and row-locked (<c>FOR UPDATE</c>) until the command's
    /// transaction ends; null when not found (another entity's claim is "not found"). The lock serialises commands on one
    /// claim: a racing command waits, then sees the winner's record_version and returns CLM-ERR-STALE instead of losing on
    /// a child row's unique index (exposure sequence) with a 500. Every state-changing claim command loads through here.
    /// </summary>
    public static async Task<ClaimRow?> LoadAsync(ClaimsDbContext db, LegalEntityId legalEntity, ClaimId claimId, CancellationToken cancellationToken)
    {
        var rows = await db.Claims
            .FromSql($"SELECT * FROM clm.claim WHERE claim_id = {claimId.Value} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>True when a save lost a race on the named unique index.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string constraint) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == constraint;
}
