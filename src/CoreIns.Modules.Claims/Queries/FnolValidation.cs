using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Platform.Context;

namespace CoreIns.Modules.Claims.Queries;

/// <summary>
/// <c>clm.Fnol.validate</c> (REQ-CLM-001, journey J-01 step 3 "continuous validation"): the shape rules and the
/// <see cref="FnolAssessment"/> a submit would run, as a read. No claim, number, event or idempotency record is created,
/// so validating never consumes a claim number (REQ-CLM-043).
/// </summary>
internal sealed class FnolValidation(FnolAssessment assessment, ClaimProtection protection, RequestContext context)
{
    public async Task<FnolValidateResponse> ValidateAsync(FnolValidateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fnol = request.Fnol;
        var shape = await new FnolRequestValidator().ValidateAsync(fnol, cancellationToken).ConfigureAwait(false);
        var issues = shape.Errors.Select(e => new FnolIssue { Code = "CLM-ERR-VALIDATION", Field = e.PropertyName, Message = e.ErrorMessage }).ToList();
        if (issues.Count > 0)
        {
            return new FnolValidateResponse { Valid = false, Issues = issues, CoverageIndications = [], DuplicateCandidates = [] };
        }

        var assessed = await assessment.AssessAsync(fnol, protection.Current(context), cancellationToken).ConfigureAwait(false);
        return new FnolValidateResponse
        {
            Valid = assessed.Issues.Count == 0,
            Issues = assessed.Issues,
            PolicyNumber = assessed.Snapshot is { } snapshot ? SharedKernel.Identifiers.PolicyNumber.Parse(snapshot.PolicyNumber) : null,
            PolicyInForce = assessed.Snapshot?.InForce,
            PolicyStatusAtLoss = assessed.Snapshot is { } s ? s.Status ?? s.NotInForceReason : null,
            CoverageIndications = assessed.Indications,
            DuplicateCandidates = assessed.Duplicates,
        };
    }
}
