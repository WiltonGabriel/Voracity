using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

public sealed record EditorPinMarker(
    string Id,
    float X,
    float Y,
    string Kind,
    string Note,
    string? TargetX = null,
    string? TargetY = null);

/// <summary>
/// Live editable cartographic &amp; territorial database used by the integrated Map Editor.
/// Keeps PlanarMapTopology (Habitable, Impassable, Corridor, SeaZone), States, Locations, and Persistent UIDs synchronized.
/// </summary>
public sealed class EditableWorldAtlas
{
    public EditableWorldAtlas(
        WorldDatabase db,
        PlanarMapTopology? prebuiltTopology = null)
    {
        Nations = db.Nations.ToList();
        States = db.States.ToList();
        Hierarchy = db.GeographyHierarchy;
        Features = db.GeographyFeatures;
        AdminProfiles = db.AdminProfiles.ToList();
        DynamicProvinceNames = db.DynamicProvinceNames.ToList();
        SpecialLocations = db.SpecialLocations.ToList();
        SeaZones = db.SeaZones.ToList();
        SourceDb = db;

        PersistentUidByProvinceId = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextUid = 1;
        foreach (var st in States)
        {
            foreach (var p in st.EnsureProvinceDetails())
            {
                var uid = p.PersistentUid > 0 ? p.PersistentUid : nextUid++;
                PersistentUidByProvinceId[p.Id] = uid;
                if (uid >= nextUid)
                    nextUid = uid + 1;
            }
        }

        foreach (var sp in SpecialLocations)
        {
            var uid = sp.PersistentUid > 0 ? sp.PersistentUid : nextUid++;
            PersistentUidByProvinceId[sp.Id] = uid;
            if (uid >= nextUid)
                nextUid = uid + 1;
        }

        foreach (var sz in SeaZones)
        {
            var uid = sz.PersistentUid > 0 ? sz.PersistentUid : nextUid++;
            PersistentUidByProvinceId[sz.Id] = uid;
            if (uid >= nextUid)
                nextUid = uid + 1;
        }

        NextPersistentUid = nextUid;

        Topology = prebuiltTopology ?? BuildTopologyFromCurrentState();
        EnsureProvincesCarryPersistentUids();
    }

    public WorldDatabase SourceDb { get; }
    public List<NationDefinition> Nations { get; }
    public List<StateDefinition> States { get; }
    public GeographyHierarchyData Hierarchy { get; set; }
    public GeographyFeaturesData Features { get; set; }
    public List<AdministrationProfile> AdminProfiles { get; }
    public List<DynamicProvinceNameRule> DynamicProvinceNames { get; }
    public List<SpecialLocationDefinition> SpecialLocations { get; }
    public List<SeaZoneDefinition> SeaZones { get; }
    public Dictionary<string, int> PersistentUidByProvinceId { get; }
    public int NextPersistentUid { get; internal set; }
    public PlanarMapTopology Topology { get; internal set; }
    public List<EditorPinMarker> Pins { get; } = new();

    public int AllocatePersistentUid(string provinceId)
    {
        if (PersistentUidByProvinceId.TryGetValue(provinceId, out var existing))
            return existing;
        var uid = NextPersistentUid++;
        PersistentUidByProvinceId[provinceId] = uid;
        return uid;
    }

