using Voracity.Core.Data;

namespace Voracity.Tests.Integration;

public sealed class ModdingDataLoaderIntegrationTests
{
    public static string ResolveDataRoot()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var candidate = Path.Combine(current, "data");
            if (Directory.Exists(Path.Combine(candidate, "base")))
                return candidate;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate Voracity data/base directory.");
    }

    [Fact]
    public void BaseDataset_IsInternallyConsistentAndMatchesCanonIds()
    {
        var db = ModdingDataLoader.Load(ResolveDataRoot());

        Assert.Equal(9, db.Ancestries.Count);
        Assert.True(db.Goods.Count >= 34);
        Assert.True(db.Nations.Count >= 8);
        Assert.True(db.States.Count >= 14);
        Assert.True(db.Buildings.Count >= 8);
        Assert.True(db.Laws.Count >= 9);
        Assert.True(db.Technologies.Count >= 9);
        Assert.True(db.JournalEntries.Count >= 4);
        Assert.NotEqual("menu.new_game", db.Localization.Get("pt-BR", "menu.new_game"));
        Assert.NotEqual("menu.new_game", db.Localization.Get("en-US", "menu.new_game"));

        var nationIds = db.Nations.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var ancestryIds = db.Ancestries.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var stateIds = db.States.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var state in db.States)
        {
            Assert.Contains(state.OwnerNationId, nationIds);
            Assert.NotEmpty(state.Provinces);
            foreach (var adj in state.AdjacentStateIds)
                Assert.Contains(adj, stateIds);

            foreach (var pop in state.InitialPops)
            {
                Assert.Contains(pop.PrimaryAncestry, ancestryIds);
                if (pop.SecondaryAncestry is not null)
                    Assert.Contains(pop.SecondaryAncestry, ancestryIds);
            }
        }
    }

    [Fact]
    public void ModLoader_AppliesLoadOrderOverridesAndIgnoresDisabledMods()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"voracity_mod_int_{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(Path.Combine(ResolveDataRoot(), "base"), Path.Combine(tempRoot, "base"));

            CreateMod(tempRoot, "mod_first", 10, true, """
                [
                  { "id": "mythril", "name": "Mythril First", "category": "industrial", "basePriceGold": 110.0, "isLocal": false }
                ]
                """);
            CreateMod(tempRoot, "mod_second", 20, true, """
                [
                  { "id": "mythril", "name": "Mythril Second", "category": "industrial", "basePriceGold": 135.0, "isLocal": false },
                  { "id": "starlight_ore", "name": "Minério Estelar", "category": "industrial", "basePriceGold": 160.0, "isLocal": false }
                ]
                """);
            CreateMod(tempRoot, "mod_disabled", 30, false, """
                [
                  { "id": "mythril", "name": "Should Not Load", "category": "industrial", "basePriceGold": 999.0, "isLocal": false }
                ]
                """);

            var db = ModdingDataLoader.Load(tempRoot);

            Assert.Equal(2, db.LoadedMods.Count);
            Assert.Equal("mod_first", db.LoadedMods[0].Id);
            Assert.Equal("mod_second", db.LoadedMods[1].Id);
            Assert.Contains(db.Goods, g => g.Id == "mythril" && g.BasePriceGold == 135.0);
            Assert.Contains(db.Goods, g => g.Id == "starlight_ore");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void CreateMod(string tempRoot, string id, int loadOrder, bool enabled, string goodsJson)
    {
        var dir = Path.Combine(tempRoot, "mods", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $$"""
            { "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "loadOrder": {{loadOrder}}, "enabled": {{enabled.ToString().ToLowerInvariant()}} }
            """);
        File.WriteAllText(Path.Combine(dir, "goods.json"), goodsJson);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
    }
}
