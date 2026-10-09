using CoreIns.Modules.Billing.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.IntegrationTests.Bil.Refunds;

/// <summary>
/// SL3-BIL-REFUND: the pure netting and refund calculation (REQ-BIL-181, REQ-BIL-182): the credit is first set against open debit
/// items of the same term, then the other terms, and only the rest is refunded; the credit set key is the business reference of
/// the payment (REQ-BIL-202). Amounts are illustrative test data.
/// </summary>
public sealed class RefundPlannerTests
{
    private static readonly PolicyId Policy = PolicyId.New();
    private static readonly PolicyTermId Term1 = PolicyTermId.New();
    private static readonly PolicyTermId Term2 = PolicyTermId.New();

    private static CreditSource Credit(PolicyTermId term, decimal remaining, string type = "PREMIUM") =>
        new(Guid.CreateVersion7(), InvoiceId.New(), Policy, term, PolicyTransactionId.New(), type, "PREMIUM", remaining);

    private static DebitTarget Debit(PolicyTermId term, decimal open, int due = 10, string number = "INV-1", int line = 1) =>
        new(InvoiceId.New(), Guid.CreateVersion7(), term, new BusinessDate(2027, 1, due), number, line, open);

    [Fact]
    public void REQ_BIL_182_example_150_credit_and_40_overdue_on_another_term_nets_40_and_refunds_110()
    {
        var credit = Credit(Term1, 150m);
        var debit = Debit(Term2, 40m);

        var plan = RefundPlanner.Plan([credit], [debit]);

        plan.Netted.ShouldBe(40m);
        plan.Netting.ShouldHaveSingleItem().Amount.ShouldBe(40m);
        plan.Total.ShouldBe(110m);
        plan.Lines.ShouldHaveSingleItem().Amount.ShouldBe(110m);
    }

    [Fact]
    public void REQ_BIL_182_same_term_credit_is_used_first_and_debit_is_never_overpaid()
    {
        var sameTerm = Credit(Term1, 30m);
        var otherTerm = Credit(Term2, 100m);
        var debit = Debit(Term1, 50m);

        var plan = RefundPlanner.Plan([otherTerm, sameTerm], [debit]);

        plan.Netting.Select(n => (n.Credit.CreditItemId, n.Amount)).ShouldBe([(sameTerm.CreditItemId, 30m), (otherTerm.CreditItemId, 20m)]);
        plan.Netted.ShouldBe(50m);
        plan.Total.ShouldBe(80m);
        plan.Lines.ShouldHaveSingleItem().Credit.CreditItemId.ShouldBe(otherTerm.CreditItemId);
    }

    [Fact]
    public void REQ_BIL_182_debits_of_the_credits_term_are_netted_before_other_terms_then_by_due_date()
    {
        var credit = Credit(Term1, 60m);
        var late = Debit(Term2, 40m, due: 5, number: "INV-A");
        var sameTermLate = Debit(Term1, 40m, due: 20, number: "INV-B");
        var sameTermEarly = Debit(Term1, 40m, due: 15, number: "INV-C");

        var plan = RefundPlanner.Plan([credit], [late, sameTermLate, sameTermEarly]);

        plan.Netting.Select(n => (n.Target.InvoiceNumber, n.Amount)).ShouldBe([("INV-C", 40m), ("INV-B", 20m)]);
        plan.Total.ShouldBe(0m);
        plan.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void REQ_BIL_182_credit_that_is_fully_used_or_empty_leaves_nothing_to_refund_and_totals_are_exact()
    {
        RefundPlanner.Plan([], [Debit(Term1, 10m)]).Lines.ShouldBeEmpty();
        RefundPlanner.Plan([Credit(Term1, 0m)], []).Lines.ShouldBeEmpty();
        var a = Credit(Term1, 0.10m);
        var b = Credit(Term1, 0.20m);
        RefundPlanner.Plan([a, b], []).Total.ShouldBe(0.30m);
    }

    [Fact]
    public void REQ_BIL_202_the_credit_set_key_is_the_business_reference_and_is_independent_of_order()
    {
        var a = new RefundLine(Credit(Term1, 5m), 5m);
        var b = new RefundLine(Credit(Term1, 7m), 7m);

        RefundContent.CreditSetKey([a, b]).ShouldBe(RefundContent.CreditSetKey([b, a]));
        RefundContent.CreditSetKey([a]).ShouldNotBe(RefundContent.CreditSetKey([a, b]));
        RefundContent.CreditSetKey([a with { Amount = 4m }]).ShouldNotBe(RefundContent.CreditSetKey([a]));
        RefundContent.CreditSetKey([a]).Length.ShouldBe(64);
    }

    [Fact]
    public void The_actor_guid_is_the_directory_object_id_when_there_is_one_and_stable_otherwise()
    {
        var oid = Guid.NewGuid();
        RefundContent.ActorGuid($"USER:{oid}").ShouldBe(oid);
        RefundContent.ActorGuid("USER:alice").ShouldBe(RefundContent.ActorGuid("USER:alice"));
        RefundContent.ActorGuid("USER:alice").ShouldNotBe(RefundContent.ActorGuid("USER:bob"));
    }
}
