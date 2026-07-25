using EngrCAD.Core;
using Xunit;
using MeshState = (EngrCAD.Core.Vector3d[] Positions, int[] VertexOut, bool[] VertexAlive,
    int[] HeOrigin, int[] HeNext, int[] HePrev, int[] HeTwin, int[] HeFace, bool[] HeAlive,
    int[] FaceHe, bool[] FaceAlive,
    int VertexFreeHead, int HeFreeHead, int FaceFreeHead,
    int AliveVertices, int AliveHes, int AliveFaces);

namespace EngrCAD.Mesh.Tests;

/// <summary>Bit-identity assertions over the full editable-mesh SoA storage.</summary>
internal static class MeshStateAssert
{
    public static void Equal(in MeshState expected, in MeshState actual)
    {
        Assert.Equal(expected.Positions.Length, actual.Positions.Length);
        for (int i = 0; i < expected.Positions.Length; i++)
        {
            // Exact double equality is intended: change records must restore bits.
            Assert.Equal(expected.Positions[i].X, actual.Positions[i].X);
            Assert.Equal(expected.Positions[i].Y, actual.Positions[i].Y);
            Assert.Equal(expected.Positions[i].Z, actual.Positions[i].Z);
        }
        Assert.Equal(expected.VertexOut, actual.VertexOut);
        Assert.Equal(expected.VertexAlive, actual.VertexAlive);
        Assert.Equal(expected.HeOrigin, actual.HeOrigin);
        Assert.Equal(expected.HeNext, actual.HeNext);
        Assert.Equal(expected.HePrev, actual.HePrev);
        Assert.Equal(expected.HeTwin, actual.HeTwin);
        Assert.Equal(expected.HeFace, actual.HeFace);
        Assert.Equal(expected.HeAlive, actual.HeAlive);
        Assert.Equal(expected.FaceHe, actual.FaceHe);
        Assert.Equal(expected.FaceAlive, actual.FaceAlive);
        Assert.Equal(expected.VertexFreeHead, actual.VertexFreeHead);
        Assert.Equal(expected.HeFreeHead, actual.HeFreeHead);
        Assert.Equal(expected.FaceFreeHead, actual.FaceFreeHead);
        Assert.Equal(expected.AliveVertices, actual.AliveVertices);
        Assert.Equal(expected.AliveHes, actual.AliveHes);
        Assert.Equal(expected.AliveFaces, actual.AliveFaces);
    }
}

public class EditableMeshChangeTests
{
    private static EditableMesh Sphere() =>
        EditableMesh.FromMesh(MeshPrimitives.UvSphere(1, 8, 5).Triangulated());

    private static int InteriorHalfEdge(EditableMesh mesh) =>
        mesh.HalfEdgeIndices().First(he => !mesh.IsBoundaryEdge(he));

    /// <summary>Runs one recorded operation, then proves do → revert → apply round-trips bit-identically.</summary>
    private static void AssertRoundTrips(EditableMesh mesh, Func<EditableMesh, MeshOperationResult> operation)
    {
        var before = mesh.CaptureState();
        var set = mesh.BeginChangeSet();
        Assert.Equal(MeshOperationResult.Ok, operation(mesh));
        mesh.EndChangeSet();
        Assert.Single(set.Changes);
        var after = mesh.CaptureState();

        set.Revert(mesh);
        MeshStateAssert.Equal(before, mesh.CaptureState());
        mesh.Validate();

        set.Apply(mesh);
        MeshStateAssert.Equal(after, mesh.CaptureState());
        mesh.Validate();
    }

    [Fact]
    public void SplitEdge_RoundTripsUnderChangeRecord() =>
        AssertRoundTrips(Sphere(), m => m.SplitEdge(InteriorHalfEdge(m), out _, 0.3));

    [Fact]
    public void FlipEdge_RoundTripsUnderChangeRecord()
    {
        var mesh = Sphere();
        int he = mesh.HalfEdgeIndices().First(h => mesh.FlipCandidate(h));
        AssertRoundTrips(mesh, m => m.FlipEdge(he, out _));
    }

    [Fact]
    public void CollapseEdge_RoundTripsUnderChangeRecord() =>
        AssertRoundTrips(Sphere(), m => m.CollapseEdge(InteriorHalfEdge(m), out _));

    [Fact]
    public void PokeFace_RoundTripsUnderChangeRecord() =>
        AssertRoundTrips(Sphere(), m => m.PokeFace(m.FaceIndices().First(), out _));

