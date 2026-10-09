using System.Numerics;
using CoreIns.Modules.Billing.Domain;

namespace CoreIns.IntegrationTests.Bil.Receivables;

public sealed class ReceivableReferenceTests
{
    [Fact]
    public void REQ_BIL_125_RF_reference_meets_length_and_mod97_and_contains_no_party_data()
    {
        foreach (var id in new[] { Guid.Empty, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Guid.CreateVersion7() })
        {
            var reference = ReceivableReference.Create(id);
            reference.Length.ShouldBe(25);
            reference[..2].ShouldBe("RF");
            var reordered = reference[4..] + reference[..4];
            var number = string.Concat(reordered.Select(c => c <= '9' ? c.ToString() : ((int)c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            (BigInteger.Parse(number, System.Globalization.CultureInfo.InvariantCulture) % 97).ShouldBe(BigInteger.One);
            ReceivableReference.Create(id).ShouldBe(reference);
        }
    }

    [Fact]
    public void REQ_BIL_125_generated_references_differ_for_distinct_receivables()
    {
        Enumerable.Range(0, 10000).Select(_ => ReceivableReference.Create(Guid.CreateVersion7())).Distinct().Count().ShouldBe(10000);
    }
}
