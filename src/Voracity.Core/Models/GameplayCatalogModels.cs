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

public sealed record LawDefinition(
    string Id,
    string Category,
    string NameKey,
    int AuthorityBonus,
    double TaxEfficiencyBonus,
    string[] SupportingIgIds,
    string[] OpposingIgIds);

public sealed record TechnologyDefinition(
    string Id,
    string Tree,
    string NameKey,
    double CostPoints,
    double ProductionBonus,
    double MilitaryBonus,
    bool CuresWhiteDeath);

public sealed record JournalEntryDefinition(
    string Id,
    string? NationId,
    string NameKey,
    string DescriptionKey,
    string ConditionType,
    string TargetKey,
    double TargetValue,
    double RewardGold);

/// <summary>
/// Contiguous struct holding active building levels &amp; selected Production Method in a State.
/// </summary>
public struct StateBuildingSlot
{
    public int StateIndex;
    public int BuildingIndex;
    public int Levels;
    public int ActivePmIndex;
}

public sealed record BuildingSnapshot(
    string BuildingId,
    string NameKey,
    int Levels,
    string ActivePmId,
    string ActivePmNameKey,
    IReadOnlyList<(string PmId, string NameKey, bool Unlocked)> AvailablePms);

public sealed record InterestGroupSnapshot(
    string Id,
    string DisplayName,
    double CloutPercent,
    int Approval,
    bool InGovernment);

public sealed record DiplomaticRelationSnapshot(
    string TargetNationId,
    string TargetNationName,
    string TargetColorHex,
    int Opinion,
    bool TradePact,
    bool Alliance,
    bool AtWar,
    double WarScore,
    double EstimatedPower);

public sealed record TechnologyProgressSnapshot(
    string Id,
    string Tree,
    string NameKey,
    double CostPoints,
    double CurrentProgress,
    bool Completed,
    bool IsActive);

public sealed record LawStatusSnapshot(
    string Id,
    string Category,
    string NameKey,
    bool IsEnacted,
    bool IsEnacting,
    double EnactmentProgress);

public sealed record JournalEntrySnapshot(
    string Id,
    string NameKey,
    string DescriptionKey,
    double ProgressRatio,
    bool Completed,
    double RewardGold);

public sealed record PopSummarySnapshot(
    string StateId,
    string StateName,
    string Profession,
    string AncestryDisplay,
    long Population,
    float StandardOfLiving,
    float Literacy);

public sealed record NationSummarySnapshot(
    string Id,
    string Name,
    string RankTier,
    string CapitalStateId,
    string MapColorHex,
    string RulerName,
    string RulerAncestryDisplay,
    long TotalPopulation,
    double StartingTreasuryGold);
