using Voracity.Core.Data;
using Voracity.Core.Mapping;
using Voracity.Core.Models;
using Voracity.Tests.Integration;

namespace Voracity.Tests.Unit;

public sealed class EditableWorldAtlasTests
{
    private static EditableWorldAtlas CreateCanonicalAtlas()
    {
        var db = ModdingDataLoader.Load(ModdingDataLoaderIntegrationTests.ResolveDataRoot());
        return new EditableWorldAtlas(db);
    }

    [Fact]
    public void MoveSharedArcVertexCommand_UpdatesBothAdjacentFacesWithoutCracksAndUndoesCleanly()
    {
        var atlas = CreateCanonicalAtlas();
        var stack = new AtlasCommandStack();

        var sharedArc = atlas.Topology.Arcs.First(a => a.LeftFaceId > 0 && a.RightFaceId > 0 && a.Points.Length >= 2);
        var targetPtIdx = sharedArc.Points.Length > 2 ? 1 : 0;
        var originalPt = sharedArc.Points[targetPtIdx];
        var movedPt = new Point2D(
            Math.Clamp(originalPt.X + 0.008f, 0.01f, 0.99f),
            Math.Clamp(originalPt.Y - 0.006f, 0.01f, 0.99f));

        stack.Execute(atlas, new MoveSharedArcVertexCommand(sharedArc.Id, targetPtIdx, movedPt));

        var leftFace = atlas.Topology.FaceById[sharedArc.LeftFaceId];
        var rightFace = atlas.Topology.FaceById[sharedArc.RightFaceId];

        Assert.Contains(movedPt, leftFace.OuterRing);
        Assert.Contains(movedPt, rightFace.OuterRing);

        Assert.True(stack.Undo(atlas));
        leftFace = atlas.Topology.FaceById[sharedArc.LeftFaceId];
        rightFace = atlas.Topology.FaceById[sharedArc.RightFaceId];
        Assert.Contains(originalPt, leftFace.OuterRing);
        Assert.Contains(originalPt, rightFace.OuterRing);

        Assert.True(stack.Redo(atlas));
        leftFace = atlas.Topology.FaceById[sharedArc.LeftFaceId];
        rightFace = atlas.Topology.FaceById[sharedArc.RightFaceId];
        Assert.Contains(movedPt, leftFace.OuterRing);
        Assert.Contains(movedPt, rightFace.OuterRing);
    }

    [Fact]
    public void MoveSharedArcVertexCommand_InteriorPointRebuildsOnlyAdjacentTwoFaces()
    {
        var atlas = CreateCanonicalAtlas();
        var stack = new AtlasCommandStack();

        var interiorArc = atlas.Topology.Arcs.First(a => a.LeftFaceId > 0 && a.RightFaceId > 0 && a.Points.Length >= 3);
        var unrelatedFace = atlas.Topology.Faces.First(f => f.FaceId != interiorArc.LeftFaceId && f.FaceId != interiorArc.RightFaceId);
        var unrelatedRingBefore = unrelatedFace.OuterRing;

        var oldPt = interiorArc.Points[1];
        var newPt = new Point2D(Math.Clamp(oldPt.X + 0.005f, 0.01f, 0.99f), oldPt.Y);
        stack.Execute(atlas, new MoveSharedArcVertexCommand(interiorArc.Id, 1, newPt));

        var unrelatedAfter = atlas.Topology.FaceById[unrelatedFace.FaceId];
        Assert.Same(unrelatedRingBefore, unrelatedAfter.OuterRing);
    }

    [Fact]
    public void SplitProvinceCommand_PreservesParentPersistentUidAndAllocatesChildUid()
    {
        var atlas = CreateCanonicalAtlas();
        var stack = new AtlasCommandStack();

        var initialCount = atlas.BuildProvinceSnapshots().Count;
        var parentUid = atlas.AllocatePersistentUid("prov_briggston");
        var parentFace = atlas.Topology.FaceByProvinceId["prov_briggston"];

        var cmd = new SplitProvinceCommand(
            "prov_briggston",
            new Point2D(parentFace.SeedX - 0.02f, parentFace.SeedY - 0.02f),
            new Point2D(parentFace.SeedX + 0.02f, parentFace.SeedY + 0.02f),
            "prov_briggston_east",
            "Leste de Briggston");

        stack.Execute(atlas, cmd);

        Assert.Equal(initialCount + 1, atlas.BuildProvinceSnapshots().Count);
        Assert.Equal(parentUid, atlas.AllocatePersistentUid("prov_briggston"));
        var childUid = atlas.AllocatePersistentUid("prov_briggston_east");
        Assert.True(childUid > parentUid);
        Assert.True(atlas.Topology.FaceByProvinceId.ContainsKey("prov_briggston_east"));

        Assert.True(stack.Undo(atlas));
        Assert.Equal(initialCount, atlas.BuildProvinceSnapshots().Count);
        Assert.False(atlas.Topology.FaceByProvinceId.ContainsKey("prov_briggston_east"));
    }

