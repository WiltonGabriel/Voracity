using Voracity.Core.Data;
using Voracity.Core.Engine.Systems;
using Voracity.Core.Models;

namespace Voracity.Core.Engine;

public static class SaveLoadMapper
{
    public const int CurrentSchemaVersion = 2;

    public static CampaignSaveData Export(SimulationState state, string saveName)
    {
        var nCount = state.NationsRuntime.Length;
        var treasury = new long[nCount];
        var taxRates = new double[nCount];
        var courtSpend = new double[nCount];
        var govPower = new double[nCount];
        for (var n = 0; n < nCount; n++)
        {
            treasury[n] = state.NationsRuntime[n].TreasuryCoppers;
            taxRates[n] = state.NationsRuntime[n].TaxRate;
            courtSpend[n] = state.NationsRuntime[n].CostOfCourtPercent;
            govPower[n] = state.NationsRuntime[n].GovernmentPower;
        }

        var locs = new SavedLocationEntry[state.Locations.Length];
        for (var i = 0; i < state.Locations.Length; i++)
        {
            var rt = state.Locations[i];
            locs[i] = new SavedLocationEntry(
                state.LocationDefs[i].Id,
                rt.OwnerNationIndex,
                rt.Control,
                rt.RgoLevel,
                rt.Rank,
                rt.RoadLevel,
                rt.Development,
                rt.Prosperity);
        }

        var provFoods = new SavedProvinceFoodEntry[state.Provinces.Length];
        for (var s = 0; s < state.Provinces.Length; s++)
        {
            provFoods[s] = new SavedProvinceFoodEntry(
                state.States[s].Id,
                state.Provinces[s].FoodStorage,
                state.Provinces[s].IsStarving);
        }

        var gCount = state.Goods.Length;
        var mkts = new SavedMarketEntry[state.Markets.Length];
        for (var m = 0; m < state.Markets.Length; m++)
        {
            var prices = new double[gCount];
            Array.Copy(state.MarketPrices, m * gCount, prices, 0, gCount);
            mkts[m] = new SavedMarketEntry(state.MarketDefs[m].Id, prices);
        }

        var buildings = new SavedBuildingEntry[state.BuildingSlots.Length];
        for (var i = 0; i < state.BuildingSlots.Length; i++)
        {
            var b = state.BuildingSlots[i];
            buildings[i] = new SavedBuildingEntry(b.LocationIndex, b.BuildingIndex, b.Levels, b.ActivePmIndex);
        }

        var pops = new SavedPopEntry[state.Pops.Count];
        for (var i = 0; i < state.Pops.Count; i++)
        {
            var p = state.Pops[i];
            pops[i] = new SavedPopEntry(
                p.LocationIndex,
                p.NationIndex,
                p.Class,
                p.Ancestry.PrimaryIndex,
                p.Ancestry.SecondaryIndex,
                p.Population,
                p.Literacy,
                p.Satisfaction,
                p.Active);
        }

        return new CampaignSaveData(
            CurrentSchemaVersion,
            saveName,
            DateTimeOffset.UtcNow,
            state.Clock.CurrentDate,
            state.PlayerNationId,
            state.Clock.Speed,
            state.Rng.State,
            state.Clock.TotalTicksExecuted,
            treasury,
            taxRates,
            courtSpend,
            govPower,
            locs,
            provFoods,
            mkts,
            buildings,
            pops);
    }

    public static void RestoreInto(SimulationState state, CampaignSaveData save)
    {
        state.Clock.CurrentDate = save.Date;
        state.Clock.Speed = save.Speed;
        state.Clock.TotalTicksExecuted = save.TotalTicksExecuted;
        state.Clock.IsPaused = true;
        state.Rng.RestoreState(save.RngState);

        var nLimit = Math.Min(state.NationsRuntime.Length, save.NationTreasuryCoppers.Length);
        for (var n = 0; n < nLimit; n++)
        {
            state.NationsRuntime[n].TreasuryCoppers = save.NationTreasuryCoppers[n];
            state.NationsRuntime[n].TaxRate = save.NationTaxRates[n];
            state.NationsRuntime[n].CostOfCourtPercent = save.NationCourtSpendingPercent[n];
            state.NationsRuntime[n].GovernmentPower = save.NationGovernmentPower[n];
            state.NationsRuntime[n].ProximityDirty = true;
        }

        foreach (var sl in save.Locations)
        {
            if (!state.LocationIndexById.TryGetValue(sl.LocationId, out var lIdx))
                continue;
            ref var loc = ref state.Locations[lIdx];
            loc.OwnerNationIndex = sl.OwnerNationIndex;
            loc.Control = sl.Control;
            loc.RgoLevel = sl.RgoLevel;
            loc.Rank = sl.Rank;
            loc.RoadLevel = sl.RoadLevel;
            loc.Development = sl.Development;
            loc.Prosperity = sl.Prosperity;
        }

        foreach (var pf in save.ProvinceFoods)
        {
            if (!state.StateIndexById.TryGetValue(pf.ProvinceId, out var sIdx))
                continue;
            state.Provinces[sIdx].FoodStorage = pf.FoodStorage;
            state.Provinces[sIdx].IsStarving = pf.IsStarving;
        }

        var gCount = state.Goods.Length;
        foreach (var sm in save.Markets)
        {
            if (!state.MarketIndexById.TryGetValue(sm.MarketId, out var mIdx))
                continue;
            var len = Math.Min(gCount, sm.GoodPrices.Length);
            Array.Copy(sm.GoodPrices, 0, state.MarketPrices, mIdx * gCount, len);
        }

        for (var i = 0; i < Math.Min(save.Buildings.Length, state.BuildingSlots.Length); i++)
        {
            var b = save.Buildings[i];
            state.BuildingSlots[i] = new LocationBuildingSlot
            {
                LocationIndex = b.LocationIndex,
                BuildingIndex = b.BuildingIndex,
                Levels = b.Levels,
                ActivePmIndex = b.ActivePmIndex
            };
        }

        state.Pops.Clear();
        foreach (var p in save.Pops)
        {
            state.Pops.Add(new PopRecord
            {
                LocationIndex = p.LocationIndex,
                StateIndex = state.Locations[Math.Clamp(p.LocationIndex, 0, state.Locations.Length - 1)].StateIndex,
                NationIndex = p.NationIndex,
                Class = p.Class,
                Ancestry = new DualAncestry(p.PrimaryAncestry, p.SecondaryAncestry),
                Population = p.Population,
                Literacy = p.Literacy,
                Satisfaction = p.Satisfaction,
                Active = p.Active
            });
        }
    }
}
