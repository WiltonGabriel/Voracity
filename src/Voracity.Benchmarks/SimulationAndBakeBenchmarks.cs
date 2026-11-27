using BenchmarkDotNet.Attributes;
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Benchmarks;

[MemoryDiagnoser]
public class TickSimulationBenchmarks
{
    private WorldDatabase _db = null!;
    private SimulationWorld _world = null!;

    [GlobalSetup]
    public void Setup()
    {
        var root = FindDataRoot();
        _db = ModdingDataLoader.Load(root);
        _world = new SimulationWorld(_db, "AET", 1380UL);
    }

    [Benchmark]
    public void Step30Days_MonthlyCycle()
    {
        _world.StepDays(30);
    }

    private static string FindDataRoot()
    {
        var current = Directory.GetCurrentDirectory();
        for (var d = 0; d < 8 && current is not null; d++)
        {
            var candidate = Path.Combine(current, "data");
            if (Directory.Exists(Path.Combine(candidate, "base")))
                return candidate;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("data/base not found");
    }
}

[MemoryDiagnoser]
public class MapBakeBenchmarks
{
    private WorldDatabase _db = null!;
    private EditableWorldAtlas _atlas = null!;
    private IReadOnlyList<ProvinceSnapshot> _snapshots = null!;

    [GlobalSetup]
    public void Setup()
    {
        var current = Directory.GetCurrentDirectory();
        for (var d = 0; d < 8 && current is not null; d++)
        {
            var candidate = Path.Combine(current, "data");
            if (Directory.Exists(Path.Combine(candidate, "base")))
            {
                _db = ModdingDataLoader.Load(candidate);
                _atlas = new EditableWorldAtlas(_db);
                _snapshots = _atlas.BuildProvinceSnapshots();
                return;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("data/base not found");
    }

    [Benchmark]
    public BakedMapArtifact BakeFullResolution720p()
    {
        return MapBakePipeline.BakeFromTopology(_atlas.Topology, _snapshots, _db.GeographyFeatures, 720, 420);
    }
}
