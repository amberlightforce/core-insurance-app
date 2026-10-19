using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.CY.Claims;

/// <summary>
/// Cyprus Friendly Settlement: there is none (core default <c>NotApplicable</c>, SPI 23). Eligibility answers
/// <see cref="FsEligibilityResult.NotApplicable"/>; every other operation fails with a <c>NotApplicable</c> SPI error.
/// </summary>
public sealed class CyFriendlySettlementClearing : IFriendlySettlementClearing
{
    /// <summary>Error code of every non-eligibility operation.</summary>
    public const string NotApplicableCode = "FS_NOT_APPLICABLE";

    /// <inheritdoc />
    public ValueTask<FsEligibilityDecision> EvaluateEligibilityAsync(FsClaimFacts claimFacts, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new FsEligibilityDecision(
            FsEligibilityResult.NotApplicable, ["NO_FRIENDLY_SETTLEMENT_IN_PACK"], "CY-FS-NONE", "0.1.0",
            LegalStatus.NotRegulatory, Provisional: false, null, null));

    /// <inheritdoc />
    public ValueTask<FsSubmissionResult> SubmitAsync(FsSubmission submission, CancellationToken cancellationToken = default) => throw NotApplicable();

    /// <inheritdoc />
    public ValueTask<FsNotification> ReceiveNotificationAsync(FsNotificationMessage message, CancellationToken cancellationToken = default) => throw NotApplicable();

    /// <inheritdoc />
    public ValueTask<FsSettlementStatement> SettlementStatementAsync(FsStatementPeriod period, string? counterpartyCode = null, CancellationToken cancellationToken = default) => throw NotApplicable();

    /// <inheritdoc />
    public ValueTask<FsDisputeResult> SubmitDisputeAsync(FsDisputeSubmission dispute, CancellationToken cancellationToken = default) => throw NotApplicable();

    /// <inheritdoc />
    public ValueTask<FsDisputeResult> RecordReplyAsync(FsReplyRecord reply, CancellationToken cancellationToken = default) => throw NotApplicable();

    private static SpiException NotApplicable() =>
        new(new SpiError(SpiErrorCategory.NotApplicable, NotApplicableCode), "Cyprus has no Friendly Settlement clearing.");
}
