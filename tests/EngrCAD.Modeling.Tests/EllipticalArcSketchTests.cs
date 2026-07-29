using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Elliptical arcs as a first-class sketch segment. The oracles are analytic throughout —
/// an ellipse's area is πab and a prism's volume is that times the height, exactly, and
/// the signed-distance field's zero set is the curve itself — because "some solid came
/// out" would pass a segment whose parity or area term was subtly wrong.
/// </summary>
public class EllipticalArcSketchTests
{
    // ---- the exact quantities the segment must supply --------------------------

    [Fact]
    public void FullEllipse_HasTheAnalyticArea()
    {
        // pi*a*b, and the area comes from the Green's-theorem term alone, so this is a
        // direct test of SignedAreaContribution rather than of anything downstream.
        Assert.Equal(Math.PI * 6 * 2.5, Sketch.Ellipse(6, 2.5).Area(), 12);
    }

    [Fact]
    public void RotatedEllipse_HasTheSameArea()
    {
        // Area is rotation-invariant, and the term is |A x B|*sweep/2 + a centre term
        // that cancels over a closed loop — so a rotated ellipse is the cheapest check
        // that the cross product, not a pair of scalar radii, is doing the work.
        Assert.Equal(Math.PI * 6 * 2.5, Sketch.Ellipse((3, -1), 6, 2.5, rotationDegrees: 37).Area(), 12);
    }

    [Fact]
    public void EllipseArea_ReducesToTheCircularOne()
    {
        // Equal semi-axes: the ellipse segment's area term must agree with the circular
        // segment's to the last bits, since the formulas are meant to be one derivation.
        Assert.Equal(Sketch.Circle(3).Area(), Sketch.Ellipse(3, 3).Area(), 12);
    }

    [Fact]
    public void EllipseRegion_HasTheCurveAsItsZeroSetAndTheRightSign()
    {
        // The parity rule (inside/outside) and the distance are independent halves of the
        // field, so both are checked: exactly zero ON the curve, negative inside,
        // positive outside, at angles that are not the axis ends.
        const double a = 6, b = 2.5;
        var region = Sketch.Ellipse(a, b).ToRegion();
        for (int i = 0; i < 24; i++)
        {
            double t = i * 2 * Math.PI / 24;
            var on = new Vector2d(a * Math.Cos(t), b * Math.Sin(t));
            Assert.Equal(0, region.SignedDistance(on), 9);
            Assert.True(region.SignedDistance(on * 0.9) < 0, $"0.9x at t={t} should be inside");
            Assert.True(region.SignedDistance(on * 1.1) > 0, $"1.1x at t={t} should be outside");
        }
    }

    [Fact]
    public void RotatedEllipseRegion_KeepsItsParityThroughTheRotation()
    {
        // The monotone-piece inversion solves y - Cy = R sin(theta + phi), so a rotated
        // ellipse is where a wrong branch or a missed 2*pi shift would show: the whole
        // interior would flip somewhere along the ray.
        const double a = 5, b = 1.6, rotation = 31 * Math.PI / 180;
        double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
        var region = Sketch.Ellipse(default, a, b, 31).ToRegion();
        for (int i = 0; i < 32; i++)
        {
            double t = i * 2 * Math.PI / 32;
            double x = a * Math.Cos(t), y = b * Math.Sin(t);
            var on = new Vector2d(x * cos - y * sin, x * sin + y * cos);
            Assert.Equal(0, region.SignedDistance(on), 9);
            Assert.True(region.SignedDistance(on * 0.85) < 0, $"inside at t={t}");
            Assert.True(region.SignedDistance(on * 1.15) > 0, $"outside at t={t}");
        }
    }

    [Fact]
    public void EllipseBounds_AreTightAndExactForARotatedEllipse()
    {
        // The extremes are the closed-form dx/dtheta = 0 angles, so the half-width of a
        // rotated ellipse is the analytic sqrt((a cos r)^2 + (b sin r)^2) — a sampled
        // bounds would come in slightly under.
        const double a = 6, b = 2.5, rotation = 37 * Math.PI / 180;
        var bounds = Sketch.Ellipse(default, a, b, 37).Bounds;
        double halfX = Math.Sqrt(Math.Pow(a * Math.Cos(rotation), 2) + Math.Pow(b * Math.Sin(rotation), 2));
        double halfY = Math.Sqrt(Math.Pow(a * Math.Sin(rotation), 2) + Math.Pow(b * Math.Cos(rotation), 2));
        Assert.Equal(halfX, bounds.Size.X / 2, 12);
        Assert.Equal(halfY, bounds.Size.Y / 2, 12);
    }

