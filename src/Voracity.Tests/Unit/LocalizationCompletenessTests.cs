using System.Text.Json;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class LocalizationCompletenessTests
{
    [Fact]
    public void PtBrAndEnUsCatalogs_HaveExactKeyParityAndTerrainHubCoverage()
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
            "currency.gold", "hud.play", "hud.bureaucracy", "tab.journal",
            "terrain.plains", "terrain.farmlands", "terrain.forest", "terrain.hills",
            "terrain.mountains", "terrain.wetlands", "terrain.desert", "terrain.coastal",
            "hub.castle", "hub.city", "hub.temple", "hub.farm", "hub.mine", "hub.wood", "hub.port",
            "province.holding", "province.fort", "province.expand_holding", "province.fortify"
        })
        {
            Assert.True(ptKeys.ContainsKey(required), $"pt-BR missing {required}");
            Assert.True(enKeys.ContainsKey(required), $"en-US missing {required}");
        }
    }
}
