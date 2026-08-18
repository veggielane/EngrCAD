using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="TwistedSurface"/> — the exact side surface of a twisted extrusion.
///
/// <para>The claims under test are the ones that separate an EXACT surface from a swept
/// mesh: every derivative is closed form (checked against central differences, which is
/// the only independent oracle for a derivative), inverse evaluation is the two-decoupled-
/// 1D-solves reduction the whole swept family uses, an untwisted surface reproduces
/// <see cref="ExtrudedSurface"/> BIT for bit, and a re-placement carries the twist while a
/// MIRROR flips its sign — the handedness identity, derived rather than asserted.</para>
/// </summary>
public class TwistedSurfaceTests
{
    private const double Twist = Math.PI / 2;

    private static Profile Square(double side)
    {
        double h = side / 2;
        return Profile.FromPoints([
            new Vector3d(-h, -h, 0), new Vector3d(h, -h, 0),
            new Vector3d(h, h, 0), new Vector3d(-h, h, 0)]);
    }

    private static TwistedSurface Side(double twist = Twist, double sx = 1, double sy = 1) =>
        new(new Line3d((10, -10, 0), (10, 10, 0)), Frame3d.WorldXY, 40, twist, new Vector2d(sx, sy));

    // ---- the surface itself ----

    [Fact]
    public void PointAtIsTheStatedSectionTransform()
    {
        // P(u, v) = R(twist·v)·S(v)·C(u) + h·v·z, scale FIRST then rotate (the OpenSCAD
        // composition the mesh route already used) — asserted against the formula written
        // out longhand, so the two cannot drift.
        var surface = Side(Twist, 0.5, 2.0);
        for (int i = 0; i <= 4; i++)
        {
            for (int j = 0; j <= 4; j++)
            {
                double u = i / 4.0, v = j / 4.0;
                var c = surface.Generator.PointAt(surface.DomainU.ParameterAt(u));
                double sx = 1 + (0.5 - 1) * v, sy = 1 + (2.0 - 1) * v;
                double x = c.X * sx, y = c.Y * sy;
                double cos = Math.Cos(Twist * v), sin = Math.Sin(Twist * v);
                var expected = new Vector3d(x * cos - y * sin, x * sin + y * cos, 40 * v);
                Assert.True((surface.PointAt(surface.DomainU.ParameterAt(u), v) - expected).Length < 1e-12);
            }
        }
    }

    [Fact]
    public void BothPartialsAreClosedFormAndMatchCentralDifferences()
    {
        // The point of the type: no finite differences anywhere in the surface. Central
        // differences are the independent oracle — they share no line with the analytic
        // expressions, and their own O(h²) error sets the bar.
        var surface = Side(1.3, 0.7, 1.4);
        const double h = 1e-6;
        double worstU = 0, worstV = 0;
        for (int i = 1; i < 6; i++)
        {
            for (int j = 1; j < 6; j++)
            {
                double u = surface.DomainU.ParameterAt(i / 6.0), v = j / 6.0;
                var du = (surface.PointAt(u + h, v) - surface.PointAt(u - h, v)) / (2 * h);
                var dv = (surface.PointAt(u, v + h) - surface.PointAt(u, v - h)) / (2 * h);
                worstU = Math.Max(worstU, (surface.DerivativeU(u, v) - du).Length);
                worstV = Math.Max(worstV, (surface.DerivativeV(u, v) - dv).Length);
            }
        }
        // 1e-6 step on a ~40-unit surface: the difference's own truncation error is the
        // floor here, not the analytic expression's.
        Assert.True(worstU < 1e-6, $"worst dP/du disagreement {worstU:E3}");
        Assert.True(worstV < 1e-6, $"worst dP/dv disagreement {worstV:E3}");
    }

    [Fact]
    public void TheNormalPointsOutwardForACounterClockwiseGenerator()
    {
        // A rotation times a POSITIVE diagonal scale is orientation-preserving at every v,
        // so the extrusion's outward convention survives the twist: the same statement
        // ExtrudedSurface makes, and the reason the grid tessellation needs no flip.
        var surface = Side(Twist, 0.6, 0.6);
        for (int j = 0; j <= 4; j++)
        {
            double v = j / 4.0;
            var point = surface.PointAt(surface.DomainU.Mid, v);
            var normal = surface.NormalAt(surface.DomainU.Mid, v);
            var axial = new Vector3d(point.X, point.Y, 0);
            Assert.True(normal.Dot(axial) > 0, $"normal turned inward at v = {v}");
        }
    }

