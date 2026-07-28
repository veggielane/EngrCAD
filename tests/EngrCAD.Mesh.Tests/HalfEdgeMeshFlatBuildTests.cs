using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The flat index-buffer <c>Build</c> overloads. There is only ONE construction path —
/// the loop-per-face overload flattens and calls the same core — so what needs locking
/// is that the three argument shapes describe the same mesh, that the core's counting-sort
/// twin resolution reproduces the hash-table one's structure and error reporting exactly,
/// and that the shapes validate their own contracts.
/// </summary>
public class HalfEdgeMeshFlatBuildTests
{
    /// <summary>Every stored array, in order — the strongest "same mesh" statement available.</summary>
    private static string Structure(HalfEdgeMesh mesh)
    {
        var text = new System.Text.StringBuilder();
        text.Append(mesh.VertexCount).Append('|').Append(mesh.HalfEdgeCount).Append('|').Append(mesh.FaceCount);
        for (int he = 0; he < mesh.HalfEdgeCount; he++)
        {
            var h = mesh.GetHalfEdge(he);
            text.Append(';').Append(h.Origin.Index).Append(',').Append(h.Next.Index)
                .Append(',').Append(h.Prev.Index).Append(',').Append(h.Twin.Index)
                .Append(',').Append(h.IsBoundary ? -1 : h.Face.Index);
        }
        for (int v = 0; v < mesh.VertexCount; v++)
            text.Append('#').Append(mesh.GetPosition(v));
        return text.ToString();
    }

    private static IReadOnlyList<Vector3d> CubePositions() =>
    [
        (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0),
        (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1),
    ];

    private static int[][] CubeQuads() =>
    [
        [0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4],
        [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7],
    ];

    [Fact]
    public void TheThreeArgumentShapes_ProduceIdenticalMeshes()
    {
        var positions = CubePositions();
        var quads = CubeQuads();
        var corners = quads.SelectMany(q => q).ToArray();
        var faceStarts = new[] { 0, 4, 8, 12, 16, 20, 24 };

        var byLoop = HalfEdgeMesh.Build(positions, quads);
        var byOffsets = HalfEdgeMesh.Build(positions, corners, faceStarts);
        var byStride = HalfEdgeMesh.Build(positions, corners, verticesPerFace: 4);

        byLoop.Validate();
        Assert.Equal(Structure(byLoop), Structure(byOffsets));
        Assert.Equal(Structure(byLoop), Structure(byStride));
        Assert.Equal(1.0, byStride.Volume(), 12);
    }

    [Fact]
    public void MixedDegreeFaces_RoundTripThroughTheOffsetForm()
    {
        // A pyramid: one quad base plus four triangles — the offsets form has to carry a
        // ragged face table, which the uniform-stride form cannot express.
        IReadOnlyList<Vector3d> positions =
            [(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0), (1, 1, 2)];
        int[][] faces = [[0, 3, 2, 1], [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]];
        var corners = faces.SelectMany(f => f).ToArray();
        var faceStarts = new[] { 0, 4, 7, 10, 13, 16 };

        var byLoop = HalfEdgeMesh.Build(positions, faces);
        var byOffsets = HalfEdgeMesh.Build(positions, corners, faceStarts);

        byOffsets.Validate();
        Assert.True(byOffsets.IsClosed);
        Assert.Equal(Structure(byLoop), Structure(byOffsets));
    }

    [Fact]
    public void AnOpenPatch_GetsTheSameBoundaryLoopEitherWay()
    {
        // Boundary half-edges are created in a second pass keyed by origin vertex; an open
        // mesh is the only thing that exercises it.
        IReadOnlyList<Vector3d> positions =
            [(0, 0, 0), (1, 0, 0), (2, 0, 0), (0, 1, 0), (1, 1, 0), (2, 1, 0)];
        int[][] faces = [[0, 1, 4, 3], [1, 2, 5, 4]];
        var corners = faces.SelectMany(f => f).ToArray();

        var byLoop = HalfEdgeMesh.Build(positions, faces);
        var byStride = HalfEdgeMesh.Build(positions, corners, verticesPerFace: 4);

        Assert.False(byStride.IsClosed);
        Assert.Equal(6, Assert.Single(byStride.BoundaryLoops()).Count);
        Assert.Equal(Structure(byLoop), Structure(byStride));
    }

    // ---- the manifold refusals, in the flat shapes ----

