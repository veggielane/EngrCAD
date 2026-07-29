using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="Shelling"/> — exact polyhedral offset and hollowing. Topology and exact
/// positions here; volumes in Interop.Tests.
/// </summary>
public class ShellingTests
{
    private static BrepSolid Block() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));

    private static BrepFace TopOf(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    [Fact]
    public void Offset_Box_GrowsEveryPlaneByTheDistance()
    {
        var offset = Shelling.Offset(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), 0.5);
        offset.Validate();
        Assert.True(offset.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(8, offset.Vertices.Count());
        Assert.Equal(12, offset.Edges.Count());
        Assert.Equal(6, offset.Faces.Count());

        var bounds = Aabb.Empty;
        foreach (var vertex in offset.Vertices)
            bounds = bounds.Union(vertex.Position);
        Assert.True(bounds.Min.AreEqual((-0.5, -0.5, -0.5), new Tolerance(1e-12, 1e-12)));
        Assert.True(bounds.Max.AreEqual((2.5, 3.5, 4.5), new Tolerance(1e-12, 1e-12)));
    }

    [Fact]
    public void Offset_Negative_Shrinks()
    {
        var offset = Shelling.Offset(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), -0.25);
        offset.Validate();
        var bounds = Aabb.Empty;
        foreach (var vertex in offset.Vertices)
            bounds = bounds.Union(vertex.Position);
        Assert.True(bounds.Min.AreEqual((0.25, 0.25, 0.25), new Tolerance(1e-12, 1e-12)));
        Assert.True(bounds.Max.AreEqual((1.75, 2.75, 3.75), new Tolerance(1e-12, 1e-12)));
    }

    [Fact]
    public void Offset_KeepsGenusAndHoleLoops()
    {
        // A plate with a square through-hole: the offset must shrink the outer boundary and
        // GROW the hole (its walls face inward), preserving genus 1.
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var solid = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);
        var offset = Shelling.Offset(solid, -1);
        offset.Validate();
        Assert.True(offset.SatisfiesEulerFormula(genus: 1));

        var positions = offset.Vertices.Select(v => v.Position).ToList();
        Assert.Equal(16, positions.Count);
        // Outer corners moved in by 1, hole corners moved out by 1 (one vertex per z level).
        Assert.Equal(2, positions.Count(p => Math.Abs(p.X - 1) < 1e-12 && Math.Abs(p.Y - 1) < 1e-12));
        Assert.Equal(2, positions.Count(p => Math.Abs(p.X - 7) < 1e-12 && Math.Abs(p.Y - 7) < 1e-12));
        Assert.Equal(8, positions.Count(p => p.X > 6.9 && p.X < 13.1 && p.Y > 6.9 && p.Y < 13.1));
    }

    [Fact]
    public void Shell_BoxWithTopOpen_HasTrayTopology()
    {
        var block = Block();
        var tray = Shelling.Shell(block, 2, f => ReferenceEquals(f, TopOf(block)));
        tray.Validate();
        Assert.Single(tray.Shells);
        Assert.Equal(16, tray.Vertices.Count());   // 8 outer + 8 inner
        Assert.Equal(24, tray.Edges.Count());      // 12 outer + 12 inner
        Assert.Equal(11, tray.Faces.Count());      // 5 outer + 5 inner + 1 rim
        Assert.Equal(12, tray.Loops.Count());      // the rim carries the opening as a hole
        Assert.True(tray.SatisfiesEulerFormula(genus: 0));

        // The cavity is exactly the box inset by the wall thickness, open at the top.
        var inner = tray.Vertices.Select(v => v.Position)
            .Where(p => p.X > 1 && p.X < 19 && p.Y > 1 && p.Y < 29).ToList();
        Assert.Equal(8, inner.Count);
        foreach (var p in inner)
        {
            Assert.True(Math.Abs(p.X - 2) < 1e-12 || Math.Abs(p.X - 18) < 1e-12);
            Assert.True(Math.Abs(p.Y - 2) < 1e-12 || Math.Abs(p.Y - 28) < 1e-12);
            Assert.True(Math.Abs(p.Z - 2) < 1e-12 || Math.Abs(p.Z - 10) < 1e-12);
        }
        Assert.Equal(4, inner.Count(p => Math.Abs(p.Z - 10) < 1e-12)); // cavity reaches the opening
    }

    [Fact]
    public void Shell_WithNoOpening_MakesASealedVoidAsASecondShell()
    {
        var closed = Shelling.Shell(Block(), 2);
        closed.Validate();
        Assert.Equal(2, closed.Shells.Count);
        Assert.Equal(6, closed.Shells[0].Faces.Count);
        Assert.Equal(6, closed.Shells[1].Faces.Count);
        Assert.Equal(16, closed.Vertices.Count());
        Assert.Equal(24, closed.Edges.Count());
        Assert.True(closed.SatisfiesEulerFormula(genus: 0));

        // The void's faces point INTO the void (out of the material).
        foreach (var face in closed.Shells[1].Faces)
        {
            face.IsPlanar(out var origin, out var normal);
            var centre = new Vector3d(10, 15, 5);
            Assert.True((centre - origin).Dot(normal) > 0, "inner face normal must point into the cavity");
        }
    }

    [Fact]
    public void Shell_TwoOppositeOpenings_MakesAGenusOneTube()
    {
        var block = Block();
        var top = TopOf(block);
        var bottom = block.PlanarFacesWithNormal(-Vector3d.UnitZ).Single();
        var tube = Shelling.Shell(block, 2, f => ReferenceEquals(f, top) || ReferenceEquals(f, bottom));
        tube.Validate();
        Assert.Single(tube.Shells);
        Assert.Equal(10, tube.Faces.Count());   // 4 outer + 4 inner + 2 rims
        Assert.Equal(12, tube.Loops.Count());
        Assert.True(tube.SatisfiesEulerFormula(genus: 1));
    }

    [Fact]
    public void Shell_KeepsEveryFacePlanarAndSelectable()
    {
        var block = Block();
        var tray = Shelling.Shell(block, 2, f => ReferenceEquals(f, TopOf(block)));
        Assert.All(tray.Faces, f => Assert.True(f.IsPlanar(out _, out _)));
        // Outer bottom, inner bottom (facing up) and the rim are all findable by normal.
        Assert.Single(tray.PlanarFacesWithNormal(-Vector3d.UnitZ));
        Assert.Equal(2, tray.PlanarFacesWithNormal(Vector3d.UnitZ).Count()); // inner floor + rim
    }

    [Fact]
    public void Shelling_RejectsWhatItCannotDoExactly()
    {
        // Curved faces are NO LONGER refused (see CurvedShellingTests) — a cylinder shells and
        // a sphere offsets exactly. What stays refused is a curved carrier with no exact
        // offset of its own family: a swept surface's parallel is not a sweep.
        Shelling.Shell(SolidFactory.MakeCylinder(3, 5), 0.5).Validate();
        Shelling.Offset(SolidFactory.MakeSphere(2), 0.5).Validate();
        var pipe = SolidFactory.Sweep(
            Profile.FromPoints([(0, -1, 0), (0, 1, 0), (0, 1, 2), (0, -1, 2)]),
            new NurbsCurve(1, [(0, 0, 0), (10, 0, 0)], null, [0, 0, 1, 1]));
        Assert.Throws<NotSupportedException>(() => Shelling.Offset(pipe, 0.2));

        // Adjacent openings.
        var block = Block();
        var top = TopOf(block);
        var side = block.PlanarFacesWithNormal(Vector3d.UnitX).Single();
        Assert.Throws<NotSupportedException>(() =>
            Shelling.Shell(block, 1, f => ReferenceEquals(f, top) || ReferenceEquals(f, side)));

        // Too thick: the cavity would turn inside out (the block is only 10 tall).
        Assert.Throws<ArgumentException>(() => Shelling.Shell(Block(), 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shelling.Shell(Block(), 0));
        Assert.Throws<ArgumentException>(() => Shelling.Offset(Block(), -12));

        // A higher-valence vertex whose offset planes genuinely miss. A pyramid's apex is
        // concurrent and DOES offset (see PyramidApex_OffsetsExactly); shearing one side
        // face breaks that concurrency, and the refusal names it.
        var skewed = SquarePyramid(10, 8, apexOffset: 3);
        var exception = Assert.Throws<NotSupportedException>(() => Shelling.Offset(skewed, 0.5));
        Assert.Contains("do not meet in a point", exception.Message);

        // Multi-shell input (the sealed-void result of a previous shelling).
        var sealed2 = Shelling.Shell(Block(), 2);
        Assert.Throws<NotSupportedException>(() => Shelling.Shell(sealed2, 0.5));

        // Every face selected as an opening.
        Assert.Throws<ArgumentException>(() => Shelling.Shell(Block(), 1, _ => true));
    }

    [Fact]
    public void Shell_OpeningOnAFaceWithHoles_IsRejected()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var solid = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);
        var top = solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        Assert.Throws<NotSupportedException>(() => Shelling.Shell(solid, 1, f => ReferenceEquals(f, top)));
    }

    [Fact]
    public void Shell_PerFaceThickness_PutsEachInnerPlaneAtItsOwnDepth()
    {
        // Open-top tray with a 3-thick base under 1-thick walls. Each inner corner is
        // still the intersection of its three offset planes, so unequal offsets stay
        // exact: the cavity is x ∈ [1, 19], y ∈ [1, 29], z ∈ [3, 10].
        var block = Block();
        var tray = Shelling.Shell(
            block,
            f => f.IsPlanar(out _, out var n) && n.Z < -0.9 ? 3.0 : 1.0,
            f => ReferenceEquals(f, TopOf(block)));
        tray.Validate();

        var tolerance = new Tolerance(1e-12, 1e-12);
        var positions = tray.Vertices.Select(v => v.Position).ToList();
        Assert.Contains(positions, p => p.AreEqual((1, 1, 3), tolerance));
        Assert.Contains(positions, p => p.AreEqual((19, 29, 3), tolerance));
        Assert.Contains(positions, p => p.AreEqual((1, 1, 10), tolerance));
        // No inner vertex sits at the uniform-thickness depth z = 1.
        Assert.DoesNotContain(positions, p => p.AreEqual((1, 1, 1), tolerance));
    }

    [Fact]
    public void Shell_PerFaceThickness_RejectsANonPositiveWall()
    {
        var block = Block();
        Assert.Throws<ArgumentOutOfRangeException>(() => Shelling.Shell(
            block,
            f => f.IsPlanar(out _, out var n) && n.Z < -0.9 ? 0.0 : 1.0,
            f => ReferenceEquals(f, TopOf(block))));
    }

    [Fact]
    public void PyramidApex_OffsetsExactly_ThoughItIsFourValent()
    {
        // A four-valent vertex is over-determined in general — but a square pyramid's apex
        // has four planes that meet in a point BY SYMMETRY, and offsetting each of them
        // keeps that true. The least-squares corner solve finds it and the residual check
        // confirms it, so the case that used to be refused wholesale now works.
        const double half = 10, height = 8, distance = 0.5;
        var grown = Shelling.Offset(SquarePyramid(half, height, apexOffset: 0), distance);
        grown.Validate();
        Assert.True(grown.SatisfiesEulerFormula(genus: 0));

        // The apex rises by distance / sin(half-angle of the slant against the axis): the
        // offset planes' meeting point is the apex pushed along the axis by d / cos(slope),
        // with slope the angle each face's normal makes with the axis.
        var apex = grown.Vertices.Select(v => v.Position).MaxBy(p => p.Z);
        double slantLength = Math.Sqrt(half * half + height * height);
        double normalZ = half / slantLength;               // each side normal's z component
        Assert.Equal(height + distance / normalZ, apex.Z, 9);

        // The base grew too, by distance in every direction plus the side lean.
        double baseZ = grown.Vertices.Select(v => v.Position).Min(p => p.Z);
        Assert.Equal(-distance, baseZ, 12);
    }

    /// <summary>
    /// A square pyramid on the XY plane. <paramref name="apexOffset"/> slides the apex off
    /// the axis, which breaks the four side planes' concurrency without changing anything
    /// else — the difference between a corner that survives an offset and one that does not.
    /// </summary>
    private static BrepSolid SquarePyramid(double half, double height, double apexOffset)
    {
        Vector3d[] corners =
        [
            (-half, -half, 0), (half, -half, 0), (half, half, 0), (-half, half, 0),
        ];
        var apexPoint = new Vector3d(apexOffset, 0, height);
        var apex = new BrepVertex(apexPoint);
        var baseVertices = corners.Select(c => new BrepVertex(c)).ToArray();

        var baseEdges = new BrepEdge[4];
        var rails = new BrepEdge[4];
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            baseEdges[i] = new BrepEdge(
                new Line3d(corners[i], corners[next]), Interval.Unit, baseVertices[i], baseVertices[next]);
            rails[i] = new BrepEdge(
                new Line3d(corners[i], apexPoint), Interval.Unit, baseVertices[i], apex);
        }

        var faces = new List<BrepFace>(5);
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            var x = (corners[next] - corners[i]).Normalized();
            var outward = (corners[next] - corners[i]).Cross(apexPoint - corners[i]).Normalized();
            faces.Add(new BrepFace(
                new PlaneSurface(corners[i], x, outward.Cross(x)),
                [new BrepLoop(
                [
                    new BrepCoedge(baseEdges[i], sameSense: true),
                    new BrepCoedge(rails[next], sameSense: true),
                    new BrepCoedge(rails[i], sameSense: false),
                ])]));
        }
        // Base: normal −Z, so the loop opposes the base edges' direction.
        faces.Add(new BrepFace(
            new PlaneSurface(corners[0], Vector3d.UnitY, Vector3d.UnitX),
            [new BrepLoop([.. Enumerable.Range(0, 4).Reverse()
                .Select(i => new BrepCoedge(baseEdges[i], sameSense: false))])]));
        var solid = new BrepSolid([new BrepShell(faces)]);
        solid.Validate();
        return solid;
    }
}
