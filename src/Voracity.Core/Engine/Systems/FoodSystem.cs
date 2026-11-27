using System.Runtime.InteropServices;
using Voracity.Core.Models;

namespace Voracity.Core.Engine.Systems;

/// <summary>
/// Implements EU5 3-Layer Food System (Economy.md &amp; Location.md):
/// Layer 1: Location Food (Food RGOs + Subsistence unemployed Peasants/Slaves - Class Food Consumption)
/// Layer 2: Provincial Food Storage (surplus stored, deficit drawn)
/// Layer 3: Regional Market Food (excess sold, shortfall purchased; remaining deficit sets IsStarving = true)
/// </summary>
public sealed class FoodSystem : ITickSystem
{
    private long[] _subsistencePops = Array.Empty<long>();

    public void Execute(SimulationState state, TickPhase phase)
    {
        if (phase != TickPhase.Initialize && phase != TickPhase.RestoreDerived && phase != TickPhase.Monthly)
            return;

        if (_subsistencePops.Length < state.Locations.Length)
            _subsistencePops = new long[state.Locations.Length];

        Array.Clear(_subsistencePops);
        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = state.HabitableLocationOrderByAccess[i];
            state.Locations[lIdx].LocalFoodProduced = 0.0;
            state.Locations[lIdx].LocalFoodConsumed = 0.0;
        }

        // Layer 1A: Sum food consumption by EU5 SocialClass + collect subsistence peasants/slaves
        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0 || (uint)p.LocationIndex >= (uint)state.Locations.Length)
                continue;

            var traits = DualAncestry.ComputeEffectiveTraits(p.Ancestry, state.Ancestries);
            var demandPerK = SocialClassRules.FoodDemandPer1KPops(p.Class);
            state.Locations[p.LocationIndex].LocalFoodConsumed += (p.Population / 1000.0) * demandPerK * traits.FoodCalorieMultiplier;

            if (p.Class == SocialClass.Peasants || p.Class == SocialClass.Slaves)
                _subsistencePops[p.LocationIndex] += p.Population;
        }

        for (var s = 0; s < state.Provinces.Length; s++)
        {
            state.Provinces[s].NetMonthlyFood = 0.0;
            state.Provinces[s].IsStarving = false;
        }

        // Layer 1B: Food produced at each location (Food RGOs + Subsistence Farming)
        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = state.HabitableLocationOrderByAccess[i];
            ref var loc = ref state.Locations[lIdx];

            double rgoFood = 0.0;
            if (loc.RawGoodIndex >= 0 && loc.RawGoodIndex < state.Goods.Length)
            {
                var foodVal = state.Goods[loc.RawGoodIndex].FoodValue;
                if (foodVal > 0.0 && loc.RgoLevel > 0)
                    rgoFood = loc.RgoLevel * loc.RgoEmploymentRatio * foodVal * 35.0;
            }

            // Subsistence agriculture feeds local rural workers closely (~1.15 per 1k peasants/slaves before modifiers)
            var subsistenceFood = (_subsistencePops[lIdx] / 1000.0) * 1.15;
            var mod = ComputeLocalFoodProductionModifier(in loc);
            loc.LocalFoodProduced = Math.Max(0.0, (rgoFood + subsistenceFood) * mod);

            var net = loc.LocalFoodProduced - loc.LocalFoodConsumed;
            if (loc.ProvinceFoodIndex >= 0 && loc.ProvinceFoodIndex < state.Provinces.Length)
                state.Provinces[loc.ProvinceFoodIndex].NetMonthlyFood += net;
        }

        if (phase == TickPhase.Initialize || phase == TickPhase.RestoreDerived)
        {
            if (phase == TickPhase.RestoreDerived)
                return;

            // Size MaxFoodStorage to ~6 months of province consumption + location rank capacities
            var provDemand = new double[state.Provinces.Length];
            for (var i = 0; i < state.HabitableCount; i++)
            {
                var loc = state.Locations[state.HabitableLocationOrderByAccess[i]];
                if (loc.ProvinceFoodIndex >= 0 && loc.ProvinceFoodIndex < provDemand.Length)
                    provDemand[loc.ProvinceFoodIndex] += loc.LocalFoodConsumed;
            }
            for (var s = 0; s < state.Provinces.Length; s++)
            {
                state.Provinces[s].MaxFoodStorage = Math.Max(state.Provinces[s].MaxFoodStorage, provDemand[s] * 6.0);
                state.Provinces[s].FoodStorage = state.Provinces[s].MaxFoodStorage * 0.60;
            }
            return;
        }

        // Layer 2 & 3: Provincial Storage & Market Food settlement
        var grainIdx = state.GrainGoodIndex;
        var gCount = state.Goods.Length;
        for (var s = 0; s < state.Provinces.Length; s++)
        {
            ref var prov = ref state.Provinces[s];
            var ownerIdx = state.NationIndexById.GetValueOrDefault(state.States[prov.StateIndex].OwnerNationId, 0);
            var mIdx = Math.Clamp(prov.PrimaryMarketIndex, 0, state.Markets.Length - 1);
            var foodPrice = grainIdx >= 0 ? state.MarketPrices[mIdx * gCount + grainIdx] : 20.0;

            prov.FoodStorage += prov.NetMonthlyFood;

            if (prov.FoodStorage > prov.MaxFoodStorage)
            {
                var surplus = prov.FoodStorage - prov.MaxFoodStorage;
                prov.FoodStorage = prov.MaxFoodStorage;
                var tradeUnits = surplus / 200.0;
                if (grainIdx >= 0)
                    state.MarketSupply[mIdx * gCount + grainIdx] += tradeUnits;
                var saleGold = tradeUnits * foodPrice * 0.25;
                state.NationsRuntime[ownerIdx].TreasuryCoppers += MonetaryAmount.FromGold(saleGold).CopperUnits;
            }
            else if (prov.FoodStorage < 0.0)
            {
                var deficit = -prov.FoodStorage;
                var tradeUnits = deficit / 200.0;
                var availableSupply = grainIdx >= 0 ? state.MarketSupply[mIdx * gCount + grainIdx] : 100.0;
                var buyCost = MonetaryAmount.FromGold(tradeUnits * foodPrice * 0.35).CopperUnits;

                if (availableSupply >= tradeUnits * 0.5 && state.NationsRuntime[ownerIdx].TreasuryCoppers >= buyCost)
                {
                    state.NationsRuntime[ownerIdx].TreasuryCoppers -= buyCost;
                    if (grainIdx >= 0)
                        state.MarketDemand[mIdx * gCount + grainIdx] += tradeUnits;
                    prov.FoodStorage = 0.0;
                    prov.IsStarving = false;
                }
                else
                {
                    prov.FoodStorage = 0.0;
                    prov.IsStarving = true;
                }
            }
        }
    }

    public static double ComputeLocalFoodProductionModifier(in LocationRuntime loc)
    {
        var mod = 1.0;
        mod += loc.Rank switch
        {
            LocationRank.RuralSettlement => 0.10,
            LocationRank.Megalopolis => -0.67,
            _ => 0.0
        };
        mod += loc.Topography switch
        {
            TopographyType.Hills => -0.10,
            TopographyType.Mountains => -0.20,
            TopographyType.Wetlands => -0.10,
            _ => 0.0
        };
        mod += Math.Clamp(loc.Development / 100.0, 0.0, 1.0);
        return Math.Max(0.15, mod);
    }
}