    private PlanarMapTopology BuildTopologyFromCurrentState()
    {
        var db = ToWorldDatabase();
        var locations = db.BuildAllLocations();
        var corridorLinks = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var sp in SpecialLocations)
        {
            if (sp.Kind == LocationKind.Corridor && sp.ConnectedLocationIds.Length > 0)
                corridorLinks[sp.Id] = sp.ConnectedLocationIds;
        }
        return PlanarTopologyBuilder.BuildFromLocations(locations, Features, TopologyBuildOptions.Default, corridorLinks);
    }

    private void EnsureProvincesCarryPersistentUids()
    {
        for (var s = 0; s < States.Count; s++)
        {
            var st = States[s];
            var details = st.EnsureProvinceDetails();
            var updated = new ProvinceDefinition[details.Length];
            var changed = false;
            for (var p = 0; p < details.Length; p++)
            {
                var uid = AllocatePersistentUid(details[p].Id);
                if (details[p].PersistentUid != uid)
                {
                    updated[p] = details[p] with { PersistentUid = uid };
                    changed = true;
                }
                else
                {
                    updated[p] = details[p];
                }
            }
            if (changed)
                States[s] = st with { ProvinceDetails = updated };
        }
    }

    public IReadOnlyList<ProvinceSnapshot> BuildProvinceSnapshots()
    {
        var list = new List<ProvinceSnapshot>(States.Sum(s => s.Provinces.Length));
        foreach (var st in States)
        {
            var details = st.EnsureProvinceDetails();
            foreach (var pd in details)
            {
                var uid = AllocatePersistentUid(pd.Id);
                var coastal = Topology?.FaceByProvinceId.TryGetValue(pd.Id, out var f) == true ? f.IsCoastal : pd.IsCoastal;
                list.Add(new ProvinceSnapshot(
                    NumericalIndex: uid,
                    ProvinceId: pd.Id,
                    Name: pd.Name,
                    StateId: st.Id,
                    OwnerNationId: st.OwnerNationId,
                    Terrain: pd.Terrain,
                    HubType: pd.HubType,
                    IsCapital: pd.IsCapital,
                    IsCoastal: coastal,
                    AnchorX: (float)pd.AnchorX,
                    AnchorY: (float)pd.AnchorY,
                    HoldingLevel: pd.IsCapital ? 2 : 1,
                    FortLevel: pd.StartingFortLevel,
                    ResourcePotential: pd.ResourcePotential,
                    OccupiedByNationId: null,
                    Kind: pd.Kind,
                    Rank: pd.Rank,
                    Topography: pd.Topography,
                    Vegetation: pd.Vegetation,
                    Climate: pd.Climate));
            }
        }
        return list;
    }

    public void SyncStateAndProvinceDerivedMetadata()
    {
        var adjStatesByState = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var st in States)
            adjStatesByState[st.Id] = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (sa, sb) in Topology.SymmetricStateAdjacencies)
        {
            if (adjStatesByState.TryGetValue(sa, out var setA))
                setA.Add(sb);
            if (adjStatesByState.TryGetValue(sb, out var setB))
                setB.Add(sa);
        }

        for (var s = 0; s < States.Count; s++)
        {
            var st = States[s];
            var details = st.EnsureProvinceDetails();
            var updatedProvs = new ProvinceDefinition[details.Length];
            for (var p = 0; p < details.Length; p++)
            {
                var d = details[p];
                var coastal = Topology.FaceByProvinceId.TryGetValue(d.Id, out var face)
                    ? face.IsCoastal
                    : d.IsCoastal;
                updatedProvs[p] = d with
                {
                    IsCoastal = coastal,
                    PersistentUid = AllocatePersistentUid(d.Id)
                };
            }

            var adjArr = adjStatesByState.TryGetValue(st.Id, out var set)
                ? set.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

            States[s] = st with
            {
                AdjacentStateIds = adjArr,
                Provinces = updatedProvs.Select(x => x.Name).ToArray(),
                ProvinceDetails = updatedProvs
            };
        }
    }

    public void RebuildTopologyFromAnchors()
    {
        var existingTombstones = Topology.Tombstones.ToList();
        Topology = BuildTopologyFromCurrentState();
        Topology.Tombstones.AddRange(existingTombstones);
        SyncStateAndProvinceDerivedMetadata();
    }

    public WorldDatabase ToWorldDatabase()
        => SourceDb with
        {
            Nations = Nations.ToArray(),
            States = States.ToArray(),
            GeographyHierarchy = Hierarchy,
            GeographyFeatures = Features,
            AdminProfiles = AdminProfiles.ToArray(),
            DynamicProvinceNames = DynamicProvinceNames.ToArray(),
            SpecialLocations = SpecialLocations.ToArray(),
            SeaZones = SeaZones.ToArray()
        };
}
