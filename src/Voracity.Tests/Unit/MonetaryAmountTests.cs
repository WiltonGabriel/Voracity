using Voracity.Core.Models;

namespace Voracity.Tests.Unit;

public sealed class MonetaryAmountTests
{
    [Fact]
    public void MonetaryAmount_FormatsPositiveZeroAndNegativeValues()
    {
        Assert.Equal("0 Cobre", MonetaryAmount.Zero.ToUiString());
        Assert.Equal("42 Cobre", new MonetaryAmount(42).ToUiString());
        Assert.Equal("7 Prata, 5 Cobre", new MonetaryAmount(705).ToUiString());
        Assert.Equal("125 Ouro, 43 Prata, 21 Cobre", new MonetaryAmount(1_254_321).ToUiString());
        Assert.Equal("-3 Ouro, 50 Prata, 0 Cobre", new MonetaryAmount(-35_000).ToUiString());
    }

    [Fact]
    public void MonetaryAmount_OperatorsAndFromGoldRoundCorrectly()
    {
        var a = MonetaryAmount.FromGold(10.25);
        var b = MonetaryAmount.FromGold(4.75);
        Assert.Equal(15.0, (a + b).ToGold(), 4);
        Assert.Equal(5.5, (a - b).ToGold(), 4);
    }
}
