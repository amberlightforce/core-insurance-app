using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Coverage.reverify</c> as a command of the platform pipeline (REQ-CLM-058).</summary>
internal sealed record ReverifyCoverage(CoverageReverifyRequest Request) : ICommand<CoverageReverifyResponse>;

/// <summary>Shape rules.</summary>
internal sealed class ReverifyCoverageValidator : AbstractValidator<ReverifyCoverage>
{
    public ReverifyCoverageValidator()
    {
        RuleFor(c => c.Request.Decision).IsInEnum();
        RuleFor(c => c.Request.ReasonCode).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
        RuleFor(c => c.Request.Comment).MaximumLength(1024).When(c => c.Request.Comment is not null);
        RuleFor(c => c.Request.ExpectedNewSnapshotRef).MaximumLength(512).When(c => c.Request.ExpectedNewSnapshotRef is not null);

        // ADOPT is taken against a named new ref (the contract: required for ADOPT, checked for KEEP when given).
        RuleFor(c => c.Request.ExpectedNewSnapshotRef).NotEmpty().When(c => c.Request.Decision == CoverageReverifyRequest.DecisionValue.Adopt);
    }
}

/// <summary>
/// A human decides after <c>ReverificationRequired</c> (REQ-CLM-058, D-SL3-03 d): KEEP the claim's snapshot ref or ADOPT the
/// successor. Runs under the claim lock, so it serialises with payments and with the consumers (PITFALLS 15).
/// <list type="bullet">
/// <item>No open demand → CLM-ERR-ILLEGAL-TRANSITION. <c>expectedNewSnapshotRef</c> (required for ADOPT) not equal to the open
/// demand's new ref, or, for ADOPT, POL has superseded that ref again or does not answer for the claim's policy and loss
/// instant → CLM-ERR-SNAPSHOT-MISMATCH (409); POL down → CLM-ERR-DEPENDENCY-UNAVAILABLE (503).</item>
/// <item>KEEP: the status goes back to Verified; the ref, cover and exposures are untouched.</item>
/// <item>ADOPT: the claim takes the new ref and its facts; the cover check is re-run for every open exposure against the new
/// snapshot. An exposure that was covered and is not any more becomes IN_QUESTION and the claim is flagged
/// coverage-in-question: new payments on it are refused (CLM-ERR-COVERAGE-IN-QUESTION) until a coverage decision. Nothing
/// here ever clears that state or upgrades an indication (PITFALLS 6); a gained cover needs a coverage decision as well.</item>
/// </list>
/// Every open demand of the claim (several causes may be open) is closed with the same decision. The comment is P2 and stored
/// encrypted; the audit record has no free text.
/// </summary>
internal sealed class ReverifyCoverageHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    ClaimProtection protection,
    ICoverageSource coverage,
    Microsoft.Extensions.Options.IOptions<ClaimsOptions> options) : ICommandHandler<ReverifyCoverage, CoverageReverifyResponse>
{
    private const string ManagerRole = "Staff.ClaimsManager";

    public async Task<Result<CoverageReverifyResponse>> HandleAsync(ReverifyCoverage command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var claim = await ClaimSupport.LoadAsync(db, legalEntity, request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (!options.Value.ReverificationReasons.Allows(request.Decision == CoverageReverifyRequest.DecisionValue.Adopt, request.ReasonCode))
        {
            return FnolAssessment.Invalid("reasonCode", "REASON", "The reason code is not configured for this decision.");
        }

        var pending = await db.Reverifications.Where(r => r.ClaimId == claim.ClaimId && r.Status == ReverificationRow.Open)
            .OrderBy(r => r.RaisedAt).ThenBy(r => r.ReverificationId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "No re-verification is pending on the claim.");
        }

        var latest = pending[^1];
        if (request.ExpectedNewSnapshotRef is { } expected && !string.Equals(expected, latest.NewSnapshotRef, StringComparison.Ordinal))
        {
            return Mismatch("The new snapshot is not the one pending on the claim.");
        }

        var adopt = request.Decision == CoverageReverifyRequest.DecisionValue.Adopt;
        var previousRef = claim.SnapshotRef;
        var lost = 0;
        if (adopt)
        {
            var read = await coverage.ReadByRefAsync(latest.NewSnapshotRef, cancellationToken).ConfigureAwait(false);
            switch (read.Outcome)
            {
                case SnapshotReadOutcome.Unavailable:
                    return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The policy service did not answer. Try again shortly.");
                case SnapshotReadOutcome.Unverified:
                    return Mismatch("POL does not know the pending snapshot.");
            }

            var facts = read.Facts!;
            if (facts.Supersession is { Superseded: true })
            {
                return Mismatch("POL superseded the pending snapshot again; reload the claim.");
            }

            if (facts.PolicyId != claim.PolicyId.Value || facts.ValidAt != claim.SnapshotValidAt
                || !string.Equals(facts.SnapshotRef, latest.NewSnapshotRef, StringComparison.Ordinal))
            {
                return Mismatch("The pending snapshot does not describe the claim's policy at the loss instant.");
            }

            lost = await AdoptAsync(claim, facts, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Keeping a snapshot whose successor is not in force at the loss date (or that POL cannot confirm) is a coverage-
            // relevant decision: the claims manager only (m1; the full CLM.COVERAGE_DECISION authority is a follow-up).
            var read = await coverage.ReadByRefAsync(latest.NewSnapshotRef, cancellationToken).ConfigureAwait(false);
            if (read.Outcome == SnapshotReadOutcome.Unavailable)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The policy service did not answer. Try again shortly.");
            }

            if ((read.Outcome != SnapshotReadOutcome.Found || !read.Facts!.InForce) && !context.Roles.Contains(ManagerRole, StringComparer.Ordinal))
            {
                return DomainError.Of(ModuleCode.CLM, "AUTHORITY", "Keeping a snapshot that is not in force at the loss date needs the claims manager.");
            }
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var comment = request.Comment?.Trim();
        foreach (var row in pending)
        {
            row.Status = adopt ? ReverificationRow.Adopted : ReverificationRow.Kept;
            row.ReasonCode = request.ReasonCode;
            row.CommentEncrypted = string.IsNullOrEmpty(comment)
                ? null
                : await protection.EncryptAsync(legalEntity, ClaimProtection.ReverificationCommentField, row.ReverificationId, comment, cancellationToken).ConfigureAwait(false);
            row.CoverageInQuestion = adopt && lost > 0;
            row.DecidedAt = now;
            row.DecidedBy = actor;
            row.RecordVersion++;
        }

        claim.SnapshotStatus = Codes.Of(SnapshotStatus.Verified);
        claim.UpdatedAt = now;
        claim.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        return new CoverageReverifyResponse
        {
            ClaimId = claim.ClaimId,
            DecisionRecordId = latest.ReverificationId,
            Decision = adopt ? CoverageReverifyResponse.DecisionValue.Adopt : CoverageReverifyResponse.DecisionValue.Keep,
            SnapshotStatus = CoverageReverifyResponse.SnapshotStatusValue.Verified,
            SnapshotRef = claim.SnapshotRef,
            PreviousSnapshotRef = adopt ? previousRef : null,
            CoverageInQuestion = adopt && lost > 0,
            DecidedAt = now,
        };
    }

    private static DomainError Mismatch(string message) => DomainError.Of(ModuleCode.CLM, "SNAPSHOT-MISMATCH", message);

    /// <summary>Applies the adopted snapshot to the claim and re-runs the cover check; returns how many exposures lost their cover.</summary>
    private async Task<int> AdoptAsync(ClaimRow claim, PolicySnapshotFacts facts, CancellationToken cancellationToken)
    {
        claim.SnapshotRef = facts.SnapshotRef;
        claim.SnapshotSegmentId = facts.SegmentId;
        claim.PolicyTermId = facts.TermId is { } termId ? new PolicyTermId(termId) : null;
        claim.SnapshotKnownAt = facts.KnownAt;
        claim.PolicyInForceAtLoss = facts.InForce;
        claim.PolicyStatusAtLoss = facts.Status ?? facts.NotInForceReason;
        claim.SnapshotCoverageCodes = [.. facts.CoverageCodes];
        claim.ProductVersion = facts.ProductVersion ?? claim.ProductVersion;

        var now = clock.Now;
        var covered = Codes.Of(CoverageIndicationCode.Covered);
        var lost = 0;
        var exposures = await db.Exposures.Where(e => e.ClaimId == claim.ClaimId && e.Status == ClaimStates.Open).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var exposure in exposures)
        {
            // Only a covered exposure can lose its cover; an indication is never upgraded here (PITFALLS 6).
            if (exposure.CoverageIndication == covered && CoverageRules.Indicate(facts, exposure.CoverageCode) != CoverageIndicationCode.Covered)
            {
                exposure.CoverageIndication = Codes.Of(CoverageIndicationCode.InQuestion);
                exposure.CoverageDecision = Codes.Of(CoverageDecisionCode.Pending);
                exposure.UpdatedAt = now;
                exposure.RecordVersion++;
                lost++;
            }
        }

        if (lost > 0 || !facts.InForce)
        {
            claim.CoverageInQuestion = true;
        }

        return lost;
    }
}

/// <summary>Audit facts of <c>clm.Coverage.reverify</c>: the decision, the refs and the reason code; never the comment (P2).</summary>
internal sealed class ReverifyCoverageAuditor : ICommandAuditor<ReverifyCoverage, CoverageReverifyResponse>
{
    public CommandAuditFacts Describe(ReverifyCoverage command, Result<CoverageReverifyResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString());
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId), BusinessKeys = keys };
        }

        var response = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId),
            BusinessKeys = keys,
            Changes = AuditDiff.Compute(
                new { snapshotStatus = "REVERIFICATION_REQUIRED", snapshotRef = response.PreviousSnapshotRef },
                new
                {
                    snapshotStatus = "VERIFIED",
                    snapshotRef = response.PreviousSnapshotRef is null ? null : response.SnapshotRef,
                    decision = response.Decision.ToString(),
                    reasonCode = command.Request.ReasonCode,
                    coverageInQuestion = response.CoverageInQuestion,
                }),
        };
    }
}
