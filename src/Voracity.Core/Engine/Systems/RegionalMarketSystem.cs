using System.Runtime.InteropServices;
using Voracity.Core.Models;

namespace Voracity.Core.Engine.Systems;

/// <summary>
/// Implements EU5 Regional Markets, MarketAccess scaling &amp; buy priority, R.G.O. output &amp; profits,
/// and Location Buildings / Production Methods (zero Vic3 Investment Pool / Economies of Scale).
/// </summary>
public sealed class RegionalMarketSystem : ITickSystem
{
    private long[] _locationLaborers = Array.Empty<long>();
    private double[] _goodSupplyFactor = Array.Empty<double>();
    private float[] _avgDevWeight = Array.Empty<float>();

    public void Execute(SimulationState state, TickPhase phase)
    {
        if (phase != TickPhase.Initialize && phase != TickPhase.RestoreDerived && phase != TickPhase.Weekly)
            return;

        EnsureScratch(state.Locations.Length, state.Markets.Length, state.Goods.Length);

        if (phase == TickPhase.Initialize || phase == TickPhase.RestoreDerived)
            AssignLocationsToMarketsAndAccess(state);

        SortHabitableLocationsByAccessDescending(state);
        CountLocationLaborersAndUrbanDemand(state);

        Array.Clear(state.MarketSupply);
        Array.Clear(state.MarketDemand);

        // 1. Evaluate R.G.O. per habitable location + accumulate pop demand
        var habOrder = state.HabitableLocationOrderByAccess;
        var gCount = state.Goods.Length;
        var bCount = Math.Max(1, state.Buildings.Length);

        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = habOrder[i];
            ref var loc = ref state.Locations[lIdx];
            loc.MaxRgoLevel = ComputeMaxRgoSize(in loc, loc.TotalPopulation);
            if (loc.RgoLevel > loc.MaxRgoLevel && phase != TickPhase.Initialize && phase != TickPhase.RestoreDerived)
                loc.RgoLevel = Math.Max(1, loc.MaxRgoLevel);

            var workersNeeded = Math.Max(1.0, loc.RgoLevel * 1000.0);
            loc.RgoEmploymentRatio = Math.Clamp(_locationLaborers[lIdx] / workersNeeded, 0.0, 1.0);

            if (loc.RawGoodIndex >= 0 && loc.RawGoodIndex < gCount && loc.RgoLevel > 0)
            {
                var rgoOut = loc.RgoLevel * loc.RgoEmploymentRatio;
                var mIdx = Math.Clamp(loc.MarketIndex, 0, state.Markets.Length - 1);
                var price = state.MarketPrices[mIdx * gCount + loc.RawGoodIndex];
                state.MarketSupply[mIdx * gCount + loc.RawGoodIndex] += rgoOut;
                loc.RgoProfit = rgoOut * price * loc.MarketAccess;
            }
            else
            {
                loc.RgoProfit = 0.0;
            }

            // Aggregate building PM inputs as demand first
            for (var b = 0; b < state.Buildings.Length; b++)
            {
                var slot = state.BuildingSlots[lIdx * bCount + b];
                if (slot.Levels <= 0)
                    continue;
                var pms = state.Buildings[b].ProductionMethods;
                var pm = pms[Math.Clamp(slot.ActivePmIndex, 0, pms.Length - 1)];
                var mIdx = Math.Clamp(loc.MarketIndex, 0, state.Markets.Length - 1);
                foreach (var (goodId, amt) in pm.Inputs)
                {
                    if (state.GoodIndexById.TryGetValue(goodId, out var gIdx))
                        state.MarketDemand[mIdx * gCount + gIdx] += slot.Levels * amt * loc.MarketAccess;
                }
            }

            // Data-driven nation special goods output (e.g., ritual Blood Essence)
            if (loc.OwnerNationIndex >= 0 &&
                state.BloodEssenceGoodIndex >= 0 &&
                state.Nations[loc.OwnerNationIndex].ProducesBloodEssence &&
                loc.IsCapital)
            {
                var mIdx = Math.Clamp(loc.MarketIndex, 0, state.Markets.Length - 1);
                state.MarketSupply[mIdx * gCount + state.BloodEssenceGoodIndex] += 18.0;
            }
        }

        AccumulatePopDemands(state);

