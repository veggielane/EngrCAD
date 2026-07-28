using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Booleans between solids that share boundary instead of crossing it — flush embossing,
/// stacked plates, blocks butted together, a pocket whose floor is the host's own face. The
/// model is the mesh boolean's, translated: the shared region's rim is imprinted by the
/// ordinary TRANSVERSAL curves of the neighbouring faces, and the coincident fragments
/// themselves are classified by NORMAL AGREEMENT rather than by an inside/outside probe that
/// reads zero there.
///
/// <para>Before this tier a flush union took the disjoint fast path and returned the operands
/// as two touching shells — closed, valid, right volume, wrong topology — and most flush
/// configurations threw <c>Arrangement tracing did not close</c> outright.</para>
/// </summary>
public class CoplanarBooleanTests
{
    /// <summary>Validate-clean, ONE shell, closed tessellation — then the volume.</summary>
    private static double FusedVolume(Shape shape, int genus = 0)
    {
        var solid = shape.ToBrep();
        solid.Validate();
        Assert.Single(solid.Shells);
        Assert.True(solid.SatisfiesEulerFormula(genus), $"result must satisfy Euler–Poincaré at genus {genus}");
        var mesh = shape.ToMesh();
        mesh.Validate();
        Assert.True(mesh.IsClosed, "result must tessellate closed");
        return mesh.Volume();
    }

    /// <summary>Plate spanning z ∈ [−5, 5]; its top face is the mating plane.</summary>
    private static Shape Plate() => Shape.Box(40, 30, 10);

    [Fact]
    public void FlushEmboss_FusesIntoOneSolid()
    {
        // A boss sitting exactly ON the plate's top face: outward normals oppose, so the
        // shared surface is interior to the union and BOTH copies drop.
        var boss = Shape.Box(10, 8, 4).Translate(new Vector3d(0, 0, 7));
        Assert.Equal(40 * 30 * 10 + 10 * 8 * 4, FusedVolume(Plate() | boss), 9);
    }

    [Fact]
    public void FlushEmboss_KeepsNeitherCopyOfTheSharedFace()
    {
        // The plate's top face is imprinted by the boss's four walls, so it comes back in
        // pieces — but the piece UNDER the boss, and the boss's own underside, are both gone.
        var boss = Shape.Box(10, 8, 4).Translate(new Vector3d(0, 0, 7));
        var solid = (Plate() | boss).ToBrep();

        var atTheMatingPlane = solid.Faces
            .Where(f => f.IsPlanar(out var origin, out var normal) &&
                normal.IsParallelTo(Vector3d.UnitZ, Tolerance.Default) &&
                Math.Abs(origin.Z - 5) < 1e-9)
            .ToList();
        Assert.All(atTheMatingPlane, f => Assert.Equal(1, Math.Round(f.NormalAt(ProbeOn(f)).Z)));
    }

    [Fact]
    public void StackedPlates_FuseIntoOnePrism()
    {
        // The whole face is shared, so nothing is imprinted at all: the two coincident faces
        // simply drop and SealSeams pairs the rim edges left behind. This is also the case
        // that would otherwise slip through the DISJOINT fast path, since every intersection
        // curve runs along an existing boundary edge.
        var lower = Shape.Box(40, 30, 10);
        var upper = Shape.Box(40, 30, 6).Translate(new Vector3d(0, 0, 8));
        Assert.Equal(40 * 30 * 16, FusedVolume(lower | upper), 9);
    }

    [Fact]
    public void ButtedBlocks_FuseAcrossTheSharedWall()
    {
        var left = Shape.Box(10, 10, 10).Translate(new Vector3d(-5, 0, 0));
        var right = Shape.Box(10, 10, 10).Translate(new Vector3d(5, 0, 0));
        Assert.Equal(10 * 10 * 20, FusedVolume(left | right), 9);
    }

    [Fact]
    public void PartiallyOverlappingCoplanarPlates_Fuse()
    {
        // The overlap is a STRIP, so neither face's centroid lies inside the other — the
        // coincidence has to be found by sampling the shared area, not by probing centroids.
        var lower = Shape.Box(20, 10, 10);                                   // x ∈ [−10, 10]
        var upper = Shape.Box(20, 10, 6).Translate(new Vector3d(15, 0, 8));  // x ∈ [5, 25]
        Assert.Equal(20 * 10 * 10 + 20 * 10 * 6, FusedVolume(lower | upper), 9);
    }

    [Fact]
    public void PocketFlushWithBothFaces_CutsCleanThrough()
    {
        // Tool and host share BOTH horizontal faces, normals agreeing: A minus B removes all
        // of A's material there, so both copies drop and the pocket becomes a through slot.
        var tool = Shape.Box(12, 8, 10);
        Assert.Equal(40 * 30 * 10 - 12 * 8 * 10, FusedVolume(Plate() - tool, genus: 1), 9);
    }

    [Fact]
    public void PocketFlushWithTheFarFace_LeavesACeiling()
    {
        var tool = Shape.Box(10, 8, 8).Translate(new Vector3d(0, 0, -1)); // z ∈ [−5, 3]
        Assert.Equal(40 * 30 * 10 - 10 * 8 * 8, FusedVolume(Plate() - tool), 9);
    }

    [Fact]
    public void IntersectionSharingTopAndBottom_KeepsOneCopy()
    {
        // Normals AGREE on both shared planes (both solids lie below the top and above the
        // bottom), so the surface bounds the intersection — one copy survives, the first
        // solid's, which is the documented asymmetry.
        var wide = Shape.Box(20, 20, 10);
        var tall = Shape.Box(10, 30, 10);
        Assert.Equal(10 * 20 * 10, FusedVolume(wide & tall), 9);
    }

    [Fact]
    public void UnionOfNestedFootprintsSharingTheTop_KeepsOneCopy()
    {
        // Same-normal coincidence under a UNION: the small block is swallowed, its top face
        // is coplanar with the big one's, and exactly one copy of the shared patch survives.
        var big = Shape.Box(20, 20, 10);
        var small = Shape.Box(10, 10, 10);
        Assert.Equal(20 * 20 * 10, FusedVolume(big | small), 9);
    }

    /// <summary>
    /// Coincident CURVED surface is refused BY NAME. A shaft the same diameter as its bore is
    /// the case that actually occurs, and deciding the shared region's rim there needs
    /// surface–surface re-intersection the kernel does not have — so it says so, rather than
    /// cracking along the whole contact patch and reporting an unclosed solid.
    /// </summary>
    [Fact]
    public void ShaftCoincidentWithItsOwnBore_RefusesByName()
    {
        var top = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
        var bored = Plate().Drill(HoleSpec.Simple(10), [new Vector2d(0, 0)], 12, top);
        var shaft = Shape.Cylinder(5, 30);

        var message = Assert.ThrowsAny<Exception>(() => (bored | shaft).ToBrep()).Message;
        Assert.Contains("coincident CYLINDRICAL faces", message);
        Assert.Contains("radius-5", message);
    }

    /// <summary>A point strictly inside a planar face, for reading its outward normal.</summary>
    private static Vector3d ProbeOn(BrepFace face)
    {
        var loops = FaceGeometry.PullLoops(face);
        var outer = loops[0];
        var uv = new Vector2d(outer.Average(p => p.X), outer.Average(p => p.Y));
        return face.Surface.PointAt(uv.X, uv.Y);
    }
}
