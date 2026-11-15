using System.Runtime.InteropServices;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Core.Engine;

/// <summary>
/// Headless, deterministic Grand Strategy simulation engine for Voracity.
/// Implements sub-tick (6h), staggered daily, weekly market/economy/war, and monthly demographic/IG/law/JE loops.
/// </summary>
public sealed class SimulationWorld
{
    public const int MicroPopConsolidationThreshold = 50;
    public const int SubTicksPerDay = 4;

    private static readonly (string Id, string Name)[] UniversalIgs =
    [
        ("ig_aristocracy", "Aristocracia"),
        ("ig_clergy", "Liderança Religiosa"),
        ("ig_armed_forces", "Forças Armadas"),
        ("ig_artisans_guilds", "Artífices & Guildas"),
        ("ig_merchants", "Mercadores & Capital"),
        ("ig_rural_folk", "Comunidades Rurais"),
        ("ig_academics", "Acadêmicos & Arcanistas"),
        ("ig_adventurers", "Aventureiros & Dissidentes")
    ];

    private struct DiplomacyPair
    {
        public int Opinion;
        public bool TradePact;
        public bool Alliance;
        public bool AtWar;
        public double WarScore;
    }

    private readonly WorldDatabase _db;
    private readonly AncestryDefinition[] _ancestries;
    private readonly GoodDefinition[] _goods;
    private readonly NationDefinition[] _nations;
    private readonly StateDefinition[] _states;
    private readonly BuildingDefinition[] _buildings;
    private readonly LawDefinition[] _laws;
    private readonly TechnologyDefinition[] _technologies;
    private readonly JournalEntryDefinition[] _journalEntries;

    private readonly Dictionary<string, int> _ancestryIndexById;
    private readonly Dictionary<string, int> _goodIndexById;
    private readonly Dictionary<string, int> _nationIndexById;
    private readonly Dictionary<string, int> _stateIndexById;
    private readonly Dictionary<string, int> _buildingIndexById;
    private readonly Dictionary<string, int> _lawIndexById;
    private readonly Dictionary<string, int> _techIndexById;
    private readonly Dictionary<string, int> _provinceIndexById;

    private readonly (int StateIndex, ProvinceDefinition Def)[] _provinces;
    private readonly string[][] _stateProvinceIds;
    private readonly string[][] _stateResourceTags;

    // Pre-allocated contiguous simulation state arrays
    private readonly double[] _nationalMarketBuyOrders;
    private readonly double[] _nationalMarketSellOrders;
    private readonly double[] _nationalMarketPrices;
    private readonly double[] _lastMarketPrices;
    private readonly bool[] _stateDirtyFlags;
    private readonly double[] _stateHoldingThroughput;

    private readonly long[] _nationTreasuryCoppers;
    private readonly long[] _nationWeeklyBalanceCoppers;
    private readonly double[] _nationTaxRates;
    private readonly int[] _nationStress;
    private readonly int[] _nationAuthority;
    private readonly double[] _nationTechPoints;
    private readonly string?[] _nationActiveTechId;
    private readonly HashSet<string>[] _nationCompletedTechs;
    private readonly HashSet<string>[] _nationEnactedLaws;
    private readonly string?[] _nationEnactingLawId;
    private readonly double[] _nationEnactingLawProgress;
    private readonly HashSet<string>[] _nationCompletedJournalEntries;
    private readonly double[,] _nationIgClout;

    private readonly StateBuildingSlot[] _buildingSlots;
    private readonly ProvinceSlot[] _provinceSlots;
    private readonly DiplomacyPair[,] _diplomacy;

    private readonly List<PopRecord> _pops = new(512);
    private readonly Queue<GameCommand> _commandQueue = new();

    private ulong _rngState;
    private int _grainGoodIndex = -1;
    private int _ironGoodIndex = -1;
    private int _woodGoodIndex = -1;
    private int _steelGoodIndex = -1;
    private int _bloodEssenceGoodIndex = -1;
    private int _wheatFarmBuildingIndex = -1;
    private int _universityBuildingIndex = -1;
    private int _barracksBuildingIndex = -1;

    public GameDate CurrentDate { get; private set; } = new(1380, 1, 1, 0);
    public bool IsPaused { get; private set; } = true;
    public int Speed { get; private set; } = 2;
    public string PlayerNationId { get; private set; }
    public IReadOnlyList<ModManifest> LoadedMods { get; }
    public int TotalTicksExecuted { get; private set; }

