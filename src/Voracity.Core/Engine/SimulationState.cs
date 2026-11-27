using Voracity.Core.Data;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Core.Engine;

public struct LocationRuntime
{
    public int LocationIndex;
    public int PersistentUid;
    public int StateIndex;
    public int ProvinceFoodIndex;
    public int OwnerNationIndex;
    public int MarketIndex;
    public int RawGoodIndex;

    public LocationKind Kind;
    public LocationRank Rank;
    public TopographyType Topography;
    public VegetationType Vegetation;
    public ClimateType Climate;
    public RgoMethod RgoMethod;
    public RoadLevel RoadLevel;
    public IntegrationStatus IntegrationStatus;

    public int NaturalHarborSuitability;
    public double Development;
    public double Proximity;
    public double MaxControl;
    public double Control;
    public double MarketAccess;
    public double SeaZoneMaritimePresence;
    public double Prosperity;

    public bool IsCoastal;
    public bool IsCapital;
    public bool IsMarketCenter;

    public int RgoLevel;
    public int MaxRgoLevel;
    public double RgoEmploymentRatio;
    public double RgoProfit;
    public double BuildingProfit;
    public double PotentialTaxBase;
    public double EffectiveTaxBase;

    public double LocalFoodProduced;
    public double LocalFoodConsumed;
    public long TotalPopulation;
    public long PopulationCapacity;
}

public struct ProvinceFoodRuntime
{
    public int ProvinceIndex;
    public int StateIndex;
    public int PrimaryMarketIndex;
    public double FoodStorage;
    public double MaxFoodStorage;
    public double NetMonthlyFood;
    public bool IsStarving;
}

public struct MarketRuntime
{
    public int MarketIndex;
    public int CenterLocationIndex;
    public double BaseAttraction;
    public int LocationCount;
    public double AccessSum;
}

public struct NationRuntime
{
    public int NationIndex;
    public int CapitalLocationIndex;
    public long TreasuryCoppers;
    public long MonthlyBalanceCoppers;
    public double TaxRate;
    public double CostOfCourtPercent;
    public double GovernmentPower;
    public double Stability;
    public double Prestige;
    public bool ProximityDirty;
}

public struct LocationBuildingSlot
{
    public int LocationIndex;
    public int BuildingIndex;
    public int Levels;
    public int ActivePmIndex;
}

public sealed class SimulationState
{
    public readonly WorldDatabase Db;
    public readonly AncestryDefinition[] Ancestries;
    public readonly GoodDefinition[] Goods;
    public readonly NationDefinition[] Nations;
    public readonly StateDefinition[] States;
    public readonly BuildingDefinition[] Buildings;
    public readonly MarketDefinition[] MarketDefs;
    public readonly LocationDefinition[] LocationDefs;
    public readonly PlanarMapTopology Topology;

    public readonly Dictionary<string, int> AncestryIndexById;
    public readonly Dictionary<string, int> GoodIndexById;
    public readonly Dictionary<string, int> NationIndexById;
    public readonly Dictionary<string, int> StateIndexById;
    public readonly Dictionary<string, int> BuildingIndexById;
    public readonly Dictionary<string, int> MarketIndexById;
    public readonly Dictionary<string, int> LocationIndexById;
    public readonly Dictionary<int, int> LocationIndexByFaceId;
    public readonly Dictionary<string, AdministrationProfile> AdminProfileByKey;
    public readonly Dictionary<string, string> DynamicNameByKey;

    public readonly LocationRuntime[] Locations;
    public readonly ProvinceFoodRuntime[] Provinces;
    public readonly MarketRuntime[] Markets;
    public readonly NationRuntime[] NationsRuntime;
    public readonly LocationBuildingSlot[] BuildingSlots;
    public readonly List<PopRecord> Pops = new(1024);

    // Flattened market matrices indexed [mIdx * Goods.Length + gIdx]
    public readonly double[] MarketPrices;
    public readonly double[] MarketSupply;
    public readonly double[] MarketDemand;

