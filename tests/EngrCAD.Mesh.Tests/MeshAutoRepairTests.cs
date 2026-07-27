using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The editor-powered repairs: pair-wise crack welding (<c>MergeCoincidentEdges</c>) and
/// the full <c>AutoRepair</c> dispatch that adds it and hole filling on top of
/// <c>Clean</c>'s soup passes.
/// </summary>
public class MeshAutoRepairTests
{
    /// <summary>
    /// A unit box whose top face has been detached: its four corners are duplicated and
    /// lifted by <paramref name="gap"/>, so the rim is a crack — two coincident boundary
    /// loops running in opposite directions, too far apart for a vertex weld to see.
    /// </summary>
    private static (IReadOnlyList<Vector3d> Positions, List<int[]> Faces) BoxWithDetachedLid(double gap)
    {
        var (positions, faces) = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated().ToIndexed();
        var points = new List<Vector3d>(positions);
        var lifted = new Dictionary<int, int>();

        foreach (var face in faces)
        {
            if (!face.All(v => positions[v].Z == 1))
                continue; // the two triangles of the top face
            for (int i = 0; i < face.Length; i++)
            {
                if (!lifted.TryGetValue(face[i], out int copy))
                {
                    lifted[face[i]] = copy = points.Count;
                    points.Add(positions[face[i]] + new Vector3d(0, 0, gap));
                }
                face[i] = copy;
            }
        }
        Assert.Equal(4, lifted.Count);
        return (points, faces);
    }

    private static HalfEdgeMesh BoxMissingItsLid()
    {
        var (positions, faces) = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated().ToIndexed();
        return HalfEdgeMesh.Build(positions, faces.Where(f => !f.All(v => positions[v].Z == 1)).ToList());
    }

    // ---------------------------------------------------------------- crack welding

    [Fact]
    public void MergeCoincidentEdges_ClosesACrackVertexWeldingCannotSee()
    {
        // The lid sits 5e-5 above the rim: way past the 1e-7 seam tier a vertex weld uses,
        // so Clean can only report the boundary. Merging by EDGE pairs closes it, and
        // because every merge runs the operator's guards the loose tolerance is safe.
        var (positions, faces) = BoxWithDetachedLid(5e-5);

        var (cleaned, cleanReport) = MeshRepair.Clean(positions, faces);
        Assert.False(cleaned.IsClosed);
        Assert.Equal(2, cleaned.BoundaryLoops().Count); // the box's rim and the lid's

        var editable = EditableMesh.FromMesh(cleaned);
        int merged = MeshRepair.MergeCoincidentEdges(editable, 1e-3);
        var welded = editable.ToMesh();

        Assert.Equal(4, merged);
        Assert.True(welded.IsClosed);
        welded.Validate();
        Assert.Equal(2, welded.EulerCharacteristic);
        // Welding never moves geometry: the lid keeps its lifted coordinates.
        Assert.Equal(1 + 5e-5, welded.ComputeBounds().Max.Z, 12);
        Assert.Equal(0, cleanReport.CracksMerged); // Clean does not do surgery
    }

    [Fact]
    public void MergeCoincidentEdges_RefusesEdgesThatRunTheSameWay()
    {
        // Same crack, but the lid is wound the other way — its boundary half-edges run
        // parallel to the rim's rather than against them. Welding those would fold the
        // surface onto itself, so the pass must leave them alone.
        var (positions, faces) = BoxWithDetachedLid(5e-5);
        foreach (var face in faces)
        {
            if (face.All(v => positions[v].Z > 1))
                Array.Reverse(face);
        }
        var mesh = EditableMesh.FromMesh(HalfEdgeMesh.Build(positions, faces));

        int merged = MeshRepair.MergeCoincidentEdges(mesh, 1e-3);

        Assert.Equal(0, merged);
        mesh.Validate();
        Assert.False(mesh.IsClosed);
    }

    [Fact]
    public void MergeCoincidentEdges_OnAClosedMeshDoesNothing()
    {
        var mesh = EditableMesh.FromMesh(MeshPrimitives.UvSphere(1, 12, 6).Triangulated());
        int before = mesh.FaceCount;

        Assert.Equal(0, MeshRepair.MergeCoincidentEdges(mesh, 1.0));

        Assert.Equal(before, mesh.FaceCount);
        mesh.Validate();
    }