    [Fact]
    public void MergeEdges_RoundTripsUnderChangeRecord()
    {
        // The folded strip exercises the auto-weld path inside a single record.
        var mesh = EditableMesh.FromIndexed(
            [(0, 0, 0), (0, 1, 0), (1, 0, 0), (0, 1, 0)],
            [new[] { 1, 2, 0 }, new[] { 2, 3, 0 }]);
        int keep = mesh.FindHalfEdge(1, 0);
        int discard = mesh.FindHalfEdge(0, 3);
        AssertRoundTrips(mesh, m => m.MergeEdges(keep, discard, out _));
    }

    [Fact]
    public void SetPosition_RoundTripsUnderChangeRecord() =>
        AssertRoundTrips(Sphere(), m =>
        {
            m.SetPosition(3, (0.123456789012345, -7, 1e-13));
            return MeshOperationResult.Ok;
        });

    [Fact]
    public void ChangeSet_GroupsASessionAndRoundTripsAsAUnit()
    {
        var mesh = Sphere();
        var initial = mesh.CaptureState();

        var session = mesh.BeginChangeSet();
        Assert.Same(session, mesh.ActiveChangeSet);
        Assert.Equal(MeshOperationResult.Ok, mesh.SplitEdge(InteriorHalfEdge(mesh), out var split));
        mesh.SetPosition(split.NewVertex, (2, 2, 2));
        Assert.Equal(MeshOperationResult.Ok, mesh.CollapseEdge(InteriorHalfEdge(mesh), out _));
        Assert.Equal(MeshOperationResult.Ok, mesh.PokeFace(mesh.FaceIndices().First(), out _));
        mesh.EndChangeSet();
        Assert.Null(mesh.ActiveChangeSet);
        Assert.Equal(4, session.Changes.Count);
        var final = mesh.CaptureState();

        session.Revert(mesh);
        MeshStateAssert.Equal(initial, mesh.CaptureState());
        session.Apply(mesh);
        MeshStateAssert.Equal(final, mesh.CaptureState());

        // Records name their operations for editor UI.
        Assert.Equal(
            new[] { "SplitEdge", "SetPosition", "CollapseEdge", "PokeFace" },
            session.Changes.Select(c => c.Operation));
    }

    [Fact]
    public void RefusedOperation_EmitsNoRecord()
    {
        var mesh = EditableMesh.FromIndexed(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)],
            [new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 2, 0, 3 }]);
        var set = mesh.BeginChangeSet();
        Assert.Equal(MeshOperationResult.WouldCollapseTetrahedron, mesh.CollapseEdge(0, out _));
        Assert.Equal(MeshOperationResult.EdgeAlreadyExists, mesh.FlipEdge(0, out _));
        mesh.EndChangeSet();
        Assert.Empty(set.Changes);
    }

    [Fact]
    public void OutOfOrderReplay_ThrowsInsteadOfCorrupting()
    {
        var mesh = Sphere();
        var set = mesh.BeginChangeSet();
        Assert.Equal(MeshOperationResult.Ok, mesh.SplitEdge(InteriorHalfEdge(mesh), out _));
        mesh.EndChangeSet();

        // Applying on top of the already-applied state must fail loudly.
        Assert.Throws<InvalidOperationException>(() => set.Apply(mesh));
        // Reverting twice likewise.
        set.Revert(mesh);
        Assert.Throws<InvalidOperationException>(() => set.Revert(mesh));
    }

    [Fact]
    public void ChangeSetLifecycle_Guards()
    {
        var mesh = Sphere();
        mesh.BeginChangeSet();
        Assert.Throws<InvalidOperationException>(() => mesh.BeginChangeSet());
        mesh.EndChangeSet();
        Assert.Throws<InvalidOperationException>(() => mesh.EndChangeSet());
    }

    [Fact]
    public void ReplayWhileRecording_Throws()
    {
        var mesh = Sphere();
        var set = mesh.BeginChangeSet();
        Assert.Equal(MeshOperationResult.Ok, mesh.SplitEdge(InteriorHalfEdge(mesh), out _));
        mesh.EndChangeSet();
        set.Revert(mesh);

        mesh.BeginChangeSet();
        Assert.Throws<InvalidOperationException>(() => set.Apply(mesh));
        mesh.EndChangeSet();
    }
}

internal static class EditableMeshTestExtensions
{
    /// <summary>An interior triangle edge whose flip target edge does not already exist.</summary>
    public static bool FlipCandidate(this EditableMesh mesh, int he)
    {
        if (!mesh.IsHalfEdge(he) || mesh.IsBoundaryEdge(he))
            return false;
        int c = mesh.Origin(mesh.Prev(he));
        int d = mesh.Origin(mesh.Prev(mesh.Twin(he)));
        return c != d && mesh.FindHalfEdge(c, d) < 0;
    }
}
