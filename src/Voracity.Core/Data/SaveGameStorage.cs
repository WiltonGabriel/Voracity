using System.Text.Json;
using Voracity.Core.Models;

namespace Voracity.Core.Data;

public sealed record SavedPopEntry(
    int StateIndex,
    int NationIndex,
    int ProfessionIndex,
    int PrimaryAncestry,
    int SecondaryAncestry,
    long Population,
    float StandardOfLiving,
    float Literacy,
    float Radicalism,
    bool Active);

public sealed record SavedBuildingEntry(
    int StateIndex,
    int BuildingIndex,
    int Levels,
    int ActivePmIndex);

public sealed record SavedDiplomacyEntry(
    int NationA,
    int NationB,
    int Opinion,
    bool TradePact,
    bool Alliance,
    bool AtWar,
    double WarScore);

public sealed record SavedProvinceEntry(
    string ProvinceId,
    int HoldingLevel,
    int FortLevel,
    int OccupiedByNationIndex);

public sealed record CampaignSaveData(
    string SaveName,
    DateTimeOffset SavedAtUtc,
    GameDate Date,
    string PlayerNationId,
    int Speed,
    ulong RngState,
    int TotalTicksExecuted,
    long[] NationTreasuryCoppers,
    double[] NationTaxRates,
    int[] NationStress,
    int[] NationAuthority,
    double[] NationTechPoints,
    string?[] NationActiveTechId,
    Dictionary<string, string[]> NationCompletedTechs,
    Dictionary<string, string[]> NationEnactedLaws,
    string?[] NationEnactingLawId,
    double[] NationEnactingLawProgress,
    Dictionary<string, string[]> NationCompletedJournalEntries,
    SavedPopEntry[] Pops,
    SavedBuildingEntry[] Buildings,
    SavedDiplomacyEntry[] Diplomacy,
    SavedProvinceEntry[]? Provinces = null);

public sealed record SaveSlotInfo(
    string FilePath,
    string SaveName,
    string PlayerNationId,
    GameDate Date,
    DateTimeOffset SavedAtUtc);

public static class SaveGameStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetDefaultSavesDirectory(string? userDirectory = null) =>
        Path.Combine(userDirectory ?? SettingsStorage.GetDefaultUserDirectory(), "saves");

    public static string Save(CampaignSaveData data, string? savesDirectory = null)
    {
        var dir = savesDirectory ?? GetDefaultSavesDirectory();
        Directory.CreateDirectory(dir);
        var safeName = SanitizeFileName(data.SaveName);
        var path = Path.Combine(dir, $"{safeName}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
        return path;
    }

    public static CampaignSaveData Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<CampaignSaveData>(json)
            ?? throw new InvalidDataException($"Invalid campaign save file: {filePath}");
    }

    public static IReadOnlyList<SaveSlotInfo> ListSaves(string? savesDirectory = null)
    {
        var dir = savesDirectory ?? GetDefaultSavesDirectory();
        if (!Directory.Exists(dir))
            return [];

        var list = new List<SaveSlotInfo>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var data = Load(file);
                list.Add(new SaveSlotInfo(file, data.SaveName, data.PlayerNationId, data.Date, data.SavedAtUtc));
            }
            catch
            {
                // Ignore corrupted/non-save JSON files cleanly
            }
        }
        return list.OrderByDescending(s => s.SavedAtUtc).ToList();
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "voracity_autosave" : cleaned;
    }
}
