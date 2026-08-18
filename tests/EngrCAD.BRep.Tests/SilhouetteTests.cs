using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="BrepSilhouette"/> against CLOSED FORMS — a sphere's silhouette radius, a
/// cylinder's silhouette width, a torus's latitude circles — never against a picture. The
/// instrument every one of them reads is <see cref="SilhouetteCurve.Deviation"/>, the sine
/// of the angle by which the reported curve misses being edge-on, and it is
/// mutation-checked: a curve at the MIRRORED azimuth (what a flipped rotation sense would
/// produce) reads a large value on the same measure.
/// </summary>
public class SilhouetteTests
{
    private static readonly Vector3d[] Directions =
    [
        Vector3d.UnitX,
        new Vector3d(0, 1, 0),
        new Vector3d(1, 1, 0).Normalized(),
        new Vector3d(0.3, -0.7, 0.5).Normalized(),
        new Vector3d(-0.2, 0.1, 0.97).Normalized(),
    ];

    private static IEnumerable<Vector3d> Samples(Curve3d curve, int count = 48)
    {
        var d = curve.Domain;
        for (int i = 0; i <= count; i++)
            yield return curve.PointAt(d.ParameterAt((double)i / count));
    }

    // ----------------------------------------------------------------- sphere

    [Fact]
    public void SphereSilhouetteIsACircleOfExactlyTheSphereRadius()
    {
        const double radius = 7.5;
        var centre = new Vector3d(3, -2, 1);
        var sphere = SolidFactory.MakeSphere(radius, centre);

        foreach (var d in Directions)
        {
            var result = BrepSilhouette.OfSolid(sphere, SilhouetteView.Along(d));
            Assert.NotEmpty(result.Curves);
            // The CLOSED FORM below carries the claim. `Deviation` is a uniform report
            // read through the surface's own inverse evaluation, so on a revolve-backed
            // sphere its floor is FaceGeometry.InverseEvaluationTolerance rather than the
            // answer's accuracy — measured 2.4e-16 where the projection lands exactly and
            // 1.3e-7 where it does not, on geometry the assertions below hold to 1e-12.
            Assert.True(result.MaxDeviation < 1e-6,
                $"direction {d}: deviation {result.MaxDeviation:E3}");

            // Every point is at exactly the sphere's radius from its centre AND in the
            // plane through the centre perpendicular to the view — the great circle.
            foreach (var curve in result.Curves)
            {
                foreach (var p in Samples(curve.Curve))
                {
                    Assert.Equal(radius, p.DistanceTo(centre), 12);
                    Assert.Equal(0.0, (p - centre).Dot(d), 12);
                }
            }
        }
    }

    [Fact]
    public void SphereSilhouetteIsExactWhereTheMeshOutlineIsAnInscribedPolygon()
    {
        // The headline comparison: the exact answer is a circle of radius r, while the
        // mesh route can only ever return the inscribed n-gon of whatever tessellation it
        // was handed. Quoted as the RADIUS at the worst point of each, so the two are
        // measured on one scale.
        const double radius = 10;
        var sphere = SolidFactory.MakeSphere(radius);
        var d = new Vector3d(0.3, -0.7, 0.5).Normalized();

        var exact = BrepSilhouette.OfSolid(sphere, SilhouetteView.Along(d));
        double worstExact = 0;
        foreach (var curve in exact.Curves)
        {
            foreach (var p in Samples(curve.Curve, 256))
                worstExact = Math.Max(worstExact, Math.Abs(p.Length - radius));
        }
        Assert.True(worstExact < 1e-9, $"exact worst radial error {worstExact:E3}");

        // The n-gon a mesh silhouette is bounded by: a chord of an n-segment circle sits
        // r(1 − cos(pi/n)) inside the true one, which is what "mesh fidelity" costs.
        double inscribed = radius * (1 - Math.Cos(Math.PI / 32));
        Assert.True(inscribed > 1e-2, $"the n-gon deficit at 32 segments is {inscribed:F4}");
    }

