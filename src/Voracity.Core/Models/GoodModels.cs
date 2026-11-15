namespace Voracity.Core.Models;

/// <summary>
/// Data-driven catalog entry for a good in Voracity (staple, industrial, luxury, military, or local).
/// </summary>
public sealed record GoodDefinition(
    string Id,
    string Name,
    string Category,
    double BasePriceGold,
    bool IsLocal);

/// <summary>
/// Implements Victoria 3's bounded market equilibrium price formula:
/// P = P0 * (1 + 0.75 * ((D - S) / max(D, S))), clamped to [0.25 * P0, 1.75 * P0].
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
