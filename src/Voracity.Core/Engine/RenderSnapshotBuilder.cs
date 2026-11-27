using System.Runtime.InteropServices;
using Voracity.Core.Models;

namespace Voracity.Core.Engine;

public static class RenderSnapshotBuilder
{
    public static RenderSnapshot Build(SimulationState state)
    {
        var playerIdx = state.NationIndexById[state.PlayerNationId];
        var playerNation = state.Nations[playerIdx];
        ref readonly var playerRuntime = ref state.NationsRuntime[playerIdx];

        var rulerPrimaryIdx = state.AncestryIndexById.GetValueOrDefault(playerNation.RulerPrimaryAncestry, 0);
        var rulerSecondaryIdx = playerNation.RulerSecondaryAncestry is not null
            ? state.AncestryIndexById.GetValueOrDefault(playerNation.RulerSecondaryAncestry, -1)
            : -1;
        var rulerAncestry = new DualAncestry(rulerPrimaryIdx, rulerSecondaryIdx).FormatName(state.Ancestries);

        var locTotals = new long[state.Locations.Length];
        var stateTotals = new long[state.States.Length];
        var statePopByPrimaryAncestry = new long[state.States.Length, state.Ancestries.Length];
        long totalWorldPop = 0;
        long playerPop = 0;
        var activePopCount = 0;

        var playerPops = new List<PopSummarySnapshot>();
        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0)
                continue;

            activePopCount++;
            totalWorldPop += p.Population;
            locTotals[p.LocationIndex] += p.Population;
            if (p.StateIndex >= 0)
            {
                stateTotals[p.StateIndex] += p.Population;
                statePopByPrimaryAncestry[p.StateIndex, p.Ancestry.PrimaryIndex] += p.Population;
            }

