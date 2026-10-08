using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

// LOCAL STAND-INS (SL3-CONTRACTS has not merged): the typed request, response and refund preview of pol.Cancellation.create.
// The generated CancellationCreateRequest/Response carry source, reason and the preview as untyped JSON and have no `kind`.
// When SL3-CONTRACTS merges, these records are replaced by the generated ones (same member names and wire shape).

/// <summary>Body of <c>POST /api/pol/v1/cancellations</c> (<c>pol.Cancellation.create</c>).</summary>
internal sealed record CancellationRequest
{
    /// <summary>The policy whose term is cancelled.</summary>
    public required PolicyId PolicyId { get; init; }

    /// <summary>Cancellation source from the MKT code list (REQ-POL-205). Only <c>Policyholder</c> in this release.</summary>
    public required string Source { get; init; }

    /// <summary>Reason code (free code from <c>pol.cancel.reasons.&lt;source&gt;</c>).</summary>
    public required string ReasonCode { get; init; }

    /// <summary>Optional effective instant. Absent = the request time (REQ-POL-208). Present and not now is refused (REQ-POL-209).</summary>
    public Instant? EffectiveAt { get; init; }

    /// <summary><c>Standard</c> (default) or <c>Flat</c> (term start, only while the term is Scheduled, REQ-POL-217).</summary>
    public string? Kind { get; init; }
}

/// <summary>The command wrapper (<c>pol.Cancellation.create</c>).</summary>
internal sealed record CancelPolicy(CancellationRequest Request) : CoreIns.Platform.Commands.ICommand<CancellationResponse>;

/// <summary>Result of <c>pol.Cancellation.create</c>: the job, the bound transaction and the refund preview.</summary>
internal sealed record CancellationResponse
{
    public required JobId JobId { get; init; }

    public required string JobNumber { get; init; }

    public required string State { get; init; }

    public required PolicyId PolicyId { get; init; }

    public required PolicyTermId TermId { get; init; }

    public required int TermNumber { get; init; }

    public required string TermState { get; init; }

    public required PolicyTransactionId TransactionId { get; init; }

    public required string Source { get; init; }

    public required string ReasonCode { get; init; }

    /// <summary><c>STANDARD</c> or <c>FLAT</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The refund method applied: <c>PRO_RATA</c> or, for a flat cancellation, <c>FULL_REFUND</c>.</summary>
    public required string RefundMethod { get; init; }

    public required Instant EffectiveAt { get; init; }

    /// <summary>The single record time of every row of the cancellation (D-SL3-03).</summary>
    public required Instant RecordedAt { get; init; }

    public required RefundPreview RefundPreview { get; init; }

    /// <summary>REQ-POL-212: notices are recorded as not sent because DOC is not built; no DOC call is made.</summary>
    public required string Notices { get; init; }
}

/// <summary>
/// The refund per element × charge type (REQ-POL-207). Amounts are NET charge deltas: a credit is negative. Taxes follow the
/// MKT treatment of the line (REQ-POL-215); an IPT line kept by the authority is a zero delta marked provisional.
/// </summary>
internal sealed record RefundPreview
{
    public required string Currency { get; init; }

    public required IReadOnlyList<RefundPreviewLine> Lines { get; init; }

    /// <summary>Sum of the premium credits (negative).</summary>
    public required decimal PremiumCredit { get; init; }

    /// <summary>Sum of the tax and levy credits (negative or zero).</summary>
    public required decimal TaxCredit { get; init; }

    /// <summary>What the policyholder is owed back: the positive total of the credits.</summary>
    public required decimal RefundDue { get; init; }

    /// <summary>Plain-language notes, e.g. "IPT not refunded (provisional)".</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>One line of the refund preview.</summary>
internal sealed record RefundPreviewLine
{
    public required string ElementLocator { get; init; }

    public required string CoverageCode { get; init; }

    public required string ChargeType { get; init; }

    public required string ChargeCategory { get; init; }

    /// <summary>Written for the line before the cancellation.</summary>
    public required decimal Written { get; init; }

    /// <summary>The delta of the cancellation (negative = credit).</summary>
    public required decimal Amount { get; init; }

    /// <summary>Days of the cancelled remainder and the segment it came from (proration fraction); 0/0 for a tax line.</summary>
    public required int Days { get; init; }

    public required int Basis { get; init; }

    public TreatmentView? Treatment { get; init; }
}

/// <summary>The MKT treatment applied to a tax or levy line.</summary>
internal sealed record TreatmentView
{
    public required string Action { get; init; }

    public required string RuleId { get; init; }

    public required string RuleVersion { get; init; }

    public required string LegalStatus { get; init; }

    public required bool Provisional { get; init; }
}

/// <summary>Parsing of the request's text fields.</summary>
internal static class CancellationText
{
    public const string NoticesNotSent = "NOT_SENT_DOC_NOT_BUILT";

    public static bool TryKind(string? text, out Domain.CancellationKind kind)
    {
        kind = Domain.CancellationKind.Standard;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var candidate in Enum.GetValues<Domain.CancellationKind>())
        {
            if (string.Equals(candidate.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The wire code of a refund method (PFC: PRO_RATA, FULL_REFUND).</summary>
    public static string Code(RefundMethod method) => Domain.Codes.Of(method);
}

/// <summary>
/// Refund method by cancellation source (REQ-POL-206). The term's pinned artefact (MOTOR-GR 1.1, SL3-PFC-MOTOR11) is the
/// authority; until PFC exposes it to POL this table carries its content: Policyholder → ProRata (commercial, ILLUSTRATIVE);
/// every other source is absent and the command fails closed.
/// </summary>
internal interface ICancellationRefundMethods
{
    /// <summary>The method for <paramref name="source"/>, or null when the product declares none (fail closed).</summary>
    RefundMethod? Resolve(string source);
}

/// <summary>Illustrative refund methods: only Policyholder → ProRata.</summary>
internal sealed class IllustrativeRefundMethods : ICancellationRefundMethods
{
    public RefundMethod? Resolve(string source) => source == Domain.CancellationSources.Policyholder ? RefundMethod.ProRata : null;
}
