using System.Text.Json;
using Voracity.Core.Models;

namespace Voracity.Core.Data;

public sealed record SavedPopEntry(
    int LocationIndex,
    int NationIndex,
    SocialClass Class,
    int PrimaryAncestry,
    int SecondaryAncestry,
    long Population,
    float Literacy,
    float Satisfaction,
    bool Active);

public sealed record SavedLocationEntry(
    string LocationId,
    int OwnerNationIndex,
    double Control,
    int RgoLevel,
    LocationRank Rank,
    RoadLevel RoadLevel,
    double Development,
    double Prosperity);

public sealed record SavedBuildingEntry(
    int LocationIndex,
    int BuildingIndex,
    int Levels,
    int ActivePmIndex);

public sealed record SavedProvinceFoodEntry(
    string ProvinceId,
    double FoodStorage,
    bool IsStarving);

public sealed record SavedMarketEntry(
    string MarketId,
    double[] GoodPrices);

public sealed record CampaignSaveData(
    int SchemaVersion,
    string SaveName,
    DateTimeOffset SavedAtUtc,
    GameDate Date,
    string PlayerNationId,
    int Speed,
    ulong RngState,
    int TotalTicksExecuted,
    long[] NationTreasuryCoppers,
    double[] NationTaxRates,
    double[] NationCourtSpendingPercent,
    double[] NationGovernmentPower,
    SavedLocationEntry[] Locations,
    SavedProvinceFoodEntry[] ProvinceFoods,
    SavedMarketEntry[] Markets,
    SavedBuildingEntry[] Buildings,
    SavedPopEntry[] Pops);

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
