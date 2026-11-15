namespace Voracity.Core.Models;

/// <summary>
/// Deterministic fixed-point currency unit following Voracity's 100:1 monetary standard:
/// 10,000 Cobres = 100 Pratas = 1 Ouro (internal accounting unit); 100 Ouros = 1 Platina.
/// </summary>
public readonly record struct MonetaryAmount(long CopperUnits)
{
    public const long CoppersPerSilver = 100;
    public const long CoppersPerGold = 10_000;
    public const long CoppersPerPlatinum = 1_000_000;

    public static MonetaryAmount Zero => new(0);

    public static MonetaryAmount FromGold(double gold) =>
        new((long)Math.Round(gold * CoppersPerGold, MidpointRounding.AwayFromZero));

    public double ToGold() => (double)CopperUnits / CoppersPerGold;

    public static MonetaryAmount operator +(MonetaryAmount a, MonetaryAmount b) => new(a.CopperUnits + b.CopperUnits);
    public static MonetaryAmount operator -(MonetaryAmount a, MonetaryAmount b) => new(a.CopperUnits - b.CopperUnits);

    /// <summary>
    /// Formats an internal balance into human-readable Ouros, Pratas e Cobres (e.g. "125 Ouro(s) 40p").
    /// </summary>
    public string ToUiString()
    {
        var sign = CopperUnits < 0 ? "-" : string.Empty;
        var abs = Math.Abs(CopperUnits);
        var gold = abs / CoppersPerGold;
        var remAfterGold = abs % CoppersPerGold;
        var silver = remAfterGold / CoppersPerSilver;
        var copper = remAfterGold % CoppersPerSilver;

        if (gold > 0)
            return $"{sign}{gold:N0} Ouro, {silver} Prata, {copper} Cobre";
        if (silver > 0)
            return $"{sign}{silver} Prata, {copper} Cobre";
        return $"{sign}{copper} Cobre";
    }
}
