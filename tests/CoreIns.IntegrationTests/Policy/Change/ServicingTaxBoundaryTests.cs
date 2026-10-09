using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.SharedKernel;
using RatDelta = CoreIns.Modules.Rating.Contracts.Servicing.ServicingDelta;
using RatLine = CoreIns.Modules.Rating.Contracts.Servicing.ServicingTaxLine;
using Delta = CoreIns.Modules.Policy.Domain.Servicing.ServicingDelta;

namespace CoreIns.IntegrationTests.Policy.Change;

public sealed class ServicingTaxBoundaryTests
{
    private static readonly BusinessDate Date = new(2027, 1, 15);

    private static (Delta Delta, RatDelta Rat) Item(string id = "0", decimal amount = 70m, TransactionKind kind = TransactionKind.EndorsementDebit)
    {
        var key = new ChargeKey("vehicle-" + id, "MTPL", "PREM-MTPL");
        var from = Instant.FromUtc(2027, 1, 15);
        return (
            new Delta(key, "PREMIUM", from, from.Plus(TimeSpan.FromDays(100)), Date.Value, Date.AddDays(100).Value, amount, 100, 100, 365, kind, "test", "test", 0),
            new RatDelta(id, key.CoverageCode, key.ChargeType, "GR-IPT", ServicingTaxCategory.Tax, "general", new Money(amount, Currency.EUR), Date, Date.AddDays(100),
                kind == TransactionKind.EndorsementCredit ? ServicingTransactionKind.EndorsementCredit : ServicingTransactionKind.EndorsementDebit));
    }

    private static RatLine Line(RatDelta delta) => new(
        delta.DeltaRef, delta.Element, delta.TaxChargeType, delta.Category, delta.TaxClass, delta.Delta, 0.15m,
        new Money(decimal.Round(delta.Delta.Amount * 0.15m, 2), Currency.EUR), ServicingTreatmentAction.Apply,
        ServicingCustomerCredit.ProRata, ServicingAuthorityLiability.Reduce, ServicingFiscalDocument.None,
        "rate-rule", "1", "treatment-rule", "1", "Settled", false, "test source");

    [Fact]
    public void Returned_lines_cannot_be_swapped_between_premium_deltas()
    {
        var first = Item();
        var second = Item("1", 100m);

        var result = RatingServicingTaxAdapter.MapLines([first, second], new ServicingTaxLinesResult([Line(second.Rat), Line(first.Rat)]));

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.Value.ShouldBe("POL-ERR-RATING");
    }

    [Theory]
    [InlineData("element")]
    [InlineData("charge")]
    [InlineData("class")]
    [InlineData("base")]
    [InlineData("currency")]
    [InlineData("action")]
    [InlineData("rate")]
    [InlineData("rule")]
    [InlineData("status")]
    public void Invalid_calculated_line_evidence_is_refused(string changed)
    {
        var item = Item();
        var line = Line(item.Rat);
        line = changed switch
        {
            "element" => line with { Element = "OTHER" },
            "charge" => line with { TaxChargeType = "OTHER" },
            "class" => line with { TaxClass = "OTHER" },
            "base" => line with { Base = new Money(71m, Currency.EUR) },
            "currency" => line with { Amount = new Money(line.Amount.Amount, Currency.FromCode("USD")) },
            "action" => line with { TreatmentAction = (ServicingTreatmentAction)999 },
            "rate" => line with { Rate = null },
            "rule" => line with { CalculationRuleId = null },
            _ => line with { Provisional = true },
        };

        RatingServicingTaxAdapter.MapLines([item], new ServicingTaxLinesResult([line])).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Kept_credit_without_a_calculated_rate_preserves_the_treatment_and_zero_amount()
    {
        var item = Item(amount: -70m, kind: TransactionKind.EndorsementCredit);
        var line = Line(item.Rat) with
        {
            TreatmentAction = ServicingTreatmentAction.KeepNotReduced, Rate = null, Amount = new Money(0m, Currency.EUR),
            CalculationRuleId = null, CalculationRuleVersion = null,
        };

        var result = RatingServicingTaxAdapter.MapLines([item], new ServicingTaxLinesResult([line]));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Single().Action.ShouldBe(TreatmentActionCode.KeepNotReduced);
        result.Value.Single().Amount.ShouldBe(0m);
        ChangePricer.CheckTax([item.Delta], result.Value).ShouldBeNull();
    }

    [Fact]
    public void An_unchanged_debit_retains_APPLY_with_zero_tax_and_a_credit_still_requires_KEEP()
    {
        var debit = Item(amount: 0m);
        var mapped = RatingServicingTaxAdapter.MapLines([debit], new ServicingTaxLinesResult([Line(debit.Rat)])).Value;

        ChangePricer.CheckTax([debit.Delta], mapped).ShouldBeNull();
        ChangePricer.CheckTax([debit.Delta], [mapped.Single() with { Amount = 0.01m }]).ShouldNotBeNull();
        ChangePricer.CheckTax([debit.Delta with { TransactionKind = TransactionKind.EndorsementCredit }], mapped).ShouldNotBeNull();
        ChangePricer.CheckTax([debit.Delta with { Amount = -1m }], mapped).ShouldNotBeNull();
    }
}
