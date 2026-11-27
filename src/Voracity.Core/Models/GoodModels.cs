namespace Voracity.Core.Models;

/// <summary>
/// Data-driven catalog entry for a good in Voracity (EU5 RawGood vs ProducedGood, with FoodValue &amp; TransportCost).
/// </summary>
public sealed record GoodDefinition(
    string Id,
    string Name,
    string Category,
    double BasePriceGold,
    bool IsLocal,
    bool IsRawGood = false,
    double FoodValue = 0.0,
    double TransportCost = 1.0);

/// <summary>
/// Bounded market equilibrium price helper.
/// </summary>
public static class MarketPricing
{
    public const double MinMultiplier = 0.25;
    public const double MaxMultiplier = 1.75;
    public const double SpreadSensitivity = 0.75;

    public static double ComputePrice(double basePriceGold, double buyOrders, double sellOrders)
    {
        if (basePriceGold <= 0.0)
            return 0.0;

        var denom = Math.Max(buyOrders, sellOrders);
        if (denom <= 1e-6)
            return basePriceGold;

        var imbalance = (buyOrders - sellOrders) / denom;
        var rawMultiplier = 1.0 + SpreadSensitivity * imbalance;
        var clamped = Math.Clamp(rawMultiplier, MinMultiplier, MaxMultiplier);
        return Math.Round(basePriceGold * clamped, 4, MidpointRounding.AwayFromZero);
    }
}
