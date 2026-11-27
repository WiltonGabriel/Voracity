using System.Diagnostics;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

var dataPath = FindDataRoot();
if (dataPath is null)
{
    Console.Error.WriteLine("Erro: pasta 'data/base/' não encontrada.");
    return 1;
}

var nationId = "AET";
var days = 360;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--nation" && i + 1 < args.Length)
        nationId = args[++i];
    else if (args[i] == "--days" && i + 1 < args.Length && int.TryParse(args[++i], out var parsedDays))
        days = Math.Max(1, parsedDays);
}

var db = ModdingDataLoader.Load(dataPath);
var world = new SimulationWorld(db, playerNationId: nationId);

var startSnap = world.BuildRenderSnapshot();
var habCount = startSnap.Provinces.Count(p => p.Kind == LocationKind.Habitable);
var seaCount = startSnap.Provinces.Count(p => p.Kind == LocationKind.SeaZone);
Console.WriteLine($"=== VORACITY EU5 v0.1 HEADLESS CLI — PONTÁRIA ~1380 D.Q. ===");
Console.WriteLine($"Nação: {startSnap.Realm.NationName} ({startSnap.Realm.NationId}) | Dinastia: {startSnap.Realm.DynastyName}");
Console.WriteLine($"Governante: {startSnap.Realm.RulerName} [{startSnap.Realm.RulerAncestryDisplay}] | Traits: {string.Join(", ", startSnap.Realm.RulerTraits)}");
Console.WriteLine($"Mapa: {startSnap.Provinces.Count} Locations ({habCount} Habitáveis, {seaCount} Zonas Marítimas) | {startSnap.Markets.Count} Mercados Regionais");
Console.WriteLine($"População Mundial Inicial: {startSnap.TotalWorldPopulation:N0} ({startSnap.ActivePopCount} grupos Pop em {startSnap.States.Count} Áreas)");
Console.WriteLine($"Controle Médio: {startSnap.AverageControl:F1}% | Tax Base Efetiva: {startSnap.TotalEffectiveTaxBase:F2} | Saldo Comida: {startSnap.TotalFoodBalance:+0.0;-0.0;0}");

var sw = Stopwatch.StartNew();
world.StepDays(days);
sw.Stop();

var endSnap = world.BuildRenderSnapshot();
var integrity = world.AuditIntegrity();
Console.WriteLine();
Console.WriteLine($"Simulados {days} dias ({world.TotalTicksExecuted} sub-ticks de 6h) em {sw.Elapsed.TotalMilliseconds:F2} ms:");
Console.WriteLine($"Data Final:   {endSnap.Date.ToDisplayString()} | Integridade: {(integrity.IsHealthy ? "OK" : string.Join("; ", integrity.Anomalies))}");
Console.WriteLine($"Tesouro:      {endSnap.Treasury.ToUiString()} | Saldo Mensal: {endSnap.MonthlyBalance.ToUiString()}");
Console.WriteLine($"Controle:     {endSnap.AverageControl:F1}% | Tax Base Potencial/Efetiva: {endSnap.TotalPotentialTaxBase:F1} / {endSnap.TotalEffectiveTaxBase:F1}");
Console.WriteLine($"Comida:       Saldo {endSnap.TotalFoodBalance:+0.0;-0.0;0} | Províncias em Fome: {endSnap.StarvingProvincesCount}");
Console.WriteLine($"População:    {endSnap.TotalWorldPopulation:N0} ({endSnap.ActivePopCount} grupos Pop ativos)");
Console.WriteLine($"Preços-Chave: grain={endSnap.MarketPricesGold["grain"]:F2}, iron={endSnap.MarketPricesGold["iron"]:F2}, steel={endSnap.MarketPricesGold["steel"]:F2}, mythril={endSnap.MarketPricesGold["mythril"]:F2}, aether={endSnap.MarketPricesGold["aether_crystals"]:F2}");
return integrity.IsHealthy ? 0 : 2;

static string? FindDataRoot()
{
    var current = Directory.GetCurrentDirectory();
    for (var depth = 0; depth < 6 && current is not null; depth++)
    {
        var candidate = Path.Combine(current, "data");
        if (Directory.Exists(Path.Combine(candidate, "base")))
            return candidate;
        current = Directory.GetParent(current)?.FullName;
    }
    return null;
}
