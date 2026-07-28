using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Opt-in biarc adoption: <see cref="SurfaceIntersection.FitAnalytic"/> and
/// <see cref="StepWriter.Options.ArcFitTolerance"/>. Both are OFF unless asked for, and both
/// report the deviation they cost — nothing in the kernel fits implicitly.
/// </summary>
public class BiArcAdoptionTests
{
    /// <summary>A polyline sampled off a circle, the shape the marching tracer emits.</summary>
    private static PolylineCurve3d TracedArc(int samples, double radius = 6, double sweep = 1.8)
    {
        var points = new Vector3d[samples];
        for (int i = 0; i < samples; i++)
        {
            double a = sweep * i / (samples - 1);
            points[i] = new Vector3d(radius * Math.Cos(a), radius * Math.Sin(a), 2);
        }
        return new PolylineCurve3d(points);
    }

    // ---- SurfaceIntersection.FitAnalytic ----

    [Fact]
    public void Intersect_StillReturnsPolylines_NothingFitsImplicitly()
    {
        // Two cylinders whose axes cross at an angle: no analytic tier, so the tracer runs
        // and its polyline must reach the caller unchanged. The boolean pipeline depends on
        // that (a traced polyline is exact only at its vertices).
        var a = new CylinderSurface(Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX, 4);
        var b = new CylinderSurface(new Vector3d(0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 2.5);
        var region = new Aabb(new Vector3d(-8, -8, -8), new Vector3d(8, 8, 8));
        var curves = SurfaceIntersection.Intersect(a, b, region);
        Assert.NotEmpty(curves);
        Assert.All(curves, c => Assert.IsType<PolylineCurve3d>(c));
    }

    [Fact]
    public void FitAnalytic_ReplacesATracedArcWithExactGeometry_AndReportsTheDeviation()
    {
        var traced = TracedArc(200);
        var results = SurfaceIntersection.FitAnalytic([traced], 1e-3);
        var fit = Assert.Single(results);

        Assert.True(fit.Fitted);
        Assert.Equal(BiArcFitStatus.Success, fit.Status);
        Assert.True(fit.Deviation <= 1e-3, $"deviation {fit.Deviation}");
        Assert.True(fit.Curves.Count <= 2, $"{fit.Curves.Count} pieces for one arc");

        // The fitted geometry really is on the circle.
        foreach (var curve in fit.Curves)
        {
            for (int i = 0; i <= 8; i++)
            {
                var p = curve.PointAt(curve.Domain.ParameterAt(i / 8.0));
                Assert.Equal(6.0, new Vector2d(p.X, p.Y).Length, 6);
                Assert.Equal(2.0, p.Z, 9);
            }
        }
    }

    [Fact]
    public void FitAnalytic_LeavesAnalyticCurvesAlone()
    {
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 5);
        var fit = Assert.Single(SurfaceIntersection.FitAnalytic([circle], 1e-3));
        Assert.False(fit.Fitted);
        Assert.Same(circle, Assert.Single(fit.Curves));
    }

    [Fact]
    public void FitAnalytic_RefusesASpaceCurve_RatherThanFlatteningIt()
    {
        // A helical polyline is not planar, so the fit is refused BY STATUS and the original
        // curve comes back — a silently flattened space curve would be far worse than none.
        var points = new Vector3d[64];
        for (int i = 0; i < 64; i++)
        {
            double t = 3 * Math.PI * i / 63;
            points[i] = new Vector3d(4 * Math.Cos(t), 4 * Math.Sin(t), 0.9 * t);
        }
        var helix = new PolylineCurve3d(points);
        var fit = Assert.Single(SurfaceIntersection.FitAnalytic([helix], 1e-3));
        Assert.False(fit.Fitted);
        Assert.Equal(BiArcFitStatus.NotPlanar, fit.Status);
        Assert.Same(helix, Assert.Single(fit.Curves));
    }

    [Fact]
    public void TheDeviationMeasuresTheSAMPLES_WhichIsThePropertyToKnowAbout()
    {
        // A coarse 20-gon "fits" at an absurd tolerance, because the deviation is measured
        // from the INPUT SAMPLES to the chain and a chain through every sample has almost
        // none. It says nothing about the true curve between the samples - that is a
        // property of the tracer's step, not of the fit - and it costs pieces to buy.
        var points = new Vector3d[21];
        for (int i = 0; i <= 20; i++)
        {
            double a = 2 * Math.PI * i / 20;
            points[i] = new Vector3d(10 * Math.Cos(a), 10 * Math.Sin(a), 0);
        }
        var coarse = new PolylineCurve3d(points);
        var fit = Assert.Single(SurfaceIntersection.FitAnalytic([coarse], 1e-9));
        Assert.True(fit.Fitted);
        Assert.True(fit.Deviation <= 1e-9, $"deviation {fit.Deviation}");

        // ...yet the POLYLINE it replaced runs 0.12 away from the fit at every chord
        // midpoint, because the fit interpolates the samples and arcs through them bulge
        // out to the circle those samples came from.
        var midpoint = (points[0] + points[1]) * 0.5;
        double nearest = double.PositiveInfinity;
        foreach (var curve in fit.Curves)
        {
            for (int i = 0; i <= 200; i++)
            {
                var p = curve.PointAt(curve.Domain.ParameterAt(i / 200.0));
                nearest = Math.Min(nearest, p.DistanceTo(midpoint));
            }
        }
        Assert.True(nearest > 0.1, $"fit sits {nearest} from the polyline's own midpoint");
    }

