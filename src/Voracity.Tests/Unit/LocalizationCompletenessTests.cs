using System.Text.Json;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class LocalizationCompletenessTests
{
    [Fact]
    public void PtBrAndEnUsCatalogs_HaveExactKeyParityAndEu5Coverage()
    {
        var dataRoot = ModdingDataLoaderIntegrationTests.ResolveDataRoot();
        var ptPath = Path.Combine(dataRoot, "base", "localization", "pt-BR.json");
        var enPath = Path.Combine(dataRoot, "base", "localization", "en-US.json");

        var ptKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ptPath))!;
        var enKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath))!;

        var missingInEn = ptKeys.Keys.Except(enKeys.Keys).ToList();
        var missingInPt = enKeys.Keys.Except(ptKeys.Keys).ToList();

        Assert.Empty(missingInEn);
        Assert.Empty(missingInPt);

        foreach (var required in new[]
        {
            "currency.gold", "hud.treasury", "hud.tax_base", "hud.control", "hud.proximity",
            "hud.food", "hud.gov_power", "hud.court_cost", "tab.realm", "tab.markets", "tab.pops",
            "lens.political", "lens.terrain", "lens.rgo_market", "lens.control", "lens.pop_food",
            "loc.rgo", "loc.expand_rgo", "loc.upgrade_rank", "loc.upgrade_road", "loc.market_access",
            "estate.nobles", "estate.clergy", "estate.burghers", "estate.commoners", "estate.tribes",
            "class.nobles", "class.clerics", "class.burghers", "class.laborers",
            "class.soldiers", "class.peasants", "class.tribesmen", "class.slaves",
            "terrain.plains", "terrain.farmlands", "terrain.forest", "terrain.hills",
            "terrain.mountains", "terrain.wetlands", "terrain.desert", "terrain.coastal",
            "settings.debug_mode", "settings.debug_hint", "debug.title", "debug.step_5y", "debug.bench_speeds", "debug.audit"
        })
        {
            Assert.True(ptKeys.ContainsKey(required), $"pt-BR missing {required}");
            Assert.True(enKeys.ContainsKey(required), $"en-US missing {required}");
        }
    }
}