    // ---- exact in all three representations ------------------------------------

    [Fact]
    public void EllipticalPrism_IsExactInBrepAndMesh()
    {
        // The B-Rep side carries an Ellipse3d rather than a flattened outline, so the
        // ONLY error is the inscribed-polygon deficit — which means the discrete truth is
        // available in closed form and this can be an IDENTITY rather than a tolerance.
        // Sampling an ellipse at n even ANGLES gives vertices (a cos t, b sin t), whose
        // shoelace area is (n/2)ab sin(2pi/n) exactly (the cross terms collapse to
        // sin(t_{k+1} - t_k)); the analytic pi*a*b is the n -> infinity limit.
        const double a = 6, b = 2.5, h = 4;
        const int segments = 256;
        double analytic = Math.PI * a * b * h;
        double inscribed = 0.5 * a * b * segments * Math.Sin(2 * Math.PI / segments) * h;
        var prism = Shape.Extrude(Sketch.Ellipse(a, b), h);

        Assert.All(prism.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var solid = prism.ToBrep();
        solid.Validate();

        var mesh = BRepTessellator.Tessellate(solid, segments, 32);
        Assert.True(mesh.IsClosed);
        Assert.Equal(inscribed, mesh.Volume(), 9);
        // ...and that discrete truth is 1.0e-4 relative under the analytic value, which is
        // (2pi/n)^2/6 — i.e. the tessellation really is sampling at the density asked for.
        // (Before the tessellator learned that an ellipse's parameter is an angle, this
        // read 0.64% under: the deficit of a 23-gon, whatever segmentsPerCircle said.)
        Assert.Equal(1.004e-4, (analytic - mesh.Volume()) / analytic, 7);
    }

    [Fact]
    public void EllipticalPrism_IsImplicitNativeAndConvergesToTheSameVolume()
    {
        // Implicit-Native via Sdf.ExtrudedRegion, so the field is the sketch's own exact
        // 2D distance rather than a mesh SDF — which is what makes an elliptical lattice
        // or blend meaningful at all.
        const double a = 6, b = 2.5, h = 4;
        var prism = Shape.Extrude(Sketch.Ellipse(a, b), h);
        Assert.All(prism.Explain(TargetRep.Implicit).Entries,
            e => Assert.Equal(NodeSupport.Native, e.Support));

        var sdf = prism.ToImplicit();
        // Sample the exact lateral surface at MID-HEIGHT: z = 0 is the bottom cap, where
        // the rim is a corner and the field is zero for the other reason.
        for (int i = 0; i < 16; i++)
        {
            double t = i * 2 * Math.PI / 16;
            Assert.Equal(0, sdf.Evaluate((a * Math.Cos(t), b * Math.Sin(t), h / 2)), 9);
        }
        Assert.True(sdf.Evaluate((0, 0, h / 2)) < 0);
        Assert.True(sdf.Evaluate((a + 1, 0, h / 2)) > 0);
    }

    [Fact]
    public void EllipticalRevolve_SweepsThePappusVolume()
    {
        // An off-axis ellipse revolved a full turn is a torus of elliptical section:
        // V = (pi*a*b) * 2*pi*R by Pappus, exact whatever the section's shape.
        //
        // Note WHICH route this measures: a full turn of a SINGLE CLOSED curve is
        // B-Rep-Impossible (the documented gap), so the mesh comes from Surface Nets over
        // Sdf.RevolvedRegion — which is exactly why the ellipse's exact 2D field matters,
        // and why the band is the polygonizer's rather than a tessellation's. It
        // converges: 2.1% at resolution 64, 0.5% at 128.
        const double a = 1.2, b = 0.6, r = 5;
        double exact = Math.PI * a * b * 2 * Math.PI * r;
        var ring = Shape.Revolve(Sketch.Ellipse((r, 0), a, b));
        Assert.All(ring.Explain(TargetRep.Implicit).Entries,
            e => Assert.Equal(NodeSupport.Native, e.Support));

        var coarse = ring.ToMesh(new MeshQuality { SdfResolution = 64 });
        var fine = ring.ToMesh(new MeshQuality { SdfResolution = 128 });
        Assert.True(fine.IsClosed);
        Assert.True(Math.Abs(fine.Volume() - exact) / exact < 8e-3,
            $"elliptical ring volume {fine.Volume()} vs {exact}");
        Assert.True(Math.Abs(fine.Volume() - exact) < Math.Abs(coarse.Volume() - exact),
            $"refining must improve: {coarse.Volume()} then {fine.Volume()} against {exact}");
    }

    // ---- the builder's SVG-shaped arc command ----------------------------------

    [Fact]
    public void EllipticalArcTo_JoinsTwoLinesIntoAClosedProfile()
    {
        // A half-ellipse capping a rectangle: area = w*h + pi*a*b/2, with a = w/2.
        const double w = 8, h = 3, b = 2;
        double exact = w * h + Math.PI * (w / 2) * b / 2;
        var sketch = Sketch.Start(-w / 2, 0)
            .LineTo(w / 2, 0)
            .LineTo(w / 2, h)
            .EllipticalArcTo((-w / 2, h), w / 2, b, largeArc: false, clockwise: false)
            .Close();

        Assert.Equal(exact, sketch.Area(), 9);
        Assert.Equal(4, sketch.Segments.Count);   // two lines, the arc, and the closing line
    }

    [Fact]
    public void EllipticalArcTo_LargeArcAndSweepFlagsPickTheFourArcs()
    {
        // SVG's two flags select among the four arcs of a given ellipse through two
        // points, and the area each encloses against the chord separates them.
        //
        // The chord must be SHORTER than 2a, or the fixture is degenerate: at exactly 2a
        // the centre sits on the chord midpoint, every arc is a half-ellipse, and all four
        // areas coincide — so the test would report one distinct value however broken the
        // flags were. A 4-unit chord on a (3, 1.5) ellipse leaves real room.
        var results = new List<(bool Large, bool Cw, double Area, double MidY)>();
        foreach (bool large in new[] { false, true })
        {
            foreach (bool cw in new[] { false, true })
            {
                var sketch = Sketch.Start(-2, 0)
                    .EllipticalArcTo((2, 0), 3, 1.5, largeArc: large, clockwise: cw)
                    .Close();
                var arc = sketch.ToCurves().OfType<Ellipse2d>().Single();
                results.Add((large, cw, sketch.Area(), arc.PointAt(0.5).Y));
            }
        }

        // AREA separates large from small, and it is the ONLY thing area can separate:
        // the clockwise and counter-clockwise arcs are mirror images about the chord, so
        // they enclose equal areas by construction. Asserting four distinct areas would
        // therefore be asserting something false.
        Assert.Equal(2, results.Select(r => Math.Round(r.Area, 9)).Distinct().Count());
        double small = results.First(r => !r.Large).Area;
        double big = results.First(r => r.Large).Area;
        Assert.True(big > small, $"large arc {big} should enclose more than small {small}");
        Assert.All(results, r => Assert.True(r.Area > 0 && r.Area < Math.PI * 3 * 1.5));

        // The SWEEP flag is what puts the arc on one side of the chord or the other, so
        // that is what the midpoint's ordinate reads — and it is the half the area test
        // structurally cannot see. Counter-clockwise from left to right puts the CENTRE
        // above the chord, so the arc bulges DOWN; clockwise is the mirror.
        Assert.All(results, r => Assert.True(r.Cw ? r.MidY > 0 : r.MidY < 0,
            $"large={r.Large} cw={r.Cw} bulged to y={r.MidY}"));

        // And it is the SAME convention the circular builder uses — pinned here rather
        // than asserted twice, so the two cannot drift into meaning opposite things by
        // the same flag name.
        foreach (bool cw in new[] { false, true })
        {
            var circular = Sketch.Start(-2, 0).ArcTo((2, 0), 3, clockwise: cw).Close();
            var elliptical = Sketch.Start(-2, 0).EllipticalArcTo((2, 0), 3, 3, clockwise: cw).Close();
            // An ellipse with equal semi-axes IS that circle, so the two agree in area.
            Assert.Equal(circular.Area(), elliptical.Area(), 9);
        }
    }

    [Fact]
    public void EllipticalArcTo_ScalesTheAxesUpWhenTheyCannotReach()
    {
        // SVG F.6.6: semi-axes too small to span the chord are scaled by the common factor
        // that just reaches, so the ASPECT survives. A 10-unit chord with (1, 0.5) becomes
        // (5, 2.5) and the half-ellipse's area is pi*5*2.5/2.
        var sketch = Sketch.Start(-5, 0)
            .EllipticalArcTo((5, 0), 1, 0.5)
            .Close();
        Assert.Equal(Math.PI * 5 * 2.5 / 2, sketch.Area(), 6);
    }

    [Fact]
    public void EllipticalArcTo_RejectsCoincidentEndpoints()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            Sketch.Start(0, 0).EllipticalArcTo((0, 0), 2, 1));
        Assert.Contains("distinct endpoints", e.Message);
    }

    // ---- the round trips a sketch has to survive -------------------------------

    [Fact]
    public void EllipticalArc_RoundTripsThroughTheCurve2dVocabulary()
    {
        // ToCurves/FromCurves is the seam feature persistence uses, so an ellipse that
        // could not cross it would silently degrade a saved model.
        var original = Sketch.Start(-4, 0)
            .EllipticalArcTo((4, 0), 4, 2, rotationDegrees: 20)
            .Close();
        var curves = original.ToCurves();
        Assert.Contains(curves, c => c is Ellipse2d);

        var restored = Sketch.FromCurves(curves);
        Assert.Equal(original.Area(), restored.Area(), 12);
        Assert.Equal(original.Segments.Count, restored.Segments.Count);
    }

    [Fact]
    public void EllipticalArc_ExportsAsAnExactSvgArcCommand()
    {
        // SVG's A command carries rx, ry AND a rotation, so this is one of the few
        // exports where an ellipse loses nothing.
        var drawing = new SvgDrawing();
        drawing.Add(Sketch.Ellipse((0, 0), 6, 2.5, rotationDegrees: 30));
        string svg = drawing.ToSvg();
        Assert.Contains(" A6", svg);
        Assert.Contains("2.5", svg);
    }

    [Fact]
    public void ConstrainedSketch_MovesAnEllipticalArcWithItsChord()
    {
        // An elliptical arc carries no centre/radius variables, so it rides the chord
        // similarity exactly as a bézier does: pulling one joint must keep the arc's
        // shape relative to its endpoints AND leave the loop closed.
        var drawn = Sketch.Start(0, 0)
            .LineTo(8, 0)
            .EllipticalArcTo((8, 4), 3, 2)
            .LineTo(0, 4)
            .Close();

        var constrained = drawn.Constrain();
        constrained.Fix(constrained.Point(0));
        constrained.Horizontal(constrained.Line(0));
        constrained.Distance(constrained.Point(0), constrained.Point(1), 10);
        var result = constrained.TrySolve();

        Assert.True(result.Converged, result.ToString());
        var solved = result.Sketch!;
        Assert.Equal(10.0, solved.Segments[0].End.X - solved.Segments[0].Start.X, 6);
        // Still an elliptical arc, and still closed (the Sketch ctor validates closure).
        Assert.Contains(solved.ToCurves(), c => c is Ellipse2d);
    }

    [Fact]
    public void ConstrainedSketch_RefusesToTreatAnEllipticalArcAsALineOrArc()
    {
        // The refusal names what the segment IS rather than assuming bézier.
        var constrained = Sketch.Start(-3, 0).EllipticalArcTo((3, 0), 3, 1.5).Close().Constrain();
        Assert.Contains("elliptical arc", Assert.Throws<ArgumentException>(() => constrained.Line(0)).Message);
        Assert.Contains("elliptical arc", Assert.Throws<ArgumentException>(() => constrained.Arc(0)).Message);
    }
}