    // Pre-allocated scratch buffers for zero-GC tick evaluation
    public readonly int[] HabitableLocationOrderByAccess;
    public readonly int HabitableCount;
    public readonly double[] EstateWeightedShares;
    public readonly double[] EstateTaxYieldGold;
    public readonly double[] NationMonthlyTaxBase;
    public readonly double[] NationMonthlyIncomeGold;
    public readonly double[] NationMonthlyUpkeepGold;

    public readonly int GrainGoodIndex;
    public readonly int LumberGoodIndex;
    public readonly int ToolsGoodIndex;
    public readonly int BloodEssenceGoodIndex;
    public readonly int WheatFarmBuildingIndex;
    public readonly int ToolingBuildingIndex;
    public readonly int UniversityBuildingIndex;

    public readonly IEstateTaxHook EstateHook = new DefaultEstateTaxHook();
    public readonly SplitMix64Rng Rng;
    public readonly SimulationClock Clock = new();
    public string PlayerNationId { get; set; }

    public SimulationState(WorldDatabase db, string playerNationId, ulong seed, PlanarMapTopology? prebuiltTopology = null)
    {
        Db = db;
        Rng = new SplitMix64Rng(seed);
        Ancestries = db.Ancestries.ToArray();
        Goods = db.Goods.ToArray();
        Nations = db.Nations.ToArray();
        States = db.States.ToArray();
        Buildings = db.Buildings.ToArray();
        MarketDefs = db.Markets.Count > 0
            ? db.Markets.ToArray()
            : [new MarketDefinition("mkt_default", "market.default", "", "#D4A338", 1.0)];

        AncestryIndexById = BuildMap(Ancestries, x => x.Id);
        GoodIndexById = BuildMap(Goods, x => x.Id);
        NationIndexById = BuildMap(Nations, x => x.Id);
        StateIndexById = BuildMap(States, x => x.Id);
        BuildingIndexById = BuildMap(Buildings, x => x.Id);
        MarketIndexById = BuildMap(MarketDefs, x => x.Id);

        PlayerNationId = NationIndexById.ContainsKey(playerNationId) ? playerNationId : Nations[0].Id;

        LocationDefs = db.BuildAllLocations().ToArray();
        LocationIndexById = new Dictionary<string, int>(LocationDefs.Length, StringComparer.Ordinal);
        LocationIndexByFaceId = new Dictionary<int, int>(LocationDefs.Length);

        var nextUid = 1;
        for (var i = 0; i < LocationDefs.Length; i++)
        {
            if (LocationDefs[i].PersistentUid >= nextUid)
                nextUid = LocationDefs[i].PersistentUid + 1;
        }
        for (var i = 0; i < LocationDefs.Length; i++)
        {
            var loc = LocationDefs[i];
            if (loc.PersistentUid <= 0)
            {
                loc = loc with { PersistentUid = nextUid++ };
                LocationDefs[i] = loc;
            }
            LocationIndexById[loc.Id] = i;
            LocationIndexByFaceId[loc.PersistentUid] = i;
        }

        var corridorLinks = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var sp in db.SpecialLocations)
        {
            if (sp.Kind == LocationKind.Corridor && sp.ConnectedLocationIds.Length > 0)
                corridorLinks[sp.Id] = sp.ConnectedLocationIds;
        }

        Topology = prebuiltTopology ?? PlanarTopologyBuilder.BuildFromLocations(LocationDefs, db.GeographyFeatures, TopologyBuildOptions.Default, corridorLinks);