    [Fact]
    public void FlatBuild_RejectsANonManifoldEdge_WithTheSameMessage()
    {
        var positions = new Vector3d[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1), (0, -1, 0) };
        int[] corners = [0, 1, 2, 1, 0, 3, 0, 1, 4];
        var ex = Assert.Throws<ArgumentException>(
            () => HalfEdgeMesh.Build(positions, corners, verticesPerFace: 3));
        Assert.Contains("Directed edge 0 → 1 appears twice", ex.Message);
        Assert.Contains("non-manifold", ex.Message);
    }

    [Fact]
    public void FlatBuild_RejectsInconsistentWinding()
    {
        var positions = new Vector3d[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0) };
        int[] corners = [0, 1, 2, 3, 1, 2];
        Assert.Throws<ArgumentException>(() => HalfEdgeMesh.Build(positions, corners, verticesPerFace: 3));
    }

    [Fact]
    public void FlatBuild_RejectsADegenerateEdge_NamingTheFace()
    {
        var positions = new Vector3d[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (2, 0, 0) };
        int[] corners = [0, 1, 2, 1, 3, 3];
        var ex = Assert.Throws<ArgumentException>(
            () => HalfEdgeMesh.Build(positions, corners, verticesPerFace: 3));
        Assert.Contains("Face 1 contains a degenerate edge (3 → 3)", ex.Message);
    }

    [Fact]
    public void FlatBuild_RejectsAnOutOfRangeIndex()
    {
        var positions = new Vector3d[] { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        int[] corners = [0, 1, 7];
        var ex = Assert.Throws<ArgumentException>(
            () => HalfEdgeMesh.Build(positions, corners, verticesPerFace: 3));
        Assert.Contains("vertex 7", ex.Message);
        Assert.Contains("only 3 positions", ex.Message);
    }

    [Fact]
    public void FlatBuild_RejectsABowTieVertex()
    {
        // Two triangles meeting at one vertex only: the vertex carries two boundary fans.
        var positions = new Vector3d[] { (0, 0, 0), (1, 0, 0), (1, 1, 0), (-1, 0, 0), (-1, -1, 0) };
        int[] corners = [0, 1, 2, 0, 4, 3];
        var ex = Assert.Throws<ArgumentException>(
            () => HalfEdgeMesh.Build(positions, corners, verticesPerFace: 3));
        Assert.Contains("bow-tie", ex.Message);
    }

    // ---- the contracts of the shapes themselves ----

    [Fact]
    public void OffsetForm_RejectsAFaceTableThatDoesNotSpanTheBuffer()
    {
        var positions = CubePositions();
        var corners = CubeQuads().SelectMany(q => q).ToArray();
        var ex = Assert.Throws<ArgumentException>(
            () => HalfEdgeMesh.Build(positions, corners, new[] { 0, 4, 8 }));
        Assert.Contains("faceStarts", ex.Message);
    }

    [Fact]
    public void StrideForm_RejectsARaggedBufferAndADegenerateStride()
    {
        var positions = CubePositions();
        var corners = new[] { 0, 1, 2, 3, 4 };
        Assert.Throws<ArgumentException>(() => HalfEdgeMesh.Build(positions, corners, verticesPerFace: 4));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HalfEdgeMesh.Build(positions, corners, verticesPerFace: 2));
    }

    [Fact]
    public void FlatBuild_RejectsAFaceWithFewerThanThreeCorners()
    {
        var positions = new Vector3d[] { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var ex = Assert.Throws<ArgumentException>(
            () => HalfEdgeMesh.Build(positions, new[] { 0, 1, 2, 0, 1 }, new[] { 0, 3, 5 }));
        Assert.Contains("at least 3", ex.Message);
    }

    /// <summary>
    /// A vertex fan of high valence is where the counting sort's per-bucket scan is
    /// longest, and a shared HUB vertex is where a hash table and a bucket disagree most
    /// visibly if the bucket key were wrong: every one of these edges keys on the hub.
    /// </summary>
    [Fact]
    public void AHighValenceFan_PairsCorrectly()
    {
        const int spokes = 64;
        var positions = new List<Vector3d> { Vector3d.Zero };
        for (int i = 0; i < spokes; i++)
        {
            double angle = 2 * Math.PI * i / spokes;
            positions.Add((Math.Cos(angle), Math.Sin(angle), 0));
        }
        var corners = new List<int>();
        for (int i = 0; i < spokes; i++)
        {
            corners.Add(0);
            corners.Add(1 + i);
            corners.Add(1 + (i + 1) % spokes);
        }

        var mesh = HalfEdgeMesh.Build(positions, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners), 3);
        mesh.Validate();
        Assert.Equal(spokes, mesh.GetVertex(0).Valence);
        Assert.Equal(spokes, Assert.Single(mesh.BoundaryLoops()).Count);
    }
}