        // Compute supply fulfillment ratio per (Market, Good) before resolving building outputs in buy-priority order
        for (var idx = 0; idx < _goodSupplyFactor.Length; idx++)
        {
            var dem = state.MarketDemand[idx];
            var sup = state.MarketSupply[idx];
            _goodSupplyFactor[idx] = dem <= 1e-6 ? 1.0 : Math.Clamp(sup / dem, 0.25, 1.0);
        }

        // Track remaining available input units per (Market, Good) so high-MarketAccess locations buy scarce inputs first
        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = habOrder[i]; // sorted descending by MarketAccess!
            ref var loc = ref state.Locations[lIdx];
            var mIdx = Math.Clamp(loc.MarketIndex, 0, state.Markets.Length - 1);

            double totalBuildingProfit = 0.0;
            for (var b = 0; b < state.Buildings.Length; b++)
            {
                var slot = state.BuildingSlots[lIdx * bCount + b];
                if (slot.Levels <= 0)
                    continue;

                var pms = state.Buildings[b].ProductionMethods;
                var pm = pms[Math.Clamp(slot.ActivePmIndex, 0, pms.Length - 1)];

                var inputAvailability = 1.0;
                double inputCost = 0.0;
                foreach (var (goodId, amt) in pm.Inputs)
                {
                    if (!state.GoodIndexById.TryGetValue(goodId, out var gIdx))
                        continue;
                    var needed = slot.Levels * amt * loc.MarketAccess;
                    var availFactor = _goodSupplyFactor[mIdx * gCount + gIdx];
                    // High MarketAccess priority mitigates shortage up to 1.0 before low-access locations suffer
                    var effectiveAvail = Math.Clamp(availFactor + (loc.MarketAccess - 0.5) * 0.25, 0.20, 1.0);
                    if (effectiveAvail < inputAvailability)
                        inputAvailability = effectiveAvail;
                    inputCost += needed * effectiveAvail * state.MarketPrices[mIdx * gCount + gIdx];
                }

                double outputRevenue = 0.0;
                foreach (var (goodId, amt) in pm.Outputs)
                {
                    if (!state.GoodIndexById.TryGetValue(goodId, out var gIdx))
                        continue;
                    var produced = slot.Levels * amt * loc.MarketAccess * inputAvailability;
                    state.MarketSupply[mIdx * gCount + gIdx] += produced;
                    outputRevenue += produced * state.MarketPrices[mIdx * gCount + gIdx];
                }

                var profit = (outputRevenue - inputCost) * loc.MarketAccess;
                if (profit > 0.0)
                    totalBuildingProfit += profit;
            }