    [Fact]
    public void InverseEvaluationRecoversEveryParameter()
    {
        var surface = Side(Twist, 0.5, 1.5);
        double worst = 0;
        for (int i = 0; i <= 8; i++)
        {
            for (int j = 0; j <= 8; j++)
            {
                double u = surface.DomainU.ParameterAt(i / 8.0), v = j / 8.0;
                Assert.True(surface.TryProjectPoint(surface.PointAt(u, v), out var uv, 1e-9));
                worst = Math.Max(worst, Math.Abs(uv.X - u) + Math.Abs(uv.Y - v));
            }
        }
        Assert.True(worst < 1e-9, $"worst parameter round-trip error {worst:E3}");
    }

    [Fact]
    public void InverseEvaluationWorksOnACurvedGeneratorThatDoublesBack()
    {
        // The SeedSelection lesson: a generator whose planar projection folds hides two
        // branches inside one seed interval, and a single-seed solve returns the MIRRORED
        // parameter — a point tens of millimetres away that still passes a structural
        // check. Refining from every local minimum and its neighbours is what covers it.
        var hairpin = new NurbsCurve(3,
            [new Vector3d(12, -8, 0), new Vector3d(2, -3, 0), new Vector3d(2, 3, 0), new Vector3d(12, 8, 0)],
            [1, 1, 1, 1], [0, 0, 0, 0, 1, 1, 1, 1]);
        var surface = new TwistedSurface(hairpin, Frame3d.WorldXY, 30, 0.9, new Vector2d(1, 1));
        int failures = 0;
        for (int i = 0; i <= 20; i++)
        {
            for (int j = 0; j <= 4; j++)
            {
                double u = surface.DomainU.ParameterAt(i / 20.0), v = j / 4.0;
                var point = surface.PointAt(u, v);
                if (!surface.TryProjectPoint(point, out var uv, 1e-7) ||
                    (surface.PointAt(uv.X, uv.Y) - point).Length > 1e-7)
                {
                    failures++;
                }
            }
        }
        Assert.Equal(0, failures);
    }

    // ---- the untwisted degeneration ----

    [Fact]
    public void ZeroTwistAndUnitScaleIsTheExtrudedSurfaceBitForBit()
    {
        // An exact-zero semantic test, the repo's rule: a literally untwisted, unscaled
        // section sweep IS a plain extrusion, and must not differ in the last bits either
        // — a tessellation that welds is a tessellation whose two sides agree exactly.
        var generator = new Line3d((10, -10, 0), (10, 10, 0));
        var twisted = new TwistedSurface(generator, Frame3d.WorldXY, 40, 0, new Vector2d(1, 1));
        var extruded = new ExtrudedSurface(generator, (0, 0, 40));
        for (int i = 0; i <= 8; i++)
        {
            for (int j = 0; j <= 8; j++)
            {
                double u = i / 8.0, v = j / 8.0;
                var a = twisted.PointAt(u, v);
                var b = extruded.PointAt(u, v);
                Assert.Equal(BitConverter.DoubleToInt64Bits(b.X), BitConverter.DoubleToInt64Bits(a.X));
                Assert.Equal(BitConverter.DoubleToInt64Bits(b.Y), BitConverter.DoubleToInt64Bits(a.Y));
                Assert.Equal(BitConverter.DoubleToInt64Bits(b.Z), BitConverter.DoubleToInt64Bits(a.Z));
            }
        }
        Assert.False(twisted.IsTwisted);
        Assert.Equal(1, twisted.NaturalVSegments(32));
        Assert.Equal(1, twisted.PanelSegments(32));
    }

    // ---- density rules ----