    [Fact]
    public void PerspectiveSphereSilhouetteIsThePolarCircle()
    {
        const double radius = 4;
        var centre = new Vector3d(1, 2, 3);
        var sphere = SolidFactory.MakeSphere(radius, centre);
        var eye = centre + new Vector3d(0, 0, 30);
        double distance = 30;

        var result = BrepSilhouette.OfSolid(sphere, SilhouetteView.From(eye));
        Assert.NotEmpty(result.Curves);
        Assert.True(result.MaxDeviation < 1e-6, $"deviation {result.MaxDeviation:E3}");

        double expectedRadius = radius * Math.Sqrt(distance * distance - radius * radius) / distance;
        double expectedHeight = radius * radius / distance;
        foreach (var curve in result.Curves)
        {
            foreach (var p in Samples(curve.Curve))
            {
                var offset = p - centre;
                Assert.Equal(expectedHeight, offset.Z, 10);
                Assert.Equal(expectedRadius, new Vector2d(offset.X, offset.Y).Length, 10);
            }
        }
        // And it is SMALLER than the great circle: a near eye sees less than half a sphere.
        Assert.True(expectedRadius < radius);
    }

    [Fact]
    public void EyeInsideTheSphereIsRefusedByName()
    {
        var sphere = SolidFactory.MakeSphere(5);
        var result = BrepSilhouette.OfSolid(sphere, SilhouetteView.From(new Vector3d(1, 0, 0)));
        Assert.Empty(result.Curves);
        Assert.All(result.Notes, n => Assert.Contains("inside the sphere", n));
    }

    // ----------------------------------------------------------------- cylinder

    [Fact]
    public void CylinderSilhouetteIsTwoRulingsExactlyOneDiameterApart()
    {
        const double radius = 3.25;
        var cylinder = SolidFactory.MakeCylinder(radius, 12);

        foreach (var d in Directions)
        {
            if (Math.Abs(d.Z) > 0.999)
                continue;   // the along-axis case has its own test
            var result = BrepSilhouette.OfSolid(cylinder, SilhouetteView.Along(d));
            var rulings = result.Curves.Where(c => c.Face.Surface is CylinderSurface).ToList();
            Assert.Equal(2, rulings.Count);
            Assert.All(rulings, r => Assert.Equal(SilhouetteFidelity.Exact, r.Fidelity));
            Assert.True(result.MaxDeviation < 1e-12, $"{d}: {result.MaxDeviation:E3}");

            // The projected distance between the two rulings is the DIAMETER, at any view
            // angle that is not along the axis — the closed form a silhouette must meet.
            var a = rulings[0].Curve.PointAt(0.5);
            var b = rulings[1].Curve.PointAt(0.5);
            var offset = b - a;
            double projected = (offset - d * offset.Dot(d)).Length;
            Assert.Equal(2 * radius, projected, 10);
        }
    }

    [Fact]
    public void CylinderViewedAlongItsAxisIsRefusedByName()
    {
        var cylinder = SolidFactory.MakeCylinder(3, 10);
        var result = BrepSilhouette.OfSolid(cylinder, SilhouetteView.Along(Vector3d.UnitZ));
        Assert.DoesNotContain(result.Curves, c => c.Face.Surface is CylinderSurface);
        Assert.Contains(result.Notes, n => n.Contains("viewed along its own axis"));
    }

    [Fact]
    public void ARulingIsBoundedByTheFaceRatherThanByTheInfiniteCarrier()
    {
        // A CylinderSurface's v domain is infinite; the emitted ruling must be the face's
        // own extent, or an unclipped silhouette draws a line off both ends of the solid.
        const double height = 12;
        var cylinder = SolidFactory.MakeCylinder(3, height);
        var result = BrepSilhouette.OfSolid(cylinder, SilhouetteView.Along(Vector3d.UnitX));
        foreach (var curve in result.Curves.Where(c => c.Face.Surface is CylinderSurface))
        {
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            foreach (var p in Samples(curve.Curve))
            {
                low = Math.Min(low, p.Z);
                high = Math.Max(high, p.Z);
            }
            Assert.Equal(0.0, low, 9);
            Assert.Equal(height, high, 9);
        }
    }