            loc.BuildingProfit = totalBuildingProfit;
        }

        // 3. Smooth price updates per regional market (never mutate saved MarketPrices on RestoreDerived)
        if (phase == TickPhase.RestoreDerived)
            return;

        for (var m = 0; m < state.Markets.Length; m++)
        {
            for (var g = 0; g < gCount; g++)
            {
                var idx = m * gCount + g;
                var target = MarketPricing.ComputePrice(
                    state.Goods[g].BasePriceGold,
                    state.MarketDemand[idx],
                    state.MarketSupply[idx]);
                var current = state.MarketPrices[idx];
                state.MarketPrices[idx] = current + (target - current) * 0.35;
            }
        }
    }

    public static void AssignLocationsToMarketsAndAccess(SimulationState state)
    {
        for (var m = 0; m < state.Markets.Length; m++)
        {
            state.Markets[m].LocationCount = 0;
            state.Markets[m].AccessSum = 0.0;
        }

        for (var i = 0; i < state.Locations.Length; i++)
        {
            ref var loc = ref state.Locations[i];
            if (loc.Kind != LocationKind.Habitable)
                continue;

            var def = state.LocationDefs[i];
            var bestMarketIdx = loc.MarketIndex;
            var bestScore = double.NegativeInfinity;

            for (var m = 0; m < state.Markets.Length; m++)
            {
                var cIdx = state.Markets[m].CenterLocationIndex;
                if (cIdx < 0)
                    continue;
                var cDef = state.LocationDefs[cIdx];
                var dx = (def.SeedX - cDef.SeedX) * 1.714;
                var dy = def.SeedY - cDef.SeedY;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var score = state.Markets[m].BaseAttraction - dist * 3.0;
                if (string.Equals(state.MarketDefs[m].Id, def.MarketId, StringComparison.Ordinal))
                    score += 0.35;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMarketIdx = m;
                }
            }

            bestMarketIdx = Math.Clamp(bestMarketIdx, 0, state.Markets.Length - 1);
            loc.MarketIndex = bestMarketIdx;

            var centerIdx = state.Markets[bestMarketIdx].CenterLocationIndex;
            double access = 1.0;
            if (centerIdx >= 0 && centerIdx != i)
            {
                var centerDef = state.LocationDefs[centerIdx];
                var dx = (def.SeedX - centerDef.SeedX) * 1.714;
                var dy = def.SeedY - centerDef.SeedY;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var roadBonus = (int)loc.RoadLevel * 0.04 + (loc.IsCoastal ? 0.06 : 0.0);
                access = Math.Clamp(1.0 - dist * 1.35 + roadBonus, 0.25, 1.0);
            }

            loc.MarketAccess = access;
            state.Markets[bestMarketIdx].LocationCount++;
            state.Markets[bestMarketIdx].AccessSum += access;
        }
    }

    public static int ComputeMaxRgoSize(in LocationRuntime loc, long totalPop)
    {
        var baseMax = 2.0 + 0.25 * (totalPop / 100_000.0) + 0.1 * loc.Development;
        var mult = 1.0;
        if (loc.Rank == LocationRank.RuralSettlement) mult += 1.0;
        if (loc.Vegetation == VegetationType.Farmland) mult += 0.10;
        if (loc.Rank == LocationRank.Megalopolis) mult -= 0.50;
        return Math.Clamp((int)Math.Floor(baseMax * Math.Max(0.25, mult)), 1, 25);
    }

    private void EnsureScratch(int locationCount, int marketCount, int goodCount)
    {
        if (_locationLaborers.Length < locationCount)
        {
            _locationLaborers = new long[locationCount];
            _avgDevWeight = new float[locationCount];
        }
        var total = Math.Max(1, marketCount * goodCount);
        if (_goodSupplyFactor.Length != total)
            _goodSupplyFactor = new double[total];
    }

    private void SortHabitableLocationsByAccessDescending(SimulationState state)
    {
        Array.Sort(state.HabitableLocationOrderByAccess, 0, state.HabitableCount, Comparer<int>.Create((a, b) =>
            state.Locations[b].MarketAccess.CompareTo(state.Locations[a].MarketAccess)));
    }

    private void CountLocationLaborersAndUrbanDemand(SimulationState state)
    {
        Array.Clear(_locationLaborers);
        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = state.HabitableLocationOrderByAccess[i];
            state.Locations[lIdx].TotalPopulation = 0;
        }

        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0 || (uint)p.LocationIndex >= (uint)state.Locations.Length)
                continue;

            state.Locations[p.LocationIndex].TotalPopulation += p.Population;
            if (p.Class == SocialClass.Laborers || p.Class == SocialClass.Slaves)
                _locationLaborers[p.LocationIndex] += p.Population;
        }
    }

    private static void AccumulatePopDemands(SimulationState state)
    {
        var gCount = state.Goods.Length;
        var mCount = state.Markets.Length;
        var woodIdx = state.LumberGoodIndex;
        var grainIdx = state.GrainGoodIndex;
        var bloodIdx = state.BloodEssenceGoodIndex;

        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0 || (uint)p.LocationIndex >= (uint)state.Locations.Length)
                continue;

            var loc = state.Locations[p.LocationIndex];
            var mIdx = Math.Clamp(loc.MarketIndex, 0, mCount - 1);
            var traits = DualAncestry.ComputeEffectiveTraits(p.Ancestry, state.Ancestries);
            var units = p.Population / 100_000.0 * (1.0 + loc.Development * 0.01);

            if (traits.RequiresBloodEssence && bloodIdx >= 0)
                state.MarketDemand[mIdx * gCount + bloodIdx] += units * 0.85;
            else if (grainIdx >= 0)
                state.MarketDemand[mIdx * gCount + grainIdx] += units * traits.FoodCalorieMultiplier;

            if (woodIdx >= 0)
                state.MarketDemand[mIdx * gCount + woodIdx] += units * 0.30;
        }
    }
}
