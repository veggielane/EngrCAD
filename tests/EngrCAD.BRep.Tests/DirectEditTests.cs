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
    public void MovingACurvedFace_IsRefusedByName()
    {
        var error = Assert.Throws<NotSupportedException>(() => DirectEdit.MoveFaces(
            SolidFactory.MakeCylinder(5, 10), (1, 0, 0), f => f.Surface is CylinderSurface));
        Assert.Contains("moves a curved carrier's own axis", error.Message);
        Assert.Contains("OffsetFaces", error.Message);
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
    public void DeletingAFaceWhoseWoundOnlyPartlyBoundsANeighbour_IsRefusedByName()
    {
        // A box's top face: its four wound edges are part of the four SIDE faces' outer loops,
        // so healing would mean extending those sides until they meet — which for a prism they
        // never do. Named, not attempted.
        var error = Assert.Throws<NotSupportedException>(
            () => DirectEdit.DeleteFaces(Block(), Normal(Vector3d.UnitZ)));
        Assert.Contains("only PART of the way round a loop", error.Message);
        Assert.Contains("EXTENDED", error.Message);
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

    private static bool BitsEqual(in Vector3d a, in Vector3d b) =>
        BitConverter.DoubleToInt64Bits(a.X) == BitConverter.DoubleToInt64Bits(b.X)
        && BitConverter.DoubleToInt64Bits(a.Y) == BitConverter.DoubleToInt64Bits(b.Y)
        && BitConverter.DoubleToInt64Bits(a.Z) == BitConverter.DoubleToInt64Bits(b.Z);
}