    // ----------------------------------------------------------------- cone

    [Fact]
    public void ConeSilhouetteIsTwoExactRulings()
    {
        var cone = SolidFactory.MakeCone(6, 2, 9);
        foreach (var d in Directions)
        {
            if (Math.Abs(d.Z) > 0.9)
                continue;
            var result = BrepSilhouette.OfSolid(cone, SilhouetteView.Along(d));
            var side = result.Curves.Where(c => c.Face.Surface is RevolvedSurface).ToList();
            Assert.Equal(2, side.Count);
            Assert.All(side, c => Assert.Equal(SilhouetteFidelity.Exact, c.Fidelity));
            Assert.True(result.MaxDeviation < 1e-11, $"{d}: {result.MaxDeviation:E3}");

            // A cone's silhouette rulings are STRAIGHT: the generator carried rigidly, so
            // the underlying curve is still the generator's own line.
            foreach (var c in side)
                Assert.IsType<Line3d>(c.Curve.Underlying);
        }
    }

    [Fact]
    public void TheDeviationInstrumentSeesAMirroredAzimuth()
    {
        // The one thing a wrong rotation SENSE would produce: the ruling at the mirrored
        // azimuth. Nothing about the curve's type, length or position on the surface would
        // show it — only the measure does, and here it reads six orders larger.
        var cone = SolidFactory.MakeCone(6, 2, 9);
        var d = new Vector3d(1, 0.4, 0).Normalized();
        var face = cone.Faces.Single(f => f.Surface is RevolvedSurface);
        var revolved = (RevolvedSurface)face.Surface;

        var truth = BrepSilhouette.OfFace(face, SilhouetteView.Along(d));
        Assert.True(truth.MaxDeviation < 1e-11);

        // Reflecting the answer in the axial half-plane the view lies in is exactly what a
        // flipped sense gives; measure it on the SAME instrument by rotating one ruling
        // through twice its own azimuth offset.
        double azimuth = Math.Atan2(d.Y, d.X);
        var mirrored = Matrix4d.CreateFromAxisAngle(revolved.AxisDirection, 2 * azimuth);
        double worst = 0;
        foreach (var curve in truth.Curves)
        {
            var moved = new TransformedCurve(curve.Curve, mirrored);
            foreach (var p in Samples(moved))
            {
                if (!revolved.TryProjectPoint(p, out var uv, FaceGeometry.InverseEvaluationTolerance))
                    continue;
                var n = revolved.NormalAt(uv.X, uv.Y);
                worst = Math.Max(worst, Math.Abs(n.Dot(d) / n.Length));
            }
        }
        Assert.True(worst > 1e-2, $"the mirrored azimuth reads {worst:E3}; the instrument cannot see it");
    }

    // ----------------------------------------------------------------- torus

    [Fact]
    public void TorusViewedAlongItsAxisSilhouettesToItsTwoEquators()
    {
        const double major = 12, minor = 4;
        var torus = SolidFactory.MakeTorus(major, minor);
        var result = BrepSilhouette.OfSolid(torus, SilhouetteView.Along(Vector3d.UnitZ));

        // Down the axis, the condition collapses onto the generator alone and its roots
        // are the extremal-radius latitude circles: EXACT circles at major +/- minor.
        var circles = result.Curves
            .Select(c => c.Curve.Underlying)
            .OfType<Circle3d>()
            .Select(c => c.Radius)
            .OrderBy(r => r)
            .ToList();
        Assert.Equal(2, circles.Count);
        Assert.Equal(major - minor, circles[0], 9);
        Assert.Equal(major + minor, circles[1], 9);
        Assert.All(result.Curves, c => Assert.Equal(SilhouetteFidelity.Exact, c.Fidelity));
        Assert.True(result.MaxDeviation < 1e-9, $"{result.MaxDeviation:E3}");
    }

