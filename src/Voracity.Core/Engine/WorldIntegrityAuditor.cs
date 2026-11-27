using System.Runtime.InteropServices;
using Voracity.Core.Models;

namespace Voracity.Core.Engine;

public static class WorldIntegrityAuditor
{
    public static SimulationIntegrityReport Audit(SimulationState state)
    {
        var anomalies = new List<string>();
        long totalPop = 0;
        var activePops = 0;
        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active)
                continue;
            activePops++;
            if (p.Population <= 0)
                anomalies.Add($"Active pop #{i} has non-positive population ({p.Population}).");
            else
                totalPop += p.Population;

            if (float.IsNaN(p.Literacy) || float.IsInfinity(p.Literacy))
                anomalies.Add($"Pop #{i} invalid Literacy.");
            if (float.IsNaN(p.Satisfaction) || float.IsInfinity(p.Satisfaction))
                anomalies.Add($"Pop #{i} invalid Satisfaction.");
        }

        if (totalPop <= 0)
            anomalies.Add("Total world population <= 0.");

        long totalTreasury = 0;
        for (var n = 0; n < state.NationsRuntime.Length; n++)
        {
            var nat = state.NationsRuntime[n];
            totalTreasury += nat.TreasuryCoppers;
            if (double.IsNaN(nat.TaxRate) || nat.TaxRate is < 0.0 or > 1.0)
                anomalies.Add($"Nation {state.Nations[n].Id} invalid tax rate ({nat.TaxRate}).");
        }

        double ctrlSum = 0.0;
        int ctrlCount = 0;
        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = state.HabitableLocationOrderByAccess[i];
            var loc = state.Locations[lIdx];
            if (double.IsNaN(loc.Control) || loc.Control is < -0.01 or > 100.01)
                anomalies.Add($"Location {state.LocationDefs[lIdx].Id} Control out of bounds: {loc.Control}.");
            if (double.IsNaN(loc.Proximity) || loc.Proximity is < -0.01 or > 100.01)
                anomalies.Add($"Location {state.LocationDefs[lIdx].Id} Proximity out of bounds: {loc.Proximity}.");
            ctrlSum += loc.Control;
            ctrlCount++;
        }

        var starving = 0;
        for (var s = 0; s < state.Provinces.Length; s++)
        {
            if (double.IsNaN(state.Provinces[s].FoodStorage) || state.Provinces[s].FoodStorage < -0.01)
                anomalies.Add($"Province {s} invalid food storage ({state.Provinces[s].FoodStorage}).");
            if (state.Provinces[s].IsStarving)
                starving++;
        }

        double minRatio = double.MaxValue;
        double maxRatio = double.MinValue;
        var gCount = state.Goods.Length;
        for (var m = 0; m < state.Markets.Length; m++)
        {
            for (var g = 0; g < gCount; g++)
            {
                var price = state.MarketPrices[m * gCount + g];
                var baseP = state.Goods[g].BasePriceGold;
                if (double.IsNaN(price) || double.IsInfinity(price) || price <= 0)
                {
                    anomalies.Add($"Market {m} good {state.Goods[g].Id} invalid price {price}.");
                    continue;
                }
                var r = price / baseP;
                if (r < minRatio) minRatio = r;
                if (r > maxRatio) maxRatio = r;
                if (r < MarketPricing.MinMultiplier - 0.001 || r > MarketPricing.MaxMultiplier + 0.001)
                    anomalies.Add($"Market {m} good {state.Goods[g].Id} ratio {r:F3} out of bounds.");
            }
        }
        if (minRatio == double.MaxValue) { minRatio = 1.0; maxRatio = 1.0; }

        return new SimulationIntegrityReport(
            state.Clock.CurrentDate,
            state.Clock.TotalTicksExecuted,
            state.Nations.Length,
            state.Locations.Length,
            state.Markets.Length,
            activePops,
            totalPop,
            totalTreasury,
            minRatio,
            maxRatio,
            ctrlCount > 0 ? ctrlSum / ctrlCount : 0.0,
            starving,
            anomalies.Count == 0,
            anomalies);
    }
}