            if (p.NationIndex == playerIdx)
            {
                playerPop += p.Population;
                playerPops.Add(new PopSummarySnapshot(
                    state.LocationDefs[p.LocationIndex].Id,
                    state.LocationDefs[p.LocationIndex].DefaultName,
                    p.Class,
                    p.Ancestry.FormatName(state.Ancestries),
                    p.Population,
                    p.Literacy,
                    p.Satisfaction));
            }
        }

        // Player primary market prices for quick dictionary lookup + full regional markets list
        var capMarketIdx = playerRuntime.CapitalLocationIndex >= 0
            ? Math.Clamp(state.Locations[playerRuntime.CapitalLocationIndex].MarketIndex, 0, state.Markets.Length - 1)
            : 0;

        var gCount = state.Goods.Length;
        var priceMap = new Dictionary<string, double>(gCount, StringComparer.Ordinal);
        for (var g = 0; g < gCount; g++)
            priceMap[state.Goods[g].Id] = state.MarketPrices[capMarketIdx * gCount + g];

        var marketSnapshots = new List<MarketSnapshot>(state.Markets.Length);
        for (var m = 0; m < state.Markets.Length; m++)
        {
            var md = state.MarketDefs[m];
            var mr = state.Markets[m];
            var goodList = new List<MarketGoodSnapshot>(gCount);
            for (var g = 0; g < gCount; g++)
            {
                goodList.Add(new MarketGoodSnapshot(
                    state.Goods[g].Id,
                    state.MarketPrices[m * gCount + g],
                    state.Goods[g].BasePriceGold,
                    state.MarketSupply[m * gCount + g],
                    state.MarketDemand[m * gCount + g],
                    state.Goods[g].FoodValue));
            }
            marketSnapshots.Add(new MarketSnapshot(
                md.Id,
                md.NameKey,
                md.CenterLocationId,
                md.ColorHex,
                mr.LocationCount,
                mr.LocationCount > 0 ? mr.AccessSum / mr.LocationCount : 1.0,
                goodList));
        }

        var bCount = Math.Max(1, state.Buildings.Length);
        var locSnapshots = new List<ProvinceSnapshot>(state.Locations.Length);
        var stateControlSum = new double[state.States.Length];
        var stateTaxSum = new double[state.States.Length];
        var stateLocCount = new int[state.States.Length];
        var stateProvIds = new List<string>[state.States.Length];
        var stateResTags = new HashSet<string>[state.States.Length];
        for (var s = 0; s < state.States.Length; s++)
        {
            stateProvIds[s] = new List<string>(4);
            stateResTags[s] = new HashSet<string>(StringComparer.Ordinal);
        }

        double playerPotTax = 0.0;
        double playerEffTax = 0.0;
        double playerCtrlSum = 0.0;
        int playerLocCount = 0;

        for (var i = 0; i < state.Locations.Length; i++)
        {
            var def = state.LocationDefs[i];
            ref readonly var rt = ref state.Locations[i];
            var ownerNat = rt.OwnerNationIndex >= 0 ? state.Nations[rt.OwnerNationIndex] : null;
            var mDef = state.MarketDefs[Math.Clamp(rt.MarketIndex, 0, state.MarketDefs.Length - 1)];

            List<BuildingSlotSnapshot>? bList = null;
            if (rt.Kind == LocationKind.Habitable)
            {
                bList = new List<BuildingSlotSnapshot>(state.Buildings.Length);
                for (var b = 0; b < state.Buildings.Length; b++)
                {
                    var bDef = state.Buildings[b];
                    var slot = state.BuildingSlots[i * bCount + b];
                    var pm = bDef.ProductionMethods[Math.Clamp(slot.ActivePmIndex, 0, bDef.ProductionMethods.Length - 1)];
                    var pms = bDef.ProductionMethods.Select(x => (x.Id, x.NameKey)).ToArray();
                    bList.Add(new BuildingSlotSnapshot(bDef.Id, bDef.NameKey, slot.Levels, pm.Id, pm.NameKey, pms));
                }
            }

            string? dynName = null;
            string? adminTitle = null;
            if (ownerNat is not null)
            {
                adminTitle = ResolveAdminTitle(state, ownerNat, isAreaLevel: false);
                dynName = ResolveDynamicName(state, def.Id, ownerNat);
            }

            if (rt.StateIndex >= 0)
            {
                stateControlSum[rt.StateIndex] += rt.Control;
                stateTaxSum[rt.StateIndex] += rt.EffectiveTaxBase;
                stateLocCount[rt.StateIndex]++;
                stateProvIds[rt.StateIndex].Add(def.Id);
                if (!string.IsNullOrEmpty(def.RawGoodId))
                    stateResTags[rt.StateIndex].Add(def.RawGoodId);
            }

            if (rt.OwnerNationIndex == playerIdx && rt.Kind == LocationKind.Habitable)
            {
                playerPotTax += rt.PotentialTaxBase;
                playerEffTax += rt.EffectiveTaxBase;
                playerCtrlSum += rt.Control;
                playerLocCount++;
            }

            locSnapshots.Add(new ProvinceSnapshot(
                NumericalIndex: rt.PersistentUid,
                ProvinceId: def.Id,
                Name: dynName ?? def.DefaultName,
                StateId: def.AreaId,
                OwnerNationId: ownerNat?.Id ?? "NONE",
                Terrain: rt.Topography.ToString().ToLowerInvariant(),
                HubType: rt.Rank.ToString().ToLowerInvariant(),
                IsCapital: rt.IsCapital,
                IsCoastal: rt.IsCoastal,
                AnchorX: def.SeedX,
                AnchorY: def.SeedY,
                HoldingLevel: (int)rt.Rank + 1,
                FortLevel: 0,
                ResourcePotential: def.RawGoodId,
                OccupiedByNationId: null,
                LocalAdminTitle: adminTitle,
                DynamicDisplayName: dynName,
                Kind: rt.Kind,
                Rank: rt.Rank,
                Topography: rt.Topography,
                Vegetation: rt.Vegetation,
                Climate: rt.Climate,
                RgoMethod: rt.RgoMethod,
                NaturalHarborSuitability: rt.NaturalHarborSuitability,
                Development: rt.Development,
                RoadLevel: rt.RoadLevel,
                IntegrationStatus: rt.IntegrationStatus,
                Proximity: rt.Proximity,
                MaxControl: rt.MaxControl,
                Control: rt.Control,
                MarketId: rt.Kind == LocationKind.Habitable ? mDef.Id : null,
                MarketAccess: rt.MarketAccess,
                RgoLevel: rt.RgoLevel,
                MaxRgoLevel: rt.MaxRgoLevel,
                RgoEmploymentRatio: rt.RgoEmploymentRatio,
                PotentialTaxBase: rt.PotentialTaxBase,
                EffectiveTaxBase: rt.EffectiveTaxBase,
                LocalFoodProduced: rt.LocalFoodProduced,
                LocalFoodConsumed: rt.LocalFoodConsumed,
                Population: locTotals[i],
                PopulationCapacity: rt.PopulationCapacity,
                Prosperity: rt.Prosperity,
                SeaZoneMaritimePresence: rt.SeaZoneMaritimePresence,
                Buildings: bList,
                IsMarketCenter: rt.IsMarketCenter));
        }

        var stateSnapshots = new List<StateSnapshot>(state.States.Length);
        var provFoodSnapshots = new List<ProvinceFoodSnapshot>(state.Provinces.Length);
        double totalPlayerFoodBalance = 0.0;
        var playerStarvingCount = 0;

        for (var s = 0; s < state.States.Length; s++)
        {
            var st = state.States[s];
            var ownerIdx = state.NationIndexById.GetValueOrDefault(st.OwnerNationId, 0);
            var ownerNat = state.Nations[ownerIdx];
            var pf = state.Provinces[s];

            if (ownerIdx == playerIdx)
            {
                totalPlayerFoodBalance += pf.NetMonthlyFood;
                if (pf.IsStarving) playerStarvingCount++;
            }

            var domAncIdx = 0;
            long maxAncPop = -1;
            for (var a = 0; a < state.Ancestries.Length; a++)
            {
                if (statePopByPrimaryAncestry[s, a] > maxAncPop)
                {
                    maxAncPop = statePopByPrimaryAncestry[s, a];
                    domAncIdx = a;
                }
            }

            var avgCtrl = stateLocCount[s] > 0 ? stateControlSum[s] / stateLocCount[s] : 0.0;
            stateSnapshots.Add(new StateSnapshot(
                st.Id,
                st.Name,
                st.OwnerNationId,
                ownerNat.MapColorHex,
                (float)st.MapX,
                (float)st.MapY,
                (float)st.Radius,
                stateTotals[s],
                avgCtrl,
                stateTaxSum[s],
                state.Ancestries[domAncIdx].MapColorHex,
                st.Id == playerNation.CapitalStateId,
                st.AdjacentStateIds,
                st.Provinces,
                stateProvIds[s],
                stateResTags[s].ToArray(),
                st.RegionId,
                st.SuperRegionId,
                ResolveAdminTitle(state, ownerNat, isAreaLevel: true)));

            provFoodSnapshots.Add(new ProvinceFoodSnapshot(
                st.Id,
                st.Name,
                st.Id,
                pf.FoodStorage,
                pf.MaxFoodStorage,
                pf.NetMonthlyFood,
                pf.IsStarving));
        }

        var estates = new List<EstateSummarySnapshot>(5);
        double playerWeightedTotal = 0.0;
        for (var e = 1; e <= 5; e++)
            playerWeightedTotal += state.EstateWeightedShares[playerIdx * 6 + e];
        for (var e = 1; e <= 5; e++)
        {
            var share = state.EstateWeightedShares[playerIdx * 6 + e];
            var sharePct = playerWeightedTotal > 1e-6 ? share / playerWeightedTotal * 100.0 : 0.0;
            var yield = state.EstateTaxYieldGold[playerIdx * 6 + e];
            estates.Add(new EstateSummarySnapshot(
                (EstateKind)e,
                sharePct,
                playerRuntime.TaxRate,
                MonetaryAmount.FromGold(yield)));
        }

        var header = new RealmHeader(
            playerNation.Id,
            playerNation.Name,
            playerNation.DynastyName,
            playerNation.RulerName,
            playerNation.HeirName,
            rulerAncestry,
            playerNation.EnsureRulerTraits(),
            playerRuntime.GovernmentPower,
            playerRuntime.Stability,
            playerRuntime.Prestige,
            playerRuntime.CostOfCourtPercent);

        var alerts = playerStarvingCount + (playerRuntime.MonthlyBalanceCoppers < 0 ? 1 : 0);

        return new RenderSnapshot(
            state.Clock.CurrentDate,
            state.Clock.IsPaused,
            state.Clock.Speed,
            header,
            new MonetaryAmount(playerRuntime.TreasuryCoppers),
            new MonetaryAmount(playerRuntime.MonthlyBalanceCoppers),
            playerRuntime.TaxRate,
            playerPotTax,
            playerEffTax,
            playerLocCount > 0 ? playerCtrlSum / playerLocCount : 0.0,
            totalPlayerFoodBalance,
            playerStarvingCount,
            totalWorldPop,
            playerPop,
            activePopCount,
            alerts,
            priceMap,
            marketSnapshots,
            stateSnapshots,
            locSnapshots,
            provFoodSnapshots,
            estates,
            playerPops);
    }

    private static string? ResolveAdminTitle(SimulationState state, NationDefinition nation, bool isAreaLevel)
    {
        if (state.AdminProfileByKey.TryGetValue(nation.Id, out var byTag))
            return isAreaLevel ? byTag.AreaTitle : byTag.ProvinceTitle;
        if (state.AdminProfileByKey.TryGetValue(nation.PrimaryAncestryId, out var byAnc))
            return isAreaLevel ? byAnc.AreaTitle : byAnc.ProvinceTitle;
        if (state.AdminProfileByKey.TryGetValue(nation.RankTier, out var byRank))
            return isAreaLevel ? byRank.AreaTitle : byRank.ProvinceTitle;
        return null;
    }

    private static string? ResolveDynamicName(SimulationState state, string locationId, NationDefinition nation)
    {
        if (state.DynamicNameByKey.TryGetValue($"{locationId}|{nation.Id}", out var byTag))
            return byTag;
        if (state.DynamicNameByKey.TryGetValue($"{locationId}|{nation.RulerPrimaryAncestry}", out var byAnc))
            return byAnc;
        return null;
    }
}
