using System.Text.Json;
using System.Text.Json.Serialization;
using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

public sealed record AtlasSnapshotRecord(
    string VersionId,
    string Label,
    long TimestampUnixSeconds,
    NationDefinition[] Nations,
    StateDefinition[] States,
    GeographyHierarchyData Hierarchy,
    GeographyFeaturesData Features,
    EditorPinMarker[] Pins,
    ProvinceTombstone[] Tombstones);

public sealed record AtlasDeltaSummary(
    string GeneratedUtc,
    string BaselineVersionId,
    int TotalStates,
    int TotalProvinces,
    string[] AddedProvinces,
    string[] RemovedProvinces,
    string[] OwnerChangedStates,
    int TotalRivers,
    int TotalMountains,
    int TotalStraits,
    EditorPinMarker[] Pins,
    ProvinceTombstone[] Tombstones);

/// <summary>
/// Manages versioned snapshots, Onion-Skin diffing, JSON persistence into data/base/,
/// and structured AI-assist _delta.json exports from the Map Editor.
/// </summary>
public static class AtlasVersioning
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AtlasSnapshotRecord CaptureSnapshot(EditableWorldAtlas atlas, string label)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new AtlasSnapshotRecord(
            VersionId: $"v_{stamp}_{Guid.NewGuid():N}"[..20],
            Label: label,
            TimestampUnixSeconds: stamp,
            Nations: atlas.Nations.ToArray(),
            States: atlas.States.ToArray(),
            Hierarchy: atlas.Hierarchy,
            Features: atlas.Features,
            Pins: atlas.Pins.ToArray(),
            Tombstones: atlas.Topology.Tombstones.ToArray());
    }

    public static void RestoreSnapshot(EditableWorldAtlas atlas, AtlasSnapshotRecord snapshot)
    {
        atlas.Nations.Clear();
        atlas.Nations.AddRange(snapshot.Nations);
        atlas.States.Clear();
        atlas.States.AddRange(snapshot.States);
        atlas.Hierarchy = snapshot.Hierarchy;
        atlas.Features = snapshot.Features;
        atlas.Pins.Clear();
        atlas.Pins.AddRange(snapshot.Pins);

        atlas.PersistentUidByProvinceId.Clear();
        var maxUid = 1;
        foreach (var st in atlas.States)
        {
            foreach (var p in st.EnsureProvinceDetails())
            {
                var uid = p.PersistentUid > 0 ? p.PersistentUid : maxUid;
                atlas.PersistentUidByProvinceId[p.Id] = uid;
                maxUid = Math.Max(maxUid, uid + 1);
            }
        }
        atlas.NextPersistentUid = maxUid;
        atlas.RebuildTopologyFromAnchors();
        atlas.Topology.Tombstones.Clear();
        atlas.Topology.Tombstones.AddRange(snapshot.Tombstones);
    }

    public static string SaveVersionFile(string versionsDir, AtlasSnapshotRecord snapshot)
    {
        Directory.CreateDirectory(versionsDir);
        var path = Path.Combine(versionsDir, $"{snapshot.VersionId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, PrettyJsonOptions));
        return path;
    }

    public static IReadOnlyList<AtlasSnapshotRecord> ListSavedVersions(string versionsDir)
    {
        if (!Directory.Exists(versionsDir))
            return Array.Empty<AtlasSnapshotRecord>();

        var result = new List<AtlasSnapshotRecord>();
        foreach (var file in Directory.EnumerateFiles(versionsDir, "*.json").OrderByDescending(x => x, StringComparer.Ordinal))
        {
            try
            {
                var snap = JsonSerializer.Deserialize<AtlasSnapshotRecord>(File.ReadAllText(file), PrettyJsonOptions);
                if (snap is not null)
                    result.Add(snap);
            }
            catch
            {
                // Skip unreadable user file
            }
        }
        return result;
    }

    public static AtlasDeltaSummary ComputeDelta(AtlasSnapshotRecord baseline, EditableWorldAtlas current)
    {
        var baseProvs = baseline.States.SelectMany(s => s.EnsureProvinceDetails()).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var currProvs = current.States.SelectMany(s => s.EnsureProvinceDetails()).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        var added = currProvs.Except(baseProvs).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var removed = baseProvs.Except(currProvs).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var baseOwner = baseline.States.ToDictionary(s => s.Id, s => s.OwnerNationId, StringComparer.Ordinal);
        var ownerChanged = current.States
            .Where(s => baseOwner.TryGetValue(s.Id, out var oldOwner) && oldOwner != s.OwnerNationId)
            .Select(s => $"{s.Id}: {baseOwner[s.Id]} -> {s.OwnerNationId}")
            .ToArray();

        return new AtlasDeltaSummary(
            GeneratedUtc: DateTimeOffset.UtcNow.ToString("O"),
            BaselineVersionId: baseline.VersionId,
            TotalStates: current.States.Count,
            TotalProvinces: currProvs.Count,
            AddedProvinces: added,
            RemovedProvinces: removed,
            OwnerChangedStates: ownerChanged,
            TotalRivers: current.Features.Rivers.Length,
            TotalMountains: current.Features.MountainRanges.Length,
            TotalStraits: current.Features.Straits.Length,
            Pins: current.Pins.ToArray(),
            Tombstones: current.Topology.Tombstones.ToArray());
    }

    public static void SaveDeltaJson(string outputPath, AtlasDeltaSummary delta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(delta, PrettyJsonOptions));
    }

    /// <summary>
    /// Persists edited Nations, States/Provinces, GeographyHierarchy, GeographyFeatures, and AdminProfiles back to data/base/.
    /// </summary>
    public static void SaveAtlasToDataRoot(EditableWorldAtlas atlas, string baseDataDir)
    {
        atlas.SyncStateAndProvinceDerivedMetadata();
        Directory.CreateDirectory(baseDataDir);

        File.WriteAllText(
            Path.Combine(baseDataDir, "nations.json"),
            JsonSerializer.Serialize(atlas.Nations.ToArray(), PrettyJsonOptions));

        File.WriteAllText(
            Path.Combine(baseDataDir, "states_provinces.json"),
            JsonSerializer.Serialize(atlas.States.ToArray(), PrettyJsonOptions));

        File.WriteAllText(
            Path.Combine(baseDataDir, "geography_hierarchy.json"),
            JsonSerializer.Serialize(atlas.Hierarchy, PrettyJsonOptions));

        File.WriteAllText(
            Path.Combine(baseDataDir, "geography_features.json"),
            JsonSerializer.Serialize(atlas.Features, PrettyJsonOptions));

        if (atlas.AdminProfiles.Count > 0 || atlas.DynamicProvinceNames.Count > 0)
        {
            var payload = new AdminProfilesFilePayload(
                atlas.AdminProfiles.ToArray(),
                atlas.DynamicProvinceNames.ToArray());
            File.WriteAllText(
                Path.Combine(baseDataDir, "admin_profiles.json"),
                JsonSerializer.Serialize(payload, PrettyJsonOptions));
        }
    }

    public sealed record AdminProfilesFilePayload(
        AdministrationProfile[] Profiles,
        DynamicProvinceNameRule[] DynamicProvinceNames);
}
