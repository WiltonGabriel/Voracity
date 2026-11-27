using Voracity.Core.Models;

namespace Voracity.Core.Engine.Commands;

public sealed class SimulationCommandBus
{
    private readonly Queue<GameCommand> _queue = new();

    public void Enqueue(GameCommand command) => _queue.Enqueue(command);

    public void DrainAll(SimulationState state)
    {
        var bCount = Math.Max(1, state.Buildings.Length);
        while (_queue.Count > 0)
        {
            switch (_queue.Dequeue())
            {
                case SetPausedCommand paused:
                    state.Clock.IsPaused = paused.Paused;
                    break;

                case SetSpeedCommand speed:
                    state.Clock.Speed = Math.Clamp(speed.Speed, 1, 5);
                    break;

                case SetTaxRateCommand tax when state.NationIndexById.TryGetValue(tax.NationId, out var nIdx):
                    state.NationsRuntime[nIdx].TaxRate = Math.Clamp(tax.TaxRate, 0.0, 0.50);
                    break;

                case SetCourtSpendingCommand court when state.NationIndexById.TryGetValue(court.NationId, out var nIdx):
                    state.NationsRuntime[nIdx].CostOfCourtPercent = Math.Clamp(court.CostOfCourtPercent, 0.0, 100.0);
                    break;

                case BuildStructureCommand build when state.LocationIndexById.TryGetValue(build.LocationId, out var lIdx) &&
                                                      state.BuildingIndexById.TryGetValue(build.BuildingId, out var bIdx):
                    HandleBuildStructure(state, lIdx, bIdx, build.Levels, bCount);
                    break;

                case SwitchProductionMethodCommand sw when state.LocationIndexById.TryGetValue(sw.LocationId, out var lIdx) &&
                                                           state.BuildingIndexById.TryGetValue(sw.BuildingId, out var bIdx):
                    HandleSwitchPm(state, lIdx, bIdx, sw.PmId, bCount);
                    break;

                case UpgradeLocationCommand upg when state.LocationIndexById.TryGetValue(upg.LocationId, out var lIdx):
                    HandleLocationUpgrade(state, lIdx, upg.UpgradeKind);
                    break;
            }
        }
    }

    private static void HandleBuildStructure(SimulationState state, int locIdx, int buildingIdx, int levels, int bCount)
    {
        if (levels <= 0)
            return;

        ref var loc = ref state.Locations[locIdx];
        if (loc.Kind != LocationKind.Habitable || loc.OwnerNationIndex < 0)
            return;

        var totalBuildingLevels = 0;
        for (var b = 0; b < state.Buildings.Length; b++)
            totalBuildingLevels += state.BuildingSlots[locIdx * bCount + b].Levels;

        var maxAllowed = ComputeBuildingLevelsCap(in loc);
        ref var slot = ref state.BuildingSlots[locIdx * bCount + buildingIdx];
        var overCap = Math.Max(0, totalBuildingLevels - maxAllowed);
        var costMult = 1.0 + overCap * 0.05;
        var unitCost = state.Buildings[buildingIdx].CostGold * costMult;
        var totalCost = MonetaryAmount.FromGold(unitCost * levels).CopperUnits;

        ref var owner = ref state.NationsRuntime[loc.OwnerNationIndex];
        if (owner.TreasuryCoppers < totalCost)
            return;

        owner.TreasuryCoppers -= totalCost;
        slot.Levels += levels;
    }

    public static int ComputeBuildingLevelsCap(in LocationRuntime loc)
    {
        var cap = (int)Math.Floor(loc.Development);
        cap += loc.Rank switch
        {
            LocationRank.Town => 25,
            LocationRank.City => 100,
            LocationRank.Megalopolis => 200,
            _ => 4
        };
        if (loc.IsCapital) cap += 5;
        if (loc.IsMarketCenter) cap += 5;
        return Math.Max(4, cap);
    }

    private static void HandleSwitchPm(SimulationState state, int locIdx, int buildingIdx, string pmId, int bCount)
    {
        var pms = state.Buildings[buildingIdx].ProductionMethods;
        for (var i = 0; i < pms.Length; i++)
        {
            if (pms[i].Id == pmId)
            {
                state.BuildingSlots[locIdx * bCount + buildingIdx].ActivePmIndex = i;
                break;
            }
        }
    }

    private static void HandleLocationUpgrade(SimulationState state, int locIdx, LocationUpgradeKind upgradeKind)
    {
        ref var loc = ref state.Locations[locIdx];
        if (loc.Kind != LocationKind.Habitable || loc.OwnerNationIndex < 0)
            return;

        ref var owner = ref state.NationsRuntime[loc.OwnerNationIndex];

        switch (upgradeKind)
        {
            case LocationUpgradeKind.ExpandRgo:
                if (loc.RgoLevel >= loc.MaxRgoLevel)
                    return;
                var rgoCost = MonetaryAmount.FromGold(50.0).CopperUnits;
                if (owner.TreasuryCoppers < rgoCost)
                    return;
                owner.TreasuryCoppers -= rgoCost;
                loc.RgoLevel++;
                break;

            case LocationUpgradeKind.UpgradeRank:
                if (!TryGetRankUpgradeRequirements(in loc, out var nextRank, out var goldCost, out var minPop))
                    return;
                if (loc.TotalPopulation < minPop)
                    return;
                var rankCost = MonetaryAmount.FromGold(goldCost).CopperUnits;
                if (owner.TreasuryCoppers < rankCost)
                    return;
                owner.TreasuryCoppers -= rankCost;
                loc.Rank = nextRank;
                owner.ProximityDirty = true;
                break;

            case LocationUpgradeKind.UpgradeRoad:
                if (loc.RoadLevel >= RoadLevel.Railroad)
                    return;
                var nextRoad = loc.RoadLevel + 1;
                var roadGold = nextRoad switch
                {
                    RoadLevel.GravelRoad => 120.0,
                    RoadLevel.PavedRoad => 260.0,
                    RoadLevel.ModernRoad => 500.0,
                    RoadLevel.Railroad => 900.0,
                    _ => 120.0
                };
                var roadCost = MonetaryAmount.FromGold(roadGold).CopperUnits;
                if (owner.TreasuryCoppers < roadCost)
                    return;
                owner.TreasuryCoppers -= roadCost;
                loc.RoadLevel = nextRoad;
                owner.ProximityDirty = true;
                break;
        }
    }

    public static bool TryGetRankUpgradeRequirements(
        in LocationRuntime loc,
        out LocationRank nextRank,
        out double goldCost,
        out long minPop)
    {
        switch (loc.Rank)
        {
            case LocationRank.RuralSettlement:
                nextRank = LocationRank.Town;
                goldCost = 500.0;
                minPop = 5_000;
                return true;
            case LocationRank.Town:
                nextRank = LocationRank.City;
                goldCost = 2000.0;
                minPop = 30_000;
                return true;
            case LocationRank.City when loc.IsCapital && loc.IsMarketCenter:
                nextRank = LocationRank.Megalopolis;
                goldCost = 8000.0;
                minPop = 400_000;
                return true;
            default:
                nextRank = loc.Rank;
                goldCost = 0.0;
                minPop = long.MaxValue;
                return false;
        }
    }
}
