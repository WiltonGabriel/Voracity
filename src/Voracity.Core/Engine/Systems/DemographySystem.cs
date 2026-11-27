using System.Runtime.InteropServices;
using Voracity.Core.Models;

namespace Voracity.Core.Engine.Systems;

/// <summary>
/// Implements EU5 Demographics (Population.md) + Voracity Dual Ancestry (50%/50%) + Micro-Pop Consolidation:
/// - 8 EU5 Social Classes
/// - PopulationCapacity cap
/// - EU5 growth curve (+0.002 Rural, Free Land, Starvation -0.025, Large Food Storage) + DualAncestry modifier
/// - Monthly Promotion/Demotion toward RGO &amp; Urban job openings
/// </summary>
public sealed class DemographySystem : ITickSystem
{
    public const int MicroPopConsolidationThreshold = 50;

    private long[] _locationTotals = Array.Empty<long>();

    public void Execute(SimulationState state, TickPhase phase)
    {
        if (phase != TickPhase.Initialize && phase != TickPhase.RestoreDerived && phase != TickPhase.Monthly)
            return;

        if (_locationTotals.Length < state.Locations.Length)
            _locationTotals = new long[state.Locations.Length];

        Array.Clear(_locationTotals);
        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (p.Active && p.Population > 0 && (uint)p.LocationIndex < (uint)_locationTotals.Length)
                _locationTotals[p.LocationIndex] += p.Population;
        }

