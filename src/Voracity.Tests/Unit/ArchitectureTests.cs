using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Voracity.Core.Engine;

namespace Voracity.Tests.Unit;

public sealed class ArchitectureTests
{
    private static readonly Assembly CoreAssembly = typeof(SimulationWorld).Assembly;

    [Fact]
    public void CoreAssembly_ShouldNotDependOnGodotOrClient()
    {
        var result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Godot", "GodotSharp", "Voracity.Client")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Voracity.Core must remain 100% pure .NET without Godot dependencies. Violations: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void CoreModels_ShouldNotDependOnEngineOrData()
    {
        var result = Types.InAssembly(CoreAssembly)
            .That()
            .ResideInNamespace("Voracity.Core.Models")
            .ShouldNot()
            .HaveDependencyOnAny("Voracity.Core.Engine", "Voracity.Core.Data")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Voracity.Core.Models must stay pure domain models. Violations: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void CoreMapping_ShouldNotDependOnGodot()
    {
        var result = Types.InAssembly(CoreAssembly)
            .That()
            .ResideInNamespace("Voracity.Core.Mapping")
            .ShouldNot()
            .HaveDependencyOnAny("Godot", "GodotSharp", "Voracity.Client")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Voracity.Core.Mapping must remain pure .NET. Violations: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
