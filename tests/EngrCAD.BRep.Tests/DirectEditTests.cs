using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="DirectEdit"/> — editing a history-less solid by acting on its faces. Exact
/// positions, topology and refusals here; the volume identities live in Interop.Tests, which
/// can measure them.
/// </summary>
public class DirectEditTests
{
    private static readonly Tolerance Exact = new(1e-12, 1e-12);

    private static BrepSolid Block() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));

    private static Func<BrepFace, bool> Normal(Vector3d direction) =>
        face => face.IsPlanar(out _, out var n) && n.Normalized().Dot(direction.Normalized()) > 1 - 1e-9;

    private static Aabb BoundsOf(BrepSolid solid)
    {
        var bounds = Aabb.Empty;
        foreach (var vertex in solid.Vertices)
            bounds = bounds.Union(vertex.Position);
        return bounds;
    }

    // ---- offset ----

    [Fact]
    public void OffsetOfOneFace_MovesThatFaceAndLeavesEveryOtherVertexBitIdentical()
    {
        // Off the origin ON PURPOSE: a corner is re-derived by the three-plane Cramer solve,
        // which reproduces every coordinate bit for bit EXCEPT an exact zero — see
        // AnUnmovedCornerAtTheOrigin_ComesBackAsNegativeZero below. Away from zero the
        // bit-identity claim is exactly right, and it is the assertion with teeth here: an
        // implementation that quietly offset every plane by a rounded-to-zero amount would
        // still land inside any tolerance.
        var block = SolidFactory.MakeBox(new Aabb((5, 7, 3), (25, 37, 13)));
        var pushed = DirectEdit.OffsetFaces(block, 2.5, Normal(Vector3d.UnitZ));
        pushed.Validate();
        Assert.True(pushed.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(6, pushed.Faces.Count());
        Assert.Equal(12, pushed.Edges.Count());
        Assert.Equal(8, pushed.Vertices.Count());

        var bounds = BoundsOf(pushed);
        Assert.True(bounds.Min.AreEqual((5, 7, 3), Exact));
        Assert.True(bounds.Max.AreEqual((25, 37, 15.5), Exact));

        var bottom = pushed.Vertices.Where(v => v.Position.Z == 3).ToList();
        Assert.Equal(4, bottom.Count);
        foreach (var vertex in bottom)
            Assert.Contains(block.Vertices, original => BitsEqual(original.Position, vertex.Position));
    }

    [Fact]
    public void AnUnmovedCornerAtTheOrigin_ComesBackAsNegativeZero()
    {
        // A property worth pinning rather than working around silently. Every corner is
        // re-solved by Cramer, whose determinant for a box's three planes is -1, so a
        // coordinate that is exactly zero comes back as -0.0. It compares EQUAL to 0.0 by
        // every value test and differs only in the sign bit, so nothing downstream can see it
        // — but a bit-level fixture placed at the origin would fail for this reason and not
        // for a geometric one.
        var pushed = DirectEdit.OffsetFaces(Block(), 2.5, Normal(Vector3d.UnitZ));
        var corner = pushed.Vertices.Single(v =>
            v.Position.X == 0 && v.Position.Y == 0 && v.Position.Z == 0);
        Assert.Equal(0.0, corner.Position.Z);
        Assert.True(double.IsNegative(corner.Position.Z));
        Assert.NotEqual(BitConverter.DoubleToInt64Bits(0.0),
            BitConverter.DoubleToInt64Bits(corner.Position.Z));
    }

    [Fact]
    public void OffsetOfOneFace_TakesTheFaceSOwnNormal_NotAWorldAxis()
    {
        // A wedge whose slanted face's normal is (1, 0, 1)/sqrt(2): pushing it by d moves its
        // plane by d ALONG THAT NORMAL, so the apex edge rises by d*sqrt(2) rather than by d.
        var wedge = SolidFactory.Extrude(
            Profile.FromPoints([(0, 0, 0), (10, 0, 0), (0, 0, 10)]), (0, 6, 0));
        var slant = wedge.Faces.Single(f =>
            f.IsPlanar(out _, out var n) && n.Normalized().AreEqual(
                new Vector3d(1, 0, 1).Normalized(), new Tolerance(1e-9, 1e-9)));

        const double d = 1.5;
        var pushed = DirectEdit.OffsetFaces(wedge, d, f => ReferenceEquals(f, slant));
        pushed.Validate();

        // The right-angle corner is where the two unmoved planes (x = 0 is not one of them —
        // the unmoved faces are z = 0 and x = 0) meet the moved slant: solving gives the two
        // legs each growing by d*sqrt(2).
        double leg = 10 + d * Math.Sqrt(2);
        var bounds = BoundsOf(pushed);
        Assert.True(bounds.Min.AreEqual((0, 0, 0), Exact));
        Assert.True(bounds.Max.AreEqual((leg, 6, leg), Exact));
    }

    [Fact]
    public void OffsetOfSeveralCoplanarFaces_MovesThemTogether()
    {
        // Both caps of a plate pushed apart by the same distance: the plate thickens by 2d
        // and nothing else moves.
        var block = Block();
        var pushed = DirectEdit.OffsetFaces(
            block, 1.25, face => face.IsPlanar(out _, out var n) && Math.Abs(n.Normalized().Z) > 1 - 1e-9);
        pushed.Validate();
        var bounds = BoundsOf(pushed);
        Assert.True(bounds.Min.AreEqual((0, 0, -1.25), Exact));
        Assert.True(bounds.Max.AreEqual((20, 30, 11.25), Exact));
    }

    [Fact]
    public void OffsetOfOneFace_Negative_PullsItIn()
    {
        var pushed = DirectEdit.OffsetFaces(Block(), -3, Normal(Vector3d.UnitX));
        pushed.Validate();
        var bounds = BoundsOf(pushed);
        Assert.True(bounds.Max.AreEqual((17, 30, 10), Exact));
        Assert.True(bounds.Min.AreEqual((0, 0, 0), Exact));
    }

    [Fact]
    public void OffsetOfAHoledPlatesBoreWall_NarrowsTheBore_BecausePositiveGrowsTheSOLID()
    {
        // The sign rule is about MATERIAL, not about the bore: a bore wall's outward normal
        // points into the void, so a positive offset adds material there and the hole closes
        // in. That is the same convention whole-solid offsetting already has (a negative
        // whole-solid offset shrinks the outline and GROWS every hole), and it is the half a
        // reader gets backwards, so it is asserted in both directions below.
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var solid = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);

        var walls = new HashSet<BrepFace>(solid.Faces.Where(f =>
            f.IsPlanar(out var o, out var n) && Math.Abs(n.Normalized().Z) < 1e-9
            && o.X > 1 && o.X < 19 && o.Y > 1 && o.Y < 19));
        Assert.Equal(4, walls.Count);

        var narrowed = DirectEdit.OffsetFaces(solid, 1, walls.Contains);
        narrowed.Validate();
        Assert.True(narrowed.SatisfiesEulerFormula(genus: 1));
        var inner = narrowed.Vertices.Select(v => v.Position).Where(p => p.X > 1 && p.X < 19).ToList();
        Assert.Equal(8, inner.Count);
        Assert.Equal(9, inner.Min(p => p.X), 12);
        Assert.Equal(11, inner.Max(p => p.X), 12);

        var widened = DirectEdit.OffsetFaces(solid, -1, walls.Contains);
        widened.Validate();
        var wide = widened.Vertices.Select(v => v.Position).Where(p => p.X > 1 && p.X < 19).ToList();
        Assert.Equal(7, wide.Min(p => p.X), 12);
        Assert.Equal(13, wide.Max(p => p.X), 12);

        // The outer boundary is untouched either way.
        foreach (var edited in (BrepSolid[])[narrowed, widened])
        {
            var bounds = BoundsOf(edited);
            Assert.True(bounds.Min.AreEqual((0, 0, 0), Exact));
            Assert.True(bounds.Max.AreEqual((20, 20, 5), Exact));
        }
    }

    [Fact]
    public void OffsetOfACylindersTopCap_TakesTheCurvedPathAndKeepsTheRadius()
    {
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        var grown = DirectEdit.OffsetFaces(cylinder, 3, Normal(Vector3d.UnitZ));
        grown.Validate();
        Assert.Equal(3, grown.Faces.Count());

        // The side stays a cylinder of the SAME radius (only the cap moved), and both rims
        // keep radius 5 with the top rim lifted.
        var side = grown.Faces.Single(f => f.Surface is CylinderSurface);
        Assert.Equal(5, ((CylinderSurface)side.Surface).Radius, 12);
        var heights = grown.Vertices.Select(v => v.Position.Z).OrderBy(z => z).ToList();
        Assert.Equal(0, heights[0], 12);
        Assert.Equal(13, heights[^1], 12);
        foreach (var vertex in grown.Vertices)
            Assert.Equal(5, Math.Sqrt(vertex.Position.X * vertex.Position.X + vertex.Position.Y * vertex.Position.Y), 12);
    }

    [Fact]
    public void OffsetOfACylindersWall_GrowsTheRadiusAndLeavesTheCapsWhereTheyAre()
    {
        var grown = DirectEdit.OffsetFaces(
            SolidFactory.MakeCylinder(5, 10), 1.5, f => f.Surface is CylinderSurface);
        grown.Validate();
        var side = grown.Faces.Single(f => f.Surface is CylinderSurface);
        Assert.Equal(6.5, ((CylinderSurface)side.Surface).Radius, 12);
        foreach (var vertex in grown.Vertices)
        {
            Assert.Equal(6.5,
                Math.Sqrt(vertex.Position.X * vertex.Position.X + vertex.Position.Y * vertex.Position.Y), 12);
            Assert.True(vertex.Position.Z is 0 or 10);
        }
    }

    [Fact]
    public void OffsetWithASelectorThatMatchesNothing_IsRefusedByName()
    {
        var error = Assert.Throws<ArgumentException>(
            () => DirectEdit.OffsetFaces(Block(), 1, _ => false));
        Assert.Contains("matched none", error.Message);
    }

    // ---- move ----

    [Fact]
    public void MovingAPlanarFace_IsExactlyOffsettingItByTheProjectedDistance()
    {
        // The claim the implementation is built on, measured rather than asserted in prose: a
        // plane is invariant under translation within itself, so a move by v and an offset by
        // v.n land on the same plane. The two arithmetics differ (n.(o + v) against
        // n.o + d*(n.n)), so they agree to round-off rather than bit for bit.
        var block = Block();
        var v = new Vector3d(1.5, -2, 4);
        var moved = DirectEdit.MoveFaces(block, v, Normal(Vector3d.UnitZ));
        var offset = DirectEdit.OffsetFaces(block, v.Dot(Vector3d.UnitZ), Normal(Vector3d.UnitZ));

        var a = moved.Vertices.Select(x => x.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        var b = offset.Vertices.Select(x => x.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        Assert.Equal(b.Count, a.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.True(a[i].AreEqual(b[i], Exact), $"{a[i]} vs {b[i]}");
    }

    [Fact]
    public void MovingAFaceParallelToItself_ChangesNothing()
    {
        // The consequence that surprises, and the one a "translate the boundary" implementation
        // would get wrong: sliding a plane along itself is the same plane, so the solid is
        // unchanged. Asserted as geometry, not as a short-circuit — the operation really runs.
        var block = Block();
        var moved = DirectEdit.MoveFaces(block, (7, -3, 0), Normal(Vector3d.UnitZ));
        moved.Validate();
        var bounds = BoundsOf(moved);
        Assert.True(bounds.Min.AreEqual((0, 0, 0), Exact));
        Assert.True(bounds.Max.AreEqual((20, 30, 10), Exact));
    }

    [Fact]
    public void MovingSeveralFacesByOneVector_MovesEachByItsOwnProjection()
    {
        // +Z moves up by 4, +X moves out by 1.5, and the two are one call.
        var moved = DirectEdit.MoveFaces(
            Block(), (1.5, -2, 4),
            face => face.IsPlanar(out _, out var n)
                && (n.Normalized().Dot(Vector3d.UnitZ) > 1 - 1e-9 || n.Normalized().Dot(Vector3d.UnitX) > 1 - 1e-9));
        moved.Validate();
        var bounds = BoundsOf(moved);
        Assert.True(bounds.Min.AreEqual((0, 0, 0), Exact));
        Assert.True(bounds.Max.AreEqual((21.5, 30, 14), Exact));
    }

    [Fact]
    public void MovingACurvedFace_CarriesItsAxisWithIt()
    {
        // The refusal this replaces read "a translation moves a curved carrier's own axis" —
        // which is true, and is now what the rebuild DOES rather than what stops it. The
        // assertion with teeth is the RADIUS: a rim rebuilt about the old axis would come back
        // at |old centre - new corner|, which is not 5.
        var moved = DirectEdit.MoveFaces(
            SolidFactory.MakeCylinder(5, 10), (2, 0, 0), f => f.Surface is CylinderSurface);
        moved.Validate();
        Assert.Equal(3, moved.Faces.Count());

        var side = moved.Faces.Single(f => f.Surface is CylinderSurface);
        var cylinder = (CylinderSurface)side.Surface;
        Assert.Equal(5, cylinder.Radius, 12);
        Assert.True(cylinder.Origin.AreEqual((2, 0, 0), Exact));
        foreach (var vertex in moved.Vertices)
        {
            var radial = vertex.Position - new Vector3d(2, 0, vertex.Position.Z);
            Assert.Equal(5, radial.Length, 10);
            Assert.True(vertex.Position.Z is 0 or 10);
        }
    }

    [Fact]
    public void MovingACurvedFace_KeepsEveryRebuiltRimOnItsOwnCarrierPhase()
    {
        // The phase rule at the one point that carries a phase. A closed rim has ONE vertex
        // and it is a SEAM, so a corner solve — which only knows how to find the nearest point
        // of the carriers' common locus to a seed — puts it at an arbitrary angle once the
        // axis has moved. Here the move is DIAGONAL on purpose: a purely axial one lands the
        // old seam on the new u = 0 by symmetry and the defect is invisible.
        var moved = DirectEdit.MoveFaces(
            SolidFactory.MakeCylinder(5, 10), (2, 3, 0), f => f.Surface is CylinderSurface);
        moved.Validate();
        var cylinder = (CylinderSurface)moved.Faces.Single(f => f.Surface is CylinderSurface).Surface;
        foreach (var vertex in moved.Vertices)
        {
            var radial = vertex.Position - (cylinder.Origin + cylinder.Axis.Normalized() * vertex.Position.Z);
            Assert.Equal(5, radial.Length, 10);
            // u = 0 of the carrier's own frame: the rim's seam sits exactly on +X.
            Assert.Equal(5, radial.Dot(cylinder.XDirection.Normalized()), 9);
            Assert.Equal(0, radial.Dot(cylinder.YDirection.Normalized()), 9);
        }
    }

    [Fact]
    public void MovingAPlanarSelection_TakesTheOffsetReductionUnchanged()
    {
        // The curved branch must not capture the planar case: an all-planar selection still
        // reduces to an offset by v.n, which is what keeps every move that ever worked
        // bit-identical. Asserted against the offset itself rather than against a number.
        var block = Block();
        var v = new Vector3d(1.5, -2, 4);
        var moved = DirectEdit.MoveFaces(block, v, Normal(Vector3d.UnitZ));
        var offset = DirectEdit.OffsetFaces(block, v.Z, Normal(Vector3d.UnitZ));
        var a = moved.Vertices.Select(x => x.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        var b = offset.Vertices.Select(x => x.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        for (int i = 0; i < a.Count; i++)
            Assert.True(BitsEqual(a[i], b[i]), $"{a[i]} vs {b[i]}");
    }

    // ---- rotate ----

    [Fact]
    public void RotatingASideFaceAboutItsOwnBottomEdge_IsTheExactFrustumIntegral()
    {
        // A draft angle put on a body with no history. The block's +X face is hinged about the
        // line it meets the base in, so the XZ section becomes a trapezoid and the volume is a
        // CLOSED FORM: depth * height * (width + height*tan(theta)/2). The naive "area times
        // distance" answer has no meaning here at all, which is the point of the fixture.
        const double angle = 5;
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (40, 30, 10)));
        var side = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().X > 1 - 1e-9);
        var leaned = DirectEdit.RotateFaces(
            box, new Ray3d((40, 0, 0), Vector3d.UnitY), angle, f => ReferenceEquals(f, side));
        leaned.Validate();
        Assert.True(leaned.SatisfiesEulerFormula(genus: 0));

        double lean = 10 * Math.Tan(angle * Math.PI / 180);
        var top = leaned.Vertices.Where(v => v.Position.Z > 9.9 && v.Position.X > 1).ToList();
        Assert.Equal(2, top.Count);
        foreach (var vertex in top)
            Assert.Equal(40 + lean, vertex.Position.X, 9);
        // The hinge line itself did not move.
        Assert.Equal(2, leaned.Vertices.Count(v => v.Position.Z == 0 && v.Position.X == 40));
    }

    [Fact]
    public void RotatingByZero_LeavesEveryVertexWhereItWas()
    {
        var box = SolidFactory.MakeBox(new Aabb((5, 7, 3), (25, 37, 13)));
        var side = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().X > 1 - 1e-9);
        var turned = DirectEdit.RotateFaces(
            box, new Ray3d((25, 7, 3), Vector3d.UnitY), 0, f => ReferenceEquals(f, side));
        turned.Validate();
        foreach (var vertex in turned.Vertices)
            Assert.Contains(box.Vertices, original => original.Position.AreEqual(vertex.Position, Exact));
    }

    [Fact]
    public void RotatingWithADegenerateAxis_IsRefusedByName()
    {
        var error = Assert.Throws<ArgumentException>(() => DirectEdit.RotateFaces(
            Block(), new Ray3d(Vector3d.Zero, Vector3d.Zero), 5, Normal(Vector3d.UnitZ)));
        Assert.Contains("no direction", error.Message);
    }

    // ---- replace ----

    [Fact]
    public void ReplacingACylindersWallWithACone_GivesTheExactFrustum()
    {
        // OCCT's BRepTools_ReShape on a face: the wall's carrier is swapped for a slanted
        // revolve and every corner re-solved. Both rims stay exact circles because a cone
        // against an axis-perpendicular plane is an analytic pair, so the answer is the
        // frustum's own closed form rather than a fit.
        const double bottom = 6, top = 3, height = 12;
        var cylinder = SolidFactory.MakeCylinder(bottom, height);
        var wall = cylinder.Faces.Single(f => f.Surface is CylinderSurface);
        var cone = new RevolvedSurface(
            new Line3d((bottom, 0, 0), (top, 0, height)), Vector3d.Zero, Vector3d.UnitZ);

        var frustum = DirectEdit.ReplaceFaceSurfaces(
            cylinder, f => ReferenceEquals(f, wall) ? cone : null);
        frustum.Validate();
        Assert.Equal(3, frustum.Faces.Count());

        var radii = frustum.Vertices
            .Select(v => (v.Position.Z, R: Math.Sqrt(v.Position.X * v.Position.X + v.Position.Y * v.Position.Y)))
            .OrderBy(p => p.Z).ToList();
        Assert.Equal(bottom, radii[0].R, 9);
        Assert.Equal(top, radii[^1].R, 9);
        Assert.Equal(0, radii[0].Z, 9);
        Assert.Equal(height, radii[^1].Z, 9);
    }

    [Fact]
    public void ReplacingAFaceWithAnOppositelyFacingSurface_IsRefusedByName()
    {
        // The gate nothing downstream can supply: the loops, the counts and Euler-Poincare are
        // all unchanged by an inside-out swap, so a structural check passes it happily.
        var block = Block();
        var top = block.Faces.Single(f => f.IsPlanar(out var o, out var n)
            && n.Normalized().Z > 1 - 1e-9);
        // The same plane with its two in-plane directions swapped: identical point set,
        // opposite normal.
        var inverted = new PlaneSurface((0, 0, 10), Vector3d.UnitY, Vector3d.UnitX);
        var error = Assert.Throws<NotSupportedException>(
            () => DirectEdit.ReplaceFaceSurfaces(block, f => ReferenceEquals(f, top) ? inverted : null));
        Assert.Contains("faces the opposite way", error.Message);
        Assert.Contains("inside out", error.Message);
    }

    [Fact]
    public void ReplacingNothing_IsRefusedByName()
    {
        var error = Assert.Throws<ArgumentException>(() => DirectEdit.ReplaceFaceSurfaces(Block(), _ => null));
        Assert.Contains("nothing to replace", error.Message);
    }

    [Fact]
    public void ReplacingAFaceWhoseNewRimHasNoExactIntersection_IsRefusedByName()
    {
        // The exactness policy, stated as a boundary rather than as a promise: a cone meets a
        // box's SIDE plane in a curve only the marching tracer can sample, and baking a chordal
        // rim into a solid is the mistake the arc-rim corner policy already refuses.
        var box = SolidFactory.MakeBox(new Aabb((-20, -20, 0), (20, 20, 10)));
        var top = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Z > 1 - 1e-9);
        var cone = new RevolvedSurface(
            new Line3d((0, 0, 20), (40, 0, 0)), Vector3d.Zero, Vector3d.UnitZ);
        var error = Assert.Throws<NotSupportedException>(
            () => DirectEdit.ReplaceFaceSurfaces(box, f => ReferenceEquals(f, top) ? cone : null));
        Assert.Contains("marching tracer", error.Message);
    }

    // ---- delete ----

    [Fact]
    public void DeletingAThroughHolesWalls_RestoresThePlainPlateBitForBit()
    {
        // The entry's own verification: a deletion must RESTORE the body the feature was added
        // to, not merely look as though the feature is gone. The plate's own geometry is never
        // touched by the operation, so the recovered vertices are bit-identical to the plate
        // built without the hole at all.
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var holed = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);
        var plain = SolidFactory.Extrude(plate, (0, 0, 5));
        Assert.True(holed.SatisfiesEulerFormula(genus: 1));

        var walls = new HashSet<BrepFace>(holed.Faces.Where(f =>
            f.IsPlanar(out var o, out var n) && Math.Abs(n.Normalized().Z) < 1e-9
            && o.X > 1 && o.X < 19 && o.Y > 1 && o.Y < 19));
        Assert.Equal(4, walls.Count);

        var filled = DirectEdit.DeleteFaces(holed, walls.Contains);
        filled.Validate();
        Assert.True(filled.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(plain.Faces.Count(), filled.Faces.Count());
        Assert.Equal(plain.Edges.Count(), filled.Edges.Count());
        Assert.Equal(plain.Vertices.Count(), filled.Vertices.Count());

        // Bit-for-bit against the plate that never had a hole.
        var expected = plain.Vertices.Select(v => v.Position).ToList();
        foreach (var vertex in filled.Vertices)
            Assert.Contains(expected, p => BitsEqual(p, vertex.Position));
        Assert.All(filled.Faces, f => Assert.Single(f.Loops));
    }

    [Fact]
    public void DeletingFaces_LeavesTheInputSolidUntouched()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var holed = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);
        var walls = new HashSet<BrepFace>(holed.Faces.Where(f =>
            f.IsPlanar(out var o, out _) && o.X > 1 && o.X < 19 && o.Y > 1 && o.Y < 19
            && f.IsPlanar(out _, out var n) && Math.Abs(n.Normalized().Z) < 1e-9));

        _ = DirectEdit.DeleteFaces(holed, walls.Contains);
        holed.Validate();
        Assert.Equal(10, holed.Faces.Count());
        Assert.True(holed.SatisfiesEulerFormula(genus: 1));
    }

    [Fact]
    public void DeletingFaces_InheritsProvenanceOntoEverySurvivor()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var holed = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);
        var top = holed.Faces.Single(f => f.IsPlanar(out var o, out var n)
            && n.Normalized().Dot(Vector3d.UnitZ) > 1 - 1e-9 && o.Z > 4);
        top.AddProvenance("plate");

        var walls = new HashSet<BrepFace>(holed.Faces.Where(f =>
            f.IsPlanar(out var o, out var n) && Math.Abs(n.Normalized().Z) < 1e-9
            && o.X > 1 && o.X < 19 && o.Y > 1 && o.Y < 19));
        var filled = DirectEdit.DeleteFaces(holed, walls.Contains);

        // Exactly the ONE face that carried the tag carries it afterwards: an off-by-one in a
        // parent array leaves the count right and the meaning wrong.
        var tagged = filled.Faces.Where(f => f.Provenance.Contains("plate")).ToList();
        Assert.Single(tagged);
        Assert.True(tagged[0].IsPlanar(out var origin, out var normal));
        Assert.True(normal.Normalized().Dot(Vector3d.UnitZ) > 1 - 1e-9);
        Assert.Equal(5, origin.Z, 12);
    }

    [Fact]
    public void DeletingABoxsTopFace_IsRefusedByBothHealsWithBothReasons()
    {
        // A box's top face: its four wound edges are part of the four SIDE faces' outer loops,
        // so there is no whole loop to drop — AND the extension has no answer either, because a
        // face whose whole boundary is wound has no OPPOSITE pair to extend toward each other
        // (a box's four sides extended past its deleted top never meet). Both routes are named,
        // which is what makes the message tell a reader which shape would have worked.
        var error = Assert.Throws<NotSupportedException>(
            () => DirectEdit.DeleteFaces(Block(), Normal(Vector3d.UnitZ)));
        Assert.Contains("only PART of the way round a loop", error.Message);
        Assert.Contains("4 exposed edges rather than the two a blend strip has", error.Message);
    }

    [Fact]
    public void DeletingACylindersCap_IsRefusedBecauseTheNeighboursSecondLoopIsNotAHole()
    {
        // The gate that a structural check cannot supply: dropping the side band's top ring
        // would leave an OPEN tube which satisfies Validate() and Euler-Poincare alike.
        var error = Assert.Throws<NotSupportedException>(() => DirectEdit.DeleteFaces(
            SolidFactory.MakeCylinder(5, 10), Normal(Vector3d.UnitZ)));
        Assert.Contains("not necessarily a hole", error.Message);
        Assert.Contains("PLANAR", error.Message);
    }

    [Fact]
    public void DeletingEveryFace_IsRefusedByName()
    {
        var error = Assert.Throws<ArgumentException>(() => DirectEdit.DeleteFaces(Block(), _ => true));
        Assert.Contains("nothing would be left", error.Message);
    }

    [Fact]
    public void DeletingWithASelectorThatMatchesNothing_IsRefusedByName()
    {
        var error = Assert.Throws<ArgumentException>(() => DirectEdit.DeleteFaces(Block(), _ => false));
        Assert.Contains("matched none", error.Message);
    }

    // ---- delete by EXTENDING the neighbours ----

    [Fact]
    public void DeletingAFilletBand_ReproducesTheUnfilletedBoxBitForBit()
    {
        // The headline, and the strongest oracle available: the healed body does not merely
        // LOOK like the box the fillet was put on — its faces keep their own carriers, so the
        // corners re-solve to the very same numbers and every vertex comes back bit-identical
        // to a box that never had a fillet. A resemblance test passes a wrong heal.
        var box = SolidFactory.MakeBox(new Aabb((-30, -20, -5), (30, 20, 5)));
        var top = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Z > 1 - 1e-9);
        var filleted = Filleting.FilletRim(box, top, 3);
        filleted.Validate();
        Assert.Equal(10, filleted.Faces.Count());

        var healed = DirectEdit.DeleteFaces(filleted, f => !f.IsPlanar(out _, out _));
        healed.Validate();
        Assert.True(healed.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(6, healed.Faces.Count());
        Assert.Equal(12, healed.Edges.Count());
        Assert.Equal(8, healed.Vertices.Count());

        var expected = box.Vertices.Select(v => v.Position).ToList();
        foreach (var vertex in healed.Vertices)
            Assert.Contains(expected, p => BitsEqual(p, vertex.Position));
    }

    [Fact]
    public void DeletingAChamferBand_ReproducesTheUnchamferedBoxBitForBit()
    {
        var box = SolidFactory.MakeBox(new Aabb((-30, -20, -5), (30, 20, 5)));
        var top = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Z > 1 - 1e-9);
        var chamfered = Filleting.ChamferRim(box, top, 3);

        // The chamfer strips are the planes that are neither horizontal nor vertical.
        var healed = DirectEdit.DeleteFaces(chamfered, f =>
            f.IsPlanar(out _, out var n)
            && Math.Abs(n.Normalized().Z) > 1e-9 && Math.Abs(n.Normalized().Z) < 1 - 1e-9);
        healed.Validate();
        Assert.Equal(6, healed.Faces.Count());
        Assert.Equal(8, healed.Vertices.Count());

        var expected = box.Vertices.Select(v => v.Position).ToList();
        foreach (var vertex in healed.Vertices)
            Assert.Contains(expected, p => BitsEqual(p, vertex.Position));
    }

    [Fact]
    public void DeletingACircularRimFillet_ClosesTheCylinderBackUp()
    {
        // A CLOSED strip: both its exposed edges are whole circles and it has no cross edge at
        // all, so the two seam vertices are one point of the healed body. That merge is what
        // puts BOTH neighbours' carriers in the corner's list — without it each seam lands on
        // whichever single surface it started on, which is nowhere near the rim.
        var cylinder = SolidFactory.MakeCylinder(10, 20);
        var cap = cylinder.Faces.Single(f =>
            f.IsPlanar(out var o, out var n) && n.Normalized().Z > 1 - 1e-9);
        var filleted = Filleting.FilletRim(cylinder, cap, 2);
        filleted.Validate();

        var band = filleted.Faces.Single(f => f.Surface is RevolvedSurface);
        var healed = DirectEdit.DeleteFaces(filleted, f => ReferenceEquals(f, band));
        healed.Validate();
        Assert.Equal(3, healed.Faces.Count());

        // Both rims come back at the cylinder's own radius, at the caps' own heights.
        foreach (var vertex in healed.Vertices)
        {
            Assert.Equal(10,
                Math.Sqrt(vertex.Position.X * vertex.Position.X + vertex.Position.Y * vertex.Position.Y), 9);
            Assert.True(Math.Abs(vertex.Position.Z) < 1e-9 || Math.Abs(vertex.Position.Z - 20) < 1e-9);
        }
    }

    [Fact]
    public void DeletingAFilletBand_LeavesTheInputSolidUntouched()
    {
        // Rim surgery rewrites loops in place, so a half-edited failure is the one outcome
        // that must be impossible. Here it is STRUCTURAL rather than arranged: the heal builds
        // entirely fresh topology over the input's own curves and surfaces and never writes to
        // it, so the input is intact whether the heal succeeded or refused.
        var box = SolidFactory.MakeBox(new Aabb((-30, -20, -5), (30, 20, 5)));
        var top = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Z > 1 - 1e-9);
        var filleted = Filleting.FilletRim(box, top, 3);
        var before = filleted.Vertices.Select(v => v.Position).ToList();

        _ = DirectEdit.DeleteFaces(filleted, f => !f.IsPlanar(out _, out _));
        filleted.Validate();
        Assert.Equal(10, filleted.Faces.Count());
        Assert.Equal(before.Count, filleted.Vertices.Count());
        foreach (var (a, b) in before.Zip(filleted.Vertices.Select(v => v.Position)))
            Assert.True(BitsEqual(a, b));

        // And a REFUSED edit leaves it untouched for the same reason.
        Assert.Throws<NotSupportedException>(() => DirectEdit.DeleteFaces(filleted, Normal(Vector3d.UnitZ)));
        filleted.Validate();
        Assert.Equal(10, filleted.Faces.Count());
    }

    [Fact]
    public void DeletingAFilletBand_InheritsProvenanceOntoTheFaceThatCarriedIt()
    {
        var box = SolidFactory.MakeBox(new Aabb((-30, -20, -5), (30, 20, 5)));
        var top = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Z > 1 - 1e-9);
        var filleted = Filleting.FilletRim(box, top, 3);
        var side = filleted.Faces.Single(f =>
            f.IsPlanar(out _, out var n) && n.Normalized().X > 1 - 1e-9);
        side.AddProvenance("wall");

        var healed = DirectEdit.DeleteFaces(filleted, f => !f.IsPlanar(out _, out _));
        var tagged = healed.Faces.Where(f => f.Provenance.Contains("wall")).ToList();
        Assert.Single(tagged);
        Assert.True(tagged[0].IsPlanar(out _, out var normal));
        Assert.True(normal.Normalized().X > 1 - 1e-9);
    }

    [Fact]
    public void DeletingAWholeSolidRoundingsPatches_IsRefusedByName()
    {
        // A corner patch of FilletAllEdges borders THREE bands, so it is not a strip and has no
        // opposite pair. Refused with the count, not attempted — the extension can have no
        // answer, and guessing one would silently reshape the corner.
        var rounded = Filleting.FilletAllEdges(
            SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8))), 2);
        var error = Assert.Throws<NotSupportedException>(
            () => DirectEdit.DeleteFaces(rounded, f => !f.IsPlanar(out _, out _)));
        Assert.Contains("blend strip", error.Message);
    }

    [Fact]
    public void DeletingOnlyTheBandOfAPartialRun_IsRefusedByName()
    {
        // A run that stops mid-rim ends in TERMINATION faces, so the band has four exposed
        // edges rather than two. Deleting the terminations WITH it makes those two interior
        // and the strip well posed again, which the next test asserts — so the refusal names a
        // selection that is too small rather than a shape that cannot be healed.
        var box = SolidFactory.MakeBox(new Aabb((-30, -20, -5), (30, 20, 5)));
        var top = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Z > 1 - 1e-9);
        var run = Filleting.FilletEdges(box,
            BrepQueries.RimEdges(top).Where(e => e.Curve.PointAt(e.Domain.ParameterAt(0.5)).Y < -19), 3);
        var error = Assert.Throws<NotSupportedException>(
            () => DirectEdit.DeleteFaces(run, f => !f.IsPlanar(out _, out _)));
        Assert.Contains("4 exposed edges rather than the two a blend strip has", error.Message);
    }

    [Fact]
    public void DeletingAPartialRunWithItsTerminations_ReproducesTheBox()
    {
        var box = SolidFactory.MakeBox(new Aabb((-30, -20, -5), (30, 20, 5)));
        var top = box.Faces.Single(f => f.IsPlanar(out _, out var n) && n.Normalized().Z > 1 - 1e-9);
        var run = Filleting.FilletEdges(box,
            BrepQueries.RimEdges(top).Where(e => e.Curve.PointAt(e.Domain.ParameterAt(0.5)).Y < -19), 3);

        // The band plus the two three-sided termination planes it ends on.
        var doomed = run.Faces.Where(f =>
            !f.IsPlanar(out _, out _)
            || (f.Bounds().Min.Z > 1.9 && f.Loops[0].Coedges.Count == 3)).ToList();
        Assert.Equal(3, doomed.Count);

        var healed = DirectEdit.DeleteFaces(run, doomed.Contains);
        healed.Validate();
        Assert.Equal(6, healed.Faces.Count());
        // Every vertex is a corner of the original box (the run's stop points leave extra,
        // collinear vertices on the rim it stopped on — honest topology, not a defect).
        foreach (var vertex in healed.Vertices)
        {
            Assert.Equal(30, Math.Abs(vertex.Position.X), 9);
            Assert.Equal(20, Math.Abs(vertex.Position.Y), 9);
            Assert.Equal(5, Math.Abs(vertex.Position.Z), 9);
        }
    }

    private static bool BitsEqual(in Vector3d a, in Vector3d b) =>
        BitConverter.DoubleToInt64Bits(a.X) == BitConverter.DoubleToInt64Bits(b.X)
        && BitConverter.DoubleToInt64Bits(a.Y) == BitConverter.DoubleToInt64Bits(b.Y)
        && BitConverter.DoubleToInt64Bits(a.Z) == BitConverter.DoubleToInt64Bits(b.Z);
}
