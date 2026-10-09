using System.Text.Json;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using RatDayCount = CoreIns.Modules.Rating.Contracts.Api.DayCountConvention;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

/// <summary>How a charge type of the pinned product artefact is handled (REQ-PFC-134, D-SL3-17).</summary>
/// <param name="Flat">FLAT or FULLY_EARNED: never prorated.</param>
/// <param name="RefundableOnCancel">Only a FLAT charge whose <c>cancellationTreatment</c> is PRO_RATA is credited on cancellation; FULLY_EARNED and NON_REFUNDABLE never are.</param>
internal sealed record ChargeHandlingInfo(bool Flat, bool RefundableOnCancel);

/// <summary>
/// What a cancellation reads from the term's pinned product artefact, by hash (D-SL3-20): the day-count convention and the
/// handling of every charge type. Anything missing or unknown refuses the cancellation (fail closed, PITFALLS 10): nothing is
/// guessed, and no default convention exists.
/// </summary>
internal sealed record PinnedArtefact(DayCountConvention Convention, IReadOnlyDictionary<string, ChargeHandlingInfo> Handling)
{
    public static async Task<Result<PinnedArtefact>> LoadAsync(IProductArtifactService products, string artefactHash, CancellationToken cancellationToken)
    {
        JsonElement? artefact;
        try
        {
            artefact = (await products.GetAsync(artefactHash, cancellationToken: cancellationToken).ConfigureAwait(false)).CanonicalJsonArtefact;
        }
        catch (DomainException)
        {
            return Refused("the term's pinned product artefact cannot be read");
        }

        if (artefact is not { ValueKind: JsonValueKind.Object } json)
        {
            return Refused("the term's pinned product artefact has no content");
        }

        if (!json.TryGetProperty("dayCount", out var dayCount) || dayCount.ValueKind != JsonValueKind.String
            || !DayCountConventions.TryParse(dayCount.GetString()?.Trim().ToUpperInvariant().Replace('/', '_'), out var convention))
        {
            return Refused("the pinned product artefact declares no known day-count convention (dayCount)");
        }

        var handling = new Dictionary<string, ChargeHandlingInfo>(StringComparer.Ordinal);
        if (json.TryGetProperty("chargeTypes", out var chargeTypes) && chargeTypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var charge in chargeTypes.EnumerateArray())
            {
                if (!charge.TryGetProperty("code", out var code) || code.GetString() is not { } name)
                {
                    continue;
                }

                var kind = charge.TryGetProperty("handling", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                var treatment = charge.TryGetProperty("cancellationTreatment", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                switch (kind)
                {
                    case "PRO_RATA":
                        handling[name] = new ChargeHandlingInfo(false, false);
                        break;
                    case "FLAT":
                        handling[name] = new ChargeHandlingInfo(true, treatment == "PRO_RATA");
                        break;
                    case "FULLY_EARNED":
                        handling[name] = new ChargeHandlingInfo(true, false);
                        break;
                }
            }
        }

        return new PinnedArtefact(convention, handling);
    }

    internal static DomainError Refused(string detail) =>
        DomainError.Of(ModuleCode.POL, "GATE-FAILED", $"The cancellation cannot be priced: {detail}. Nothing was changed.");

    internal static RatDayCount ToRating(DayCountConvention convention) =>
        convention == DayCountConvention.Act365F ? RatDayCount.Act365f : RatDayCount.TermRatio;
}

/// <summary>An <see cref="IProration"/> that must be told the term it prorates for (the pinned artefact and configuration it was priced under).</summary>
internal interface IBoundProration : IProration
{
    /// <summary>Binds the term; until then <see cref="IProration.Fraction"/> fails closed.</summary>
    void Bind(string productArtefactHash, ConfigurationHash configurationHash, ServicingTerm term);
}

/// <summary>
/// The production <see cref="IProration"/>: the shared proration of RAT (<c>rat.Proration.prorate</c>, REQ-RAT-004) behind POL's port. RAT
/// reads the day-count from the pinned artefact and refuses a convention the artefact does not declare; the fraction RAT returns must
/// equal the fraction the engine is about to use (<c>days/termDays</c> for TERM_RATIO, <c>days/365</c> and 1 for a full term for
/// ACT/365F), otherwise the call fails closed. Never <see cref="ReferenceProration"/>, which is test-only (PITFALLS 43).
/// </summary>
internal sealed class RatingProrationAdapter(IRatingProrationEngine rating) : IBoundProration
{
    private string? _artefact;
    private ConfigurationHash? _configuration;
    private ServicingTerm? _term;

    public void Bind(string productArtefactHash, ConfigurationHash configurationHash, ServicingTerm term)
    {
        _artefact = productArtefactHash;
        _configuration = configurationHash;
        _term = term;
    }

    public ProrationFraction Fraction(DayCountConvention convention, int days, int termDays)
    {
        if (_term is not { } term || _artefact is null || _configuration is not { } configuration)
        {
            throw new DomainException(DomainError.Of(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", "The proration service is not bound to a term."));
        }

        if (days < 0 || termDays <= 0 || days > termDays)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Days must be within the term.");
        }

        var expected = convention switch
        {
            DayCountConvention.TermRatio => new ProrationFraction(days, termDays),
            DayCountConvention.Act365F => days == termDays ? new ProrationFraction(1, 1) : new ProrationFraction(days, 365),
            _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "Unknown convention."),
        };
        if (days == 0 || expected == new ProrationFraction(1, 1))
        {
            // Nothing to prorate: no days, or the full term of an ACT/365F term (capped at the annual amount, so a 366-day term is 1, not 366/365).
            return expected;
        }

        var from = term.From.ToBusinessDate(term.Zone);
        var response = rating.ProrateAsync(
            new Modules.Rating.Contracts.Api.ProrationProrateRequest
            {
                AnnualRates =
                [
                    new Modules.Rating.Contracts.Api.ProrationAnnualRate
                    {
                        SegmentId = "p", ElementLocator = "x", CoverageCode = "x", ChargeType = "x", ChargeCategory = "PREMIUM",
                        AnnualAmount = new Money(1m, term.Currency), Handling = Modules.Rating.Contracts.Api.ProrationAnnualRate.HandlingValue.Proratable,
                    },
                ],
                Term = DateRange.Of(from, from.AddDays(termDays)),
                Periods = [new Modules.Rating.Contracts.Api.ProrationPeriod { SegmentId = "p", Period = DateRange.Of(from, from.AddDays(days)) }],
                Convention = PinnedArtefact.ToRating(convention),
                ProductArtefactHash = Sha256Hash.Parse(_artefact),
                ConfigurationHash = configuration,
            }).GetAwaiter().GetResult();
        var line = response.Lines.Single();
        var wanted = (decimal)expected.Numerator / expected.Denominator;
        if (Math.Abs(line.Fraction - wanted) > 0.000001m)
        {
            throw new DomainException(PinnedArtefact.Refused($"RAT prorates {days}/{termDays} days as {line.Fraction}, not {wanted}"));
        }

        return expected;
    }
}
