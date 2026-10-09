using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.IntegrationTests.Claims.Money2;

public sealed class Money2DomainTests
{
    [Fact]
    public void REQ_CLM_096_Net_incurred_follows_the_PRD_example()
    {
        var amounts = new LineAmounts(1300m, 900m, 900m, 500m, 300m);
        amounts.OpenReserve.ShouldBe(400m);
        amounts.Incurred.ShouldBe(1300m);
        amounts.OpenRecoveryReserve.ShouldBe(200m);
        amounts.NetIncurred.ShouldBe(800m);
    }

    [Fact]
    public void D_SL4_07_Recovery_reserve_split_does_not_evade_exposure_authority()
    {
        var exposure = ExposureId.New();
        var line = new ReserveLineRow { ReserveLineId = ReserveLineId.New(), ExposureId = exposure, CostType = "INDEMNITY", Currency = "EUR" };
        var transactions = Enumerable.Range(0, 20).Select(_ => new FinancialTransactionRow
        {
            Kind = "RECOVERY_RESERVE", ReserveLineId = line.ReserveLineId, ExposureId = exposure, Amount = 5000m, Currency = "EUR",
        }).ToList();
        var content = new SetContent(new TransactionSetRow(), transactions,
            new Dictionary<ReserveLineId, ReserveLineRow> { [line.ReserveLineId] = line }, []);
        var requirements = SetAuthority.Requirements(content, [line], new Dictionary<ReserveLineId, LineAmounts>());
        requirements.Single().Amount.ShouldBe(100000m);
        requirements.Single().Basis.ShouldBe("EXPOSURE_TOTAL_RECOVERY_RESERVE");
    }
}
