using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Events;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Rating.Services;

using Mode = RateRateRequest.EnvelopeDetail.ModeValue;

/// <summary>
/// The rating modes of <c>rat.Rate.rate</c> and what each one changes (REQ-RAT-038..041, REQ-POL-093, REQ-POL-249 subset):
/// <list type="bullet">
/// <item><b>FULL / QUICK / DRY_RUN</b> (new business): unchanged. The artefact is the one the caller names, else the one active on the rating basis date.</item>
/// <item><b>ENDORSEMENT</b>: rates under the term's pinned artefact, never the currently active one (REQ-POL-093). The pin travels in the envelope's <c>ratingArtefactHash</c> until SL3-CONTRACTS adds <c>pinnedRatingArtefactHash</c>; it is mandatory, and an unknown hash or one that belongs to another product or version is <c>RAT-ERR-INPUT</c>. Segments are the changed part of one annual term (up to a year), not full years.</item>
/// <item><b>RENEWAL</b>: rates under the artefact active at the new term start, as the caller resolved it (<c>ratingArtefactHash</c>), else resolved here on the rating basis date, which the caller sets to the new term start. Segments are full years like new business. No cap and no change explanation in this slice.</item>
/// </list>
/// ENDORSEMENT and RENEWAL are automated decisions (PRD-03 §6.2) and bindable; QUICK and DRY_RUN never are.
/// </summary>
internal static class RatingModes
{
    public static void EnsureSupported(Mode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw Error("ENVELOPE", $"Mode {mode} is not available (FULL, QUICK, DRY_RUN, ENDORSEMENT and RENEWAL are).");
        }
    }

    /// <summary>Bindable ratings: FULL, ENDORSEMENT and RENEWAL.</summary>
    public static bool IsBindable(Mode mode) => mode is Mode.Full or Mode.Endorsement or Mode.Renewal;

    /// <summary>Automated-decision flag (REQ-RAT-036): true for ENDORSEMENT and RENEWAL; FULL keeps its slice-1 value (false).</summary>
    public static bool IsAutomatedDecision(Mode mode) => mode is Mode.Endorsement or Mode.Renewal;

    public static RatingCalculatedV1.ModeValue EventMode(Mode mode) => mode switch
    {
        Mode.Quick => RatingCalculatedV1.ModeValue.Quick,
        Mode.Endorsement => RatingCalculatedV1.ModeValue.Endorsement,
        Mode.Renewal => RatingCalculatedV1.ModeValue.Renewal,
        _ => RatingCalculatedV1.ModeValue.Full,
    };

    /// <summary>
    /// A segment must be one year (new business, renewal). In an endorsement it is the changed part of the term: it starts on the
    /// effective date and may end up to a year later (a term bound on 29 Feb ends on 28 Feb, so the test is the date arithmetic, not 365 days).
    /// </summary>
    public static bool SegmentPeriodAllowed(Mode mode, DateRange period)
    {
        var end = period.End;
        return mode == Mode.Endorsement
            ? end is { } e && e > period.Start && e <= period.Start.AddYears(1)
            : end == period.Start.AddYears(1);
    }

    public static string PeriodMessage(Mode mode, string segmentId) => mode == Mode.Endorsement
        ? $"Segment {segmentId} must end after it starts and no later than one year after it starts."
        : $"Segment {segmentId} must be one year: the slice rates annual terms only (D6).";

    /// <summary>
    /// The artefact an ENDORSEMENT rates under: the term's pinned one. Nothing is resolved by date, so a newer activation never
    /// changes the price of a change inside an old term (REQ-POL-093).
    /// </summary>
    public static async Task<CompiledArtefact> ResolvePinnedAsync(
        RatingStore store, RateRateRequest.EnvelopeDetail envelope, string? version, CancellationToken cancellationToken)
    {
        var pinned = envelope.RatingArtefactHash
            ?? throw Error("INPUT", "ENDORSEMENT rates under the term's pinned rating artefact; send its hash (pinnedRatingArtefactHash).");
        var artefact = await store.LoadAsync(pinned.Value, cancellationToken).ConfigureAwait(false)
            ?? throw Error("INPUT", "The pinned rating artefact is unknown.");
        if (!string.Equals(artefact.Definition.ProductCode, envelope.ProductCode, StringComparison.Ordinal)
            || (version is not null && !string.Equals(artefact.Definition.ProductVersion, version, StringComparison.Ordinal)))
        {
            throw Error("INPUT", "The pinned rating artefact does not belong to this product version.");
        }

        return artefact;
    }

    private static DomainException Error(string code, string detail) => new(DomainError.Of(ModuleCode.RAT, code, detail));
}