    [Fact]
    public void FitAnalytic_RejectsANonPositiveTolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SurfaceIntersection.FitAnalytic([TracedArc(50)], 0));
    }

    // ---- StepWriter.Options.ArcFitTolerance ----

    private static BrepSolid Plate() =>
        SolidFactory.MakeBox(new Aabb(new Vector3d(-10, -10, 0), new Vector3d(10, 10, 5)));

    [Fact]
    public void StepWriter_DefaultsToTheExactBytesItAlwaysWrote()
    {
        var solid = Plate();
        string plain = StepWriter.Write(solid);
        var withOptions = StepWriter.Write(solid, new StepWriter.Options(), "EngrCAD part");
        Assert.Equal(plain, withOptions.Text);
        Assert.Equal(0, withOptions.ArcFitCount);
        Assert.Equal(0, withOptions.ArcFitDeviation);
    }

    [Fact]
    public void StepWriter_ExportsAFittedArcChainInsteadOfASampledPolyline()
    {
        // A polyline-backed edge is the case that matters: a traced rim currently exports as
        // a degree-1 spline with one control point per sample.
        var traced = TracedArc(120);
        string sampled = EmitOneCurve(traced, options: null, out int sampledCount, out _);
        var fitted = EmitOneCurveResult(traced, new StepWriter.Options(ArcFitTolerance: 1e-3));

        Assert.True(sampledCount > 0);
        Assert.Equal(sampledCount, fitted.ArcFitCount);
        Assert.Equal(0, fitted.SampledCurveCount);
        Assert.True(fitted.ArcFitDeviation <= 1e-3);
        // Far fewer CARTESIAN_POINTs: the whole point of the option.
        Assert.True(CountOf(fitted.Text, "CARTESIAN_POINT") < CountOf(sampled, "CARTESIAN_POINT") / 4,
            $"{CountOf(fitted.Text, "CARTESIAN_POINT")} vs {CountOf(sampled, "CARTESIAN_POINT")}");
    }

    [Fact]
    public void AFittedChainReadsBackAsTheSameGeometry()
    {
        // The chain is emitted as ONE degree-2 rational B-spline, which is inside the entity
        // set StepReader already parses — so the file round-trips rather than needing a
        // COMPOSITE_CURVE the reader would skip.
        var traced = TracedArc(120);
        var result = EmitOneCurveResult(traced, new StepWriter.Options(ArcFitTolerance: 1e-3));
        var read = StepReader.Read(result.Text);
        Assert.NotEmpty(read.Solids);

        var edges = read.Solids[0].Shells
            .SelectMany(s => s.Faces).SelectMany(f => f.Loops).SelectMany(l => l.Coedges)
            .Select(c => c.Edge.Curve).ToList();
        // Every point of the re-read spline sits on the traced circle.
        foreach (var curve in edges.OfType<NurbsCurve>().Where(c => c.Degree == 2))
        {
            for (int i = 0; i <= 16; i++)
            {
                var p = curve.PointAt(curve.Domain.ParameterAt(i / 16.0));
                // Within the FIT tolerance the file was written at, not to machine
                // precision: that deviation is exactly what result.ArcFitDeviation reports.
                Assert.True(Math.Abs(new Vector2d(p.X, p.Y).Length - 6.0) <= 1e-3,
                    $"radius {new Vector2d(p.X, p.Y).Length}");
            }
        }
    }

    private static int CountOf(string text, string token)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    /// <summary>
    /// Builds a one-face solid whose single curved edge is <paramref name="curve"/>, so the
    /// writer's curve path is exercised end to end. A ruled band between the curve and its
    /// translate, capped by two straight rungs.
    /// </summary>
    private static BrepSolid BandAround(Curve3d curve)
    {
        var lift = new Vector3d(0, 0, 4);
        var top = curve.Transformed(Matrix4d.CreateTranslation(lift));
        var surface = new ExtrudedSurface(curve, lift);

        var v0 = new BrepVertex(curve.PointAt(curve.Domain.Start));
        var v1 = new BrepVertex(curve.PointAt(curve.Domain.End));
        var v2 = new BrepVertex(v1.Position + lift);
        var v3 = new BrepVertex(v0.Position + lift);

        var bottom = new BrepEdge(curve, curve.Domain, v0, v1);
        var right = new BrepEdge(new Line3d(v1.Position, v2.Position), Interval.Unit, v1, v2);
        var upper = new BrepEdge(top, top.Domain, v3, v2);
        var left = new BrepEdge(new Line3d(v0.Position, v3.Position), Interval.Unit, v0, v3);

        var loop = new BrepLoop(
        [
            new BrepCoedge(bottom, true),
            new BrepCoedge(right, true),
            new BrepCoedge(upper, false),
            new BrepCoedge(left, false),
        ]);
        var face = new BrepFace(surface, [loop]);
        return new BrepSolid([new BrepShell([face])]);
    }

    private static string EmitOneCurve(
        Curve3d curve, StepWriter.Options? options, out int sampled, out int fitted)
    {
        var result = StepWriter.Write(BandAround(curve), options ?? new StepWriter.Options());
        sampled = result.SampledCurveCount;
        fitted = result.ArcFitCount;
        return result.Text;
    }

    private static StepWriter.Result EmitOneCurveResult(Curve3d curve, StepWriter.Options options) =>
        StepWriter.Write(BandAround(curve), options);
}
