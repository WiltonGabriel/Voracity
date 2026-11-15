using System.Text.Json;
using Voracity.Core.Models;

namespace Voracity.Core.Data;

public sealed record ModManifest(
    string Id,
    string Name,
    string Version,
    int LoadOrder,
    bool Enabled);

public sealed record WorldDatabase(
    IReadOnlyList<AncestryDefinition> Ancestries,
    IReadOnlyList<GoodDefinition> Goods,
    IReadOnlyList<NationDefinition> Nations,
    IReadOnlyList<StateDefinition> States,
    IReadOnlyList<BuildingDefinition> Buildings,
    IReadOnlyList<LawDefinition> Laws,
    IReadOnlyList<TechnologyDefinition> Technologies,
    IReadOnlyList<JournalEntryDefinition> JournalEntries,
    LocalizationCatalog Localization,
    IReadOnlyList<ModManifest> LoadedMods);

/// <summary>
/// OpenVic / Paradox Jomini-inspired declarative data loader.
/// Loads data/base/ first, then overlays enabled mods in data/mods/ sorted by LoadOrder.
/// </summary>
public static class ModdingDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static WorldDatabase Load(string dataRootPath)
    {
        var baseDir = Path.Combine(dataRootPath, "base");
        if (!Directory.Exists(baseDir))
            throw new DirectoryNotFoundException($"Base data directory not found: {baseDir}");

        var ancestries = ReadJsonArray<AncestryDefinition>(Path.Combine(baseDir, "ancestries.json"));
        var goods = ReadJsonArray<GoodDefinition>(Path.Combine(baseDir, "goods.json"));
        var nations = ReadJsonArray<NationDefinition>(Path.Combine(baseDir, "nations.json"));
        var states = ReadJsonArray<StateDefinition>(Path.Combine(baseDir, "states_provinces.json"));
        var buildings = ReadOptionalJsonArray<BuildingDefinition>(Path.Combine(baseDir, "buildings.json"));
        var laws = ReadOptionalJsonArray<LawDefinition>(Path.Combine(baseDir, "laws.json"));
        var technologies = ReadOptionalJsonArray<TechnologyDefinition>(Path.Combine(baseDir, "technologies.json"));
        var journalEntries = ReadOptionalJsonArray<JournalEntryDefinition>(Path.Combine(baseDir, "journal_entries.json"));

        var locTables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        LoadLocalizationDir(Path.Combine(baseDir, "localization"), locTables);

        var loadedMods = new List<ModManifest>();
        var modsRoot = Path.Combine(dataRootPath, "mods");
        if (Directory.Exists(modsRoot))
        {
            var discovered = new List<(ModManifest Manifest, string Dir)>();
            foreach (var dir in Directory.GetDirectories(modsRoot))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is { Enabled: true })
                    discovered.Add((manifest, dir));
            }

            foreach (var (manifest, dir) in discovered.OrderBy(m => m.Manifest.LoadOrder).ThenBy(m => m.Manifest.Id, StringComparer.Ordinal))
            {
                MergeById(ancestries, Path.Combine(dir, "ancestries.json"), x => x.Id);
                MergeById(goods, Path.Combine(dir, "goods.json"), x => x.Id);
                MergeById(nations, Path.Combine(dir, "nations.json"), x => x.Id);
                MergeById(states, Path.Combine(dir, "states_provinces.json"), x => x.Id);
                MergeById(buildings, Path.Combine(dir, "buildings.json"), x => x.Id);
                MergeById(laws, Path.Combine(dir, "laws.json"), x => x.Id);
                MergeById(technologies, Path.Combine(dir, "technologies.json"), x => x.Id);
                MergeById(journalEntries, Path.Combine(dir, "journal_entries.json"), x => x.Id);
                LoadLocalizationDir(Path.Combine(dir, "localization"), locTables);
                loadedMods.Add(manifest);
            }
        }

        return new WorldDatabase(
            ancestries,
            goods,
            nations,
            EnsureProvinceDetails(states),
            buildings,
            laws,
            technologies,
            journalEntries,
            new LocalizationCatalog(locTables),
            loadedMods);
    }

    private static List<StateDefinition> EnsureProvinceDetails(List<StateDefinition> states)
    {
        for (var i = 0; i < states.Count; i++)
        {
            var st = states[i];
            if (st.ProvinceDetails is { Length: > 0 })
                continue;

            var count = Math.Max(1, st.Provinces.Length);
            var generated = new ProvinceDefinition[count];
            for (var p = 0; p < count; p++)
            {
                var angle = p * (2.0 * Math.PI / count);
                var dist = p == 0 ? 0.0 : Math.Max(0.025, st.Radius * 0.45);
                var ax = Math.Clamp(st.MapX + Math.Cos(angle) * dist, 0.08, 0.92);
                var ay = Math.Clamp(st.MapY + Math.Sin(angle) * dist * 0.85, 0.10, 0.88);
                var pName = p < st.Provinces.Length ? st.Provinces[p] : $"{st.Name} {p + 1}";
                generated[p] = new ProvinceDefinition(
                    Id: $"{st.Id}_p{p + 1}",
                    Name: pName,
                    Terrain: st.MacroRegion == "AridBelt" ? "desert" : (st.MacroRegion == "UndergroundSouth" ? "mountains" : "plains"),
                    HubType: p == 0 ? "castle" : (p % 3 == 1 ? "farm" : (p % 3 == 2 ? "mine" : "city")),
                    IsCapital: p == 0,
                    IsCoastal: false,
                    AnchorX: ax,
                    AnchorY: ay,
                    StartingFortLevel: p == 0 ? 1 : 0);
            }
            states[i] = st with { ProvinceDetails = generated };
        }
        return states;
    }

    private static List<T> ReadJsonArray<T>(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions)
            ?? throw new InvalidDataException($"Failed to deserialize array from {filePath}");
    }

    private static List<T> ReadOptionalJsonArray<T>(string filePath) =>
        File.Exists(filePath) ? ReadJsonArray<T>(filePath) : [];

    private static void LoadLocalizationDir(string locDir, Dictionary<string, Dictionary<string, string>> targetTables)
    {
        if (!Directory.Exists(locDir))
            return;

        foreach (var file in Directory.GetFiles(locDir, "*.json"))
        {
            var langCode = Path.GetFileNameWithoutExtension(file);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file), JsonOptions);
            if (entries is null)
                continue;

            if (!targetTables.TryGetValue(langCode, out var table))
            {
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                targetTables[langCode] = table;
            }

            foreach (var (k, v) in entries)
                table[k] = v;
        }
    }

    private static void MergeById<T>(List<T> target, string overrideFile, Func<T, string> idSelector)
    {
        if (!File.Exists(overrideFile))
            return;

        var overrides = ReadJsonArray<T>(overrideFile);
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < target.Count; i++)
            indexById[idSelector(target[i])] = i;

        foreach (var item in overrides)
        {
            var id = idSelector(item);
            if (indexById.TryGetValue(id, out var existingIndex))
                target[existingIndex] = item;
            else
            {
                indexById[id] = target.Count;
                target.Add(item);
            }
        }
    }
}
