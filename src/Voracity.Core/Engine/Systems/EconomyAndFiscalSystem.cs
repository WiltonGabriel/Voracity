using System.Runtime.InteropServices;
using Voracity.Core.Models;

namespace Voracity.Core.Engine.Systems;

/// <summary>
/// Implements EU5 Tax Base &amp; Expected Expenses (Economy.md):
/// EffectiveTaxBase = PotentialTaxBase * (Control / 100.0), distributed across Estate Tax Base Shares
/// (Nobles 100, Clergy 25, Burghers 40, Commoners 1, Tribes 0.01) + Peasant Enfranchisement + Cost of the Court.
/// </summary>
public sealed class EconomyAndFiscalSystem : ITickSystem
{
    public void Execute(SimulationState state, TickPhase phase)
    {
        if (phase != TickPhase.Initialize && phase != TickPhase.RestoreDerived && phase != TickPhase.Monthly)
            return;

        Array.Clear(state.EstateWeightedShares);
        Array.Clear(state.EstateTaxYieldGold);
        Array.Clear(state.NationMonthlyTaxBase);
        Array.Clear(state.NationMonthlyIncomeGold);
        Array.Clear(state.NationMonthlyUpkeepGold);

        var enfranchisement = state.EstateHook.PeasantEnfranchisement;
        var bCount = Math.Max(1, state.Buildings.Length);

        // Step 1: Compute per-location PotentialTaxBase & EffectiveTaxBase + Estate shares per nation
        var locWeights = new double[state.Locations.Length * 6];
        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0 || (uint)p.LocationIndex >= (uint)state.Locations.Length)
                continue;

            var estate = SocialClassRules.ToEstate(p.Class);
            var weight = p.Population * state.EstateHook.GetWeight(estate);
            locWeights[p.LocationIndex * 6 + (int)estate] += weight;
        }

        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = state.HabitableLocationOrderByAccess[i];
            ref var loc = ref state.Locations[lIdx];

            loc.PotentialTaxBase = Math.Max(0.0, loc.RgoProfit + loc.BuildingProfit);
            loc.EffectiveTaxBase = loc.PotentialTaxBase * (Math.Clamp(loc.Control, 0.0, 100.0) / 100.0);

            if (loc.OwnerNationIndex < 0)
                continue;

            var nIdx = loc.OwnerNationIndex;
            state.NationMonthlyTaxBase[nIdx] += loc.EffectiveTaxBase;

            // Apply EU5 Peasant Enfranchisement redistribution: Commoners *(1-enf) -> Nobles
            var commonersW = locWeights[lIdx * 6 + (int)EstateKind.Commoners];
            var redistributedToNobles = commonersW * (1.0 - enfranchisement);
            locWeights[lIdx * 6 + (int)EstateKind.Commoners] = commonersW * enfranchisement;
            locWeights[lIdx * 6 + (int)EstateKind.Nobles] += redistributedToNobles;

            var totalW = 0.0;
            for (var e = 1; e <= 5; e++)
                totalW += locWeights[lIdx * 6 + e];

            if (totalW > 1e-6)
            {
                for (var e = 1; e <= 5; e++)
                {
                    var share = loc.EffectiveTaxBase * (locWeights[lIdx * 6 + e] / totalW);
                    state.EstateWeightedShares[nIdx * 6 + e] += share;
                }
            }
            else
            {
                state.EstateWeightedShares[nIdx * 6 + (int)EstateKind.Commoners] += loc.EffectiveTaxBase;
            }

            // Building maintenance
            for (var b = 0; b < state.Buildings.Length; b++)
            {
                var slot = state.BuildingSlots[lIdx * bCount + b];
                if (slot.Levels > 0)
                    state.NationMonthlyUpkeepGold[nIdx] += slot.Levels * state.Buildings[b].BaseUpkeepGold;
            }
        }

        // Step 2: Calculate Estate Tax income & Expected Expense (Cost of the Court)
        for (var n = 0; n < state.NationsRuntime.Length; n++)
        {
            ref var nat = ref state.NationsRuntime[n];
            double taxIncomeGold = 0.0;
            for (var e = 1; e <= 5; e++)
            {
                var estateTaxBase = state.EstateWeightedShares[n * 6 + e];
                var estateYield = estateTaxBase * nat.TaxRate;
                state.EstateTaxYieldGold[n * 6 + e] = estateYield;
                taxIncomeGold += estateYield;
            }

            // EU5 Economy.md: Cost of the Court = up to 10% of country's tax base at 100% slider, granting +0.02 GovPower per 1%
            var courtExpenseGold = state.NationMonthlyTaxBase[n] * 0.10 * (nat.CostOfCourtPercent / 100.0);
            var netMonthlyGold = taxIncomeGold - state.NationMonthlyUpkeepGold[n] - courtExpenseGold;

            nat.MonthlyBalanceCoppers = MonetaryAmount.FromGold(netMonthlyGold).CopperUnits;
            if (phase == TickPhase.Monthly)
            {
                nat.TreasuryCoppers += nat.MonthlyBalanceCoppers;
                nat.GovernmentPower = Math.Clamp(nat.GovernmentPower - 0.5 + 0.02 * nat.CostOfCourtPercent, 0.0, 100.0);
            }
        }
    }
}