    [Fact]
    public void TheRowCountFollowsTheTwistRateAndNotAConstant()
    {
        // One v row per circular facet angle of twist: a quarter turn at 32 segments per
        // circle is 8 rows, a full turn 32, four turns 128 — so refining the density
        // refines the twist, which is what makes the tessellation converge.
        Assert.Equal(8, Side(Math.PI / 2).NaturalVSegments(32));
        Assert.Equal(32, Side(2 * Math.PI).NaturalVSegments(32));
        Assert.Equal(128, Side(8 * Math.PI).NaturalVSegments(32));
        Assert.Equal(64, Side(Math.PI / 2).NaturalVSegments(256));
        // A twist smaller than one facet angle still gets a mid row (the mesh route's own
        // floor), so a rail is never described by a single chord.
        Assert.Equal(2, Side(0.01).NaturalVSegments(32));
    }

    [Fact]
    public void APanelIsSubdividedToTheArcItsOwnRadiusWouldGet()
    {
        // The twist-matched profile subdivision. A side of a 20-square runs from radius
        // 14.142 (a corner) inward, so the cell length is capped at 14.142·2π/n and a
        // 20-long side takes ceil(20 / that) cells: 8 at n = 32, and it must SCALE with
        // the density or the panel error stays first order.
        var surface = Side();
        Assert.Equal(8, surface.PanelSegments(32));
        Assert.Equal(15, surface.PanelSegments(64));
        Assert.Equal(29, surface.PanelSegments(128));

        // An ANISOTROPIC top section is scanned too, and the finer of the two wins: the
        // count has to be one number for the whole face, or the grid and the top edge
        // polyline round apart and the face falls off its own natural grid.
        var stretched = Side(Twist, 0.5, 1.5);
        Assert.Equal(
            Math.Max(stretched.PanelSegments(64), 15),
            stretched.PanelSegments(64));
        Assert.True(stretched.PanelSegments(64) >= 15);
    }

    // ---- placement ----

    [Fact]
    public void ARigidPlacementCarriesTheTwistAndTheDomainVerbatim()
    {
        var surface = Side(Twist, 0.5, 1.5);
        var m = Matrix4d.CreateTranslation((5, -2, 7)) * Matrix4d.CreateRotationX(0.6) * Matrix4d.CreateRotationZ(1.1);
        var moved = (TwistedSurface)GeometryTransform.Apply(surface, m);

        Assert.Equal(surface.Twist, moved.Twist);              // an angle: an isometry preserves it
        Assert.Equal(surface.Height, moved.Height);            // a length: likewise
        Assert.Equal(surface.ScaleTop, moved.ScaleTop);        // ratios: dimensionless
        Assert.Equal(surface.DomainU.Start, moved.DomainU.Start);
        Assert.Equal(surface.DomainU.End, moved.DomainU.End);

        double worst = 0;
        for (int i = 0; i <= 5; i++)
        {
            for (int j = 0; j <= 5; j++)
            {
                double u = surface.DomainU.ParameterAt(i / 5.0), v = j / 5.0;
                worst = Math.Max(worst, (moved.PointAt(u, v) - m.TransformPoint(surface.PointAt(u, v))).Length);
            }
        }
        Assert.True(worst < 1e-12, $"moved surface differs from the moved points by {worst:E3}");
    }

