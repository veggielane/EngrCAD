using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Shelling through two openings that SHARE an edge. Their rims meet at zero width there, so
/// the annulus is cut open and the two boundaries become one loop — a loop-surgery pass rather
/// than the "merge the two openings into one face" the backlog first proposed (they lie on
/// different planes, so that face does not exist).
///
/// <para><b>Validate() is deliberately not the oracle here.</b> A half-done merge leaves a
/// solid that validates and is wrong — every edge used twice, every loop closed, and material
/// where the rim should be open — so the tests below assert exact CORNER POSITIONS and exact
/// loop CONTENT, with the closed-form volumes in Interop.Tests where a mesh exists.</para>
/// </summary>
public class AdjacentOpeningTests
{
    private const double Tight = 1e-12;

    private static BrepSolid Cube() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10)));

    private static BrepFace FaceWithNormal(BrepSolid solid, Vector3d normal) =>
        solid.PlanarFacesWithNormal(normal).Single();

    private static bool At(in Vector3d point, double x, double y, double z) =>
        point.AreEqual((x, y, z), new Tolerance(Tight, Tight));

    [Fact]
    public void TopAndSideOpen_MergesTheTwoRimsIntoOneLoopEach()
    {
        // A 10-cube open on top (z = 10) and on one side (x = 10), 1 thick. Both opening
        // planes stay put, so the cavity is x in [1, 10], y in [1, 9], z in [1, 10].
        var cube = Cube();
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var side = FaceWithNormal(cube, Vector3d.UnitX);
        var tray = Shelling.Shell(cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, side));
        tray.Validate();
        Assert.Single(tray.Shells);
        Assert.True(tray.SatisfiesEulerFormula(genus: 0));

        // 4 outer walls + 4 inner walls + 2 rims. Each rim is ONE simply-connected face with
        // ONE loop — not an annulus, which is the whole difference this feature makes.
        Assert.Equal(10, tray.Faces.Count());
        Assert.Equal(10, tray.Loops.Count());
        Assert.All(tray.Faces, f => Assert.Single(f.Loops));

        // The two rims are the faces on the opening planes; each closes an 8-corner "C".
        var topRim = tray.Faces.Single(f =>
            f.IsPlanar(out var o, out var n) && n.Z > 0.9 && Math.Abs(o.Z - 10) < Tight);
        var sideRim = tray.Faces.Single(f =>
            f.IsPlanar(out var o, out var n) && n.X > 0.9 && Math.Abs(o.X - 10) < Tight);
        Assert.Equal(8, topRim.OuterLoop.Coedges.Count);
        Assert.Equal(8, sideRim.OuterLoop.Coedges.Count);
    }

    [Fact]
    public void TheMergedRimTracesTheExactCShape()
    {
        // The loop's own corners, in traversal order, against the closed form: the outer
        // square [0,10]^2 minus the inner rectangle [1,10] x [1,9], cut open along x = 10.
        var cube = Cube();
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var side = FaceWithNormal(cube, Vector3d.UnitX);
        var tray = Shelling.Shell(cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, side));

        var topRim = tray.Faces.Single(f =>
            f.IsPlanar(out var o, out var n) && n.Z > 0.9 && Math.Abs(o.Z - 10) < Tight);
        var walk = topRim.OuterLoop.Coedges.Select(c => c.StartVertex.Position).ToList();

        // Every corner is at z = 10 (the opening's plane never moved) and the SET is exactly
        // the eight the C-shape has. Compared as a set because which corner the loop starts
        // at is the input face's own loop order, not a property of the surgery.
        Assert.All(walk, p => Assert.Equal(10, p.Z, 12));
        (double X, double Y)[] expected =
        [
            (0, 0), (10, 0), (10, 1), (1, 1), (1, 9), (10, 9), (10, 10), (0, 10),
        ];
        foreach (var (x, y) in expected)
            Assert.Contains(walk, p => At(p, x, y, 10));
        Assert.Equal(8, walk.Count);

        // ... and consecutive corners are joined by an axis-aligned edge, so the walk really
        // traces the C rather than visiting the same eight points in some other order.
        for (int i = 0; i < walk.Count; i++)
        {
            var step = walk[(i + 1) % walk.Count] - walk[i];
            Assert.True(step.Length > Tight, "no zero-length edge in the merged rim");
            Assert.True(Math.Abs(step.X) < Tight || Math.Abs(step.Y) < Tight,
                $"the rim's edge {i} is not axis aligned: {step}");
        }
    }

    [Fact]
    public void TheSharedEdgeIsCutBackToItsTwoEndPieces_SharedByBothRims()
    {
        // The load-bearing claim: the shared edge's middle stretch (between the two inner
        // corners) belongs to NEITHER rim and is not built, while its two end pieces are ONE
        // object each, used by both rims in opposite directions.
        var cube = Cube();
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var side = FaceWithNormal(cube, Vector3d.UnitX);
        var tray = Shelling.Shell(cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, side));

        // Edges lying on the shared line x = 10, z = 10.
        var onSharedLine = tray.Edges.Where(e =>
        {
            var a = e.StartVertex.Position;
            var b = e.EndVertex.Position;
            return Math.Abs(a.X - 10) < Tight && Math.Abs(a.Z - 10) < Tight
                && Math.Abs(b.X - 10) < Tight && Math.Abs(b.Z - 10) < Tight;
        }).ToList();
        Assert.Equal(2, onSharedLine.Count);

        // Exactly the two end pieces: y in [0, 1] and y in [9, 10]. Nothing spans [1, 9].
        var spans = onSharedLine
            .Select(e => (Lo: Math.Min(e.StartVertex.Position.Y, e.EndVertex.Position.Y),
                          Hi: Math.Max(e.StartVertex.Position.Y, e.EndVertex.Position.Y)))
            .OrderBy(s => s.Lo)
            .ToList();
        Assert.Equal(0, spans[0].Lo, 12);
        Assert.Equal(1, spans[0].Hi, 12);
        Assert.Equal(9, spans[1].Lo, 12);
        Assert.Equal(10, spans[1].Hi, 12);

        // Each is used exactly twice, once by each rim, with opposite senses — which is what
        // makes the merged result two-manifold rather than merely closed.
        foreach (var piece in onSharedLine)
        {
            var uses = piece.Uses.ToList();
            Assert.Equal(2, uses.Count);
            Assert.NotEqual(uses[0].SameSense, uses[1].SameSense);
            Assert.NotEqual(uses[0].Loop.Face, uses[1].Loop.Face);
        }
    }

    [Fact]
    public void TwoOppositeSharedEdges_CutTheRimIntoTwoFaces()
    {
        // Open the top and BOTH x faces: the top rim's region is two disjoint strips, so it
        // comes back as two simply-connected FACES rather than one loop or one annulus.
        var cube = Cube();
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var plusX = FaceWithNormal(cube, Vector3d.UnitX);
        var minusX = FaceWithNormal(cube, -Vector3d.UnitX);
        var shelled = Shelling.Shell(cube, 1,
            f => ReferenceEquals(f, top) || ReferenceEquals(f, plusX) || ReferenceEquals(f, minusX));
        shelled.Validate();
        Assert.Single(shelled.Shells);

        var topRims = shelled.Faces
            .Where(f => f.IsPlanar(out var o, out var n) && n.Z > 0.9 && Math.Abs(o.Z - 10) < Tight)
            .ToList();
        Assert.Equal(2, topRims.Count);
        Assert.All(topRims, f => Assert.Single(f.Loops));
        Assert.All(topRims, f => Assert.Equal(4, f.OuterLoop.Coedges.Count));

        // The two strips are y in [0, 1] and y in [9, 10] across the full x span.
        var strips = topRims
            .Select(f => f.OuterLoop.Coedges.Select(c => c.StartVertex.Position.Y).ToList())
            .Select(ys => (Lo: ys.Min(), Hi: ys.Max()))
            .OrderBy(s => s.Lo)
            .ToList();
        Assert.Equal(0, strips[0].Lo, 12);
        Assert.Equal(1, strips[0].Hi, 12);
        Assert.Equal(9, strips[1].Lo, 12);
        Assert.Equal(10, strips[1].Hi, 12);
    }

    [Fact]
    public void ThreeOpeningsAtOneVertex_AreRefusedByName()
    {
        // The rim closes to a POINT at a vertex all three of whose faces are open: the corner
        // is the meeting of three stationary planes, so it does not move at all and there is
        // no width in any direction to build a rim from.
        var cube = Cube();
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var x = FaceWithNormal(cube, Vector3d.UnitX);
        var y = FaceWithNormal(cube, Vector3d.UnitY);
        var exception = Assert.Throws<NotSupportedException>(() => Shelling.Shell(
            cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, x) || ReferenceEquals(f, y)));
        Assert.Contains("all selected as openings", exception.Message);
    }

    [Fact]
    public void AWallThatConsumesTheSharedRim_IsRefusedByName()
    {
        // The two inner corners walk along the shared edge from its ends; past half its length
        // they meet and there is no rim left. (The inner-edge fold check fires first, which is
        // the same statement in the offset's own words.)
        var cube = Cube();
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var side = FaceWithNormal(cube, Vector3d.UnitX);
        Assert.Throws<ArgumentException>(() =>
            Shelling.Shell(cube, 6, f => ReferenceEquals(f, top) || ReferenceEquals(f, side)));
    }

    [Fact]
    public void AdjacentOpeningsOnACurvedBody_StayRefusedByName()
    {
        // A quarter-turn annular wedge: every face carries one loop, so the refusal reached is
        // the adjacency one rather than the has-holes one. The shared edge between the outer
        // band and the top sector is an ARC, so its two rim pieces would be sub-curves, each
        // needing its own parameter solved on the shared carrier.
        var wedge = SolidFactory.Revolve(
            Profile.FromPoints([(5, 0, 0), (8, 0, 0), (8, 0, 3), (5, 0, 3)]),
            Vector3d.Zero, Vector3d.UnitZ, Math.PI / 2);
        Assert.All(wedge.Faces, f => Assert.Single(f.Loops));
        // Located by Bounds(): a revolve spells its flat sectors as RevolvedSurfaces too, so a
        // surface-TYPE test would match two faces. The outer band is the one reaching radius 8
        // in BOTH azimuths and spanning the full height; the top sector is the one flat at z = 3.
        var band = wedge.Faces.Single(f =>
            f.Bounds() is { } b && b.Max.X > 7.9 && b.Max.Y > 7.9 && b.Min.Z < 0.1 && b.Max.Z > 2.9);
        var sector = wedge.Faces.Single(f => f.Bounds().Min.Z > 2.9);
        var exception = Assert.Throws<NotSupportedException>(() =>
            Shelling.Shell(wedge, 0.5, f => ReferenceEquals(f, band) || ReferenceEquals(f, sector)));
        Assert.Contains("sub-CURVES", exception.Message);
    }

    [Fact]
    public void NonAdjacentOpeningsAreUntouched_LoopForLoop()
    {
        // The surgery must not reach a shelling that has no shared edge at all: the two-rim
        // tray keeps its ANNULUS (an outer loop plus a hole), which is what the merged case
        // deliberately is not.
        var cube = Cube();
        var top = FaceWithNormal(cube, Vector3d.UnitZ);
        var bottom = FaceWithNormal(cube, -Vector3d.UnitZ);
        var tube = Shelling.Shell(cube, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, bottom));
        tube.Validate();
        Assert.True(tube.SatisfiesEulerFormula(genus: 1));

        var rims = tube.Faces.Where(f => f.Loops.Count == 2).ToList();
        Assert.Equal(2, rims.Count);
        Assert.All(rims, f => Assert.Equal(4, f.Loops[0].Coedges.Count));
        Assert.All(rims, f => Assert.Equal(4, f.Loops[1].Coedges.Count));
    }
}