        // Update PopulationCapacity every month/init
        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = state.HabitableLocationOrderByAccess[i];
            ref var loc = ref state.Locations[lIdx];
            loc.TotalPopulation = _locationTotals[lIdx];
            loc.PopulationCapacity = ComputePopulationCapacity(state.LocationDefs[lIdx].BasePopulationCapacity, in loc);
        }

        if (phase == TickPhase.Initialize || phase == TickPhase.RestoreDerived)
            return;

        var rngJitter = (state.Rng.NextDouble() - 0.5) * 0.0001;
        var gCount = state.Goods.Length;
        var grainIdx = state.GrainGoodIndex;

        for (var i = 0; i < popSpan.Length; i++)
        {
            ref var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0 || (uint)p.LocationIndex >= (uint)state.Locations.Length)
                continue;

            var loc = state.Locations[p.LocationIndex];
            var capRatio = loc.PopulationCapacity > 0
                ? (double)loc.TotalPopulation / loc.PopulationCapacity
                : 1.0;

            var mIdx = Math.Clamp(loc.MarketIndex, 0, state.Markets.Length - 1);
            var foodPriceRatio = grainIdx >= 0 && state.Goods[grainIdx].BasePriceGold > 0
                ? state.MarketPrices[mIdx * gCount + grainIdx] / state.Goods[grainIdx].BasePriceGold
                : 1.0;

            var isStarving = loc.ProvinceFoodIndex >= 0 && state.Provinces[loc.ProvinceFoodIndex].IsStarving;
            var monthsStored = loc.ProvinceFoodIndex >= 0 && loc.LocalFoodConsumed > 1e-3
                ? Math.Clamp(state.Provinces[loc.ProvinceFoodIndex].FoodStorage / loc.LocalFoodConsumed, 0.0, 120.0)
                : 0.0;

            var traits = DualAncestry.ComputeEffectiveTraits(p.Ancestry, state.Ancestries);
            var rate = ComputeMonthlyGrowthRate(
                in loc,
                capRatio,
                foodPriceRatio,
                monthsStored,
                isStarving,
                traits.MonthlyGrowthModifier) + rngJitter;

            // Stop positive growth above PopulationCapacity per Population.md
            if (capRatio >= 1.0 && rate > 0.0)
                rate = 0.0;

            var delta = (long)Math.Round(p.Population * rate, MidpointRounding.AwayFromZero);
            p.Population = Math.Max(1, p.Population + delta);
        }

        PromoteAndDemoteTowardJobs(state);
        ConsolidateMicroPops(state);
    }

    public static long ComputePopulationCapacity(double baseCapacityUnits, in LocationRuntime loc)
    {
        var cap = baseCapacityUnits * 25_000.0;
        cap += loc.Rank switch
        {
            LocationRank.Town => 250_000.0,
            LocationRank.City => 900_000.0,
            LocationRank.Megalopolis => 3_500_000.0,
            _ => 0.0
        };
        cap += loc.Development * 12_000.0;
        if (loc.IsCoastal) cap *= 1.15;
        if (loc.IsCapital) cap *= 1.25;
        if (loc.Topography == TopographyType.Mountains) cap *= 0.50;
        return Math.Max(50_000L, (long)Math.Round(cap));
    }

    public static double ComputeMonthlyGrowthRate(
        in LocationRuntime loc,
        double capRatio,
        double foodPriceRatio,
        double monthsFoodStored,
        bool isStarving,
        double ancestryGrowthModifier)
    {
        double rate = 0.0005;
        if (loc.Rank == LocationRank.RuralSettlement)
            rate += 0.002;

        // Prosperity -0.005..+0.002
        rate += loc.Prosperity >= 0.5
            ? (loc.Prosperity - 0.5) * 2.0 * 0.002
            : (0.5 - loc.Prosperity) * 2.0 * -0.005;

        // Food price -0.001..+0.001
        rate += Math.Clamp((1.0 - foodPriceRatio) * 0.002, -0.001, 0.001);

        // Free land
        if (capRatio < 0.10 && loc.TotalPopulation < 10_000)
            rate += 0.015 * (1.0 - capRatio * 10.0);
        else if (capRatio < 1.0)
            rate += 0.0025 * (1.0 - capRatio);

        // Large food storage (+0.0008 per 12 months, up to 120 months)
        rate += (Math.Min(120.0, monthsFoodStored) / 12.0) * 0.0008;

        if (isStarving)
            rate -= 0.025;

        // Dual Ancestry genetic growth & Morte Branca pressure
        if (ancestryGrowthModifier <= -0.30)
            rate = Math.Min(rate, -0.0006);
        else
            rate *= 1.0 + ancestryGrowthModifier * 0.35;

        return rate;
    }

    private static void PromoteAndDemoteTowardJobs(SimulationState state)
    {
        // Promote up to 1% of Peasants in each location into Laborers if RGO is understaffed
        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref var p = ref popSpan[i];
            if (!p.Active || p.Population <= 500 || p.Class != SocialClass.Peasants)
                continue;
            var loc = state.Locations[p.LocationIndex];
            if (loc.RgoEmploymentRatio < 0.95)
            {
                var move = Math.Max(10L, p.Population / 200);
                p.Population -= move;
                // Find matching Laborers pop in same location
                for (var j = 0; j < popSpan.Length; j++)
                {
                    if (popSpan[j].Active &&
                        popSpan[j].LocationIndex == p.LocationIndex &&
                        popSpan[j].Class == SocialClass.Laborers &&
                        popSpan[j].Ancestry == p.Ancestry)
                    {
                        popSpan[j].Population += move;
                        break;
                    }
                }
            }
        }
    }

    public static int ConsolidateMicroPops(SimulationState state)
    {
        var consolidated = 0;
        var list = state.Pops;
        for (var i = 0; i < list.Count; i++)
        {
            var small = list[i];
            if (!small.Active || small.Population > MicroPopConsolidationThreshold)
                continue;

            var targetIdx = -1;
            for (var j = 0; j < list.Count; j++)
            {
                if (i == j || !list[j].Active)
                    continue;
                var cand = list[j];
                if (cand.LocationIndex == small.LocationIndex &&
                    cand.Class == small.Class &&
                    cand.Ancestry == small.Ancestry &&
                    cand.Population > MicroPopConsolidationThreshold)
                {
                    targetIdx = j;
                    break;
                }
            }

            if (targetIdx >= 0)
            {
                var target = list[targetIdx];
                var combined = target.Population + small.Population;
                target.Literacy = (float)((target.Literacy * target.Population + small.Literacy * small.Population) / combined);
                target.Satisfaction = (float)((target.Satisfaction * target.Population + small.Satisfaction * small.Population) / combined);
                target.Population = combined;
                list[targetIdx] = target;

                small.Active = false;
                small.Population = 0;
                list[i] = small;
                consolidated++;
            }
        }

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].Active || list[i].Population <= 0)
                list.RemoveAt(i);
        }

        return consolidated;
    }
}