    [Fact]
    public void ReflectingTheAxisNegatesTheTwistTheHandednessIdentity()
    {
        // F·Rot(Z, θ)·F⁻¹ = Rot(F·Z, −θ) for a reflection F. So the mirror image of a
        // twisted extrusion is the OPPOSITE twist about the mapped axis — the same
        // identity a left-hand thread rides — which is what makes Shape.Mirror Native by
        // re-DECLARING the twist rather than re-placing it. Asserted here as geometry: the
        // reflected points of the +θ surface ARE the points of the −θ surface.
        var surface = Side(Twist);
        var mirror = Matrix4d.CreateScale((1, -1, 1));     // reflect in the frame's XZ plane
        var reflectedGenerator = GeometryTransform.Apply(surface.Generator, mirror);
        var opposite = new TwistedSurface(
            reflectedGenerator, Frame3d.WorldXY, surface.Height, -surface.Twist, surface.ScaleTop);

        double worst = 0;
        for (int i = 0; i <= 6; i++)
        {
            for (int j = 0; j <= 6; j++)
            {
                double u = surface.DomainU.ParameterAt(i / 6.0), v = j / 6.0;
                worst = Math.Max(worst,
                    (opposite.PointAt(u, v) - mirror.TransformPoint(surface.PointAt(u, v))).Length);
            }
        }
        Assert.True(worst < 1e-12, $"the mirrored surface is not the negated twist ({worst:E3})");

        // The mutation that proves it: keeping the SAME sign misses by the whole twist.
        var kept = new TwistedSurface(
            reflectedGenerator, Frame3d.WorldXY, surface.Height, surface.Twist, surface.ScaleTop);
        double miss = 0;
        for (int j = 1; j <= 6; j++)
        {
            double v = j / 6.0;
            miss = Math.Max(miss,
                (kept.PointAt(surface.DomainU.Mid, v)
                 - mirror.TransformPoint(surface.PointAt(surface.DomainU.Mid, v))).Length);
        }
        Assert.True(miss > 1, $"the wrong-sign construction should be metres out, measured {miss:E3}");
    }

    // ---- the solid factory ----

    [Fact]
    public void TheFactoryBuildsOneSideFacePerProfileSegmentPlusTwoCaps()
    {
        var solid = SolidFactory.TwistExtrude(Square(20), Frame3d.WorldXY, 40, Twist, new Vector2d(1, 1));
        solid.Validate();
        Assert.Equal(6, solid.Faces.Count());
        Assert.Equal(4, solid.Faces.Count(f => f.Surface is TwistedSurface));
        Assert.Equal(2, solid.Faces.Count(f => f.Surface is PlaneSurface));
        Assert.Equal(12, solid.Edges.Count());
        // Every rail rides the SAME master surface object: the rail edge and the two grid
        // columns it separates then come out of one evaluation rather than three.
        var masters = solid.Edges
            .Select(e => e.Curve)
            .OfType<TwistedRailCurve>()
            .Select(r => r.Surface)
            .Distinct()
            .ToList();
        Assert.Single(masters);
    }

    [Fact]
    public void AHoleTwistsAboutTheSameAxisAndStaysAHole()
    {
        var solid = SolidFactory.TwistExtrude(
            Square(20), Frame3d.WorldXY, 40, Twist, new Vector2d(1, 1),
            [Profile.Circle((0, 0, 0), (1, 0, 0), (0, 1, 0), 5)]);
        solid.Validate();
        // 4 outer sides + 1 bore skin + 2 caps.
        Assert.Equal(7, solid.Faces.Count());
        // The bore's own surface twists about the SAME axis, so its section at every
        // height is still the circle it started as — measured on the surface, not assumed.
        var bore = solid.Faces.Select(f => f.Surface).OfType<TwistedSurface>()
            .Single(s => s.Generator.IsClosed);
        for (int j = 0; j <= 4; j++)
        {
            for (int i = 0; i < 8; i++)
            {
                var p = bore.PointAt(bore.DomainU.ParameterAt(i / 8.0), j / 4.0);
                Assert.Equal(5.0, Math.Sqrt(p.X * p.X + p.Y * p.Y), 9);
            }
        }
    }

