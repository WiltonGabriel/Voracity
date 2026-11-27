using Shouldly;
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
    public void BaseDataset_IsInternallyConsistentAndMatchesEu5Catalogs()
    {
        var db = ModdingDataLoader.Load(ResolveDataRoot());

        db.Ancestries.Count.ShouldBe(9);
        db.Goods.Count.ShouldBe(34);
        db.Nations.Count.ShouldBe(27);
        db.States.Count.ShouldBe(36);
        db.Buildings.Count.ShouldBeGreaterThanOrEqualTo(8);
        db.Markets.Count.ShouldBe(6);
        db.SeaZones.Count.ShouldBeGreaterThanOrEqualTo(25);
        db.SpecialLocations.Count.ShouldBeGreaterThanOrEqualTo(6);

        db.Localization.Get("pt-BR", "menu.new_game").ShouldNotBe("menu.new_game");
        db.Localization.Get("en-US", "menu.new_game").ShouldNotBe("menu.new_game");

        var validator = new WorldDatabaseValidator();
        var result = validator.Validate(db);
        result.IsValid.ShouldBeTrue(string.Join(" | ", result.Errors.Select(e => e.ErrorMessage)));

        var nationIds = db.Nations.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var ancestryIds = db.Ancestries.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var stateIds = db.States.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var state in db.States)
        {
            nationIds.ShouldContain(state.OwnerNationId);
            state.Provinces.ShouldNotBeEmpty();
            foreach (var adj in state.AdjacentStateIds)
                stateIds.ShouldContain(adj);

            foreach (var pop in state.InitialPops)
            {
                ancestryIds.ShouldContain(pop.PrimaryAncestry);
                if (pop.SecondaryAncestry is not null)
                    ancestryIds.ShouldContain(pop.SecondaryAncestry);
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

            db.LoadedMods.Count.ShouldBe(2);
            db.LoadedMods[0].Id.ShouldBe("mod_first");
            db.LoadedMods[1].Id.ShouldBe("mod_second");
            db.Goods.ShouldContain(g => g.Id == "mythril" && g.BasePriceGold == 135.0);
            db.Goods.ShouldContain(g => g.Id == "starlight_ore");
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
