using CoreIns.Modules.Billing.Domain;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>The pure posting rules of the billing sub-ledger (REQ-BIL-280, REQ-BIL-286, REQ-BIL-287). No database.</summary>
public sealed class PostingTests
{
    private static readonly LedgerRule[] Rules =
    [
        new("BLR-WRITTEN-PREMIUM", 1, EntryTypes.Written, "PREMIUM", "DIRECT_BILL", "*", "*", "LA-01", "LA-04", "AMOUNT"),
        new("BLR-WRITTEN-TAX-DUE", 1, EntryTypes.Written, "TAX", "DIRECT_BILL", "*", RuleQualifiers.IptLiabilityDue, "LA-01", "LA-27", "AMOUNT"),
        new("BLR-WRITTEN-TAX-WRITTEN", 1, EntryTypes.Written, "TAX", "DIRECT_BILL", "*", RuleQualifiers.IptLiabilityWritten, "LA-01", "LA-06", "AMOUNT"),
        new("BLR-BILLED", 1, EntryTypes.Billed, "*", "DIRECT_BILL", "*", "*", "LA-02", "LA-01", "AMOUNT"),
    ];

    private static PostingKey Key(string type, string category, string qualifier = "*") => new(type, category, "DIRECT_BILL", "GR", qualifier);

    [Fact]
    public void REQ_BIL_286_the_IPT_liability_point_selects_the_credit_account()
    {
        Posting.Match(Rules, Key(EntryTypes.Written, "TAX", RuleQualifiers.IptLiabilityDue)).Value.CreditAccount.ShouldBe("LA-27");
        Posting.Match(Rules, Key(EntryTypes.Written, "TAX", RuleQualifiers.IptLiabilityWritten)).Value.CreditAccount.ShouldBe("LA-06");
        Posting.Match(Rules, Key(EntryTypes.Written, "PREMIUM")).Value.CreditAccount.ShouldBe("LA-04");
        Posting.Match(Rules, Key(EntryTypes.Billed, "TAX")).Value.RuleId.ShouldBe("BLR-BILLED");
    }

    [Fact]
    public void REQ_BIL_287_no_rule_or_an_ambiguous_rule_is_refused_never_a_default_account()
    {
        Posting.Match(Rules, Key(EntryTypes.Written, "SURCHARGE")).Error!.Code.Value.ShouldBe("BIL-ERR-NO-RULE");
        LedgerRule[] twice = [.. Rules, Rules[0] with { RuleId = "DUPLICATE" }];
        Posting.Match(twice, Key(EntryTypes.Written, "PREMIUM")).IsFailure.ShouldBeTrue();
        Posting.Amount(Rules[0] with { AmountExpression = "AMOUNT * 2" }, Money.Of(10m, "EUR")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void REQ_BIL_280_lines_balance_per_currency_and_refuse_unrounded_or_negative_amounts()
    {
        var dims = new LineDimensions();
        var lines = Posting.Lines([new PostingLeg(Rules[0], Money.Of(312.35m, "EUR"), dims), new PostingLeg(Rules[1], Money.Of(46.85m, "EUR"), dims)]);
        lines.Count.ShouldBe(4);
        Posting.IsBalanced(lines).ShouldBeTrue();
        Posting.Lines([new PostingLeg(Rules[0], Money.Of(0m, "EUR"), dims)]).ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => Posting.Lines([new PostingLeg(Rules[0], Money.Of(1.005m, "EUR"), dims)]));
        Should.Throw<InvalidOperationException>(() => Posting.Lines([new PostingLeg(Rules[0], Money.Of(-1m, "EUR"), dims)]));
        Posting.IsBalanced([new DraftLine("LA-01", LedgerSide.Debit, Money.Of(1m, "EUR"), "x", dims)]).ShouldBeFalse();
    }
}
