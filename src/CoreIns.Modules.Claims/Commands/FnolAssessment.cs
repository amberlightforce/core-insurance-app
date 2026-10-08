using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>The outcome of assessing an FNOL: what a submit would refuse, and what it would record.</summary>
internal sealed class FnolAssessmentResult
{
    /// <summary>Every finding (clm.Fnol.validate lists them all).</summary>
    public List<FnolIssue> Issues { get; } = [];

    /// <summary>The error a submit returns: the first blocking finding.</summary>
    public DomainError? Error { get; set; }

    public PolicySnapshotFacts? Snapshot { get; set; }

    public Instant LossAt { get; set; }

    public BusinessDate LossDate { get; set; }

    public BusinessDate NoticeOn { get; set; }

    public List<CoverageIndication> Indications { get; } = [];

    public List<DuplicateCandidate> Duplicates { get; } = [];

    public void Fail(DomainError error, string? field)
    {
        Issues.Add(new FnolIssue { Code = error.Code.Value, Field = field, Message = error.Detail ?? error.Code.Value });
        Error ??= error;
    }
}

/// <summary>
/// The FNOL checks shared by <c>clm.Fnol.submit</c> and <c>clm.Fnol.validate</c> (REQ-CLM-001, -002, -030, -041, -048, -049):
/// mandatory fields (CLM-ERR-FNOL-001), loss and notice dates (CLM-ERR-LOSS-DATE), cover on the POL snapshot at the loss
/// instant (CLM-ERR-POLICY-UNVERIFIED / CLM-ERR-DEPENDENCY-UNAVAILABLE), coverage indications and probable duplicates
/// (CLM-ERR-DUPLICATE-CANDIDATES unless the user chose LINK or OVERRIDE). Reads only; writes nothing.
/// </summary>
internal sealed class FnolAssessment(ICoverageSource coverage, ClaimReader reader, IClock clock, IOptions<ClaimsOptions> options)
{
    public async Task<FnolAssessmentResult> AssessAsync(FnolSubmitRequest request, LegalEntityId legalEntity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new FnolAssessmentResult();
        var settings = options.Value;
        var now = clock.Now;
        var today = settings.DateOf(now);

        // 1. Mandatory FNOL core (REQ-CLM-030): a missing field is CLM-ERR-FNOL-001 naming it; nothing else is checked then.
        var missing = new List<string>();
        Require(missing, "lineOfBusiness", request.LineOfBusiness);
        if (request.PolicyId is null)
        {
            missing.Add("policyId");
        }

        if (request.LossAt is null)
        {
            missing.Add("lossAt");
        }

        Require(missing, "lossCause", request.LossCause);
        Require(missing, "lossLocation", request.LossLocation);
        Require(missing, "description", request.Description);
        Require(missing, "channel", request.Channel);
        foreach (var field in missing)
        {
            result.Fail(new DomainError(ErrorCode.For(ModuleCode.CLM, "FNOL-001"), $"The FNOL field {field} is missing.")
            {
                FieldErrors = [new FieldError(field, "REQUIRED", "clm.fnol.required", $"{field} is required.")],
            }, field);
        }

        if (missing.Count > 0)
        {
            return result;
        }

        // 2. Dates (REQ-CLM-030, SCR-CLM-01): loss ≤ now; notice defaults to today (Athens) and is never before the loss or in the future.
        result.LossAt = request.LossAt!.Value;
        result.LossDate = settings.DateOf(result.LossAt);
        result.NoticeOn = request.NoticeOn ?? today;
        if (result.LossAt > now)
        {
            result.Fail(DomainError.Of(ModuleCode.CLM, "LOSS-DATE", "The loss date and time is in the future."), "lossAt");
        }

        if (result.NoticeOn > today)
        {
            result.Fail(DomainError.Of(ModuleCode.CLM, "LOSS-DATE", "The notice date is in the future."), "noticeOn");
        }

        if (result.LossDate > result.NoticeOn)
        {
            result.Fail(DomainError.Of(ModuleCode.CLM, "LOSS-DATE", "The loss date is after the notice date."), "lossAt");
        }

        if (result.Error is not null)
        {
            return result;
        }

        // 3. Cover on the POL snapshot valid at the loss instant, known now (REQ-CLM-002, pol.Snapshot.get).
        var read = await coverage.ReadAsync(request.PolicyId!.Value.Value, result.LossAt, now, cancellationToken).ConfigureAwait(false);
        switch (read.Outcome)
        {
            case SnapshotReadOutcome.Unavailable:
                result.Fail(DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The policy service is not available; try again shortly."), "policyId");
                return result;
            case SnapshotReadOutcome.Unverified:
                result.Fail(DomainError.Of(ModuleCode.CLM, "POLICY-UNVERIFIED", "The policy could not be verified at the loss date."), "policyId");
                return result;
        }

        var snapshot = read.Facts!;
        if (request.PolicyNumber is { } number && !string.Equals(number.Value, snapshot.PolicyNumber, StringComparison.Ordinal))
        {
            result.Fail(DomainError.Of(ModuleCode.CLM, "POLICY-UNVERIFIED", "The policy number does not match the selected policy."), "policyNumber");
            return result;
        }

        result.Snapshot = snapshot;

        // 4. Coverage indications (REQ-CLM-048/049): every selected coverage of the snapshot and every proposed exposure's coverage.
        var codes = snapshot.CoverageCodes.Concat((request.Exposures ?? []).Select(e => e.CoverageCode)).Distinct(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            result.Indications.Add(new CoverageIndication
            {
                CoverageCode = code,
                Indication = Codes.Map<CoverageIndicationCode, CoverageIndication.IndicationValue>(CoverageRules.Indicate(snapshot, code)),
            });
        }

        // 5. Probable duplicates (REQ-CLM-041): same policy, same loss date (± window), same cause need a LINK or OVERRIDE choice.
        result.Duplicates.AddRange(await reader.DuplicateCandidatesAsync(
            legalEntity, snapshot.PolicyId, result.LossDate, request.LossCause!, settings.DuplicateWindowDays, cancellationToken).ConfigureAwait(false));
        if (result.Duplicates.Count > 0)
        {
            var decision = request.DuplicateDecision;
            if (decision is null)
            {
                result.Fail(new DomainError(ErrorCode.For(ModuleCode.CLM, "DUPLICATE-CANDIDATES"), "Probable duplicate claims exist; link to one or override with a reason.")
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["candidateClaimNumbers"] = string.Join(",", result.Duplicates.Select(d => d.ClaimNumber.Value)),
                    },
                }, "duplicateDecision");
            }
            else if (decision.Action == DuplicateDecision.ActionValue.Link && !result.Duplicates.Any(d => d.ClaimId == decision.LinkedClaimId))
            {
                result.Fail(Invalid("duplicateDecision.linkedClaimId", "LINK_TARGET", "LINK names a claim that is not a duplicate candidate."), "duplicateDecision.linkedClaimId");
            }
        }

        return result;
    }

    private static void Require(List<string> missing, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add(field);
        }
    }

    internal static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.CLM, "VALIDATION"), message) { FieldErrors = [new FieldError(field, code, "clm." + code.ToLowerInvariant(), message)] };
}