        AdminProfileByKey = new Dictionary<string, AdministrationProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var ap in db.AdminProfiles)
            AdminProfileByKey[ap.CultureTag] = ap;

        DynamicNameByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dyn in db.DynamicProvinceNames)
            DynamicNameByKey[$"{dyn.ProvinceId}|{dyn.CultureOrNationId}"] = dyn.LocalizedName;
        foreach (var ap in db.AdminProfiles)
        {
            if (ap.DynamicProvinceNames is null) continue;
            foreach (var dyn in ap.DynamicProvinceNames)
                DynamicNameByKey[$"{dyn.ProvinceId}|{dyn.CultureOrNationId}"] = dyn.LocalizedName;
        }

        GrainGoodIndex = GoodIndexById.GetValueOrDefault("grain", -1);
        LumberGoodIndex = GoodIndexById.GetValueOrDefault("wood", -1);
        ToolsGoodIndex = GoodIndexById.GetValueOrDefault("tools", -1);
        BloodEssenceGoodIndex = GoodIndexById.GetValueOrDefault("blood_essence", -1);
        WheatFarmBuildingIndex = BuildingIndexById.GetValueOrDefault("building_wheat_farms", -1);
        ToolingBuildingIndex = BuildingIndexById.GetValueOrDefault("building_tooling_workshops", -1);
        UniversityBuildingIndex = BuildingIndexById.GetValueOrDefault("building_university", -1);

        Locations = new LocationRuntime[LocationDefs.Length];
        Provinces = new ProvinceFoodRuntime[States.Length];
        Markets = new MarketRuntime[MarketDefs.Length];
        NationsRuntime = new NationRuntime[Nations.Length];

        var bCount = Math.Max(1, Buildings.Length);
        BuildingSlots = new LocationBuildingSlot[LocationDefs.Length * bCount];

        var totalMatrix = Markets.Length * Math.Max(1, Goods.Length);
        MarketPrices = new double[totalMatrix];
        MarketSupply = new double[totalMatrix];
        MarketDemand = new double[totalMatrix];
        for (var m = 0; m < Markets.Length; m++)
        {
            for (var g = 0; g < Goods.Length; g++)
                MarketPrices[m * Goods.Length + g] = Goods[g].BasePriceGold;
        }

        EstateWeightedShares = new double[Nations.Length * 6];
        EstateTaxYieldGold = new double[Nations.Length * 6];
        NationMonthlyTaxBase = new double[Nations.Length];
        NationMonthlyIncomeGold = new double[Nations.Length];
        NationMonthlyUpkeepGold = new double[Nations.Length];

        InitializeNations();
        var habList = InitializeLocationsProvincesMarketsAndBuildings(bCount);
        HabitableLocationOrderByAccess = habList.ToArray();
        HabitableCount = HabitableLocationOrderByAccess.Length;
        InitializePops();
    }

    private void InitializeNations()
    {
        for (var n = 0; n < Nations.Length; n++)
        {
            var nd = Nations[n];
            var capIdx = -1;
            if (StateIndexById.TryGetValue(nd.CapitalStateId, out var capStateIdx))
            {
                var details = States[capStateIdx].EnsureProvinceDetails();
                if (details.Length > 0)
                {
                    var capDef = details.FirstOrDefault(p => p.IsCapital) ?? details[0];
                    LocationIndexById.TryGetValue(capDef.Id, out capIdx);
                }
            }

            NationsRuntime[n] = new NationRuntime
            {
                NationIndex = n,
                CapitalLocationIndex = capIdx,
                TreasuryCoppers = MonetaryAmount.FromGold(nd.StartingTreasuryGold).CopperUnits,
                TaxRate = 0.20,
                CostOfCourtPercent = Math.Clamp(nd.StartingCourtSpendingPercent, 0.0, 100.0),
                GovernmentPower = 60.0 + nd.AuthorityLevel * 5.0,
                Stability = 25.0,
                Prestige = 20.0,
                ProximityDirty = true
            };
        }
    }

    private List<int> InitializeLocationsProvincesMarketsAndBuildings(int bCount)
    {
        var habIndices = new List<int>(LocationDefs.Length);

        for (var m = 0; m < MarketDefs.Length; m++)
        {
            var centerIdx = LocationIndexById.GetValueOrDefault(MarketDefs[m].CenterLocationId, -1);
            Markets[m] = new MarketRuntime
            {
                MarketIndex = m,
                CenterLocationIndex = centerIdx,
                BaseAttraction = MarketDefs[m].BaseAttraction
            };
        }

        for (var s = 0; s < States.Length; s++)
        {
            Provinces[s] = new ProvinceFoodRuntime
            {
                ProvinceIndex = s,
                StateIndex = s,
                PrimaryMarketIndex = 0,
                FoodStorage = 300.0,
                MaxFoodStorage = 600.0
            };
        }

        for (var i = 0; i < LocationDefs.Length; i++)
        {
            var def = LocationDefs[i];
            var stateIdx = !string.IsNullOrEmpty(def.AreaId) ? StateIndexById.GetValueOrDefault(def.AreaId, -1) : -1;
            var ownerIdx = !string.IsNullOrEmpty(def.OwnerNationId) ? NationIndexById.GetValueOrDefault(def.OwnerNationId, -1) : -1;
            var marketIdx = !string.IsNullOrEmpty(def.MarketId) ? MarketIndexById.GetValueOrDefault(def.MarketId, 0) : 0;
            var rawGoodIdx = !string.IsNullOrEmpty(def.RawGoodId) ? GoodIndexById.GetValueOrDefault(def.RawGoodId, GrainGoodIndex) : -1;
            var coastal = Topology.FaceById.TryGetValue(def.PersistentUid, out var face) && face.IsCoastal;

            var initRgo = def.Kind == LocationKind.Habitable
                ? (def.Rank >= LocationRank.City ? 3 : def.Rank == LocationRank.Town ? 2 : 2)
                : 0;

            Locations[i] = new LocationRuntime
            {
                LocationIndex = i,
                PersistentUid = def.PersistentUid,
                StateIndex = stateIdx,
                ProvinceFoodIndex = stateIdx,
                OwnerNationIndex = ownerIdx,
                MarketIndex = marketIdx,
                RawGoodIndex = rawGoodIdx,
                Kind = def.Kind,
                Rank = def.Rank,
                Topography = def.Topography,
                Vegetation = def.Vegetation,
                Climate = def.Climate,
                RgoMethod = def.RgoMethod,
                RoadLevel = def.RoadLevel,
                IntegrationStatus = IntegrationStatus.Core,
                NaturalHarborSuitability = def.NaturalHarborSuitability,
                Development = def.Development,
                Proximity = def.IsCapital ? 100.0 : 40.0,
                MaxControl = def.IsCapital ? 100.0 : 50.0,
                Control = def.IsCapital ? 95.0 : 45.0,
                MarketAccess = 1.0,
                SeaZoneMaritimePresence = def.SeaZoneMaritimePresence,
                Prosperity = 0.50,
                IsCoastal = coastal,
                IsCapital = def.IsCapital,
                IsMarketCenter = def.IsMarketCenter,
                RgoLevel = initRgo,
                MaxRgoLevel = 4,
                RgoEmploymentRatio = 1.0
            };

            if (def.Kind == LocationKind.Habitable)
            {
                habIndices.Add(i);
                if (stateIdx >= 0 && def.IsCapital)
                    Provinces[stateIdx].PrimaryMarketIndex = marketIdx;

                for (var b = 0; b < Buildings.Length; b++)
                {
                    var bid = Buildings[b].Id;
                    var lvls = bid switch
                    {
                        "building_wheat_farms" => def.RawGoodId == "grain" || def.Rank == LocationRank.RuralSettlement ? 1 : 0,
                        "building_tooling_workshops" => def.Rank >= LocationRank.Town ? 1 : 0,
                        "building_university" => def.Rank >= LocationRank.City ? 1 : 0,
                        "building_logging_camps" => def.Vegetation is VegetationType.Forest or VegetationType.Woods ? 1 : 0,
                        _ => 0
                    };
                    BuildingSlots[i * bCount + b] = new LocationBuildingSlot
                    {
                        LocationIndex = i,
                        BuildingIndex = b,
                        Levels = lvls,
                        ActivePmIndex = 0
                    };
                }
            }
        }

        // Set initial province food capacity from member location ranks
        for (var s = 0; s < Provinces.Length; s++)
        {
            double cap = 400.0;
            foreach (var lIdx in habIndices)
            {
                if (Locations[lIdx].ProvinceFoodIndex != s)
                    continue;
                cap += Locations[lIdx].Rank switch
                {
                    LocationRank.Town => 250.0,
                    LocationRank.City => 500.0,
                    LocationRank.Megalopolis => 1000.0,
                    _ => 100.0
                };
            }
            Provinces[s].MaxFoodStorage = cap;
            Provinces[s].FoodStorage = cap * 0.55;
        }

        return habIndices;
    }

    private void InitializePops()
    {
        var stateLocationLists = new List<int>[States.Length];
        for (var s = 0; s < States.Length; s++)
            stateLocationLists[s] = new List<int>(4);

        for (var i = 0; i < Locations.Length; i++)
        {
            if (Locations[i].Kind == LocationKind.Habitable && Locations[i].ProvinceFoodIndex >= 0)
                stateLocationLists[Locations[i].ProvinceFoodIndex].Add(i);
        }

        for (var s = 0; s < States.Length; s++)
        {
            var st = States[s];
            var locs = stateLocationLists[s];
            if (locs.Count == 0)
                continue;

            var ownerIdx = NationIndexById.GetValueOrDefault(st.OwnerNationId, 0);
            var weights = new double[locs.Count];
            var totalW = 0.0;
            for (var li = 0; li < locs.Count; li++)
            {
                var l = Locations[locs[li]];
                var w = l.Rank switch
                {
                    LocationRank.Megalopolis => 3.5,
                    LocationRank.City => 2.2,
                    LocationRank.Town => 1.5,
                    _ => 1.0
                };
                weights[li] = w;
                totalW += w;
            }

            foreach (var spec in st.InitialPops)
            {
                var primaryIdx = AncestryIndexById[spec.PrimaryAncestry];
                var secondaryIdx = spec.SecondaryAncestry is not null && AncestryIndexById.TryGetValue(spec.SecondaryAncestry, out var sec)
                    ? sec
                    : -1;
                var cls = SocialClassRules.ParseSocialClass(spec.Profession);

                for (var li = 0; li < locs.Count; li++)
                {
                    var sharePop = Math.Max(50L, (long)Math.Round(spec.Population * (weights[li] / totalW)));
                    Pops.Add(new PopRecord
                    {
                        LocationIndex = locs[li],
                        StateIndex = s,
                        NationIndex = ownerIdx,
                        Class = cls,
                        Ancestry = new DualAncestry(primaryIdx, secondaryIdx),
                        Population = sharePop,
                        Literacy = (float)Math.Clamp(spec.Literacy, 0.01, 1.0),
                        Satisfaction = 72.0f,
                        Active = true
                    });
                }
            }

            // Ensure every Habitable location has initial Laborers + Nobles/Clerics/Burghers headroom if Urban
            for (var li = 0; li < locs.Count; li++)
            {
                var lIdx = locs[li];
                var hasLaborers = false;
                for (var p = 0; p < Pops.Count; p++)
                {
                    if (Pops[p].LocationIndex == lIdx && Pops[p].Class == SocialClass.Laborers)
                    {
                        hasLaborers = true;
                        break;
                    }
                }
                if (!hasLaborers)
                {
                    var ancIdx = AncestryIndexById.GetValueOrDefault(Nations[ownerIdx].PrimaryAncestryId, 0);
                    Pops.Add(new PopRecord
                    {
                        LocationIndex = lIdx,
                        StateIndex = s,
                        NationIndex = ownerIdx,
                        Class = SocialClass.Laborers,
                        Ancestry = new DualAncestry(ancIdx, -1),
                        Population = Math.Max(1500, Locations[lIdx].RgoLevel * 1000L),
                        Literacy = 0.20f,
                        Satisfaction = 75.0f,
                        Active = true
                    });
                }
            }
        }
    }

    private static Dictionary<string, int> BuildMap<T>(T[] items, Func<T, string> selector)
    {
        var dict = new Dictionary<string, int>(items.Length, StringComparer.Ordinal);
        for (var i = 0; i < items.Length; i++)
            dict[selector(items[i])] = i;
        return dict;
    }
}