    [Fact]
    public void TorusSilhouetteIsExactAtItsOwnVerticesAndConvergesInLength()
    {
        var torus = SolidFactory.MakeTorus(12, 4);
        var d = new Vector3d(0.6, 0, 0.8).Normalized();

        double Length(int samples)
        {
            var result = BrepSilhouette.OfSolid(
                torus, SilhouetteView.Along(d), new SilhouetteOptions { Samples = samples });
            Assert.NotEmpty(result.Curves);
            // The closed-form vertices are ON the silhouette; the chords between them are
            // not, which is exactly what the Sampled fidelity claims.
            Assert.True(result.MaxDeviation < 1e-7, $"{samples}: {result.MaxDeviation:E3}");
            double total = 0;
            foreach (var c in result.Curves)
            {
                var points = Samples(c.Curve, 4096).ToList();
                for (int i = 1; i < points.Count; i++)
                    total += points[i].DistanceTo(points[i - 1]);
            }
            return total;
        }

        double l0 = Length(48), l1 = Length(96), l2 = Length(192), l3 = Length(384);
        double d0 = Math.Abs(l1 - l0), d1 = Math.Abs(l2 - l1), d2 = Math.Abs(l3 - l2);
        // A chorded curve's length converges on the true one quadratically, so successive
        // differences fall by ~4 — the property that says the sampling refines the SAME
        // curve rather than wandering.
        Assert.True(d1 < d0 * 0.5, $"lengths {l0:F6} {l1:F6} {l2:F6} {l3:F6}; steps {d0:E3} {d1:E3} {d2:E3}");
        Assert.True(d2 < d1 * 0.5, $"lengths {l0:F6} {l1:F6} {l2:F6} {l3:F6}; steps {d0:E3} {d1:E3} {d2:E3}");
    }

    // ----------------------------------------------------------------- plane