    public SimulationWorld(WorldDatabase db, string playerNationId = "AET", ulong seed = 13800101UL)
    {
        _db = db;
        _ancestries = db.Ancestries.ToArray();
        _goods = db.Goods.ToArray();
        _nations = db.Nations.ToArray();
        _states = db.States.ToArray();
        _buildings = db.Buildings.ToArray();
        _laws = db.Laws.ToArray();
        _technologies = db.Technologies.ToArray();
        _journalEntries = db.JournalEntries.ToArray();
        LoadedMods = db.LoadedMods;
        _rngState = seed == 0 ? 1UL : seed;

        _ancestryIndexById = BuildIndexMap(_ancestries, a => a.Id);
        _goodIndexById = BuildIndexMap(_goods, g => g.Id);
        _nationIndexById = BuildIndexMap(_nations, n => n.Id);
        _stateIndexById = BuildIndexMap(_states, s => s.Id);
        _buildingIndexById = BuildIndexMap(_buildings, b => b.Id);
        _lawIndexById = BuildIndexMap(_laws, l => l.Id);
        _techIndexById = BuildIndexMap(_technologies, t => t.Id);

        var provList = new List<(int StateIndex, ProvinceDefinition Def)>(_states.Length * 4);
        _stateProvinceIds = new string[_states.Length][];
        _stateResourceTags = new string[_states.Length][];
        for (var s = 0; s < _states.Length; s++)
        {
            var details = _states[s].ProvinceDetails ?? [];
            var pIds = new string[details.Length];
            var rTags = new HashSet<string>(StringComparer.Ordinal);
            for (var p = 0; p < details.Length; p++)
            {
                pIds[p] = details[p].Id;
                if (!string.IsNullOrWhiteSpace(details[p].ResourcePotential))
                    rTags.Add(details[p].ResourcePotential!);
                provList.Add((s, details[p]));
            }
            _stateProvinceIds[s] = pIds;
            _stateResourceTags[s] = rTags.ToArray();
        }
        _provinces = provList.ToArray();
        _provinceIndexById = new Dictionary<string, int>(_provinces.Length, StringComparer.Ordinal);
        for (var p = 0; p < _provinces.Length; p++)
            _provinceIndexById[_provinces[p].Def.Id] = p;

        PlayerNationId = _nationIndexById.ContainsKey(playerNationId) ? playerNationId : _nations[0].Id;

        _nationalMarketBuyOrders = new double[_goods.Length];
        _nationalMarketSellOrders = new double[_goods.Length];
        _nationalMarketPrices = new double[_goods.Length];
        _lastMarketPrices = new double[_goods.Length];
        for (var i = 0; i < _goods.Length; i++)
        {
            _nationalMarketPrices[i] = _goods[i].BasePriceGold;
            _lastMarketPrices[i] = _goods[i].BasePriceGold;
        }

        _goodIndexById.TryGetValue("grain", out _grainGoodIndex);
        _goodIndexById.TryGetValue("iron", out _ironGoodIndex);
        _goodIndexById.TryGetValue("wood", out _woodGoodIndex);
        _goodIndexById.TryGetValue("steel", out _steelGoodIndex);
        _goodIndexById.TryGetValue("blood_essence", out _bloodEssenceGoodIndex);

        if (!_buildingIndexById.TryGetValue("building_wheat_farms", out _wheatFarmBuildingIndex))
            _wheatFarmBuildingIndex = _buildings.Length > 0 ? 0 : -1;
        if (!_buildingIndexById.TryGetValue("building_university", out _universityBuildingIndex))
            _universityBuildingIndex = -1;
        if (!_buildingIndexById.TryGetValue("building_barracks", out _barracksBuildingIndex))
            _barracksBuildingIndex = -1;

        _nationTreasuryCoppers = new long[_nations.Length];
        _nationWeeklyBalanceCoppers = new long[_nations.Length];
        _nationTaxRates = new double[_nations.Length];
        _nationStress = new int[_nations.Length];
        _nationAuthority = new int[_nations.Length];
        _nationTechPoints = new double[_nations.Length];
        _nationActiveTechId = new string?[_nations.Length];
        _nationCompletedTechs = new HashSet<string>[_nations.Length];
        _nationEnactedLaws = new HashSet<string>[_nations.Length];
        _nationEnactingLawId = new string?[_nations.Length];
        _nationEnactingLawProgress = new double[_nations.Length];
        _nationCompletedJournalEntries = new HashSet<string>[_nations.Length];
        _nationIgClout = new double[_nations.Length, UniversalIgs.Length];

        for (var i = 0; i < _nations.Length; i++)
        {
            _nationTreasuryCoppers[i] = MonetaryAmount.FromGold(_nations[i].StartingTreasuryGold).CopperUnits;
            _nationTaxRates[i] = 0.12;
            _nationAuthority[i] = _nations[i].AuthorityLevel;
            _nationCompletedTechs[i] = new HashSet<string>(StringComparer.Ordinal);
            _nationEnactedLaws[i] = new HashSet<string>(StringComparer.Ordinal)
            {
                _nations[i].Id == "LEV" ? "law_laissez_faire" : "law_feudal_agrarianism",
                "law_free_labor",
                _nations[i].Id == "DAL" ? "law_inquisitorial_hunt" : "law_moderate_regulation"
            };
            _nationCompletedJournalEntries[i] = new HashSet<string>(StringComparer.Ordinal);
            if (_technologies.Length > 0)
                _nationActiveTechId[i] = _technologies[0].Id;
        }

        _stateDirtyFlags = new bool[_states.Length];
        Array.Fill(_stateDirtyFlags, true);
        _stateHoldingThroughput = new double[_states.Length];
        Array.Fill(_stateHoldingThroughput, 1.0);

        _buildingSlots = new StateBuildingSlot[_states.Length * Math.Max(1, _buildings.Length)];
        _provinceSlots = new ProvinceSlot[_provinces.Length];
        _diplomacy = new DiplomacyPair[_nations.Length, _nations.Length];

        InitializePopsBuildingsAndDiplomacy();
        RecomputeAllStateHoldingThroughput();
        RecomputeIgCloutForAllNations();
        RecomputeMarketOrdersAndPrices();
    }

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
                ancDisplay,
                popByNation.GetValueOrDefault(n.Id, 0),
                n.StartingTreasuryGold));
        }
        return result;
    }

    public void EnqueueCommand(GameCommand command) => _commandQueue.Enqueue(command);

    public void StepSubTicks(int count)
    {
        for (var i = 0; i < count; i++)
        {
            DrainCommands();
            ExecuteSubTick6h();
        }
    }

    public void StepDays(int days) => StepSubTicks(days * SubTicksPerDay);

    public void AddPopForSimulation(
        string stateId,
        string profession,
        string primaryAncestryId,
        string? secondaryAncestryId,
        long population,
        float sol = 10f,
        float literacy = 0.2f)
    {
        var stateIdx = _stateIndexById[stateId];
        var nationIdx = _nationIndexById[_states[stateIdx].OwnerNationId];
        var primaryIdx = _ancestryIndexById[primaryAncestryId];
        var secondaryIdx = secondaryAncestryId is not null && _ancestryIndexById.TryGetValue(secondaryAncestryId, out var sec)
            ? sec
            : -1;

        _pops.Add(new PopRecord
        {
            StateIndex = stateIdx,
            NationIndex = nationIdx,
            ProfessionIndex = ProfessionIndex(profession),
            Ancestry = new DualAncestry(primaryIdx, secondaryIdx),
            Population = population,
            StandardOfLiving = sol,
            Literacy = literacy,
            Radicalism = 0f,
            Active = true
        });
        _stateDirtyFlags[stateIdx] = true;
    }

    public EffectiveAncestryTraits GetEffectiveTraits(DualAncestry ancestry) =>
        DualAncestry.ComputeEffectiveTraits(ancestry, _ancestries);

    public double GetMarketPriceGold(string goodId) =>
        _goodIndexById.TryGetValue(goodId, out var idx) ? _nationalMarketPrices[idx] : 0.0;

    public CampaignSaveData ExportSaveData(string saveName)
    {
        var completedTechs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var enactedLaws = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var completedJes = new Dictionary<string, string[]>(StringComparer.Ordinal);

        for (var n = 0; n < _nations.Length; n++)
        {
            completedTechs[_nations[n].Id] = _nationCompletedTechs[n].ToArray();
            enactedLaws[_nations[n].Id] = _nationEnactedLaws[n].ToArray();
            completedJes[_nations[n].Id] = _nationCompletedJournalEntries[n].ToArray();
        }

        var pops = new SavedPopEntry[_pops.Count];
        for (var i = 0; i < _pops.Count; i++)
        {
            var p = _pops[i];
            pops[i] = new SavedPopEntry(
                p.StateIndex,
                p.NationIndex,
                p.ProfessionIndex,
                p.Ancestry.PrimaryIndex,
                p.Ancestry.SecondaryIndex,
                p.Population,
                p.StandardOfLiving,
                p.Literacy,
                p.Radicalism,
                p.Active);
        }

        var buildings = new SavedBuildingEntry[_buildingSlots.Length];
        for (var i = 0; i < _buildingSlots.Length; i++)
        {
            var b = _buildingSlots[i];
            buildings[i] = new SavedBuildingEntry(b.StateIndex, b.BuildingIndex, b.Levels, b.ActivePmIndex);
        }

        var diploList = new List<SavedDiplomacyEntry>();
        for (var a = 0; a < _nations.Length; a++)
        {
            for (var b = a + 1; b < _nations.Length; b++)
            {
                var d = _diplomacy[a, b];
                diploList.Add(new SavedDiplomacyEntry(a, b, d.Opinion, d.TradePact, d.Alliance, d.AtWar, d.WarScore));
            }
        }

        var provs = new SavedProvinceEntry[_provinceSlots.Length];
        for (var i = 0; i < _provinceSlots.Length; i++)
        {
            var slot = _provinceSlots[i];
            provs[i] = new SavedProvinceEntry(_provinces[i].Def.Id, slot.HoldingLevel, slot.FortLevel, slot.OccupiedByNationIndex);
        }

        return new CampaignSaveData(
            saveName,
            DateTimeOffset.UtcNow,
            CurrentDate,
            PlayerNationId,
            Speed,
            _rngState,
            TotalTicksExecuted,
            _nationTreasuryCoppers.ToArray(),
            _nationTaxRates.ToArray(),
            _nationStress.ToArray(),
            _nationAuthority.ToArray(),
            _nationTechPoints.ToArray(),
            _nationActiveTechId.ToArray(),
            completedTechs,
            enactedLaws,
            _nationEnactingLawId.ToArray(),
            _nationEnactingLawProgress.ToArray(),
            completedJes,
            pops,
            buildings,
            diploList.ToArray(),
            provs);
    }

    public static SimulationWorld FromSaveData(WorldDatabase db, CampaignSaveData save)
    {
        var world = new SimulationWorld(db, save.PlayerNationId, save.RngState)
        {
            CurrentDate = save.Date,
            Speed = save.Speed,
            TotalTicksExecuted = save.TotalTicksExecuted,
            IsPaused = true
        };

        Array.Copy(save.NationTreasuryCoppers, world._nationTreasuryCoppers, Math.Min(save.NationTreasuryCoppers.Length, world._nations.Length));
        Array.Copy(save.NationTaxRates, world._nationTaxRates, Math.Min(save.NationTaxRates.Length, world._nations.Length));
        Array.Copy(save.NationStress, world._nationStress, Math.Min(save.NationStress.Length, world._nations.Length));
        Array.Copy(save.NationAuthority, world._nationAuthority, Math.Min(save.NationAuthority.Length, world._nations.Length));
        Array.Copy(save.NationTechPoints, world._nationTechPoints, Math.Min(save.NationTechPoints.Length, world._nations.Length));
        Array.Copy(save.NationActiveTechId, world._nationActiveTechId, Math.Min(save.NationActiveTechId.Length, world._nations.Length));
        Array.Copy(save.NationEnactingLawId, world._nationEnactingLawId, Math.Min(save.NationEnactingLawId.Length, world._nations.Length));
        Array.Copy(save.NationEnactingLawProgress, world._nationEnactingLawProgress, Math.Min(save.NationEnactingLawProgress.Length, world._nations.Length));

        for (var n = 0; n < world._nations.Length; n++)
        {
            var id = world._nations[n].Id;
            if (save.NationCompletedTechs.TryGetValue(id, out var techs))
                world._nationCompletedTechs[n] = new HashSet<string>(techs, StringComparer.Ordinal);
            if (save.NationEnactedLaws.TryGetValue(id, out var laws))
                world._nationEnactedLaws[n] = new HashSet<string>(laws, StringComparer.Ordinal);
            if (save.NationCompletedJournalEntries.TryGetValue(id, out var jes))
                world._nationCompletedJournalEntries[n] = new HashSet<string>(jes, StringComparer.Ordinal);
        }

        world._pops.Clear();
        foreach (var p in save.Pops)
        {
            world._pops.Add(new PopRecord
            {
                StateIndex = p.StateIndex,
                NationIndex = p.NationIndex,
                ProfessionIndex = p.ProfessionIndex,
                Ancestry = new DualAncestry(p.PrimaryAncestry, p.SecondaryAncestry),
                Population = p.Population,
                StandardOfLiving = p.StandardOfLiving,
                Literacy = p.Literacy,
                Radicalism = p.Radicalism,
                Active = p.Active
            });
        }

        for (var i = 0; i < Math.Min(save.Buildings.Length, world._buildingSlots.Length); i++)
        {
            var b = save.Buildings[i];
            world._buildingSlots[i] = new StateBuildingSlot
            {
                StateIndex = b.StateIndex,
                BuildingIndex = b.BuildingIndex,
                Levels = b.Levels,
                ActivePmIndex = b.ActivePmIndex
            };
        }

        foreach (var d in save.Diplomacy)
        {
            var pair = new DiplomacyPair
            {
                Opinion = d.Opinion,
                TradePact = d.TradePact,
                Alliance = d.Alliance,
                AtWar = d.AtWar,
                WarScore = d.WarScore
            };
            world._diplomacy[d.NationA, d.NationB] = pair;
            world._diplomacy[d.NationB, d.NationA] = pair;
        }

        if (save.Provinces is not null)
        {
            foreach (var sp in save.Provinces)
            {
                if (!world._provinceIndexById.TryGetValue(sp.ProvinceId, out var pIdx))
                    continue;
                world._provinceSlots[pIdx].HoldingLevel = Math.Clamp(sp.HoldingLevel, 1, 10);
                world._provinceSlots[pIdx].FortLevel = Math.Clamp(sp.FortLevel, 0, 5);
                world._provinceSlots[pIdx].OccupiedByNationIndex = sp.OccupiedByNationIndex >= -1 && sp.OccupiedByNationIndex < world._nations.Length
                    ? sp.OccupiedByNationIndex
                    : -1;
            }
        }

        world.RecomputeAllStateHoldingThroughput();
        world.RecomputeIgCloutForAllNations();
        world.RecomputeMarketOrdersAndPrices();
        return world;
    }

    public RenderSnapshot BuildRenderSnapshot()
    {
        var playerIdx = _nationIndexById[PlayerNationId];
        var playerNation = _nations[playerIdx];

        var rulerPrimaryIdx = _ancestryIndexById.GetValueOrDefault(playerNation.RulerPrimaryAncestry, 0);
        var rulerSecondaryIdx = playerNation.RulerSecondaryAncestry is not null
            ? _ancestryIndexById.GetValueOrDefault(playerNation.RulerSecondaryAncestry, -1)
            : -1;
        var rulerAncestry = new DualAncestry(rulerPrimaryIdx, rulerSecondaryIdx).FormatName(_ancestries);

        var stateTotals = new long[_states.Length];
        var stateWeightedSol = new double[_states.Length];
        var statePopByPrimaryAncestry = new long[_states.Length, _ancestries.Length];
        long totalWorldPop = 0;
        long playerPop = 0;
        var activePopCount = 0;

        var playerPops = new List<PopSummarySnapshot>();
        var popSpan = CollectionsMarshal.AsSpan(_pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0)
                continue;

            activePopCount++;
            totalWorldPop += p.Population;
            stateTotals[p.StateIndex] += p.Population;
            stateWeightedSol[p.StateIndex] += p.Population * p.StandardOfLiving;
            statePopByPrimaryAncestry[p.StateIndex, p.Ancestry.PrimaryIndex] += p.Population;

            if (p.NationIndex == playerIdx)
            {
                playerPop += p.Population;
                playerPops.Add(new PopSummarySnapshot(
                    _states[p.StateIndex].Id,
                    _states[p.StateIndex].Name,
                    ProfessionName(p.ProfessionIndex),
                    p.Ancestry.FormatName(_ancestries),
                    p.Population,
                    p.StandardOfLiving,
                    p.Literacy));
            }
        }

        double avgPriceRatioSum = 0.0;
        var priceMap = new Dictionary<string, double>(_goods.Length, StringComparer.Ordinal);
        for (var g = 0; g < _goods.Length; g++)
        {
            priceMap[_goods[g].Id] = _nationalMarketPrices[g];
            if (_goods[g].BasePriceGold > 0)
                avgPriceRatioSum += _nationalMarketPrices[g] / _goods[g].BasePriceGold;
        }
        var globalPriceRatio = _goods.Length > 0 ? avgPriceRatioSum / _goods.Length : 1.0;

        var stateSnapshots = new List<StateSnapshot>(_states.Length);
        var bCount = Math.Max(1, _buildings.Length);
        for (var s = 0; s < _states.Length; s++)
        {
            var st = _states[s];
            var ownerIdx = _nationIndexById[st.OwnerNationId];
            var pop = stateTotals[s];
            var avgSol = pop > 0 ? (float)(stateWeightedSol[s] / pop) : 0f;

            var domAncestryIdx = 0;
            long maxAncPop = -1;
            for (var a = 0; a < _ancestries.Length; a++)
            {
                if (statePopByPrimaryAncestry[s, a] > maxAncPop)
                {
                    maxAncPop = statePopByPrimaryAncestry[s, a];
                    domAncestryIdx = a;
                }
            }

            var buildings = new List<BuildingSnapshot>(_buildings.Length);
            var farmLevels = 0;
            for (var b = 0; b < _buildings.Length; b++)
            {
                var slot = _buildingSlots[s * bCount + b];
                if (b == _wheatFarmBuildingIndex)
                    farmLevels = slot.Levels;

                var bDef = _buildings[b];
                var pmDef = bDef.ProductionMethods[Math.Clamp(slot.ActivePmIndex, 0, bDef.ProductionMethods.Length - 1)];
                var availPms = new List<(string, string, bool)>(bDef.ProductionMethods.Length);
                foreach (var pm in bDef.ProductionMethods)
                {
                    var unlocked = pm.RequiredTechId is null || _nationCompletedTechs[ownerIdx].Contains(pm.RequiredTechId);
                    availPms.Add((pm.Id, pm.NameKey, unlocked));
                }

                buildings.Add(new BuildingSnapshot(bDef.Id, bDef.NameKey, slot.Levels, pmDef.Id, pmDef.NameKey, availPms));
            }

            var diploWithPlayer = _diplomacy[playerIdx, ownerIdx];
            stateSnapshots.Add(new StateSnapshot(
                st.Id,
                st.Name,
                st.OwnerNationId,
                _nations[ownerIdx].MapColorHex,
                (float)st.MapX,
                (float)st.MapY,
                (float)st.Radius,
                st.ArableLand,
                pop,
                avgSol,
                globalPriceRatio,
                _ancestries[domAncestryIdx].MapColorHex,
                farmLevels,
                ownerIdx == playerIdx ? 100.0 : diploWithPlayer.Opinion,
                ownerIdx != playerIdx && diploWithPlayer.AtWar,
                ownerIdx != playerIdx && diploWithPlayer.TradePact,
                ownerIdx != playerIdx && diploWithPlayer.Alliance,
                st.Id == playerNation.CapitalStateId,
                st.AdjacentStateIds,
                st.Provinces,
                _stateProvinceIds[s],
                _stateResourceTags[s],
                buildings));
        }

        var provinceSnapshots = new List<ProvinceSnapshot>(_provinces.Length);
        for (var p = 0; p < _provinces.Length; p++)
        {
            var (stateIdx, pDef) = _provinces[p];
            var st = _states[stateIdx];
            var slot = _provinceSlots[p];
            var occNationId = slot.OccupiedByNationIndex >= 0 && slot.OccupiedByNationIndex < _nations.Length
                ? _nations[slot.OccupiedByNationIndex].Id
                : null;

            provinceSnapshots.Add(new ProvinceSnapshot(
                NumericalIndex: p + 1,
                ProvinceId: pDef.Id,
                Name: pDef.Name,
                StateId: st.Id,
                OwnerNationId: st.OwnerNationId,
                Terrain: pDef.Terrain,
                HubType: pDef.HubType,
                IsCapital: pDef.IsCapital,
                IsCoastal: pDef.IsCoastal,
                AnchorX: (float)pDef.AnchorX,
                AnchorY: (float)pDef.AnchorY,
                HoldingLevel: slot.HoldingLevel,
                FortLevel: slot.FortLevel,
                ResourcePotential: pDef.ResourcePotential,
                OccupiedByNationId: occNationId));
        }

        var igSnapshots = new List<InterestGroupSnapshot>(UniversalIgs.Length);
        for (var k = 0; k < UniversalIgs.Length; k++)
        {
            var clout = _nationIgClout[playerIdx, k];
            var approval = ComputeIgApproval(playerIdx, UniversalIgs[k].Id);
            igSnapshots.Add(new InterestGroupSnapshot(
                UniversalIgs[k].Id,
                UniversalIgs[k].Name,
                clout,
                approval,
                clout >= 14.0));
        }

        var lawSnapshots = new List<LawStatusSnapshot>(_laws.Length);
        foreach (var law in _laws)
        {
            var enacted = _nationEnactedLaws[playerIdx].Contains(law.Id);
            var enacting = _nationEnactingLawId[playerIdx] == law.Id;
            lawSnapshots.Add(new LawStatusSnapshot(
                law.Id,
                law.Category,
                law.NameKey,
                enacted,
                enacting,
                enacting ? _nationEnactingLawProgress[playerIdx] : 0.0));
        }

        var activeTechId = _nationActiveTechId[playerIdx];
        var techSnapshots = new List<TechnologyProgressSnapshot>(_technologies.Length);
        foreach (var tech in _technologies)
        {
            var completed = _nationCompletedTechs[playerIdx].Contains(tech.Id);
            var active = activeTechId == tech.Id;
            techSnapshots.Add(new TechnologyProgressSnapshot(
                tech.Id,
                tech.Tree,
                tech.NameKey,
                tech.CostPoints,
                active ? _nationTechPoints[playerIdx] : (completed ? tech.CostPoints : 0.0),
                completed,
                active));
        }

        var diploSnapshots = new List<DiplomaticRelationSnapshot>(_nations.Length - 1);
        for (var other = 0; other < _nations.Length; other++)
        {
            if (other == playerIdx)
                continue;
            var pair = _diplomacy[playerIdx, other];
            diploSnapshots.Add(new DiplomaticRelationSnapshot(
                _nations[other].Id,
                _nations[other].Name,
                _nations[other].MapColorHex,
                pair.Opinion,
                pair.TradePact,
                pair.Alliance,
                pair.AtWar,
                pair.WarScore,
                ComputeNationMilitaryPower(other)));
        }

        var jeSnapshots = new List<JournalEntrySnapshot>();
        foreach (var je in _journalEntries)
        {
            if (je.NationId is not null && je.NationId != playerNation.Id)
                continue;
            var completed = _nationCompletedJournalEntries[playerIdx].Contains(je.Id);
            var ratio = completed ? 1.0 : EvaluateJournalProgress(playerIdx, je);
            jeSnapshots.Add(new JournalEntrySnapshot(je.Id, je.NameKey, je.DescriptionKey, ratio, completed, je.RewardGold));
        }

        var alerts = 0;
        if (_nationWeeklyBalanceCoppers[playerIdx] < 0) alerts++;
        if (_nationStress[playerIdx] >= 150) alerts++;
        if (_nationActiveTechId[playerIdx] is null && _nationCompletedTechs[playerIdx].Count < _technologies.Length) alerts++;

        return new RenderSnapshot(
            CurrentDate,
            IsPaused,
            Speed,
            playerNation.Id,
            playerNation.Name,
            new MonetaryAmount(_nationTreasuryCoppers[playerIdx]),
            new MonetaryAmount(_nationWeeklyBalanceCoppers[playerIdx]),
            _nationTaxRates[playerIdx],
            BureaucracyBalance: 120 + CountNationBuildingLevels(playerIdx, _universityBuildingIndex) * 15,
            AuthorityLevel: _nationAuthority[playerIdx],
            RulerStress: _nationStress[playerIdx],
            RulerName: playerNation.RulerName,
            RulerAncestryDisplay: rulerAncestry,
            HeirName: playerNation.HeirName,
            TotalWorldPopulation: totalWorldPop,
            PlayerPopulation: playerPop,
            ActivePopCount: activePopCount,
            ActiveAlertsCount: alerts,
            ActiveLawId: _nationEnactingLawId[playerIdx],
            ActiveLawProgress: _nationEnactingLawProgress[playerIdx],
            ActiveTechId: activeTechId,
            ActiveTechProgress: _nationTechPoints[playerIdx],
            MarketPricesGold: priceMap,
            States: stateSnapshots,
            Provinces: provinceSnapshots,
            PlayerInterestGroups: igSnapshots,
            PlayerLaws: lawSnapshots,
            PlayerTechnologies: techSnapshots,
            PlayerDiplomacy: diploSnapshots,
            PlayerJournalEntries: jeSnapshots,
            PlayerPops: playerPops);
    }

    private void InitializePopsBuildingsAndDiplomacy()
    {
        var bCount = Math.Max(1, _buildings.Length);
        for (var s = 0; s < _states.Length; s++)
        {
            var st = _states[s];
            var nationIdx = _nationIndexById[st.OwnerNationId];

            for (var b = 0; b < _buildings.Length; b++)
            {
                var initialLevels = _buildings[b].Id switch
                {
                    "building_wheat_farms" => Math.Max(1, st.ArableLand / 15),
                    "building_logging_camps" => 2,
                    "building_iron_mines" => st.MacroRegion is "UndergroundSouth" or "EastCentral" ? 2 : 1,
                    "building_barracks" => 2,
                    "building_university" => st.OwnerNationId == "AET" || st.OwnerNationId == "HAS" || st.OwnerNationId == "DAL" ? 1 : 0,
                    _ => 0
                };

                _buildingSlots[s * bCount + b] = new StateBuildingSlot
                {
                    StateIndex = s,
                    BuildingIndex = b,
                    Levels = initialLevels,
                    ActivePmIndex = 0
                };
            }

            foreach (var spec in st.InitialPops)
            {
                var primaryIdx = _ancestryIndexById[spec.PrimaryAncestry];
                var secondaryIdx = spec.SecondaryAncestry is not null && _ancestryIndexById.TryGetValue(spec.SecondaryAncestry, out var sec)
                    ? sec
                    : -1;

                _pops.Add(new PopRecord
                {
                    StateIndex = s,
                    NationIndex = nationIdx,
                    ProfessionIndex = ProfessionIndex(spec.Profession),
                    Ancestry = new DualAncestry(primaryIdx, secondaryIdx),
                    Population = spec.Population,
                    StandardOfLiving = (float)spec.Sol,
                    Literacy = (float)spec.Literacy,
                    Radicalism = 0f,
                    Active = true
                });
            }
        }

        for (var p = 0; p < _provinces.Length; p++)
        {
            var (stateIdx, pDef) = _provinces[p];
            _provinceSlots[p] = new ProvinceSlot
            {
                ProvinceIndex = p,
                StateIndex = stateIdx,
                HoldingLevel = pDef.IsCapital ? 2 : 1,
                FortLevel = pDef.StartingFortLevel,
                OccupiedByNationIndex = -1
            };
        }

        // Seed canonical rivalries & trade ties in 1380 D.Q.
        for (var a = 0; a < _nations.Length; a++)
        {
            for (var b = a + 1; b < _nations.Length; b++)
            {
                var opinion = 10;
                if ((_nations[a].Id == "HAS" && _nations[b].Id == "AET") || (_nations[a].Id == "AET" && _nations[b].Id == "HAS"))
                    opinion = -35;
                else if ((_nations[a].Id == "CRZ" && _nations[b].Id == "KHN") || (_nations[a].Id == "KHN" && _nations[b].Id == "CRZ"))
                    opinion = -70;
                else if ((_nations[a].Id == "IRH" && _nations[b].Id == "BGF") || (_nations[a].Id == "BGF" && _nations[b].Id == "IRH"))
                    opinion = 55;

                var pair = new DiplomacyPair { Opinion = opinion, TradePact = opinion >= 40 };
                _diplomacy[a, b] = pair;
                _diplomacy[b, a] = pair;
            }
        }
    }

    public void DrainCommandsImmediate() => DrainCommands();

    private void DrainCommands()
    {
        var bCount = Math.Max(1, _buildings.Length);
        while (_commandQueue.Count > 0)
        {
            switch (_commandQueue.Dequeue())
            {
                case SetPausedCommand paused:
                    IsPaused = paused.Paused;
                    break;

                case SetSpeedCommand speed:
                    Speed = Math.Clamp(speed.Speed, 1, 5);
                    break;

                case SetTaxRateCommand tax when _nationIndexById.TryGetValue(tax.NationId, out var nIdx):
                    _nationTaxRates[nIdx] = Math.Clamp(tax.TaxRate, 0.05, 0.35);
                    break;

                case InvestStateFarmCommand invest when _stateIndexById.TryGetValue(invest.StateId, out var sIdx) && _wheatFarmBuildingIndex >= 0:
                    TryExpandBuilding(sIdx, _wheatFarmBuildingIndex, invest.Levels, bCount);
                    break;

                case BuildStructureCommand build when _stateIndexById.TryGetValue(build.StateId, out var sIdx) && _buildingIndexById.TryGetValue(build.BuildingId, out var bIdx):
                    TryExpandBuilding(sIdx, bIdx, build.Levels, bCount);
                    break;

                case UpgradeProvinceHoldingCommand upgradeProv when _provinceIndexById.TryGetValue(upgradeProv.ProvinceId, out var pIdx):
                    TryUpgradeProvince(pIdx, upgradeProv.UpgradeKind);
                    break;

                case SwitchProductionMethodCommand sw when _stateIndexById.TryGetValue(sw.StateId, out var sIdx) && _buildingIndexById.TryGetValue(sw.BuildingId, out var bIdx):
                    var ownerIdx = _nationIndexById[_states[sIdx].OwnerNationId];
                    var pms = _buildings[bIdx].ProductionMethods;
                    for (var pmIdx = 0; pmIdx < pms.Length; pmIdx++)
                    {
                        if (pms[pmIdx].Id != sw.PmId)
                            continue;
                        var reqTech = pms[pmIdx].RequiredTechId;
                        if (reqTech is null || _nationCompletedTechs[ownerIdx].Contains(reqTech))
                        {
                            _buildingSlots[sIdx * bCount + bIdx].ActivePmIndex = pmIdx;
                            _stateDirtyFlags[sIdx] = true;
                        }
                        break;
                    }
                    break;

                case EnactLawCommand enact when _nationIndexById.TryGetValue(enact.NationId, out var nIdx) && _lawIndexById.ContainsKey(enact.LawId):
                    if (!_nationEnactedLaws[nIdx].Contains(enact.LawId))
                    {
                        _nationEnactingLawId[nIdx] = enact.LawId;
                        _nationEnactingLawProgress[nIdx] = 0.0;
                    }
                    break;

                case StartResearchCommand res when _nationIndexById.TryGetValue(res.NationId, out var nIdx) && _techIndexById.ContainsKey(res.TechId):
                    if (!_nationCompletedTechs[nIdx].Contains(res.TechId))
                    {
                        if (_nationActiveTechId[nIdx] != res.TechId)
                            _nationTechPoints[nIdx] = 0.0;
                        _nationActiveTechId[nIdx] = res.TechId;
                    }
                    break;

                case CourtActionCommand court when _nationIndexById.TryGetValue(court.NationId, out var nIdx):
                    ExecuteCourtAction(nIdx, court.Action);
                    break;

                case DiplomaticActionCommand diplo when _nationIndexById.TryGetValue(diplo.ActorNationId, out var aIdx) && _nationIndexById.TryGetValue(diplo.TargetNationId, out var tIdx) && aIdx != tIdx:
                    ExecuteDiplomaticAction(aIdx, tIdx, diplo.Action);
                    break;
            }
        }
    }

    private void TryExpandBuilding(int stateIdx, int buildingIdx, int levels, int bCount)
    {
        if (levels <= 0)
            return;

        ref var slot = ref _buildingSlots[stateIdx * bCount + buildingIdx];
        var maxCap = _buildings[buildingIdx].Category == "rural"
            ? Math.Max(4, _states[stateIdx].ArableLand / 4)
            : 50;

        var target = Math.Min(maxCap, slot.Levels + levels);
        var added = target - slot.Levels;
        if (added <= 0)
            return;

        var ownerIdx = _nationIndexById[_states[stateIdx].OwnerNationId];
        var costCoppers = MonetaryAmount.FromGold(_buildings[buildingIdx].CostGold * added).CopperUnits;
        if (_nationTreasuryCoppers[ownerIdx] >= costCoppers)
        {
            _nationTreasuryCoppers[ownerIdx] -= costCoppers;
            slot.Levels = target;
            _stateDirtyFlags[stateIdx] = true;
        }
    }

    private void TryUpgradeProvince(int provinceIdx, string upgradeKind)
    {
        ref var slot = ref _provinceSlots[provinceIdx];
        var stateIdx = slot.StateIndex;
        var ownerIdx = _nationIndexById[_states[stateIdx].OwnerNationId];

        if (string.Equals(upgradeKind, "fort", StringComparison.OrdinalIgnoreCase))
        {
            if (slot.FortLevel >= 5)
                return;
            var costCoppers = MonetaryAmount.FromGold(180.0 + slot.FortLevel * 110.0).CopperUnits;
            if (_nationTreasuryCoppers[ownerIdx] < costCoppers)
                return;

            _nationTreasuryCoppers[ownerIdx] -= costCoppers;
            slot.FortLevel++;
            _stateDirtyFlags[stateIdx] = true;
            return;
        }

        if (slot.HoldingLevel >= 10)
            return;

        var holdingCost = MonetaryAmount.FromGold(220.0 + slot.HoldingLevel * 90.0).CopperUnits;
        if (_nationTreasuryCoppers[ownerIdx] < holdingCost)
            return;

        _nationTreasuryCoppers[ownerIdx] -= holdingCost;
        slot.HoldingLevel++;
        RecomputeStateHoldingThroughput(stateIdx);
        _stateDirtyFlags[stateIdx] = true;
    }

    private void RecomputeAllStateHoldingThroughput()
    {
        for (var s = 0; s < _states.Length; s++)
            RecomputeStateHoldingThroughput(s);
    }

    private void RecomputeStateHoldingThroughput(int stateIdx)
    {
        var sumExtra = 0;
        var count = 0;
        for (var p = 0; p < _provinceSlots.Length; p++)
        {
            if (_provinceSlots[p].StateIndex != stateIdx)
                continue;
            sumExtra += Math.Max(0, _provinceSlots[p].HoldingLevel - 1);
            count++;
        }
        var avgExtra = count > 0 ? (double)sumExtra / count : 0.0;
        _stateHoldingThroughput[stateIdx] = 1.0 + avgExtra * 0.06;
    }

    private void ExecuteCourtAction(int nationIdx, CourtActionKind action)
    {
        switch (action)
        {
            case CourtActionKind.RoyalBanquet:
                var banquetCost = MonetaryAmount.FromGold(400.0).CopperUnits;
                if (_nationTreasuryCoppers[nationIdx] >= banquetCost)
                {
                    _nationTreasuryCoppers[nationIdx] -= banquetCost;
                    _nationStress[nationIdx] = Math.Max(0, _nationStress[nationIdx] - 35);
                }
                break;

            case CourtActionKind.ResearchGrant:
                var grantCost = MonetaryAmount.FromGold(500.0).CopperUnits;
                if (_nationTreasuryCoppers[nationIdx] >= grantCost && _nationActiveTechId[nationIdx] is not null)
                {
                    _nationTreasuryCoppers[nationIdx] -= grantCost;
                    _nationTechPoints[nationIdx] += 30.0;
                    _nationStress[nationIdx] = Math.Min(300, _nationStress[nationIdx] + 5);
                    CheckTechCompletion(nationIdx);
                }
                break;

            case CourtActionKind.RaiseAuthority when _nationAuthority[nationIdx] < 3:
                _nationAuthority[nationIdx]++;
                _nationStress[nationIdx] = Math.Min(300, _nationStress[nationIdx] + 10);
                break;

            case CourtActionKind.LowerAuthority when _nationAuthority[nationIdx] > 0:
                _nationAuthority[nationIdx]--;
                _nationStress[nationIdx] = Math.Max(0, _nationStress[nationIdx] - 10);
                break;
        }
    }

    private void ExecuteDiplomaticAction(int aIdx, int tIdx, DiplomaticActionKind action)
    {
        var pair = _diplomacy[aIdx, tIdx];
        switch (action)
        {
            case DiplomaticActionKind.ImproveRelations when !pair.AtWar:
                var cost = MonetaryAmount.FromGold(150.0).CopperUnits;
                if (_nationTreasuryCoppers[aIdx] >= cost)
                {
                    _nationTreasuryCoppers[aIdx] -= cost;
                    pair.Opinion = Math.Clamp(pair.Opinion + 20, -100, 100);
                }
                break;

            case DiplomaticActionKind.ToggleTradePact when !pair.AtWar && pair.Opinion >= -10:
                pair.TradePact = !pair.TradePact;
                if (pair.TradePact)
                    pair.Opinion = Math.Clamp(pair.Opinion + 10, -100, 100);
                break;

            case DiplomaticActionKind.ToggleAlliance when !pair.AtWar && (pair.Alliance || pair.Opinion >= 35):
                pair.Alliance = !pair.Alliance;
                break;

            case DiplomaticActionKind.DeclareWar when !pair.AtWar:
                pair.AtWar = true;
                pair.TradePact = false;
                pair.Alliance = false;
                pair.WarScore = 0.0;
                pair.Opinion = Math.Clamp(pair.Opinion - 60, -100, 100);
                _nationStress[aIdx] = Math.Min(300, _nationStress[aIdx] + 12);
                break;

            case DiplomaticActionKind.MakePeace when pair.AtWar:
                SettleWarPeace(aIdx, tIdx, ref pair);
                break;
        }

        _diplomacy[aIdx, tIdx] = pair;
        _diplomacy[tIdx, aIdx] = new DiplomacyPair
        {
            Opinion = pair.Opinion,
            TradePact = pair.TradePact,
            Alliance = pair.Alliance,
            AtWar = pair.AtWar,
            WarScore = -pair.WarScore
        };
    }

    private void SettleWarPeace(int winnerSideA, int otherSideB, ref DiplomacyPair pairAtoB)
    {
        var reparations = MonetaryAmount.FromGold(1200.0).CopperUnits;
        if (pairAtoB.WarScore > 15.0)
        {
            var transfer = Math.Min(_nationTreasuryCoppers[otherSideB], reparations);
            _nationTreasuryCoppers[otherSideB] -= transfer;
            _nationTreasuryCoppers[winnerSideA] += transfer;
        }
        else if (pairAtoB.WarScore < -15.0)
        {
            var transfer = Math.Min(_nationTreasuryCoppers[winnerSideA], reparations);
            _nationTreasuryCoppers[winnerSideA] -= transfer;
            _nationTreasuryCoppers[otherSideB] += transfer;
        }

        pairAtoB.AtWar = false;
        pairAtoB.WarScore = 0.0;
        ClearWarOccupation(winnerSideA, otherSideB);
    }

    private void ExecuteSubTick6h()
    {
        TotalTicksExecuted++;
        CurrentDate = CurrentDate.AdvanceOneSubTick();

        if (CurrentDate.SubTick6h != 0)
            return;

        ExecuteStaggeredDailyStateStep(CurrentDate.Day % 7);

        if (CurrentDate.Day % 7 == 0)
        {
            ExecuteWeeklyEconomyTick();
            ExecuteWeeklyWarAndTechTick();
        }

        if (CurrentDate.Day == 1)
        {
            ExecuteMonthlyDemographicTick();
            ConsolidateMicroPops();
            RecomputeIgCloutForAllNations();
            ExecuteMonthlyLawAndJournalTick();
        }
    }

    private void ExecuteStaggeredDailyStateStep(int bucket)
    {
        for (var s = 0; s < _states.Length; s++)
        {
            if (s % 7 == bucket && _stateDirtyFlags[s])
                _stateDirtyFlags[s] = false;
        }
    }

    private void ExecuteWeeklyEconomyTick()
    {
        RecomputeMarketOrdersAndPrices();

        Array.Clear(_nationWeeklyBalanceCoppers, 0, _nationWeeklyBalanceCoppers.Length);
        var popSpan = CollectionsMarshal.AsSpan(_pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0)
                continue;

            var taxBaseGold = (p.Population / 10_000.0) * (0.45 + p.StandardOfLiving * 0.08);
            var taxGold = taxBaseGold * _nationTaxRates[p.NationIndex];
            _nationWeeklyBalanceCoppers[p.NationIndex] += MonetaryAmount.FromGold(taxGold).CopperUnits;
        }

        // Trade pacts yield small mutual customs income
        for (var a = 0; a < _nations.Length; a++)
        {
            for (var b = a + 1; b < _nations.Length; b++)
            {
                if (_diplomacy[a, b].TradePact && !_diplomacy[a, b].AtWar)
                {
                    var tradeBonus = MonetaryAmount.FromGold(45.0).CopperUnits;
                    _nationWeeklyBalanceCoppers[a] += tradeBonus;
                    _nationWeeklyBalanceCoppers[b] += tradeBonus;
                }
            }
        }

        var bCount = Math.Max(1, _buildings.Length);
        for (var s = 0; s < _states.Length; s++)
        {
            var ownerIdx = _nationIndexById[_states[s].OwnerNationId];
            for (var b = 0; b < _buildings.Length; b++)
            {
                var slot = _buildingSlots[s * bCount + b];
                if (slot.Levels <= 0)
                    continue;
                var upkeepGold = slot.Levels * _buildings[b].BaseUpkeepGold;
                _nationWeeklyBalanceCoppers[ownerIdx] -= MonetaryAmount.FromGold(upkeepGold).CopperUnits;
            }
        }

        for (var p = 0; p < _provinceSlots.Length; p++)
        {
            var pSlot = _provinceSlots[p];
            var ownerIdx = _nationIndexById[_states[pSlot.StateIndex].OwnerNationId];
            if (pSlot.OccupiedByNationIndex >= 0)
            {
                // Occupied province diverts modest requisition revenue to occupier
                _nationWeeklyBalanceCoppers[pSlot.OccupiedByNationIndex] += MonetaryAmount.FromGold(12.0 * pSlot.HoldingLevel).CopperUnits;
            }
            else if (pSlot.HoldingLevel > 1)
            {
                _nationWeeklyBalanceCoppers[ownerIdx] += MonetaryAmount.FromGold((pSlot.HoldingLevel - 1) * 18.0).CopperUnits;
            }
        }

        for (var n = 0; n < _nations.Length; n++)
        {
            _nationTreasuryCoppers[n] += _nationWeeklyBalanceCoppers[n];
        }
    }

    private void ExecuteWeeklyWarAndTechTick()
    {
        // Tech progression per nation
        for (var n = 0; n < _nations.Length; n++)
        {
            var activeTechId = _nationActiveTechId[n];
            if (activeTechId is null)
                continue;

            var unis = CountNationBuildingLevels(n, _universityBuildingIndex);
            var avgLit = ComputeNationAverageLiteracy(n);
            var weeklyPts = 4.0 + avgLit * 12.0 + unis * 5.0;
            _nationTechPoints[n] += weeklyPts;
            CheckTechCompletion(n);
        }

        // Weekly war score progression
        for (var a = 0; a < _nations.Length; a++)
        {
            for (var b = a + 1; b < _nations.Length; b++)
            {
                if (!_diplomacy[a, b].AtWar)
                    continue;

                var powerA = ComputeNationMilitaryPower(a);
                var powerB = ComputeNationMilitaryPower(b);
                var diff = (powerA - powerB) / Math.Max(10.0, powerA + powerB);
                var stepScore = Math.Clamp(diff * 28.0, -15.0, 15.0);
                if (Math.Abs(stepScore) < 1.5)
                    stepScore = powerA >= powerB ? 3.5 : -3.5;

                var pair = _diplomacy[a, b];
                pair.WarScore = Math.Clamp(pair.WarScore + stepScore, -100.0, 100.0);

                if (pair.WarScore >= 10.0)
                    AdvanceWarOccupation(winnerIdx: a, loserIdx: b, warScoreAbs: pair.WarScore);
                else if (pair.WarScore <= -10.0)
                    AdvanceWarOccupation(winnerIdx: b, loserIdx: a, warScoreAbs: -pair.WarScore);

                if (Math.Abs(pair.WarScore) >= 100.0)
                    SettleWarPeace(a, b, ref pair);

                _diplomacy[a, b] = pair;
                _diplomacy[b, a] = new DiplomacyPair
                {
                    Opinion = pair.Opinion,
                    TradePact = pair.TradePact,
                    Alliance = pair.Alliance,
                    AtWar = pair.AtWar,
                    WarScore = -pair.WarScore
                };
            }
        }
    }

    private void CheckTechCompletion(int nationIdx)
    {
        var activeTechId = _nationActiveTechId[nationIdx];
        if (activeTechId is null || !_techIndexById.TryGetValue(activeTechId, out var tIdx))
            return;

        if (_nationTechPoints[nationIdx] < _technologies[tIdx].CostPoints)
            return;

        _nationCompletedTechs[nationIdx].Add(activeTechId);
        _nationTechPoints[nationIdx] = 0.0;
        _nationActiveTechId[nationIdx] = null;

        // Auto-select next uncompleted tech in queue for seamless gameplay
        foreach (var candidate in _technologies)
        {
            if (!_nationCompletedTechs[nationIdx].Contains(candidate.Id))
            {
                _nationActiveTechId[nationIdx] = candidate.Id;
                break;
            }
        }
    }

    private void RecomputeMarketOrdersAndPrices()
    {
        Array.Clear(_nationalMarketBuyOrders, 0, _nationalMarketBuyOrders.Length);
        Array.Clear(_nationalMarketSellOrders, 0, _nationalMarketSellOrders.Length);

        var popSpan = CollectionsMarshal.AsSpan(_pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0)
                continue;

            var traits = DualAncestry.ComputeEffectiveTraits(p.Ancestry, _ancestries);
            var units = p.Population / 100_000.0;

            if (traits.RequiresBloodEssence && _bloodEssenceGoodIndex >= 0)
                _nationalMarketBuyOrders[_bloodEssenceGoodIndex] += units * 1.2;
            else if (_grainGoodIndex >= 0)
                _nationalMarketBuyOrders[_grainGoodIndex] += units * traits.FoodCalorieMultiplier;

            if (_woodGoodIndex >= 0)
                _nationalMarketBuyOrders[_woodGoodIndex] += units * 0.35;

            if (_ironGoodIndex >= 0 && p.ProfessionIndex >= 5)
                _nationalMarketBuyOrders[_ironGoodIndex] += units * 0.20;
        }

        var bCount = Math.Max(1, _buildings.Length);
        for (var s = 0; s < _states.Length; s++)
        {
            for (var b = 0; b < _buildings.Length; b++)
            {
                var slot = _buildingSlots[s * bCount + b];
                if (slot.Levels <= 0)
                    continue;

                var pm = _buildings[b].ProductionMethods[Math.Clamp(slot.ActivePmIndex, 0, _buildings[b].ProductionMethods.Length - 1)];
                foreach (var (goodId, amountPerLevel) in pm.Inputs)
                {
                    if (_goodIndexById.TryGetValue(goodId, out var gIdx))
                        _nationalMarketBuyOrders[gIdx] += slot.Levels * amountPerLevel;
                }
                foreach (var (goodId, amountPerLevel) in pm.Outputs)
                {
                    if (_goodIndexById.TryGetValue(goodId, out var gIdx))
                        _nationalMarketSellOrders[gIdx] += slot.Levels * amountPerLevel * _stateHoldingThroughput[s];
                }
            }

            if (_steelGoodIndex >= 0)
                _nationalMarketSellOrders[_steelGoodIndex] += 8.0;
            if (_bloodEssenceGoodIndex >= 0 && _states[s].OwnerNationId == "NEC")
                _nationalMarketSellOrders[_bloodEssenceGoodIndex] += 16.0;
        }

        for (var g = 0; g < _goods.Length; g++)
        {
            _lastMarketPrices[g] = _nationalMarketPrices[g];
            var nextPrice = MarketPricing.ComputePrice(
                _goods[g].BasePriceGold,
                _nationalMarketBuyOrders[g],
                _nationalMarketSellOrders[g]);

            _nationalMarketPrices[g] = nextPrice;
            if (Math.Abs(nextPrice - _lastMarketPrices[g]) > _lastMarketPrices[g] * 0.02)
                Array.Fill(_stateDirtyFlags, true);
        }
    }

    private void ExecuteMonthlyDemographicTick()
    {
        var rngJitter = (NextRandomDouble() - 0.5) * 0.0002;
        var popSpan = CollectionsMarshal.AsSpan(_pops);

        for (var i = 0; i < popSpan.Length; i++)
        {
            ref var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0)
                continue;

            var traits = DualAncestry.ComputeEffectiveTraits(p.Ancestry, _ancestries);
            var baseMonthlyRate = 0.0010;
            var effectiveRate = baseMonthlyRate * (1.0 + traits.MonthlyGrowthModifier) + rngJitter;

            var whiteDeathCured = _nationCompletedTechs[p.NationIndex].Contains("tech_hygiene_botany");
            if (traits.MonthlyGrowthModifier <= -0.30 && p.StandardOfLiving < 16f && !whiteDeathCured)
                effectiveRate = -0.0005;

            var delta = (long)Math.Round(p.Population * effectiveRate, MidpointRounding.AwayFromZero);
            p.Population = Math.Max(1, p.Population + delta);
        }
    }

    private void RecomputeIgCloutForAllNations()
    {
        var raw = new double[_nations.Length, UniversalIgs.Length];
        var popSpan = CollectionsMarshal.AsSpan(_pops);

        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0)
                continue;

            var units = p.Population / 100_000.0;
            var targetIg = p.ProfessionIndex switch
            {
                0 => 5,          // peasants -> Rural
                1 or 5 or 6 or 9 => 3, // laborers/machinists/artisans/engineers -> Guilds
                3 or 10 => 2,    // soldiers/officers -> Armed Forces
                7 => 1,          // clergy -> Clergy
                4 or 13 => 4,    // clerks/capitalists -> Merchants
                8 or 12 => 6,    // bureaucrats/academics -> Academics
                11 => 7,         // adventurers -> Dissidents
                14 => 0,         // aristocrats -> Aristocracy
                _ => 5
            };
            raw[p.NationIndex, targetIg] += units * (1.0 + p.StandardOfLiving * 0.05);
        }

        for (var n = 0; n < _nations.Length; n++)
        {
            double total = 0.0;
            for (var k = 0; k < UniversalIgs.Length; k++)
                total += Math.Max(0.1, raw[n, k]);

            for (var k = 0; k < UniversalIgs.Length; k++)
                _nationIgClout[n, k] = Math.Round((Math.Max(0.1, raw[n, k]) / total) * 100.0, 2);
        }
    }

    private void ExecuteMonthlyLawAndJournalTick()
    {
        for (var n = 0; n < _nations.Length; n++)
        {
            var enactingId = _nationEnactingLawId[n];
            if (enactingId is not null && _lawIndexById.TryGetValue(enactingId, out var lawIdx))
            {
                var step = 0.25 + _nationAuthority[n] * 0.05;
                _nationEnactingLawProgress[n] += step;

                if (_nationEnactingLawProgress[n] >= 1.0)
                {
                    var category = _laws[lawIdx].Category;
                    foreach (var existing in _laws)
                    {
                        if (existing.Category == category)
                            _nationEnactedLaws[n].Remove(existing.Id);
                    }
                    _nationEnactedLaws[n].Add(enactingId);
                    _nationEnactingLawId[n] = null;
                    _nationEnactingLawProgress[n] = 0.0;
                }
            }

            foreach (var je in _journalEntries)
            {
                if (je.NationId is not null && je.NationId != _nations[n].Id)
                    continue;
                if (_nationCompletedJournalEntries[n].Contains(je.Id))
                    continue;

                if (EvaluateJournalProgress(n, je) >= 1.0)
                {
                    _nationCompletedJournalEntries[n].Add(je.Id);
                    _nationTreasuryCoppers[n] += MonetaryAmount.FromGold(je.RewardGold).CopperUnits;
                }
            }
        }
    }

    private double EvaluateJournalProgress(int nationIdx, JournalEntryDefinition je) => je.ConditionType switch
    {
        "total_buildings" => Math.Clamp(CountAllNationBuildingLevels(nationIdx) / Math.Max(1.0, je.TargetValue), 0.0, 1.0),
        "tech_completed" => _nationCompletedTechs[nationIdx].Contains(je.TargetKey) ? 1.0 : 0.0,
        "treasury_gold" => Math.Clamp(new MonetaryAmount(_nationTreasuryCoppers[nationIdx]).ToGold() / Math.Max(1.0, je.TargetValue), 0.0, 1.0),
        _ => 0.0
    };

    private int ComputeIgApproval(int nationIdx, string igId)
    {
        var score = 0;
        foreach (var law in _laws)
        {
            if (!_nationEnactedLaws[nationIdx].Contains(law.Id))
                continue;
            if (law.SupportingIgIds.Contains(igId)) score += 3;
            if (law.OpposingIgIds.Contains(igId)) score -= 3;
        }
        if (_nationTaxRates[nationIdx] >= 0.20) score -= 2;
        if (_nationTaxRates[nationIdx] <= 0.10) score += 2;
        return Math.Clamp(score, -10, 10);
    }

    private double ComputeNationMilitaryPower(int nationIdx)
    {
        var barracks = CountNationBuildingLevels(nationIdx, _barracksBuildingIndex);
        double soldierScore = 10.0 + barracks * 12.0;

        var popSpan = CollectionsMarshal.AsSpan(_pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.NationIndex != nationIdx || p.ProfessionIndex != 3)
                continue;

            var traits = DualAncestry.ComputeEffectiveTraits(p.Ancestry, _ancestries);
            soldierScore += (p.Population / 100_000.0) * (1.0 + traits.MartialPowerModifier);
        }

        double techMult = 1.0;
        foreach (var tid in _nationCompletedTechs[nationIdx])
        {
            if (_techIndexById.TryGetValue(tid, out var tIdx))
                techMult += _technologies[tIdx].MilitaryBonus;
        }

        var fortPower = 0.0;
        var nationId = _nations[nationIdx].Id;
        for (var p = 0; p < _provinceSlots.Length; p++)
        {
            if (_states[_provinceSlots[p].StateIndex].OwnerNationId == nationId && _provinceSlots[p].OccupiedByNationIndex < 0)
                fortPower += _provinceSlots[p].FortLevel * 3.5;
        }

        return (soldierScore + fortPower) * techMult;
    }

    private void AdvanceWarOccupation(int winnerIdx, int loserIdx, double warScoreAbs)
    {
        var loserId = _nations[loserIdx].Id;
        var winnerId = _nations[winnerIdx].Id;

        // First liberate winner provinces previously held by loser
        for (var p = 0; p < _provinceSlots.Length; p++)
        {
            if (_states[_provinceSlots[p].StateIndex].OwnerNationId == winnerId &&
                _provinceSlots[p].OccupiedByNationIndex == loserIdx)
            {
                _provinceSlots[p].OccupiedByNationIndex = -1;
                return;
            }
        }

        // Then occupy one unoccupied loser province whose fort resistance is overcome by warScoreAbs
        var bestIdx = -1;
        var lowestResist = int.MaxValue;
        for (var p = 0; p < _provinceSlots.Length; p++)
        {
            var stIdx = _provinceSlots[p].StateIndex;
            if (_states[stIdx].OwnerNationId != loserId || _provinceSlots[p].OccupiedByNationIndex >= 0)
                continue;

            var resist = _provinceSlots[p].FortLevel * 18 + (_provinces[p].Def.IsCapital ? 22 : 0);
            if (warScoreAbs >= Math.Max(10.0, resist) && resist < lowestResist)
            {
                lowestResist = resist;
                bestIdx = p;
            }
        }

        if (bestIdx >= 0)
            _provinceSlots[bestIdx].OccupiedByNationIndex = winnerIdx;
    }

    private void ClearWarOccupation(int nationA, int nationB)
    {
        var idA = _nations[nationA].Id;
        var idB = _nations[nationB].Id;
        for (var p = 0; p < _provinceSlots.Length; p++)
        {
            var ownerId = _states[_provinceSlots[p].StateIndex].OwnerNationId;
            if ((ownerId == idA && _provinceSlots[p].OccupiedByNationIndex == nationB) ||
                (ownerId == idB && _provinceSlots[p].OccupiedByNationIndex == nationA))
            {
                _provinceSlots[p].OccupiedByNationIndex = -1;
            }
        }
    }

    private double ComputeNationAverageLiteracy(int nationIdx)
    {
        double weighted = 0.0;
        long total = 0;
        var popSpan = CollectionsMarshal.AsSpan(_pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.NationIndex != nationIdx)
                continue;
            weighted += p.Population * p.Literacy;
            total += p.Population;
        }
        return total > 0 ? weighted / total : 0.2;
    }

    private int CountNationBuildingLevels(int nationIdx, int buildingIdx)
    {
        if (buildingIdx < 0)
            return 0;

        var total = 0;
        var bCount = Math.Max(1, _buildings.Length);
        var nationId = _nations[nationIdx].Id;
        for (var s = 0; s < _states.Length; s++)
        {
            if (_states[s].OwnerNationId == nationId)
                total += _buildingSlots[s * bCount + buildingIdx].Levels;
        }
        return total;
    }

    private int CountAllNationBuildingLevels(int nationIdx)
    {
        var total = 0;
        var bCount = Math.Max(1, _buildings.Length);
        var nationId = _nations[nationIdx].Id;
        for (var s = 0; s < _states.Length; s++)
        {
            if (_states[s].OwnerNationId != nationId)
                continue;
            for (var b = 0; b < _buildings.Length; b++)
                total += _buildingSlots[s * bCount + b].Levels;
        }
        return total;
    }

    public int ConsolidateMicroPops()
    {
        var mergedCount = 0;
        var popSpan = CollectionsMarshal.AsSpan(_pops);

        for (var i = 0; i < popSpan.Length; i++)
        {
            ref var micro = ref popSpan[i];
            if (!micro.Active || micro.Population >= MicroPopConsolidationThreshold)
                continue;

            var bestMatch = -1;
            for (var j = 0; j < popSpan.Length; j++)
            {
                if (i == j) continue;
                ref readonly var cand = ref popSpan[j];
                if (!cand.Active || cand.StateIndex != micro.StateIndex)
                    continue;

                if (cand.ProfessionIndex == micro.ProfessionIndex && cand.Ancestry == micro.Ancestry)
                {
                    bestMatch = j;
                    break;
                }
                if (bestMatch < 0 && cand.ProfessionIndex == micro.ProfessionIndex)
                    bestMatch = j;
                else if (bestMatch < 0)
                    bestMatch = j;
            }

            if (bestMatch >= 0)
            {
                popSpan[bestMatch].Population += micro.Population;
                micro.Population = 0;
                micro.Active = false;
                mergedCount++;
            }
        }

        return mergedCount;
    }

    private double NextRandomDouble()
    {
        _rngState += 0x9E3779B97F4A7C15UL;
        var z = _rngState;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    private static Dictionary<string, int> BuildIndexMap<T>(T[] items, Func<T, string> keySelector)
    {
        var map = new Dictionary<string, int>(items.Length, StringComparer.Ordinal);
        for (var i = 0; i < items.Length; i++)
            map[keySelector(items[i])] = i;
        return map;
    }

    private static string ProfessionName(int idx) => idx switch
    {
        0 => "Camponeses",
        1 => "Trabalhadores Braçais",
        2 => "Servos / Thralls",
        3 => "Soldados & Guardas",
        4 => "Escrivães",
        5 => "Artífices & Operadores",
        6 => "Mestres de Guilda",
        7 => "Clérigos & Inquisidores",
        8 => "Burocratas",
        9 => "Engenheiros & Alquimistas",
        10 => "Oficiais",
        11 => "Aventureiros",
        12 => "Acadêmicos & Arcanistas",
        13 => "Mercadores & Capitalistas",
        14 => "Aristocratas",
        _ => "Trabalhadores"
    };

    private static int ProfessionIndex(string profession) => profession switch
    {
        "peasants" => 0,
        "laborers" => 1,
        "serfs_thralls" => 2,
        "soldiers" => 3,
        "clerks" => 4,
        "machinists" => 5,
        "artisans_shopkeepers" => 6,
        "clergy" => 7,
        "bureaucrats" => 8,
        "engineers" => 9,
        "officers" => 10,
        "adventurers" => 11,
        "academics" => 12,
        "capitalists" => 13,
        "aristocrats" => 14,
        _ => 1
    };
}