    [Fact]
    public void MergeProvincesCommand_RecordsTombstoneAndRetiresAbsorbedProvince()
    {
        var atlas = CreateCanonicalAtlas();
        var stack = new AtlasCommandStack();

        var multiProvState = atlas.States.First(s => s.EnsureProvinceDetails().Length >= 2);
        var survivor = multiProvState.EnsureProvinceDetails()[0];
        var absorbed = multiProvState.EnsureProvinceDetails()[1];
        var absorbedUid = atlas.AllocatePersistentUid(absorbed.Id);

        stack.Execute(atlas, new MergeProvincesCommand(survivor.Id, absorbed.Id));

        Assert.DoesNotContain(atlas.BuildProvinceSnapshots(), p => p.ProvinceId == absorbed.Id);
        Assert.Contains(atlas.Topology.Tombstones, t =>
            t.RetiredProvinceId == absorbed.Id &&
            t.RetiredPersistentUid == absorbedUid &&
            t.MergedIntoProvinceId == survivor.Id);

        Assert.True(stack.Undo(atlas));
        Assert.Contains(atlas.BuildProvinceSnapshots(), p => p.ProvinceId == absorbed.Id);
        Assert.DoesNotContain(atlas.Topology.Tombstones, t => t.RetiredProvinceId == absorbed.Id);
    }

    [Fact]
    public void AtlasValidator_DetectsInvalidOwnersAndAsymmetricAdjacencies()
    {
        var atlas = CreateCanonicalAtlas();
        var s0 = atlas.States[0];
        atlas.States[0] = s0 with
        {
            OwnerNationId = "INVALID_TAG",
            AdjacentStateIds = new[] { "state_vororia" }
        };

        // Ensure target does not reciprocate if it happened to be adjacent
        var vIdx = atlas.States.FindIndex(s => s.Id == "state_vororia");
        if (vIdx >= 0)
        {
            var vs = atlas.States[vIdx];
            atlas.States[vIdx] = vs with
            {
                AdjacentStateIds = vs.AdjacentStateIds.Where(id => id != s0.Id).ToArray()
            };
        }

        var report = AtlasValidator.Validate(atlas);
        Assert.False(report.IsValid);
        Assert.True(report.ErrorCount >= 2);
        Assert.Contains(report.Issues, i => i.Code == "ORPHAN_OWNER");
        Assert.Contains(report.Issues, i => i.Code == "ASYM_ADJ");
    }

    [Fact]
    public void AtlasVersioning_CapturesRestoresAndExportsDeltaJson()
    {
        var atlas = CreateCanonicalAtlas();
        var baseline = AtlasVersioning.CaptureSnapshot(atlas, "Baseline Test");
        var stack = new AtlasCommandStack();

        var targetState = atlas.States.First(s => s.OwnerNationId == "AET");
        stack.Execute(atlas, new TransferTerritoryCommand(targetState.Id, "HAS"));
        Assert.Equal("HAS", atlas.States.First(s => s.Id == targetState.Id).OwnerNationId);

        var delta = AtlasVersioning.ComputeDelta(baseline, atlas);
        Assert.Contains(delta.OwnerChangedStates, entry => entry.Contains(targetState.Id) && entry.Contains("HAS"));

        var tempDir = Path.Combine(Path.GetTempPath(), $"voracity_atlas_ver_{Guid.NewGuid():N}");
        try
        {
            var verFile = AtlasVersioning.SaveVersionFile(tempDir, baseline);
            var deltaFile = Path.Combine(tempDir, "map_delta.json");
            AtlasVersioning.SaveDeltaJson(deltaFile, delta);

            Assert.True(File.Exists(verFile));
            Assert.True(File.Exists(deltaFile));
            Assert.NotEmpty(AtlasVersioning.ListSavedVersions(tempDir));

            AtlasVersioning.RestoreSnapshot(atlas, baseline);
            Assert.Equal("AET", atlas.States.First(s => s.Id == targetState.Id).OwnerNationId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