    [Fact]
    public void TheFactoryRefusesWhatHasNoTwistedExtrusionToBuild()
    {
        var square = Square(20);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SolidFactory.TwistExtrude(square, Frame3d.WorldXY, 0, Twist, new Vector2d(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SolidFactory.TwistExtrude(square, Frame3d.WorldXY, -5, Twist, new Vector2d(1, 1)));
        // A degenerate top section is a loft to a point or a line, not a twisted extrusion.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TwistedSurface(new Line3d((1, 0, 0), (1, 1, 0)), Frame3d.WorldXY, 10, 1, new Vector2d(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TwistedSurface(new Line3d((1, 0, 0), (1, 1, 0)), Frame3d.WorldXY, 10, double.NaN, new Vector2d(1, 1)));
        // An axis lying in the profile plane sweeps nothing.
        var edgeOn = Frame3d.FromZX(Vector3d.Zero, (1, 0, 0), (0, 0, 1));
        Assert.Throws<ArgumentException>(() =>
            SolidFactory.TwistExtrude(square, edgeOn, 40, Twist, new Vector2d(1, 1)));
    }

    [Fact]
    public void STEPRefusesATwistedFaceByName()
    {
        // A twisted surface has no AP214 entity — it joins the swept/helical bucket. The
        // refusal names the type, which is the difference between an export that cannot be
        // done and one that silently drops a face.
        var solid = SolidFactory.TwistExtrude(Square(20), Frame3d.WorldXY, 40, Twist, new Vector2d(1, 1));
        var error = Assert.Throws<NotSupportedException>(() => StepWriter.Write(solid));
        Assert.Contains(nameof(TwistedSurface), error.Message);
    }

    [Fact]
    public void TheArchiveCarriesATwistedSolidLosslessly()
    {
        // BrepArchive is the format that exists for exactly the types STEP cannot carry.
        var solid = SolidFactory.TwistExtrude(
            Square(20), Frame3d.WorldXY, 40, Twist, new Vector2d(0.6, 1.4),
            [Profile.Circle((0, 0, 0), (1, 0, 0), (0, 1, 0), 5)]);
        string archive = BrepArchive.Write(solid);
        var restored = BrepArchive.Read(archive).Single();
        restored.Validate();

        // save -> load -> save is a BYTE fixed point (the archive's own bar).
        Assert.Equal(archive, BrepArchive.Write(restored));
        Assert.Equal(solid.Faces.Count(), restored.Faces.Count());

        // ... and the surfaces came back as THEMSELVES with the same numbers, sampled.
        var before = solid.Faces.Select(f => f.Surface).OfType<TwistedSurface>().First();
        var after = restored.Faces.Select(f => f.Surface).OfType<TwistedSurface>().First();
        Assert.Equal(before.Twist, after.Twist);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.ScaleTop, after.ScaleTop);
        for (int i = 0; i <= 4; i++)
        {
            for (int j = 0; j <= 4; j++)
            {
                double u = before.DomainU.ParameterAt(i / 4.0), v = j / 4.0;
                Assert.True((before.PointAt(u, v) - after.PointAt(u, v)).Length < 1e-12);
            }
        }

        // The rails' shared master survives interning: one surface object, not four.
        Assert.Single(restored.Edges.Select(e => e.Curve).OfType<TwistedRailCurve>()
            .Select(r => r.Surface).Distinct());
    }

    [Fact]
    public void ATracedBranchOnATwistedBandTerminatesOnTheBandsOwnRails()
    {
        // A twisted band has no closed-form plane intersection, so the pair goes to the
        // marching tracer — and the tracer must LAND on the domain, because a twisted
        // face's u boundaries are the extrusion's own rails and a branch that stops one
        // march step short leaves a gap no consumer can close by tolerance.
        var solid = SolidFactory.TwistExtrude(Square(20), Frame3d.WorldXY, 40, Twist, new Vector2d(1, 1));
        var side = solid.Faces.Select(f => f.Surface).OfType<TwistedSurface>().First();
        var curves = SurfaceIntersection.Intersect(
            side, new PlaneSurface((0, 0, 20), (1, 0, 0), (0, 1, 0)),
            new Aabb((-40, -40, -20), (40, 40, 60)));

        var branch = Assert.Single(curves);
        var start = branch.PointAt(branch.Domain.Start);
        var end = branch.PointAt(branch.Domain.End);
        // The section of a straight generator at v = 0.5 is the whole 20-long side,
        // rotated by half the twist: the branch must span it EXACTLY, not one step short.
        Assert.Equal(20.0, start.DistanceTo(end), 6);
        double offSurface = 0;
        for (int i = 0; i <= 32; i++)
        {
            var p = branch.PointAt(branch.Domain.ParameterAt(i / 32.0));
            Assert.True(side.TryProjectPoint(p, out var uv, 1e-6));
            offSurface = Math.Max(offSurface, (side.PointAt(uv.X, uv.Y) - p).Length);
            Assert.Equal(20.0, p.Z, 9);
        }
        Assert.True(offSurface < 1e-9, $"traced points sit {offSurface:E3} off the exact band");
    }
}
