using Voracity.Core.Data;
using Voracity.Core.Engine.Commands;
using Voracity.Core.Engine.Systems;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Core.Engine;

/// <summary>
/// Slim SOLID facade orchestrating Voracity's modular EU5 v0.1 simulation subsystems.
/// Zero Godot dependencies; deterministic SplitMix64 RNG; CommandBus + TickPipeline.
/// </summary>
public sealed class SimulationWorld
{
    public const int MicroPopConsolidationThreshold = DemographySystem.MicroPopConsolidationThreshold;
    public const int SubTicksPerDay = SimulationClock.SubTicksPerDay;

    private readonly SimulationState _state;
    private readonly SimulationCommandBus _commandBus = new();
    private readonly TickPipeline _pipeline;

    public SimulationWorld(
        WorldDatabase db,
        string playerNationId = "AET",
        ulong seed = 13800101UL,
        PlanarMapTopology? prebuiltTopology = null)
    {
        LoadedMods = db.LoadedMods;
        _state = new SimulationState(db, playerNationId, seed, prebuiltTopology);
        _pipeline = new TickPipeline(
            new ProximityAndControlSystem(),
            new RegionalMarketSystem(),
            new FoodSystem(),
            new EconomyAndFiscalSystem(),
            new DemographySystem());

        _pipeline.InitializeAll(_state);
    }

    public GameDate CurrentDate => _state.Clock.CurrentDate;
    public bool IsPaused => _state.Clock.IsPaused;
    public int Speed => _state.Clock.Speed;
    public string PlayerNationId => _state.PlayerNationId;
    public IReadOnlyList<ModManifest> LoadedMods { get; }
    public int TotalTicksExecuted => _state.Clock.TotalTicksExecuted;
    public PlanarMapTopology Topology => _state.Topology;

    public static IReadOnlyList<NationSummarySnapshot> BuildStartingNationSummaries(WorldDatabase db)
    {
        var popByNation = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var st in db.States)
        {
            long sum = 0;
            foreach (var p in st.InitialPops)
                sum += p.Population;
            popByNation[st.OwnerNationId] = popByNation.GetValueOrDefault(st.OwnerNationId) + sum;
        }

        var ancestryMap = db.Ancestries.ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        var result = new List<NationSummarySnapshot>(db.Nations.Count);
        foreach (var n in db.Nations)
        {
            var pName = ancestryMap.GetValueOrDefault(n.RulerPrimaryAncestry, n.RulerPrimaryAncestry);
            var ancDisplay = n.RulerSecondaryAncestry is not null
                ? $"{pName} + {ancestryMap.GetValueOrDefault(n.RulerSecondaryAncestry, n.RulerSecondaryAncestry)}"
                : pName;

            result.Add(new NationSummarySnapshot(
                n.Id,
                n.Name,
                n.RankTier,
                n.CapitalStateId,
                n.MapColorHex,
                n.RulerName,
                n.DynastyName,
                ancDisplay,
                n.EnsureRulerTraits(),
                popByNation.GetValueOrDefault(n.Id, 0),
                n.StartingTreasuryGold));
        }
        return result;
    }

    public void EnqueueCommand(GameCommand command) => _commandBus.Enqueue(command);
    public void DrainCommandsImmediate() => _commandBus.DrainAll(_state);

    public void StepSubTicks(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _commandBus.DrainAll(_state);
            _pipeline.ExecuteSubTick(_state);
        }
    }

    public void StepDays(int days) => StepSubTicks(days * SubTicksPerDay);

    public RenderSnapshot BuildRenderSnapshot() => RenderSnapshotBuilder.Build(_state);
    public SimulationIntegrityReport AuditIntegrity() => WorldIntegrityAuditor.Audit(_state);
    public CampaignSaveData ExportSaveData(string saveName) => SaveLoadMapper.Export(_state, saveName);

    public static SimulationWorld FromSaveData(WorldDatabase db, CampaignSaveData save, PlanarMapTopology? prebuiltTopology = null)
    {
        var world = new SimulationWorld(db, save.PlayerNationId, save.RngState, prebuiltTopology);
        SaveLoadMapper.RestoreInto(world._state, save);
        world._pipeline.RestoreDerivedAll(world._state);
        return world;
    }

    public void AddPopForSimulation(
        string locationId,
        SocialClass socialClass,
        string primaryAncestryId,
        string? secondaryAncestryId,
        long population,
        float literacy = 0.20f,
        float satisfaction = 75f)
    {
        var lIdx = _state.LocationIndexById.TryGetValue(locationId, out var directIdx)
            ? directIdx
            : _state.Locations.First(l => l.StateIndex >= 0 && _state.States[l.StateIndex].Id == locationId && l.Kind == LocationKind.Habitable).LocationIndex;

        var ownerIdx = Math.Max(0, _state.Locations[lIdx].OwnerNationIndex);
        var primaryIdx = _state.AncestryIndexById[primaryAncestryId];
        var secondaryIdx = secondaryAncestryId is not null && _state.AncestryIndexById.TryGetValue(secondaryAncestryId, out var sec)
            ? sec
            : -1;

        _state.Pops.Add(new PopRecord
        {
            LocationIndex = lIdx,
            StateIndex = _state.Locations[lIdx].StateIndex,
            NationIndex = ownerIdx,
            Class = socialClass,
            Ancestry = new DualAncestry(primaryIdx, secondaryIdx),
            Population = population,
            Literacy = literacy,
            Satisfaction = satisfaction,
            Active = true
        });
    }

    public int ConsolidateMicroPops() => DemographySystem.ConsolidateMicroPops(_state);

    public EffectiveAncestryTraits GetEffectiveTraits(DualAncestry ancestry) =>
        DualAncestry.ComputeEffectiveTraits(ancestry, _state.Ancestries);

    public double GetMarketPriceGold(string goodId, string? marketId = null)
    {
        if (!_state.GoodIndexById.TryGetValue(goodId, out var gIdx))
            return 0.0;
        var mIdx = marketId is not null && _state.MarketIndexById.TryGetValue(marketId, out var found) ? found : 0;
        return _state.MarketPrices[mIdx * _state.Goods.Length + gIdx];
    }
}
