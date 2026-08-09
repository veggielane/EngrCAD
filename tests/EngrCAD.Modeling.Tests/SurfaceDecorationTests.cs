using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// A decoration's job is to land on the surface AND to say what the map did to it, so the tests
/// come in matched pairs: the geometry lands (every point is on the mesh), and the distortion is
/// the exponential map's own published behaviour MEASURED on the curve actually laid — exact on
/// a plane, small on a developable surface, several percent on a strongly curved one. The last
/// of those is the one that matters: a decoration reporting a comfortable MEAN over a cap that
/// stretches one way and shrinks the other would be hiding exactly what it exists to say.
/// </summary>
public class SurfaceDecorationTests
{
    /// <summary>A flat grid in the z = 0 plane, so the exp map is the identity and any departure
    /// from scale 1 is the machinery's own error rather than the surface's.</summary>
    private static HalfEdgeMesh Plane(int n, double size)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= n; j++)
        for (int i = 0; i <= n; i++)
            positions.Add(new Vector3d(-size / 2 + size * i / n, -size / 2 + size * j / n, 0));

        var faces = new List<int[]>();
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i;
            faces.Add([a, a + 1, a + n + 2]);
            faces.Add([a, a + n + 2, a + n + 1]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>A finely tessellated tube wall — a developable surface with an edge length the
    /// map can walk. MeshPrimitives.Cylinder is a PRISM with one ring, so its 60-long vertical
    /// edges put almost the whole wall outside any sensible geodesic radius; that is a property
    /// of the fixture rather than of the map, and it is why this builds its own.</summary>
    private static HalfEdgeMesh Tube(double radius, double height, int around, int along)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= along; j++)
        for (int i = 0; i < around; i++)
        {
            double a = 2 * Math.PI * i / around;
            positions.Add(new Vector3d(radius * Math.Cos(a), radius * Math.Sin(a), height * j / along));
        }

        var faces = new List<int[]>();
        for (int j = 0; j < along; j++)
        for (int i = 0; i < around; i++)
        {
            int a = j * around + i;
            int b = j * around + (i + 1) % around;
            faces.Add([a, b, b + around]);
            faces.Add([a, b + around, a + around]);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static int NearestVertex(HalfEdgeMesh mesh, in Vector3d target)
    {
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            double d = mesh.GetPosition(v).DistanceTo(target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = v;
            }
        }
        return best;
    }

    [Fact]
    public void OnAPlaneTheMapIsTheIdentitySoTheDistortionIsEssentiallyZero()
    {
        var plane = Plane(24, 40);
        int seed = NearestVertex(plane, Vector3d.Zero);
        var curve = SpaceFillingCurve.Over(new Aabb((-8, -8, 0), (8, 8, 0)), SpaceFillingFamily.Hilbert, 2.0);

        var decoration = SurfaceDecoration.Wrap(plane, seed, curve, referenceDirection: Vector3d.UnitX);

        Assert.Equal(0, decoration.UnmappedPoints);
        Assert.Single(decoration.Runs);
        Assert.Equal(curve.Points.Count, decoration.PointCount);
        // Exact on a plane, to the grade the map's own remarks claim (transport is the identity
        // and the predictions telescope).
        Assert.Equal(1.0, decoration.MinScale, 6);
        Assert.Equal(1.0, decoration.MaxScale, 6);
        Assert.True(decoration.Distortion < 1e-6, $"distortion {decoration.Distortion}");
        // And it really is ON the plane rather than merely near it.
        foreach (var run in decoration.Runs)
        {
            foreach (var p in run)
                Assert.Equal(0.0, p.Z, 9);
        }
    }

    [Fact]
    public void OnACYLINDERTheDistortionIsTheDevelopableFewPercentAndTheCurveIsOnTheSurface()
    {
        // A cylinder is developable, so the map should unroll it: the published figure is about
        // 2%, and this MEASURES what the laid curve got rather than quoting it.
        const double radius = 20;
        var tube = Tube(radius, 60, 96, 48);
        int seed = NearestVertex(tube, new Vector3d(radius, 0, 30));
        var curve = SpaceFillingCurve.Over(new Aabb((-9, -9, 0), (9, 9, 0)), SpaceFillingFamily.Hilbert, 2.5);

        var decoration = SurfaceDecoration.Wrap(tube, seed, curve, referenceDirection: Vector3d.UnitZ);

        Assert.True(decoration.PointCount > curve.Points.Count / 2,
            $"only {decoration.PointCount} of {curve.Points.Count} landed");
        // Every placed point is on the tube wall (or a cap), so the placement is real geometry.
        foreach (var run in decoration.Runs)
        {
            foreach (var p in run)
            {
                // On the FACETED wall, so the reading is the chord rather than the radius: a
                // point inside a facet sits up to the sagitta inside, and never outside.
                double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                Assert.InRange(r, radius * 0.999, radius + 1e-9);
            }
        }
        // MEASURED 3.5e-4 — three orders under the sphere below, which is the whole content of
        // "developable": a tube unrolls, so a curve laid on it keeps the spacing it was drawn at.
        Assert.True(decoration.Distortion < 1e-3, $"distortion {decoration.Distortion} on a developable surface");
    }

    [Fact]
    public void OnASPHERETheDistortionIsREPORTEDRatherThanAveragedAway()
    {
        // The case the design exists for. A sphere has no isometric flattening, so the map
        // distorts — and the honest report is the EXTREMES, because a cap that stretches one
        // direction and shrinks another has a mean that says nothing.
        var sphere = MeshPrimitives.UvSphere(20, 64, 40);
        int seed = NearestVertex(sphere, new Vector3d(0, 0, 20));
        var curve = SpaceFillingCurve.Over(new Aabb((-9, -9, 0), (9, 9, 0)), SpaceFillingFamily.Hilbert, 2.0);

        var decoration = SurfaceDecoration.Wrap(sphere, seed, curve, referenceDirection: Vector3d.UnitX);

        Assert.True(decoration.PointCount > 0);
        foreach (var run in decoration.Runs)
        {
            // On the faceted sphere: inside a facet the point is on the CHORD, so it reads up
            // to the sagitta short of the radius and never past it.
            foreach (var p in run)
                Assert.InRange(p.Length, 20.0 * 0.99, 20.0 + 1e-9);
        }

        // The measurement with teeth: the extremes straddle 1, so the mean alone would read as
        // a faithful map.
        Assert.True(decoration.MinScale < 1.0, $"MinScale {decoration.MinScale}");
        Assert.True(decoration.MaxScale > 1.0, $"MaxScale {decoration.MaxScale}");
        Assert.True(decoration.Distortion > decoration.MeanScale - 1,
            "the reported distortion must exceed what the mean suggests, or it is averaging the "
            + "very thing it exists to report");
// MEASURED on this fixture: min 0.9263, max 1.0005, mean 0.9882 — so the mean reads a
        // comfortable 1.2% while the tightest pass is 7.4% closer than it was drawn, and it is
        // the 7.95% that a bead width has to be chosen against.
        Assert.InRange(decoration.Distortion, 0.05, 0.15);
        Assert.True(decoration.Distortion > 5 * Math.Abs(decoration.MeanScale - 1),
            $"distortion {decoration.Distortion} against a mean departure of {Math.Abs(decoration.MeanScale - 1)}");
    }

    [Fact]
    public void ACurveReachingPastTheMapBREAKSAndCountsWhatItLost()
    {
        // The exp map covers a disc; a curve past it has nowhere to go, and continuing it would
        // be inventing surface. The break is the honest answer and the count is the report.
        var plane = Plane(24, 40);
        int seed = NearestVertex(plane, Vector3d.Zero);
        var curve = SpaceFillingCurve.Over(new Aabb((-18, -18, 0), (18, 18, 0)), SpaceFillingFamily.Hilbert, 3.0);

        var decoration = SurfaceDecoration.Wrap(plane, seed, curve, Vector3d.UnitX, radius: 8);

        Assert.True(decoration.UnmappedPoints > 0, "the fixture is not reaching past the map at all");
        Assert.True(decoration.Runs.Count > 1, "a curve leaving and re-entering the map must break");
        Assert.Equal(curve.Points.Count, decoration.PointCount + decoration.UnmappedPoints);
    }

    [Fact]
    public void ACurveEntirelyOffTheMapIsRefusedByName()
    {
        var plane = Plane(12, 40);
        int seed = NearestVertex(plane, Vector3d.Zero);
        var far = new Vector2d[] { new(500, 500), new(501, 500) };

        var error = Assert.Throws<ArgumentException>(
            () => SurfaceDecoration.Wrap(plane, seed, far, Vector3d.UnitX, radius: 5));
        Assert.Contains("exponential map", error.Message);
    }

    [Fact]
    public void TheDecorationSharesTheONELinkerWithTheTwoFills()
    {
        var plane = Plane(24, 40);
        int seed = NearestVertex(plane, Vector3d.Zero);
        var curve = SpaceFillingCurve.Over(new Aabb((-18, -18, 0), (18, 18, 0)), SpaceFillingFamily.Hilbert, 3.0);
        var decoration = SurfaceDecoration.Wrap(plane, seed, curve, Vector3d.UnitX, radius: 8);

        var linkage = decoration.Link();
        Assert.Equal(decoration.Runs.Count, linkage.Order.Count);
        Assert.Equal(decoration.Runs.Count, linkage.Order.Select(o => o.Index).Distinct().Count());
        Assert.True(linkage.TravelLength <= linkage.SourceOrderTravelLength + 1e-9);
    }

    [Fact]
    public void ASketchOutlineLaysEveryLoopClosedOntoTheSurface()
    {
        // The glyph/engraving convenience: a sketch's loops (outer + holes) each land as a
        // CLOSED run, in surface millimetres from the seed, on a plane where the map is exact.
        var plane = Plane(24, 40);
        int seed = NearestVertex(plane, Vector3d.Zero);
        var outline = Sketch.Rectangle(12, 8).WithHole(Sketch.Circle(2));

        var decoration = SurfaceDecoration.Wrap(plane, seed, outline, referenceDirection: Vector3d.UnitX);

        Assert.Equal(0, decoration.UnmappedPoints);
        // Two loops in, two runs out: the rectangle and its circular hole.
        Assert.Equal(2, decoration.Runs.Count);
        // Every point is on the plane and the exp map is the identity there.
        foreach (var run in decoration.Runs)
        {
            foreach (var p in run)
                Assert.Equal(0.0, p.Z, 9);
            // Each loop is drawn CLOSED (first point repeated at the end).
            Assert.True(run[0].DistanceTo(run[^1]) < 1e-9, "the loop did not close");
        }
        Assert.True(decoration.Distortion < 1e-6, $"distortion {decoration.Distortion}");
    }

    [Fact]
    public void ASinglePointDecorationReportsAScaleOfExactlyOneRatherThanAnInfinity()
    {
        var plane = Plane(12, 40);
        int seed = NearestVertex(plane, Vector3d.Zero);
        var decoration = SurfaceDecoration.Wrap(plane, seed, new Vector2d[] { new(1, 1) }, Vector3d.UnitX);

        Assert.Equal(1, decoration.PointCount);
        Assert.Equal(1.0, decoration.MinScale);
        Assert.Equal(1.0, decoration.MaxScale);
        Assert.Equal(0.0, decoration.Distortion);
    }
}