    [Fact]
    public void MergeCoincidentEdges_RejectsANonPositiveTolerance()
    {
        var mesh = EditableMesh.FromMesh(MeshPrimitives.Box(1, 1, 1).Triangulated());
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshRepair.MergeCoincidentEdges(mesh, 0));
    }

    // ---------------------------------------------------------------- the dispatch

    [Fact]
    public void AutoRepair_WeldsTheCrackAndReportsIt()
    {
        var (positions, faces) = BoxWithDetachedLid(5e-5);

        var (mesh, report) = MeshRepair.AutoRepair(
            positions, faces, new MeshRepairOptions { CrackMergeTolerance = 1e-3 });

        Assert.True(mesh.IsClosed);
        Assert.True(report.IsClosed);
        Assert.Equal(4, report.CracksMerged);
        Assert.Equal(0, report.HolesFilled);
        Assert.Equal(1 + 5e-5, mesh.Volume(), 6); // the lifted lid adds its sliver
        Assert.Contains("welded 4 crack edges", report.ToString());
    }

    [Fact]
    public void AutoRepair_FillsARealHole()
    {
        // Nothing coincident to weld here — the lid is simply missing, which is a hole.
        var open = BoxMissingItsLid();

        var (mesh, report) = MeshRepair.AutoRepair(open);

        Assert.True(mesh.IsClosed);
        Assert.Equal(0, report.CracksMerged);
        Assert.Equal(1, report.HolesFilled);
        Assert.Equal(0, report.HolesSkipped);
        Assert.Equal(1.0, mesh.Volume(), 12); // the planar fill restores the exact box
    }

    [Fact]
    public void AutoRepair_OnACleanSoupSkipsTheSurgeryEntirely()
    {
        var (positions, faces) = MeshPrimitives.Box(1, 2, 3).Triangulated().ToIndexed();

        var (mesh, report) = MeshRepair.AutoRepair(positions, faces);

        Assert.True(mesh.IsClosed);
        Assert.Equal(0, report.CracksMerged);
        Assert.Equal(0, report.HolesFilled);
        Assert.Equal(0, report.HolesSkipped);
        Assert.Equal(6.0, mesh.Volume(), 12);
    }

    /// <summary>
    /// A long, wildly non-planar boundary the fan fill would self-intersect on. The
    /// minimum-weight triangulation of the rim's own vertices is the default fallback and
    /// handles exactly this, so a repair pipeline closes it — inventing nothing, since the
    /// patch's vertices are the hole's.
    /// </summary>
    private static HalfEdgeMesh SphereWithANonPlanarBiteRemoved()
    {
        var mesh = MeshPrimitives.UvSphere(1, segments: 32, rings: 16).Triangulated();
        var (positions, faces) = mesh.ToIndexed();
        var kept = faces.Where(f => !f.Any(v => positions[v].Z > 0.3 && positions[v].X > 0)).ToList();
        var open = HalfEdgeMesh.Build(positions, kept);
        Assert.False(open.IsClosed);
        return open;
    }

    [Fact]
    public void AutoRepair_ClosesALongNonPlanarHoleWithTheMinimalFill()
    {
        var open = SphereWithANonPlanarBiteRemoved();
        int vertices = open.VertexCount;

        var (repaired, report) = MeshRepair.AutoRepair(
            open, new MeshRepairOptions { HoleFill = new HoleFillOptions { MaxSimpleFillVertices = 4 } });

        Assert.Equal(0, report.CracksMerged);
        Assert.Equal(0, report.HolesSkipped);
        Assert.True(report.HolesFilled > 0);
        Assert.True(repaired.IsClosed);
        // The fill invents nothing, so the count can only go DOWN — Clean drops the
        // vertices the removed faces orphaned before the hole is ever reached.
        Assert.True(repaired.VertexCount <= vertices,
            $"the minimal fill must add no vertices; {vertices} -> {repaired.VertexCount}");
    }

    [Fact]
    public void AutoRepair_ReportsHolesItRefusesToFill()
    {
        // Where even the minimal tier declines — here because the rim is past its cubic
        // dynamic program's size cap — the dispatch has to say so rather than emit garbage.
        var open = SphereWithANonPlanarBiteRemoved();

        var (repaired, report) = MeshRepair.AutoRepair(open, new MeshRepairOptions
        {
            HoleFill = new HoleFillOptions { MaxSimpleFillVertices = 4, MaxMinimalFillVertices = 8 },
        });

        Assert.True(report.HolesSkipped > 0);
        Assert.False(repaired.IsClosed);
        Assert.Contains(report.Notes, n => n.Contains("left open"));
    }
}
