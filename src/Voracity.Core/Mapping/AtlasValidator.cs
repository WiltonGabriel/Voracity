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
/// Validates geographic, topological, and political invariants before saving edits from the Map Editor.
/// Detects isolated territories, asymmetrical borders, missing owners, invalid capitals, and bogus interior coastal flags.
/// </summary>
public static class AtlasValidator
{
    public static AtlasValidationReport Validate(EditableWorldAtlas atlas)
    {
        var issues = new List<AtlasValidationIssue>();
        var nationIds = atlas.Nations.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var seenStateIds = new HashSet<string>(StringComparer.Ordinal);
        var seenProvIds = new HashSet<string>(StringComparer.Ordinal);
        var provById = new Dictionary<string, (StateDefinition State, ProvinceDefinition Prov)>(StringComparer.Ordinal);

        // 1. State & Province integrity
        foreach (var st in atlas.States)
        {
            if (!seenStateIds.Add(st.Id))
                issues.Add(new(ValidationSeverity.Error, "DUP_STATE", $"Duplicate State ID '{st.Id}'.", st.Id));

            if (!string.Equals(st.OwnerNationId, "NONE", StringComparison.OrdinalIgnoreCase) &&
                !nationIds.Contains(st.OwnerNationId))
            {
                issues.Add(new(ValidationSeverity.Error, "ORPHAN_OWNER",
                    $"State '{st.Id}' references unknown owner '{st.OwnerNationId}'.", st.Id));
            }

            var det = st.EnsureProvinceDetails();
            if (det.Length == 0)
            {
                issues.Add(new(ValidationSeverity.Error, "EMPTY_STATE", $"State '{st.Id}' has zero provinces.", st.Id));
                continue;
            }

            foreach (var p in det)
            {
                if (!seenProvIds.Add(p.Id))
                    issues.Add(new(ValidationSeverity.Error, "DUP_PROV", $"Duplicate Province ID '{p.Id}'.", p.Id));
                provById[p.Id] = (st, p);
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

        // 4. Isolated territory check via BFS over planar face adjacencies + strait crossings
        if (atlas.Topology.Faces.Count > 1)
        {
            var visitedFaces = new HashSet<int>();
            var largestFace = atlas.Topology.Faces.OrderByDescending(f => f.AreaNormalized).First();
            var queue = new Queue<int>();
            queue.Enqueue(largestFace.FaceId);
            visitedFaces.Add(largestFace.FaceId);

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                if (!atlas.Topology.NeighborFaceIds.TryGetValue(curr, out var nbrs))
                    continue;
                foreach (var n in nbrs)
                {
                    if (visitedFaces.Add(n))
                        queue.Enqueue(n);
                }
            }

            foreach (var f in atlas.Topology.Faces)
            {
                if (!visitedFaces.Contains(f.FaceId) && !f.IsCoastal)
                {
                    issues.Add(new(ValidationSeverity.Warning, "ISOLATED_PROV",
                        $"Inland province '{f.ProvinceId}' is isolated from the main network with no strait or sea access.",
                        f.ProvinceId));
                }

                if (f.OuterRing.Length < 3 || f.AreaNormalized <= 1e-6f)
                {
                    issues.Add(new(ValidationSeverity.Error, "DEGEN_POLY",
                        $"Province '{f.ProvinceId}' has degenerate or empty polygon geometry.", f.ProvinceId));
                }

                if (provById.TryGetValue(f.ProvinceId, out var pair) && pair.Prov.IsCoastal != f.IsCoastal)
                {
                    issues.Add(new(ValidationSeverity.Warning, "COAST_MISMATCH",
                        $"Province '{f.ProvinceId}' IsCoastal ({pair.Prov.IsCoastal}) disagrees with geometry coast contact ({f.IsCoastal}).",
                        f.ProvinceId));
                }
            }
        }

        var errors = issues.Count(i => i.Severity == ValidationSeverity.Error);
        var warnings = issues.Count(i => i.Severity == ValidationSeverity.Warning);
        return new AtlasValidationReport(errors == 0, errors, warnings, issues);
    }
}
