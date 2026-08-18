using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The straight-skeleton roof (OpenSCAD's <c>roof()</c>). Verified against CLOSED FORMS
/// throughout — a regular polygon's roof is a pyramid of known volume, a rectangle's is the
/// textbook hip roof whose RIDGE LENGTH is what proves the skeleton rather than the volume,
/// and the L-shape's roof is checked against a hand-derived face decomposition — with every
/// instrument mutation-checked so it can be shown to bite.
/// </summary>
public class RoofTests
{
    // The L-shape used throughout: [0,30]x[0,6] joined to [18,30]x[0,20]. Its skeleton was
    // derived by hand (see docs/examples/roof.md): interior nodes (3,3), (21,3), (24,6) and
    // (24,14); faces (0,0),(30,0),(24,6),(21,3),(3,3) / (30,0),(30,20),(24,14),(24,6) /
    // (30,20),(18,20),(24,14) / (18,20),(18,6),(21,3),(24,6),(24,14) /
    // (18,6),(0,6),(3,3),(21,3) / (0,6),(0,0),(3,3), whose integrals of height at unit slope
    // are 153 + 216 + 36 + 207 + 81 + 9 = 738.
    private const double LShapeUnitSlopeVolume = 738;
    private const double LShapeArea = 348;

    private static Sketch LShape() => Sketch.Polygon(
    [
        new Vector2d(0, 0), new Vector2d(30, 0), new Vector2d(30, 20),
        new Vector2d(18, 20), new Vector2d(18, 6), new Vector2d(0, 6),
    ]);

    private static Vector2d[] LShapeCorners() =>
    [
        new(0, 0), new(30, 0), new(30, 20), new(18, 20), new(18, 6), new(0, 6),
    ];

    // ---- closed forms: the pyramid and the hip roof ----

    [Fact]
    public void RegularPolygonRoof_IsAPyramidOfExactlyAreaTimesHeightOverThree()
    {
        foreach (int sides in new[] { 3, 4, 5, 6, 8, 12 })
        {
            var sketch = Sketch.Polygon(RegularPolygon(sides, 10));
            var facts = Shape.RoofFacts(sketch, RoofPitch.FromAngle(30));
            double expected = sketch.Area() * facts.Height / 3;

            // A regular polygon's skeleton is a single point, so the roof IS a pyramid.
            Assert.Equal(1, facts.Skeleton.InteriorNodeCount);
            Assert.Equal(0, facts.Skeleton.SplitEvents);
            Assert.Equal(expected, facts.Volume, 1e-9 * expected);
        }
    }

    [Fact]
    public void RectangleRoof_IsTheHipRoofWithTheClosedFormRidgeAndVolume()
    {
        const double length = 30, width = 10, pitch = 35;
        double slope = Math.Tan(pitch * Math.PI / 180);
        var sketch = Sketch.Rectangle(length, width);
        var facts = Shape.RoofFacts(sketch, RoofPitch.FromAngle(pitch));

        // Height: half the width, risen at the pitch.
        Assert.Equal(width / 2 * slope, facts.Height, 1e-12);

        // Volume: tan(pitch) * (L*W^2/4 - W^3/12), integrating min(x, L-x, y, W-y).
        double expected = slope * (length * width * width / 4 - width * width * width / 12);
        Assert.Equal(expected, facts.Volume, 1e-9 * expected);

        // The RIDGE is what proves the skeleton rather than the volume: exactly two interior
        // nodes, at the apex height, exactly (length - width) apart.
        var apex = facts.Skeleton.Nodes.Skip(sketch.ToCurves().Count).ToList();
        Assert.Equal(2, apex.Count);
        Assert.All(apex, n => Assert.Equal(width / 2, n.Time, 1e-12));
        Assert.Equal(length - width, (apex[0].Position - apex[1].Position).Length, 1e-12);
    }

    [Fact]
    public void SquareRoof_HasFourPlanesMeetingAtOnePointRatherThanFourNearCoincidentNodes()
    {
        var facts = Shape.RoofFacts(Sketch.Rectangle(20, 20), RoofPitch.FromAngle(45));
        Assert.Equal(1, facts.Skeleton.InteriorNodeCount);
        Assert.Equal(new Vector2d(0, 0), facts.Skeleton.Nodes[^1].Position);
        Assert.Equal(10, facts.Skeleton.Nodes[^1].Time, 1e-12);
        Assert.Equal(20 * 20 * 10 / 3.0, facts.Volume, 1e-9);
    }

    // ---- the split event ----

    [Fact]
    public void LShapeRoof_MatchesTheHandDerivedSkeletonAndVolume()
    {
        var facts = Shape.RoofFacts(LShape(), RoofPitch.FromAngle(45));

        Assert.True(facts.Skeleton.SplitEvents >= 1,
            "an L-shape's reflex corner must reach the opposite edge — that is a split event");
        Assert.Equal(4, facts.Skeleton.InteriorNodeCount);

        var interior = facts.Skeleton.Nodes.Skip(6)
            .OrderBy(n => n.Position.X).ThenBy(n => n.Position.Y).ToList();
        AssertNode(interior[0], 3, 3, 3);
        AssertNode(interior[1], 21, 3, 3);
        AssertNode(interior[2], 24, 6, 6);
        AssertNode(interior[3], 24, 14, 6);

        Assert.Equal(LShapeUnitSlopeVolume, facts.Volume, 1e-9 * LShapeUnitSlopeVolume);

        static void AssertNode(SkeletonNode node, double x, double y, double time)
        {
            Assert.Equal(new Vector2d(x, y), node.Position, new Vector2dComparer(1e-9));
            Assert.Equal(time, node.Time, 1e-9);
        }
    }

    [Fact]
    public void LShapeFaces_PartitionTheFootprintExactly()
    {
        var skeleton = StraightSkeleton.Of(LShapeCorners());
        double total = 0;
        foreach (var face in skeleton.Faces)
            total += PolygonArea(face.Select(i => skeleton.Nodes[i].Position).ToList());
        Assert.Equal(LShapeArea, total, 1e-9);
        Assert.Equal(6, skeleton.Faces.Count);
    }

    [Fact]
    public void SkippingSplitEvents_BreaksTheLShape_AndIsWhyTheyExist()
    {
        // The mutation that proves the instrument: a convex-only simulation cannot close an
        // L-shape's wavefront, and the verification says so rather than returning a roof.
        var convexOnly = Assert.Throws<StraightSkeletonException>(
            () => StraightSkeleton.Of(LShapeCorners(), allowSplitEvents: false));
        Assert.Contains("straight skeleton", convexOnly.Message, StringComparison.OrdinalIgnoreCase);

        // ...while the same fixture with split events is fine.
        var full = StraightSkeleton.Of(LShapeCorners(), allowSplitEvents: true);
        Assert.True(full.SplitEvents >= 1);
    }

    [Fact]
    public void ConvexPolygons_AreUnaffectedByTheSplitMachinery()
    {
        // Rule out that split events perturb the convex case: a convex footprint's skeleton
        // is bit-identical with and without them.
        var square = RegularPolygon(7, 12);
        var with = StraightSkeleton.Of(square, allowSplitEvents: true);
        var without = StraightSkeleton.Of(square, allowSplitEvents: false);
        Assert.Equal(0, with.SplitEvents);
        Assert.Equal(with.Nodes.Count, without.Nodes.Count);
        for (int i = 0; i < with.Nodes.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(with.Nodes[i].Position.X),
                BitConverter.DoubleToInt64Bits(without.Nodes[i].Position.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(with.Nodes[i].Time),
                BitConverter.DoubleToInt64Bits(without.Nodes[i].Time));
        }
    }

    [Fact]
    public void AnIsolatedSplitEvent_LandsWhereTheClosedFormSaysItDoes()
    {
        // A rectilinear L makes its split coincide with its arm's own edge event. Slanting the
        // inner ceiling separates them, so this fixture exercises a split in ISOLATION: the
        // reflex corner (18,6) travels at (1, -1.246101...) and reaches y = t at t = 2.671...
        var corners = new Vector2d[]
        {
            new(0, 0), new(30, 0), new(30, 20), new(18, 20), new(18, 6), new(0, 10),
        };
        var skeleton = StraightSkeleton.Of(corners);
        Assert.Equal(1, skeleton.SplitEvents);

        var slant = (new Vector2d(0, 10) - new Vector2d(18, 6)).Normalized();
        var normal = slant.Perpendicular;
        double vy = (1 - normal.X) / normal.Y;
        double expectedTime = 6 / (1 - vy);
        var split = skeleton.Nodes.Skip(corners.Length)
            .OrderBy(n => Math.Abs(n.Time - expectedTime)).First();
        Assert.Equal(expectedTime, split.Time, 1e-9);
        Assert.Equal(18 + expectedTime, split.Position.X, 1e-9);
    }

    [Fact]
    public void ARegularStar_ResolvesByEDGEEventsAlone_WhichIsTheFindingRatherThanTheExpectation()
    {
        // A regular star is the obvious split-event fixture and it is NOT one: every reflex
        // vertex's bisector points at the CENTRE (the notch between two points is exterior), so
        // all of them march inward together and the whole wavefront resolves by edge events —
        // measured at zero splits and ONE interior node for 5/20:4, 5/20:8, 5/20:14 and 6/20:6.
        foreach (var (points, outer, inner) in new[] { (5, 20.0, 4.0), (5, 20.0, 8.0), (5, 20.0, 14.0), (6, 20.0, 6.0) })
        {
            var skeleton = StraightSkeleton.Of(Star(points, outer, inner));
            Assert.Equal(0, skeleton.SplitEvents);
            Assert.Equal(1, skeleton.InteriorNodeCount);
            AssertPartitions(skeleton);
        }
    }

    [Fact]
    public void AnIrregularStar_DoesSplit_AndStillPartitionsItsFootprint()
    {
        // Break the symmetry and the reflex vertices stop arriving together, so one reaches a
        // non-adjacent edge first: the star family's split-event member.
        foreach (var (points, inner, jitter) in new[] { (5, 6.0, 1.7), (7, 7.0, 1.1) })
        {
            var skeleton = StraightSkeleton.Of(Star(points, 20, inner, jitter));
            Assert.True(skeleton.SplitEvents >= 1, $"{points}-point star ran {skeleton.SplitEvents} splits");
            AssertPartitions(skeleton);
        }
    }

    [Fact]
    public void TwoReflexCornersSplitTheSameEdgeAtOnce_LeavingThreeWavefrontLoops()
    {
        // A slot cut into a plate: BOTH of the slot's reflex corners reach the bottom edge at
        // the same instant, so one edge is split twice and the wavefront becomes three loops.
        var slot = new Vector2d[]
        {
            new(0, 0), new(30, 0), new(30, 20), new(20, 20),
            new(20, 5), new(10, 5), new(10, 20), new(0, 20),
        };
        var skeleton = StraightSkeleton.Of(slot);
        Assert.Equal(2, skeleton.SplitEvents);
        AssertPartitions(skeleton);

        // ...and a comb of two slots does it four times.
        var comb = new Vector2d[]
        {
            new(0, 0), new(40, 0), new(40, 20), new(32, 20), new(32, 6), new(24, 6),
            new(24, 20), new(16, 20), new(16, 6), new(8, 6), new(8, 20), new(0, 20),
        };
        var combSkeleton = StraightSkeleton.Of(comb);
        Assert.Equal(4, combSkeleton.SplitEvents);
        AssertPartitions(combSkeleton);
        Assert.True(Shape.Roof(Sketch.Polygon(comb), 40).ToMesh().IsClosed);
    }

    // ---- the solid ----

    [Fact]
    public void RoofSolid_IsValidClosedAndCarriesTheClosedFormVolume()
    {
        foreach (var (name, sketch, expected) in Fixtures())
        {
            var shape = Shape.Roof(sketch, 45);
            var solid = shape.ToBrep();
            solid.Validate();

            var mesh = shape.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
            Assert.True(mesh.IsClosed, $"{name}: the roof mesh is open");
            Assert.Equal(expected, Math.Abs(mesh.SignedVolume()), 1e-9 * expected);
        }

        static IEnumerable<(string, Sketch, double)> Fixtures()
        {
            yield return ("square", Sketch.Rectangle(20, 20), 20 * 20 * 10 / 3.0);
            yield return ("rectangle", Sketch.Rectangle(30, 10), 30 * 100 / 4.0 - 1000 / 12.0);
            yield return ("L", LShape(), LShapeUnitSlopeVolume);
        }
    }

    [Fact]
    public void RoofFaceCount_IsOnePerBaseEdgePlusTheFloor()
    {
        var solid = Shape.Roof(LShape(), 30).ToBrep();
        Assert.Equal(7, solid.Faces.Count());
        Assert.All(solid.Faces, f => Assert.True(f.IsPlanar(out _, out _),
            "every roof face is a plane — that is what makes the operation exact"));
    }

    [Fact]
    public void HeightAndPitch_AreTwoSpellingsOfOneNumber()
    {
        var sketch = LShape();
        var byAngle = Shape.RoofFacts(sketch, RoofPitch.FromAngle(37));
        var byHeight = Shape.RoofFacts(sketch, RoofPitch.FromHeight(byAngle.Height));

        Assert.Equal(byAngle.Slope, byHeight.Slope, 1e-12);
        Assert.Equal(37, byHeight.PitchDegrees, 1e-9);
        Assert.Equal(byAngle.Volume, byHeight.Volume, 1e-9 * byAngle.Volume);

        // ...and the height really is the apex: the tallest vertex of the solid.
        var solid = Shape.Roof(sketch, RoofPitch.FromHeight(9)).ToBrep();
        double top = solid.Faces.SelectMany(f => f.Loops).SelectMany(l => l.Coedges)
            .Select(c => c.Edge.StartVertex.Position.Z).Max();
        Assert.Equal(9, top, 1e-9);
    }

    [Fact]
    public void RoofVolume_ScalesWithTheCubeUnderAUniformScale()
    {
        var sketch = LShape();
        double plain = Math.Abs(Shape.Roof(sketch, 45).ToMesh().SignedVolume());
        double scaled = Math.Abs(Shape.Roof(sketch, 45).Scale(3).ToMesh().SignedVolume());
        Assert.Equal(27 * plain, scaled, 1e-9 * 27 * plain);
    }

    [Fact]
    public void MirroredRoof_IsValidAndTheSameSizeTheOtherWayRound()
    {
        var sketch = LShape();
        var mirrored = Shape.Roof(sketch, 45).Mirror(Vector3d.Zero, Vector3d.UnitX);

        // A reflection is a similarity, so the roof stays NATIVE rather than being bridged —
        // which is the claim, and it is asserted rather than inferred from the build working.
        Assert.All(mirrored.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));

        var solid = mirrored.ToBrep();
        solid.Validate();
        var mesh = mirrored.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(LShapeUnitSlopeVolume, Math.Abs(mesh.SignedVolume()), 1e-9 * LShapeUnitSlopeVolume);
    }

    [Fact]
    public void RoofExplainsItselfHonestly()
    {
        var shape = Shape.Roof(LShape(), 30);
        Assert.All(shape.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        Assert.All(shape.Explain(TargetRep.Implicit).Entries, e => Assert.Equal(NodeSupport.Bridged, e.Support));

        // A shear changes the pitch, which is the one thing a roof states.
        var sheared = shape.Transform(new Matrix4d(
            1, 0.4, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1));
        Assert.Contains(sheared.Explain(TargetRep.Brep).Entries, e => e.Support == NodeSupport.Impossible);
    }

    // ---- refusals ----

    [Fact]
    public void HolesAreRefusedByName()
    {
        var holed = Sketch.Rectangle(40, 30).WithHole(Sketch.Circle(6));
        var ex = Assert.Throws<NotSupportedException>(() => Shape.Roof(holed, 30).ToBrep());
        Assert.Contains("hole", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("merge", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurvedFootprintsAreRefusedByName()
    {
        var ex = Assert.Throws<NotSupportedException>(() => Shape.Roof(Sketch.Circle(10), 30).ToBrep());
        Assert.Contains("POLYGONAL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImpossiblePitchesAreRefusedByName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofPitch.FromAngle(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofPitch.FromAngle(90));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofPitch.FromAngle(120));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofPitch.FromHeight(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofPitch.FromHeight(-4));
    }

    [Fact]
    public void DegenerateFootprintsAreRefusedByName()
    {
        Assert.Throws<ArgumentException>(() => StraightSkeleton.Of(
            [new Vector2d(0, 0), new Vector2d(1, 0)]));
        Assert.Throws<ArgumentException>(() => StraightSkeleton.Of(
            [new Vector2d(0, 0), new Vector2d(1, 0), new Vector2d(2, 0)]));
    }

    [Fact]
    public void ClockwiseInputIsNormalised_SoWindingIsNeverTheCaller_sProblem()
    {
        var ccw = StraightSkeleton.Of(LShapeCorners());
        var cw = StraightSkeleton.Of(LShapeCorners().Reverse().ToArray());
        Assert.Equal(ccw.InteriorNodeCount, cw.InteriorNodeCount);
        Assert.Equal(ccw.MaxTime, cw.MaxTime, 1e-12);
    }

    [Fact]
    public void CollinearRunsInTheFootprintAreTolerated()
    {
        // A corner with a straight angle: its two edges are parallel, so the vertex simply
        // translates with them rather than having no velocity at all.
        var corners = new Vector2d[]
        {
            new(0, 0), new(10, 0), new(20, 0), new(20, 10), new(0, 10),
        };
        var skeleton = StraightSkeleton.Of(corners);
        double total = 0;
        foreach (var face in skeleton.Faces)
            total += PolygonArea(face.Select(i => skeleton.Nodes[i].Position).ToList());
        Assert.Equal(200, total, 1e-9);
    }

    [Fact]
    public void RoofIsDeterministic()
    {
        var a = Shape.RoofFacts(LShape(), RoofPitch.FromAngle(33));
        var b = Shape.RoofFacts(LShape(), RoofPitch.FromAngle(33));
        Assert.Equal(a.Skeleton.Nodes.Count, b.Skeleton.Nodes.Count);
        for (int i = 0; i < a.Skeleton.Nodes.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Skeleton.Nodes[i].Position.X),
                BitConverter.DoubleToInt64Bits(b.Skeleton.Nodes[i].Position.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Skeleton.Nodes[i].Position.Y),
                BitConverter.DoubleToInt64Bits(b.Skeleton.Nodes[i].Position.Y));
        }
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Volume), BitConverter.DoubleToInt64Bits(b.Volume));
    }

    // ---- helpers ----

    private static Vector2d[] RegularPolygon(int sides, double radius)
    {
        var corners = new Vector2d[sides];
        for (int i = 0; i < sides; i++)
        {
            double t = 2 * Math.PI * i / sides;
            corners[i] = new Vector2d(radius * Math.Cos(t), radius * Math.Sin(t));
        }
        return corners;
    }

    private static Vector2d[] Star(int points, double outer, double inner, double jitter = 0)
    {
        var corners = new Vector2d[points * 2];
        for (int i = 0; i < corners.Length; i++)
        {
            double r = (i % 2 == 0 ? outer : inner) + jitter * ((i * 7 % 5) - 2);
            double t = Math.PI * i / points;
            corners[i] = new Vector2d(r * Math.Cos(t), r * Math.Sin(t));
        }
        return corners;
    }

    /// <summary>The one property the whole construction stands on: the skeleton faces tile the
    /// footprint, so their areas sum to the polygon's own.</summary>
    private static void AssertPartitions(StraightSkeleton skeleton)
    {
        double total = 0;
        foreach (var face in skeleton.Faces)
            total += PolygonArea(face.Select(i => skeleton.Nodes[i].Position).ToList());
        double expected = PolygonArea(skeleton.Polygon);
        Assert.Equal(expected, total, 1e-9 * expected);
    }

    private static double PolygonArea(IReadOnlyList<Vector2d> loop)
    {
        double twice = 0;
        for (int i = 0; i < loop.Count; i++)
            twice += loop[i].Cross(loop[(i + 1) % loop.Count]);
        return twice * 0.5;
    }

    private sealed class Vector2dComparer(double tolerance) : IEqualityComparer<Vector2d>
    {
        public bool Equals(Vector2d a, Vector2d b) => (a - b).Length <= tolerance;
        public int GetHashCode(Vector2d v) => 0;
    }
}
