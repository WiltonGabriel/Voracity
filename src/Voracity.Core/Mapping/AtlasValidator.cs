using QuikGraph.Algorithms.ConnectedComponents;
using Voracity.Core.Data;
using Voracity.Core.Models;

namespace Voracity.Core.Mapping;

public enum ValidationSeverity { Error, Warning, Info }

public sealed record AtlasValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? TargetId = null);

public sealed record AtlasValidationReport(
    bool IsValid,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<AtlasValidationIssue> Issues);

/// <summary>
/// Validates geographic, topological, and political invariants using FluentValidation + QuikGraph ConnectedComponents.
/// Because IsCoastal is 100% derived from planar topology contact with SeaZone faces, manual coast mismatch cannot occur.
/// </summary>
public static class AtlasValidator
{
    public static AtlasValidationReport Validate(EditableWorldAtlas atlas)
    {
        var issues = new List<AtlasValidationIssue>();
        var nationIds = atlas.Nations.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var seenStateIds = new HashSet<string>(StringComparer.Ordinal);
        var seenProvIds = new HashSet<string>(StringComparer.Ordinal);

        // 0. FluentValidation over underlying WorldDatabase (Goods, SeaZones, SpecialLocations, Markets)
        var dbValidator = new WorldDatabaseValidator();
        var dbResult = dbValidator.Validate(atlas.ToWorldDatabase());
        foreach (var err in dbResult.Errors)
            issues.Add(new(ValidationSeverity.Error, "DATA_SCHEMA", err.ErrorMessage, err.PropertyName));

        // 1. State & Habitable Location integrity
        foreach (var st in atlas.States)
        {
            if (!seenStateIds.Add(st.Id))
                issues.Add(new(ValidationSeverity.Error, "DUP_STATE", $"Duplicate State/Area ID '{st.Id}'.", st.Id));

            if (!string.Equals(st.OwnerNationId, "NONE", StringComparison.OrdinalIgnoreCase) &&
                !nationIds.Contains(st.OwnerNationId))
            {
                issues.Add(new(ValidationSeverity.Error, "ORPHAN_OWNER",
                    $"State '{st.Id}' references unknown owner '{st.OwnerNationId}'.", st.Id));
            }

            var det = st.EnsureProvinceDetails();
            if (det.Length == 0)
            {
                issues.Add(new(ValidationSeverity.Error, "EMPTY_STATE", $"State '{st.Id}' has zero locations.", st.Id));
                continue;
            }

            foreach (var p in det)
            {
                if (!seenProvIds.Add(p.Id))
                    issues.Add(new(ValidationSeverity.Error, "DUP_PROV", $"Duplicate Location ID '{p.Id}'.", p.Id));
            }
        }

        // 2. Capitals check for playable nations
        foreach (var nat in atlas.Nations)
        {
            if (nat.Id == "NONE")
                continue;
            if (!seenStateIds.Contains(nat.CapitalStateId))
            {
                issues.Add(new(ValidationSeverity.Error, "MISSING_CAP",
                    $"Nation '{nat.Id}' capital state '{nat.CapitalStateId}' does not exist.", nat.Id));
            }
        }

        // 3. Symmetry check on StateDefinition.AdjacentStateIds
        var stateMap = atlas.States.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var st in atlas.States)
        {
            foreach (var adj in st.AdjacentStateIds)
            {
                if (!stateMap.TryGetValue(adj, out var other))
                {
                    issues.Add(new(ValidationSeverity.Error, "BAD_ADJ",
                        $"State '{st.Id}' lists unknown neighbor '{adj}'.", st.Id));
                }
                else if (!other.AdjacentStateIds.Contains(st.Id, StringComparer.Ordinal))
                {
                    issues.Add(new(ValidationSeverity.Error, "ASYM_ADJ",
                        $"Asymmetric border: '{st.Id}' -> '{adj}' is not reciprocated.", st.Id));
                }
            }
        }

        // 4. Connectedness & polygon check via QuikGraph ConnectedComponentsAlgorithm over unified graph (Land + Straits + SeaZones)
        if (atlas.Topology.Faces.Count > 1)
        {
            var cc = new ConnectedComponentsAlgorithm<int, QuikGraph.TaggedEdge<int, LocationEdgeInfo>>(atlas.Topology.AdjacencyGraph);
            cc.Compute();

            var largestFace = atlas.Topology.Faces
                .Where(f => f.Kind == LocationKind.Habitable)
                .OrderByDescending(f => f.AreaNormalized)
                .FirstOrDefault() ?? atlas.Topology.Faces[0];

            var mainComponent = cc.Components.TryGetValue(largestFace.FaceId, out var mc) ? mc : -1;

            foreach (var f in atlas.Topology.Faces)
            {
                if (f.OuterRing.Length < 3 || f.AreaNormalized <= 1e-6f)
                {
                    issues.Add(new(ValidationSeverity.Error, "DEGEN_POLY",
                        $"Location '{f.ProvinceId}' has degenerate or empty polygon geometry.", f.ProvinceId));
                }

                if (f.Kind != LocationKind.Impassable &&
                    cc.Components.TryGetValue(f.FaceId, out var comp) &&
                    comp != mainComponent &&
                    !f.IsCoastal)
                {
                    issues.Add(new(ValidationSeverity.Warning, "ISOLATED_PROV",
                        $"Inland location '{f.ProvinceId}' is isolated from the world connectivity graph.",
                        f.ProvinceId));
                }
            }
        }

        var errors = issues.Count(i => i.Severity == ValidationSeverity.Error);
        var warnings = issues.Count(i => i.Severity == ValidationSeverity.Warning);
        return new AtlasValidationReport(errors == 0, errors, warnings, issues);
    }
}
