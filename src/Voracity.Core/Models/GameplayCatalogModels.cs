namespace Voracity.Core.Models;

public sealed record ProductionMethodDefinition(
    string Id,
    string NameKey,
    string? RequiredTechId,
    Dictionary<string, double> Inputs,
    Dictionary<string, double> Outputs);

public sealed record BuildingDefinition(
    string Id,
    string NameKey,
    string Category,
    double CostGold,
    double BaseUpkeepGold,
    ProductionMethodDefinition[] ProductionMethods);

public sealed record BuildingSlotSnapshot(
    string BuildingId,
    string NameKey,
    int Levels,
    string ActivePmId,
    string ActivePmNameKey,
    IReadOnlyList<(string PmId, string NameKey)> AvailablePms);

public sealed record PopSummarySnapshot(
    string LocationId,
    string LocationName,
    SocialClass SocialClass,
    string AncestryDisplay,
    long Population,
    float Literacy,
    float Satisfaction);

public sealed record NationSummarySnapshot(
    string Id,
    string Name,
    string RankTier,
    string CapitalStateId,
    string MapColorHex,
    string RulerName,
    string DynastyName,
    string RulerAncestryDisplay,
    IReadOnlyList<string> RulerTraits,
    long TotalPopulation,
    double StartingTreasuryGold);
