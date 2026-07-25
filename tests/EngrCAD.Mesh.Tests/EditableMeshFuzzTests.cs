using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// Randomized operator sequences: guards must refuse invalid operations without
/// touching the mesh, and every accepted operation must preserve manifoldness (full
/// <see cref="EditableMesh.Validate"/> after each) and exact Euler-characteristic
/// bookkeeping. A second pass proves every accepted operation round-trips bit-exactly
/// under its change record.
/// </summary>
public class EditableMeshFuzzTests
{
    private static EditableMesh ClosedSphere() =>
        EditableMesh.FromMesh(MeshPrimitives.UvSphere(1, 10, 6).Triangulated());

    private static EditableMesh OpenGrid()
    {
        var positions = new List<Vector3d>();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
                positions.Add((x, y, 0));
        }
        var faces = new List<int[]>();
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                int i = y * 4 + x;
                faces.Add([i, i + 1, i + 5]);
                faces.Add([i, i + 5, i + 4]);
            }
        }
        return EditableMesh.FromMesh(HalfEdgeMesh.Build(positions, faces));
    }

    private static int RandomLiveHalfEdge(EditableMesh mesh, Random rng)
    {
        while (true)
        {
            int he = rng.Next(mesh.HalfEdgeCapacity);
            if (mesh.IsHalfEdge(he))
                return he;
        }
    }

    private static int RandomLiveFace(EditableMesh mesh, Random rng)
    {
        while (true)
        {
            int f = rng.Next(mesh.FaceCapacity);
            if (mesh.IsFace(f))
                return f;
        }
    }

    /// <summary>
    /// Performs one random operation; returns the expected Euler-characteristic delta
    /// (0 for all operators except merges, whose delta comes from the merge info).
    /// </summary>
    private static (MeshOperationResult Result, int EulerDelta) RandomOperation(
        EditableMesh mesh, Random rng, bool allowMerge)
    {
        int kind = rng.Next(allowMerge ? 5 : 4);
        switch (kind)
        {
            case 0:
                return (mesh.SplitEdge(RandomLiveHalfEdge(mesh, rng), out _, rng.NextDouble() * 0.9 + 0.05), 0);
            case 1:
                return (mesh.FlipEdge(RandomLiveHalfEdge(mesh, rng), out _), 0);
            case 2:
                // Keep enough faces around for further operations.
                if (mesh.FaceCount <= 8)
                    return (MeshOperationResult.WouldCreateNonManifold, 0);
                return (mesh.CollapseEdge(RandomLiveHalfEdge(mesh, rng), out _), 0);
            case 3:
                return (mesh.PokeFace(RandomLiveFace(mesh, rng), out _), 0);
            default:
            {
                var boundary = mesh.HalfEdgeIndices().Where(mesh.IsBoundaryHalfEdge).ToList();
                if (boundary.Count < 2)
                    return (MeshOperationResult.NotABoundaryEdge, 0);
                int keep = boundary[rng.Next(boundary.Count)];
                int discard = boundary[rng.Next(boundary.Count)];
                var result = mesh.MergeEdges(keep, discard, out var info);
                if (result != MeshOperationResult.Ok)
                    return (result, 0);
                int removedVertices = (info.RemovedVertex0 >= 0 ? 1 : 0) + (info.RemovedVertex1 >= 0 ? 1 : 0);
                // ΔV = -removed, ΔE = -(1 + extra welds), ΔF = 0.
                return (result, -removedVertices + 1 + info.ExtraWeldedEdges);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Fuzz_ClosedSphere_StaysManifoldWithExactEulerBookkeeping(int seed)
    {
        var rng = new Random(seed);
        var mesh = ClosedSphere();
        int expectedEuler = mesh.EulerCharacteristic;
        Assert.Equal(2, expectedEuler);

        int accepted = 0, refused = 0;
        for (int i = 0; i < 250; i++)
        {
            var countsBefore = (mesh.VertexCount, mesh.EdgeCount, mesh.FaceCount);
            int shapeBefore = mesh.ShapeTimestamp;
            var (result, eulerDelta) = RandomOperation(mesh, rng, allowMerge: false);
            if (result == MeshOperationResult.Ok)
            {
                accepted++;
                expectedEuler += eulerDelta;
                mesh.Validate();
                Assert.Equal(expectedEuler, mesh.EulerCharacteristic);
            }
            else
            {
                refused++;
                Assert.Equal(shapeBefore, mesh.ShapeTimestamp);
                Assert.Equal(countsBefore, (mesh.VertexCount, mesh.EdgeCount, mesh.FaceCount));
            }
        }

        Assert.True(accepted > 50, $"only {accepted} operations accepted");
        Assert.True(refused > 0, "expected some guarded refusals");
        var result2 = mesh.ToMesh(); // manifold-validating Build is the final safety net
        Assert.True(result2.IsClosed);
        Assert.Equal(2, result2.EulerCharacteristic);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Fuzz_OpenGrid_StaysManifoldIncludingMerges(int seed)
    {
        var rng = new Random(seed);
        var mesh = OpenGrid();
        int expectedEuler = mesh.EulerCharacteristic;
        Assert.Equal(1, expectedEuler);

        int accepted = 0;
        for (int i = 0; i < 200; i++)
        {
            var (result, eulerDelta) = RandomOperation(mesh, rng, allowMerge: true);
            if (result == MeshOperationResult.Ok)
            {
                accepted++;
                expectedEuler += eulerDelta;
                mesh.Validate();
                Assert.Equal(expectedEuler, mesh.EulerCharacteristic);
            }
        }

        Assert.True(accepted > 40, $"only {accepted} operations accepted");
        mesh.ToMesh().Validate();
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    public void Fuzz_EveryAcceptedOperationRoundTripsUnderItsChangeRecord(int seed)
    {
        var rng = new Random(seed);
        var mesh = ClosedSphere();

        for (int i = 0; i < 120; i++)
        {
            var before = mesh.CaptureState();
            var set = mesh.BeginChangeSet();
            var (result, _) = RandomOperation(mesh, rng, allowMerge: false);
            mesh.EndChangeSet();

            if (result != MeshOperationResult.Ok)
            {
                Assert.Empty(set.Changes);
                MeshStateAssert.Equal(before, mesh.CaptureState());
                continue;
            }

            var after = mesh.CaptureState();
            set.Revert(mesh);
            MeshStateAssert.Equal(before, mesh.CaptureState());
            set.Apply(mesh);
            MeshStateAssert.Equal(after, mesh.CaptureState());
        }
        mesh.Validate();
    }

    [Fact]
    public void Fuzz_WholeSessionRevertRestoresTheOriginalMeshBitExactly()
    {
        var rng = new Random(99);
        var mesh = OpenGrid();
        var initial = mesh.CaptureState();

        var session = mesh.BeginChangeSet();
        int accepted = 0;
        for (int i = 0; i < 150 || accepted < 30; i++)
        {
            if (RandomOperation(mesh, rng, allowMerge: true).Result == MeshOperationResult.Ok)
                accepted++;
            if (i > 600)
                break;
        }
        mesh.EndChangeSet();
        Assert.Equal(accepted, session.Changes.Count);
        var final = mesh.CaptureState();

        session.Revert(mesh);
        MeshStateAssert.Equal(initial, mesh.CaptureState());
        session.Apply(mesh);
        MeshStateAssert.Equal(final, mesh.CaptureState());
        mesh.Validate();
    }
}