    [Fact]
    public void AnEdgeOnPlaneIsNamedRatherThanEmittingACurve()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 8, 6)));
        var result = BrepSilhouette.OfSolid(box, SilhouetteView.Along(Vector3d.UnitZ));
        // Four of a box's six faces are edge-on to any axis view; none of them is a curve.
        Assert.Empty(result.Curves);
        Assert.Equal(4, result.Notes.Count(n => n.Contains("edge-on")));
    }

    [Fact]
    public void AnObliquelyViewedBoxHasNoSilhouetteCurvesAtAll()
    {
        // Every face is planar, so the outline is entirely modelled edges — the honest
        // answer, and the one that stops a drawing double-drawing its own box.
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 8, 6)));
        var result = BrepSilhouette.OfSolid(box, SilhouetteView.Along(new Vector3d(1, 2, 3).Normalized()));
        Assert.Empty(result.Curves);
        Assert.Empty(result.Notes);
    }

    // ----------------------------------------------------------------- extrusion

    [Fact]
    public void AnExtrudedCircleSilhouettesToTwoRulingsOneDiameterApart()
    {
        const double radius = 5;
        var circle = new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, radius);
        var solid = SolidFactory.Extrude(new Profile([circle]), (0, 0, 9));
        var d = new Vector3d(0.8, 0.6, 0).Normalized();

        var result = BrepSilhouette.OfSolid(solid, SilhouetteView.Along(d));
        var rulings = result.Curves.Where(c => c.Face.Surface is ExtrudedSurface).ToList();
        Assert.Equal(2, rulings.Count);
        Assert.All(rulings, r => Assert.Equal(SilhouetteFidelity.Exact, r.Fidelity));
        Assert.True(result.MaxDeviation < 1e-11, $"{result.MaxDeviation:E3}");

        var offset = rulings[1].Curve.PointAt(0.5) - rulings[0].Curve.PointAt(0.5);
        Assert.Equal(2 * radius, (offset - d * offset.Dot(d)).Length, 9);
    }

    [Fact]
    public void AnExtrusionViewedAlongItsOwnDirectionIsNamed()
    {
        var circle = new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var solid = SolidFactory.Extrude(new Profile([circle]), (0, 0, 9));
        var result = BrepSilhouette.OfSolid(solid, SilhouetteView.Along(Vector3d.UnitZ));
        Assert.DoesNotContain(result.Curves, c => c.Face.Surface is ExtrudedSurface);
        Assert.Contains(result.Notes, n => n.Contains("viewed along its own direction"));
    }

    // ----------------------------------------------------------------- clipping

    [Fact]
    public void ASilhouetteIsClippedToTheFaceItLiesOn()
    {
        // A quarter-turn revolve: the carrier reaches all the way round, so an unclipped
        // answer would draw rulings on surface the face does not carry.
        var generator = new Line3d((4, 0, 0), (4, 0, 8));
        var quarter = new RevolvedSurface(generator, Vector3d.Zero, Vector3d.UnitZ, Math.PI / 2);
        var face = new BrepFace(quarter, [Band(quarter)]);

        // A view whose rulings fall inside the quarter turn, and one whose do not.
        var inside = BrepSilhouette.OfFace(face, SilhouetteView.Along(new Vector3d(0, -1, 0)));
        Assert.Single(inside.Curves);
        var outside = BrepSilhouette.OfFace(face, SilhouetteView.Along(new Vector3d(0.7, 0.7, 0).Normalized()));
        Assert.Empty(outside.Curves);
    }

    [Fact]
    public void AnAzimuthOutsideAPartialRevolvesSweepIsDroppedAtTheSource()
    {
        // The domain filter is NOT the trim clip, and it cannot be: a partial revolve's
        // inverse evaluation folds an azimuth outside its own sweep back inside, so a
        // containment probe downstream reads a ruling from the missing three quarters as
        // being on the face. Dropping it where the azimuth is known in closed form is the
        // fix, and it holds with clipping switched off as well as on.
        var generator = new Line3d((4, 0, 0), (4, 0, 8));
        var quarter = new RevolvedSurface(generator, Vector3d.Zero, Vector3d.UnitZ, Math.PI / 2);
        var face = new BrepFace(quarter, [Band(quarter)]);
        var view = SilhouetteView.Along(new Vector3d(0, -1, 0));

        foreach (bool clip in new[] { true, false })
        {
            var result = BrepSilhouette.OfFace(
                face, view, new SilhouetteOptions { ClipToTrim = clip });
            // Only the u = 0 ruling is inside the quarter turn; its partner at u = pi is not.
            Assert.Single(result.Curves);
            foreach (var p in Samples(result.Curves[0].Curve))
                Assert.True(p.X > 0 && Math.Abs(p.Y) < 1e-9, $"clip={clip}: ruling at {p}");
        }
    }

    /// <summary>The four-sided loop of a partial revolve of a straight generator.</summary>
    private static BrepLoop Band(RevolvedSurface surface)
    {
        var du = surface.DomainU;
        var dv = surface.DomainV;
        Curve3d Edge(double u0, double v0, double u1, double v1) =>
            new PolylineCurve3d(
                Enumerable.Range(0, 33)
                    .Select(i => surface.PointAt(
                        u0 + (u1 - u0) * i / 32.0, v0 + (v1 - v0) * i / 32.0))
                    .ToList(),
                isClosed: false);

        var corners = new[]
        {
            new BrepVertex(surface.PointAt(du.Start, dv.Start)),
            new BrepVertex(surface.PointAt(du.End, dv.Start)),
            new BrepVertex(surface.PointAt(du.End, dv.End)),
            new BrepVertex(surface.PointAt(du.Start, dv.End)),
        };
        var edges = new[]
        {
            new BrepEdge(Edge(du.Start, dv.Start, du.End, dv.Start), new Interval(0, 1), corners[0], corners[1]),
            new BrepEdge(Edge(du.End, dv.Start, du.End, dv.End), new Interval(0, 1), corners[1], corners[2]),
            new BrepEdge(Edge(du.End, dv.End, du.Start, dv.End), new Interval(0, 1), corners[2], corners[3]),
            new BrepEdge(Edge(du.Start, dv.End, du.Start, dv.Start), new Interval(0, 1), corners[3], corners[0]),
        };
        return new BrepLoop([.. edges.Select(e => new BrepCoedge(e, sameSense: true))]);
    }

    // ----------------------------------------------------------------- tracer

    /// <summary>A real swept tube: a circular profile carried along a quarter-circle path.</summary>
    private static BrepSolid SweptTube()
    {
        var path = new CurveSegment(
            new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 15), 0, Math.PI / 2);
        var profile = new Profile([new Circle3d((15, 0, 0), Vector3d.UnitZ, Vector3d.UnitX, 3)]);
        return SolidFactory.Sweep(profile, path);
    }

    [Fact]
    public void ASweptSurfaceIsTracedAndItsVerticesLieOnTheSilhouette()
    {
        // A swept tube has no closed form, so the level-set tracer answers — and its
        // vertices are Newton-corrected onto N.d = 0, which the deviation measures.
        var result = BrepSilhouette.OfSolid(
            SweptTube(), SilhouetteView.Along(new Vector3d(0.4, 0.2, 0.9).Normalized()));
        var traced = result.Curves.Where(c => c.Face.Surface is SweptSurface).ToList();
        Assert.NotEmpty(traced);
        Assert.All(traced, c => Assert.Equal(SilhouetteFidelity.Traced, c.Fidelity));
        // The corrector drives g to 1e-13 at every vertex; the number reported here is the
        // INSTRUMENT s floor, since a swept surface has no closed-form inverse evaluation and
        // the deviation is read at whatever uv its bracketed 1-D solve returns.
        Assert.True(result.MaxDeviation < 1e-4, $"{result.MaxDeviation:E3}");
    }

    [Fact]
    public void TracingCanBeRefusedByName()
    {
        var result = BrepSilhouette.OfSolid(
            SweptTube(), SilhouetteView.Along(new Vector3d(0.4, 0.2, 0.9).Normalized()),
            new SilhouetteOptions { AllowTraced = false });
        Assert.DoesNotContain(result.Curves, c => c.Face.Surface is SweptSurface);
        Assert.Contains(result.Notes, n => n.Contains("no closed-form silhouette"));
    }

    // ----------------------------------------------------------------- determinism

    [Fact]
    public void TwoSolvesAgreeVertexForVertex()
    {
        var torus = SolidFactory.MakeTorus(12, 4);
        var view = SilhouetteView.Along(new Vector3d(0.6, 0.1, 0.8).Normalized());
        var a = BrepSilhouette.OfSolid(torus, view);
        var b = BrepSilhouette.OfSolid(torus, view);
        Assert.Equal(a.Curves.Count, b.Curves.Count);
        for (int i = 0; i < a.Curves.Count; i++)
        {
            var pa = Samples(a.Curves[i].Curve, 64).ToList();
            var pb = Samples(b.Curves[i].Curve, 64).ToList();
            for (int j = 0; j < pa.Count; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(pa[j].X), BitConverter.DoubleToInt64Bits(pb[j].X));
                Assert.Equal(BitConverter.DoubleToInt64Bits(pa[j].Y), BitConverter.DoubleToInt64Bits(pb[j].Y));
                Assert.Equal(BitConverter.DoubleToInt64Bits(pa[j].Z), BitConverter.DoubleToInt64Bits(pb[j].Z));
            }
        }
    }
}
