namespace Voracity.Core.Models;

/// <summary>
/// Data-driven definition of a playable base ancestry in Pontária (loaded from data/base/ancestries.json + mods).
/// </summary>
public sealed record AncestryDefinition(
    string Id,
    string Name,
    double MonthlyGrowthModifier,
    double TechSpreadModifier,
    double MiningProductivityModifier,
    double MartialPowerModifier,
    double ForestryAlchemyModifier,
    double LiteracyBaseModifier,
    double HeatResistanceModifier,
    double FoodCalorieMultiplier,
    bool RequiresBloodEssence,
    bool CanBeCombined,
    string MapColorHex);

/// <summary>
/// Represents Voracity's Dual Ancestry rule (Ancestry != Race): up to 2 ancestry slots per Pop or Character.
/// When SecondaryIndex >= 0, biological modifiers blend 50% / 50% between both root ancestries.
/// </summary>
public readonly record struct DualAncestry(int PrimaryIndex, int SecondaryIndex = -1)
{
    public bool IsMixed => SecondaryIndex >= 0 && SecondaryIndex != PrimaryIndex;

    public static EffectiveAncestryTraits ComputeEffectiveTraits(
        DualAncestry ancestry,
        ReadOnlySpan<AncestryDefinition> catalog)
    {
        var primary = catalog[ancestry.PrimaryIndex];
        if (!ancestry.IsMixed)
        {
            return new EffectiveAncestryTraits(
                primary.MonthlyGrowthModifier,
                primary.TechSpreadModifier,
                primary.MiningProductivityModifier,
                primary.MartialPowerModifier,
                primary.ForestryAlchemyModifier,
                primary.LiteracyBaseModifier,
                primary.HeatResistanceModifier,
                primary.FoodCalorieMultiplier,
                primary.RequiresBloodEssence);
        }

        var secondary = catalog[ancestry.SecondaryIndex];
        return new EffectiveAncestryTraits(
            (primary.MonthlyGrowthModifier + secondary.MonthlyGrowthModifier) * 0.5,
            (primary.TechSpreadModifier + secondary.TechSpreadModifier) * 0.5,
            (primary.MiningProductivityModifier + secondary.MiningProductivityModifier) * 0.5,
            (primary.MartialPowerModifier + secondary.MartialPowerModifier) * 0.5,
            (primary.ForestryAlchemyModifier + secondary.ForestryAlchemyModifier) * 0.5,
            (primary.LiteracyBaseModifier + secondary.LiteracyBaseModifier) * 0.5,
            (primary.HeatResistanceModifier + secondary.HeatResistanceModifier) * 0.5,
            (primary.FoodCalorieMultiplier + secondary.FoodCalorieMultiplier) * 0.5,
            primary.RequiresBloodEssence || secondary.RequiresBloodEssence);
    }

    public string FormatName(ReadOnlySpan<AncestryDefinition> catalog)
    {
        var p = catalog[PrimaryIndex].Name;
        return IsMixed ? $"{p} + {catalog[SecondaryIndex].Name}" : p;
    }
}

public readonly record struct EffectiveAncestryTraits(
    double MonthlyGrowthModifier,
    double TechSpreadModifier,
    double MiningProductivityModifier,
    double MartialPowerModifier,
    double ForestryAlchemyModifier,
    double LiteracyBaseModifier,
    double HeatResistanceModifier,
    double FoodCalorieMultiplier,
    bool RequiresBloodEssence);
