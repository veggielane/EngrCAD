using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Variable-law fillets and chamfers on PARTIAL rim runs: the law anchors at the run's
/// corners — including its END vertices, so a setback termination is the planar quarter
/// cross-section of whatever radius the law gives there, exact at any value. Volumes are
/// asserted through tessellation in EngrCAD.Interop.Tests (<c>VariableRunVolumeTests</c>).
/// </summary>
public class VariablePartialRunTests
{
    private static BrepSolid Box(double w = 30, double d = 20, double h = 6) =>
        SolidFactory.MakeBox(new Aabb((0, 0, 0), (w, d, h)));

    private static BrepFace Top(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    private static BrepEdge TopEdgeAlongX(BrepSolid solid, double y) =>
        Top(solid).OuterLoop.Coedges
            .Select(c => c.Edge)
            .Single(e => e.Curve.Underlying is Line3d
                && Math.Abs(e.Curve.PointAt(e.Domain.Start).Y - y) < 1e-9
                && Math.Abs(e.Curve.PointAt(e.Domain.End).Y - y) < 1e-9);

    /// <summary>1 at x = 0 rising linearly to 2 at x = 30.</summary>
    private static double LinearLaw(Vector3d p) => 1 + p.X / 30;

    [Fact]
    public void SingleEdgeRunWithALinearLaw_BuildsARuledSkinBandWithLawValuedEnds()
    {
        var box = Box();
        var solid = Filleting.FilletEdges(box, [TopEdgeAlongX(box, 0)], LinearLaw);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        // 6 box faces (top and front rebuilt) + 1 band + 2 terminations, as the constant
        // run — only the band's surface family changes.
        Assert.Equal(9, solid.Faces.Count());
        Assert.Contains(solid.Faces, f => f.Surface is LoftedSurface);

        // The law anchors at the run's END vertices: the top boundary is inset by the
        // law's own value at each end (1 at x = 0, 2 at x = 30), and the side drop
        // matches it.
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((0, 1, 6)) < 1e-9);
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((30, 2, 6)) < 1e-9);
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((0, 0, 5)) < 1e-9);
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((30, 0, 4)) < 1e-9);
        // The run vertices themselves survive: the rim beyond the run is untouched.
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((0, 0, 6)) < 1e-9);
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((30, 0, 6)) < 1e-9);
    }

    [Fact]
    public void TheTerminationCrossSections_AreQuarterArcsOfTheLawsEndRadii()
    {
        // The exactness claim for terminations: the band's end cross-section is a planar
        // quarter arc of the law's value AT the stop vertex — centred one radius below
        // the inset top point, every sample one radius from the centre.
        var box = Box();
        var solid = Filleting.FilletEdges(box, [TopEdgeAlongX(box, 0)], LinearLaw);

        foreach (var (top, radius) in ((Vector3d Top, double Radius)[])
                 [(new(0, 1, 6), 1.0), (new(30, 2, 6), 2.0)])
        {
            var centre = top - Vector3d.UnitZ * radius;
            var arc = solid.Edges.Single(e =>
                e.StartVertex.Position.DistanceTo(top) < 1e-9
                && Math.Abs(e.StartVertex.Position.Z - e.EndVertex.Position.Z - radius) < 1e-9);
            for (int i = 0; i <= 8; i++)
            {
                var point = arc.Curve.PointAt(arc.Domain.ParameterAt(i / 8.0));
                Assert.Equal(radius, point.DistanceTo(centre), 9);
            }
        }
    }

    [Fact]
    public void EveryCrossSectionOfARunBand_IsATrueCircleOfTheLerpedRadius()
    {
        var box = Box();
        var solid = Filleting.FilletEdges(box, [TopEdgeAlongX(box, 0)], LinearLaw);
        var skin = (LoftedSurface)solid.Faces.Single(f => f.Surface is LoftedSurface).Surface;

        for (int i = 0; i <= 8; i++)
        {
            double v = i / 8.0;
            var a = skin.PointAt(skin.DomainU.ParameterAt(0), v);
            var b = skin.PointAt(skin.DomainU.ParameterAt(0.5), v);
            var c = skin.PointAt(skin.DomainU.ParameterAt(1), v);
            var u = b - a;
            var w = c - a;
            var plane = u.Cross(w);
            var centre = a + (plane.Cross(u) * w.LengthSquared + w.Cross(plane) * u.LengthSquared)
                / (2 * plane.LengthSquared);
            double radius = centre.DistanceTo(a);
            for (int k = 1; k < 8; k++)
            {
                var point = skin.PointAt(skin.DomainU.ParameterAt(k / 8.0), v);
                Assert.Equal(radius, centre.DistanceTo(point), 9);
            }
        }
    }

    [Fact]
    public void AConstantLawRun_MatchesThePlainOverloadExactly()
    {
        // The law path must not perturb the case it generalizes: same faces, and every
        // vertex at exactly the same position (the full-rim precedent's assertion).
        var uniformBox = Box();
        var uniform = Filleting.FilletEdges(uniformBox, [TopEdgeAlongX(uniformBox, 0)], 1.5);
        var lawBox = Box();
        var law = Filleting.FilletEdges(lawBox, [TopEdgeAlongX(lawBox, 0)], _ => 1.5);

        Assert.Equal(uniform.Faces.Count(), law.Faces.Count());
        var uniformPoints = uniform.Vertices.Select(v => v.Position).OrderBy(p => p.X)
            .ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        var lawPoints = law.Vertices.Select(v => v.Position).OrderBy(p => p.X)
            .ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        Assert.Equal(uniformPoints.Count, lawPoints.Count);
        for (int i = 0; i < uniformPoints.Count; i++)
            Assert.Equal(uniformPoints[i], lawPoints[i]); // exact equality, not a tolerance
    }

    [Fact]
    public void ChamferRunWithALaw_BuildsPlanarStripsAndTerminations()
    {
        var box = Box();
        var solid = Filleting.ChamferEdges(box, [TopEdgeAlongX(box, 0)], LinearLaw);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(9, solid.Faces.Count());
        // A linearly varying setback keeps the strip an exact PLANE (constant top:side
        // ratio keeps the four corners coplanar) — no lofted skin anywhere.
        Assert.DoesNotContain(solid.Faces, f => f.Surface is LoftedSurface);
    }

    // ---- refusals, by name ----

    [Fact]
    public void AVaryingLawAcrossARunsSharpInteriorCorner_IsRefusedByName()
    {
        var box = Box();
        var top = Top(box);
        var alongX = TopEdgeAlongX(box, 0);
        var alongY = top.OuterLoop.Coedges.Select(c => c.Edge)
            .Single(e => e.Curve.Underlying is Line3d
                && Math.Abs(e.Curve.PointAt(e.Domain.Start).X - 30) < 1e-9
                && Math.Abs(e.Curve.PointAt(e.Domain.End).X - 30) < 1e-9);

        // LinearLaw reads 1 at (0,0,6), 2 at (30,0,6) and 2 at (30,20,6): it varies
        // along the first edge, so the sharp corner at (30,0,6) joins a cone to a
        // cylinder and there is no conic miter.
        var exception = Assert.Throws<NotSupportedException>(
            () => Filleting.FilletEdges(box, [alongX, alongY], LinearLaw));
        Assert.Contains("circumscribe a common sphere", exception.Message);
        Assert.Contains("stop the run before", exception.Message);
    }

    [Fact]
    public void AConstantLawAcrossARunsSharpCorner_StillMitersExactly()
    {
        // The refusal is about the LAW, not about sharp corners: a law that happens to be
        // constant keeps both bands equal-radius cylinders and the bicylinder ellipse is
        // back — the law CODE PATH must not lose the miter the plain overload has. (A law
        // genuinely varying along either edge of a sharp corner is refused, because that
        // band is a cone over its whole edge; on a box rim, where every interior corner
        // is sharp, variation is therefore only legal on runs whose interior corners are
        // tangent-continuous — see the rounded-plate case below.)
        var box = Box();
        var top = Top(box);
        var alongX = TopEdgeAlongX(box, 0);
        var alongY = top.OuterLoop.Coedges.Select(c => c.Edge)
            .Single(e => e.Curve.Underlying is Line3d
                && Math.Abs(e.Curve.PointAt(e.Domain.Start).X - 30) < 1e-9
                && Math.Abs(e.Curve.PointAt(e.Domain.End).X - 30) < 1e-9);

        var solid = Filleting.FilletEdges(box, [alongX, alongY], _ => 1.5);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Single(solid.Edges, e => e.Curve is Ellipse3d); // the exact miter
        Assert.DoesNotContain(solid.Faces, f => f.Surface is LoftedSurface);
    }

    [Fact]
    public void AVaryingLawAlongARunsArcEdge_IsRefusedByName()
    {
        // A rounded-rectangle plate rim (the full-rim fixture): a run of
        // straight + corner arc + straight, with a law that varies along the ARC.
        var plate = RoundedPlate(60, 40, 8, 10);
        var (bottom, arc, right) = BottomCornerRun(plate);
        var exception = Assert.Throws<NotSupportedException>(
            () => Filleting.FilletEdges(plate, [bottom, arc, right], p => 2 + 0.05 * p.Y));
        Assert.Contains("spiral", exception.Message);
    }

    [Fact]
    public void ARunOverATangentArcCorner_TakesAVaryingLawOnItsStraights()
    {
        // The straights interpolate, the arc keeps the saturated constant value, and the
        // tangent corners blend — no miter is asked for anywhere.
        var plate = RoundedPlate(60, 40, 8, 10);
        var (bottom, arc, right) = BottomCornerRun(plate);
        var solid = Filleting.FilletEdges(
            plate, [bottom, arc, right], p => 2 + 1.5 * Math.Clamp((p.X + 22) / 44, 0, 1));
        solid.Validate();
        Assert.Contains(solid.Faces, f => f.Surface is LoftedSurface);   // varying straight
        Assert.Contains(solid.Faces, f => f.Surface is RevolvedSurface); // constant arc torus
    }

    // ---- fixtures ----

    private static BrepSolid RoundedPlate(double width, double depth, double corner, double height)
    {
        double halfWidth = width / 2, halfDepth = depth / 2;
        double x = halfWidth - corner, y = halfDepth - corner;
        Vector3d P(double a, double b) => new(a, b, 0);
        Curve3d Arc(double cx, double cy, double from) =>
            new CurveSegment(
                new Circle3d(P(cx, cy), Vector3d.UnitX, Vector3d.UnitY, corner), from, from + Math.PI / 2);

        var curves = new List<Curve3d>
        {
            new Line3d(P(-x, -halfDepth), P(x, -halfDepth)),
            Arc(x, -y, -Math.PI / 2),
            new Line3d(P(halfWidth, -y), P(halfWidth, y)),
            Arc(x, y, 0),
            new Line3d(P(x, halfDepth), P(-x, halfDepth)),
            Arc(-x, y, Math.PI / 2),
            new Line3d(P(-halfWidth, y), P(-halfWidth, -y)),
            Arc(-x, -y, Math.PI),
        };
        return SolidFactory.Extrude(new Profile(curves), Vector3d.UnitZ * height);
    }

    /// <summary>The plate's bottom straight edge, its (x, −y) corner arc, and the right
    /// straight edge — a contiguous run whose interior corners are tangent-continuous.</summary>
    private static (BrepEdge Bottom, BrepEdge Arc, BrepEdge Right) BottomCornerRun(BrepSolid plate)
    {
        var rim = plate.PlanarFacesWithNormal(Vector3d.UnitZ).Single().OuterLoop.Coedges
            .Select(c => c.Edge).ToList();
        var bottom = rim.Single(e => e.Curve.Underlying is Line3d
            && Math.Abs(e.Curve.PointAt(e.Domain.Mid).Y + 20) < 1e-9);
        var right = rim.Single(e => e.Curve.Underlying is Line3d
            && Math.Abs(e.Curve.PointAt(e.Domain.Mid).X - 30) < 1e-9);
        var arc = rim.Single(e => e.Curve.Underlying is Circle3d
            && e.Curve.PointAt(e.Domain.Mid).X > 0 && e.Curve.PointAt(e.Domain.Mid).Y < 0);
        return (bottom, arc, right);
    }
}
