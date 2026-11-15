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
Console.WriteLine($"=== VORACITY MVP HEADLESS CLI — PONTÁRIA ~1380 D.Q. ===");
Console.WriteLine($"Nação: {startSnap.PlayerNationName} ({startSnap.PlayerNationId}) | Governante: {startSnap.RulerName} [{startSnap.RulerAncestryDisplay}]");
Console.WriteLine($"Data Inicial: {startSnap.Date.ToDisplayString()} | Tesouro: {startSnap.Treasury.ToUiString()} | Mods Ativos: {db.LoadedMods.Count}");
Console.WriteLine($"População Mundial Inicial: {startSnap.TotalWorldPopulation:N0} ({startSnap.ActivePopCount} grupos Pop em {startSnap.States.Count} Estados)");

var sw = Stopwatch.StartNew();
world.StepDays(days);
sw.Stop();

var endSnap = world.BuildRenderSnapshot();
Console.WriteLine();
Console.WriteLine($"Simulados {days} dias ({world.TotalTicksExecuted} sub-ticks de 6h) em {sw.Elapsed.TotalMilliseconds:F2} ms:");
Console.WriteLine($"Data Final:   {endSnap.Date.ToDisplayString()}");
Console.WriteLine($"Tesouro:      {endSnap.Treasury.ToUiString()} | Saldo Semanal: {endSnap.WeeklyBalance.ToUiString()}");
Console.WriteLine($"População:    {endSnap.TotalWorldPopulation:N0} ({endSnap.ActivePopCount} grupos Pop ativos)");
Console.WriteLine($"Preços-Chave (Ouro): grain={endSnap.MarketPricesGold["grain"]:F2}, iron={endSnap.MarketPricesGold["iron"]:F2}, steel={endSnap.MarketPricesGold["steel"]:F2}, mythril={endSnap.MarketPricesGold["mythril"]:F2}, aether_crystals={endSnap.MarketPricesGold["aether_crystals"]:F2}, blood_essence={endSnap.MarketPricesGold["blood_essence"]:F2}");
return 0;

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
