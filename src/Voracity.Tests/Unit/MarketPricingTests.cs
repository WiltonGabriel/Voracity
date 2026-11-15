using Voracity.Core.Models;

namespace Voracity.Tests.Unit;

public sealed class MarketPricingTests
{
    [Fact]
    public void ComputePrice_ClampsStrictlyAtPlusAndMinus75Percent()
    {
        const double p0 = 40.0;
        Assert.Equal(70.0, MarketPricing.ComputePrice(p0, buyOrders: 10_000, sellOrders: 0), 4);
        Assert.Equal(10.0, MarketPricing.ComputePrice(p0, buyOrders: 0, sellOrders: 10_000), 4);
        Assert.InRange(MarketPricing.ComputePrice(p0, buyOrders: 10_000, sellOrders: 1), 69.9, 70.0);
    }

    [Fact]
    public void ComputePrice_HandlesZeroVolumeAndInvalidBasePrice()
    {
        Assert.Equal(55.0, MarketPricing.ComputePrice(55.0, buyOrders: 0, sellOrders: 0), 4);
        Assert.Equal(55.0, MarketPricing.ComputePrice(55.0, buyOrders: 400, sellOrders: 400), 4);
        Assert.Equal(0.0, MarketPricing.ComputePrice(0.0, buyOrders: 100, sellOrders: 10), 4);
    }
}
