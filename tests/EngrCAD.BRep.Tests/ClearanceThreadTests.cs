using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Arc-generator helical bands, and the clearance profile they exist for.
/// <para>A printing clearance is a DISTANCE-FIELD offset of the (radius, axial) profile —
/// the same thing <c>Sdf.Thread</c>'s clearance is — so eroding an external thread miters
/// its crest corners and rounds its root corners into arcs of the clearance radius. The
/// tests here measure the three claims that makes: the offset profile is the erosion, an
/// arc-generator band is exact (points, normals, inverse evaluation), and the cut a coaxial
/// carrier makes on one is the closed form <see cref="HelicalArcCut3d"/> rather than
/// anything traced.</para>
/// </summary>
public class ClearanceThreadTests
{
    private const double Pitch = 1.0;
    private const double MajorRadius = 3.0;
    private static readonly double MinorRadius = MajorRadius - 0.625 * (Math.Sqrt(3) / 2 * Pitch);
    private const double Length = 6.0;

    /// <summary>The ISO basic profile, crest centred at phase 0 — the ShapeCompiler recipe.</summary>
    private static Vector2d[] Basic() =>
    [
        new(MajorRadius, -Pitch / 16),
        new(MajorRadius, Pitch / 16),
        new(MinorRadius, 3 * Pitch / 8),
        new(MinorRadius, 5 * Pitch / 8),
    ];

    // ---- the offset profile ----

    /// <summary>
    /// Exactly zero returns the straight pieces the corner overload has always built, so an
    /// unoffset thread cannot move. A user-parameter contract, hence an exact-zero test.
    /// </summary>
    [Fact]
    public void ZeroOffsetIsTheStraightProfile()
    {
        var pieces = SolidFactory.OffsetPitchProfile(Basic(), Pitch, 0);
        Assert.Equal(4, pieces.Count);
        Assert.All(pieces, p => Assert.False(p.IsArc));
        Assert.Equal(Basic()[0], pieces[0].Start);
        Assert.Equal(new Vector2d(Basic()[0].X, Basic()[0].Y + Pitch), pieces[^1].End);
    }

    /// <summary>
    /// The shape of the erosion, corner by corner: the crest flat drops by the clearance
    /// and MITERS, the root flat drops by the clearance and ROUNDS, and the miter's takeback
    /// is the closed form <c>c/tan(30°)</c> that a 60-degree wedge gives.
    /// </summary>
    [Fact]
    public void ErodingMitersTheCrestAndRoundsTheRoot()
    {
        const double c = 0.05;
        var pieces = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -c);

        // crest flat, flank, ARC, root flat, ARC, flank.
        Assert.Equal(6, pieces.Count);
        Assert.Equal([false, false, true, false, true, false], pieces.Select(p => p.IsArc));

        // Both flats sit exactly one clearance lower than they were.
        Assert.Equal(MajorRadius - c, pieces[0].Start.X, 1e-12);
        Assert.Equal(MajorRadius - c, pieces[0].End.X, 1e-12);
        Assert.Equal(MinorRadius - c, pieces[3].Start.X, 1e-12);
        Assert.Equal(MinorRadius - c, pieces[3].End.X, 1e-12);

        // The crest's half width loses c/tan(30 degrees) per side — the flank makes 60
        // degrees with the axis, so the offset lines meet that much further in.
        double expectedHalfWidth = Pitch / 16 - c / Math.Sqrt(3);
        Assert.Equal(-expectedHalfWidth, pieces[0].Start.Y, 1e-12);
        Assert.Equal(+expectedHalfWidth, pieces[0].End.Y, 1e-12);

        // The root flat keeps its FULL width: each rounding arc ends directly beneath its
        // own corner, so erosion moves that flat down without shortening it.
        Assert.Equal(3 * Pitch / 8, pieces[3].Start.Y, 1e-12);
        Assert.Equal(5 * Pitch / 8, pieces[3].End.Y, 1e-12);

        // Each arc is centred on the original valley corner at exactly the clearance radius,
        // and meets its flank tangentially (the flank's own normal takes it there).
        foreach (int i in (int[])[2, 4])
        {
            var arc = pieces[i];
            var center = arc.ArcCenter!.Value;
            Assert.Equal(MinorRadius, center.X, 1e-12);
            Assert.Equal(c, (arc.Start - center).Length, 1e-12);
            Assert.Equal(c, (arc.End - center).Length, 1e-12);
        }
    }

    /// <summary>
    /// A clearance past <c>tan(30°)·crestHalfWidth</c> consumes the crest flat, and the
    /// answer is a POINTED ridge rather than a refusal: that segment's offset half-plane has
    /// become redundant. On an M6×1 the threshold is 0.108, well inside the 0.1–0.25 an FDM
    /// printer wants, so this is the ordinary case rather than an edge one.
    /// </summary>
    [Fact]
    public void ALargeClearanceConsumesTheCrestFlatIntoARidge()
    {
        double threshold = (Pitch / 16) * Math.Sqrt(3);
        Assert.Equal(0.10825, threshold, 1e-5);

        // Just under: six pieces with a hairline crest.
        var narrow = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -(threshold - 1e-4));
        Assert.Equal(6, narrow.Count);

        // Just over: five, the two flanks meeting at the ridge.
        var pointed = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -(threshold + 1e-4));
        Assert.Equal(5, pointed.Count);
        Assert.Equal([false, true, false, true, false], pointed.Select(p => p.IsArc));

        // The ridge sits at the crest's own phase, and its radius is where the two offset
        // flanks cross: major + tan(30°)·crestHalfWidth·... — in closed form,
        // major + sqrt(3)·crestWidth/2 − 2c for the 60-degree flank.
        const double c = 0.2;
        var ridge = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -c);
        Assert.Equal(0, ridge[0].Start.Y, 1e-12);
        Assert.Equal(MajorRadius + Math.Sqrt(3) * (Pitch / 8) / 2 - 2 * c, ridge[0].Start.X, 1e-12);
    }

    /// <summary>
    /// The corner rule is ONE expression, <c>offset × turn</c>, so growing the profile — what
    /// the tool that cuts an INTERNAL thread has to do — rounds and miters the other way
    /// round with no second code path.
    /// </summary>
    [Fact]
    public void GrowingRoundsTheCrestAndMitersTheRoot()
    {
        const double c = 0.05;
        var pieces = SolidFactory.OffsetPitchProfile(Basic(), Pitch, +c);
        Assert.Equal(6, pieces.Count);

        // Both crest corners now round, so an arc precedes the crest flat AND the flank
        // that leaves it; both root corners miter, so the root flat has none.
        Assert.Equal([true, false, true, false, false, false], pieces.Select(p => p.IsArc));
        Assert.Equal(MajorRadius + c, pieces[1].Start.X, 1e-12);
        Assert.Equal(MajorRadius + c, pieces[1].End.X, 1e-12);
        Assert.Equal(MinorRadius + c, pieces[4].Start.X, 1e-12);
        Assert.Equal(MinorRadius + c, pieces[4].End.X, 1e-12);
        Assert.Equal(MajorRadius, pieces[0].ArcCenter!.Value.X, 1e-12);

        // And the root flat is the one that shrinks now — by the same c/tan(30°) per side.
        double expectedHalf = Pitch / 8 - c / Math.Sqrt(3);
        Assert.Equal(2 * expectedHalf, pieces[4].End.Y - pieces[4].Start.Y, 1e-12);
    }

    /// <summary>An offset that eats a flat bounded by a ROUNDED corner has no miter to
    /// replace it with — the region is not locally convex there — and is refused by name
    /// rather than guessed at. Growing an M6-shaped profile by 0.6 consumes a FLANK, whose
    /// far end is one of the rounded crest corners.</summary>
    [Fact]
    public void AnOffsetThatConsumesARoundedFlankIsRefused()
    {
        // 0.4 still works (the root flat has gone, but it was miter-bounded).
        Assert.Equal(5, SolidFactory.OffsetPitchProfile(Basic(), Pitch, +0.4).Count);

        var thrown = Assert.Throws<ArgumentException>(
            () => SolidFactory.OffsetPitchProfile(Basic(), Pitch, +0.6));
        Assert.Contains("rounded corner", thrown.Message);
    }

    // ---- the arc-generator band ----

    private static HelicalSurface ArcBand()
    {
        // The root fillet an 0.05 clearance leaves: centred on the valley corner, from the
        // flank tangent to the root-flat tangent.
        var pieces = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -0.05);
        var arc = pieces.First(p => p.IsArc);
        var center = arc.ArcCenter!.Value;
        double phi0 = Math.Atan2(arc.Start.Y - center.Y, arc.Start.X - center.X);
        double phi1 = Math.Atan2(arc.End.Y - center.Y, arc.End.X - center.X);
        double sweep = phi1 - phi0;
        sweep -= 2 * Math.PI * Math.Floor((sweep + Math.PI) / (2 * Math.PI));
        return new HelicalSurface(
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
            center, (arc.Start - center).Length, phi0, sweep, Pitch, new Interval(-8, 40));
    }

    /// <summary>
    /// The normal is the same expression as a straight generator's, evaluated at a turning
    /// tangent — so it is checked against the finite-difference cross product, which is the
    /// only independent reading available.
    /// </summary>
    [Fact]
    public void AnArcBandsNormalAndInverseEvaluationAreExact()
    {
        var band = ArcBand();
        Assert.False(band.IsStraightGenerator);

        double worstNormal = 0, worstProjection = 0;
        var random = new Random(7);
        for (int i = 0; i < 400; i++)
        {
            double u = -8 + 48 * random.NextDouble();
            double v = random.NextDouble();
            const double h = 1e-6;
            var du = (band.PointAt(u + h, v) - band.PointAt(u - h, v)) / (2 * h);
            var dv = (band.PointAt(u, v + h) - band.PointAt(u, v - h)) / (2 * h);
            worstNormal = Math.Max(worstNormal, (du.Cross(dv).Normalized() - band.NormalAt(u, v)).Length);

            var p = band.PointAt(u, v);
            Assert.True(band.TryProjectPoint(p, out var uv, 1e-9));
            worstProjection = Math.Max(worstProjection, (band.PointAt(uv.X, uv.Y) - p).Length);
        }
        // The finite difference is the inaccurate side of the normal comparison.
        Assert.True(worstNormal < 1e-7, $"worst normal disagreement {worstNormal:e3}");
        Assert.True(worstProjection < 1e-12, $"worst projection residual {worstProjection:e3}");
    }

    /// <summary>
    /// A generator whose axial coordinate is not strictly monotone is refused BY NAME: past
    /// a radial tangent the carrier equation's two arc-cosine branches meet, and a cap cut
    /// stops being single-valued. The gate IS that correctness condition.
    /// </summary>
    [Fact]
    public void AnArcGeneratorMustBeAxiallyMonotone()
    {
        var frame = Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        var center = new Vector2d(3, 0);
        // A sweep straddling phi = pi/2, where cos changes sign.
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => new HelicalSurface(frame, center, 0.2, Math.PI / 2 - 0.3, 0.6, Pitch, new Interval(0, 10)));
        Assert.Contains("strictly monotone", thrown.Message);

        // A half turn or more can never be monotone, and says so separately.
        var wide = Assert.Throws<ArgumentOutOfRangeException>(
            () => new HelicalSurface(frame, center, 0.2, 0.1, Math.PI, Pitch, new Interval(0, 10)));
        Assert.Contains("half turn", wide.Message);

        // And the sweep either side of the tangent is fine.
        _ = new HelicalSurface(frame, center, 0.2, 0.1, 1.2, Pitch, new Interval(0, 10));
    }

    // ---- the cut ----

    /// <summary>
    /// The cap cut is exact in the two ways that matter downstream: it lies IN its plane
    /// bit-exactly (the cap loop and the plane face weld there) and every point of it is on
    /// the band. Its derivatives are analytic, checked against finite differences.
    /// </summary>
    [Fact]
    public void ACapCutIsPlanarOnTheNoseAndLiesOnTheBand()
    {
        var band = ArcBand();
        double rate = band.AxialRate;
        foreach (double zCap in (double[])[0, Length])
        {
            double a = (zCap - band.ProfileEnd.Y) / rate, b = (zCap - band.ProfileStart.Y) / rate;
            var span = new Interval(Math.Min(a, b), Math.Max(a, b));
            Assert.True(HelicalArcCut3d.TryBuild(band, 0, 1, zCap, span, out var cut));
            Assert.True(cut.IsPlanar);
            // The span is the cut's own, not a clip that moved it.
            Assert.Equal(span.Start, cut.Domain.Start, 1e-9);
            Assert.Equal(span.End, cut.Domain.End, 1e-9);

            double offPlane = 0, offBand = 0, worstD = 0, worstDD = 0;
            for (int i = 0; i <= 64; i++)
            {
                double t = cut.Domain.ParameterAt(i / 64.0);
                var p = cut.PointAt(t);
                offPlane = Math.Max(offPlane, Math.Abs(p.Z - zCap));
                Assert.True(band.TryProjectPoint(p, out var uv, 1e-9));
                offBand = Math.Max(offBand, (band.PointAt(uv.X, uv.Y) - p).Length);
                if (i is > 0 and < 64)
                {
                    const double h = 1e-6, h2 = 1e-4;
                    var d = (cut.PointAt(t + h) - cut.PointAt(t - h)) / (2 * h);
                    worstD = Math.Max(worstD, (d - cut.DerivativeAt(t)).Length / Math.Max(1, d.Length));
                    var dd = (cut.PointAt(t + h2) - cut.PointAt(t) * 2 + cut.PointAt(t - h2)) / (h2 * h2);
                    worstDD = Math.Max(worstDD, (dd - cut.SecondDerivativeAt(t)).Length / Math.Max(1, dd.Length));
                }
            }
            // The axial coordinate is the carrier's own constant, so this is EXACT rather
            // than small: it is what the cap loop welds on.
            Assert.Equal(0, offPlane);
            Assert.True(offBand < 1e-12, $"cut left the band by {offBand:e3}");
            Assert.True(worstD < 1e-7, $"derivative disagreement {worstD:e3}");
            Assert.True(worstDD < 1e-4, $"second derivative disagreement {worstDD:e3}");
        }
    }

    /// <summary>
    /// The other two members of the coaxial family land on the same curve: a coaxial
    /// CYLINDER keeps the radius exactly (one iso-v helix) and a CONE gives the general
    /// form. Both are checked by measuring the carrier's own equation along the cut.
    /// </summary>
    [Fact]
    public void CylinderAndConeCarriersCutTheSameFamily()
    {
        var band = ArcBand();
        double rMid = band.RadiusAt(0.5), zMid = band.AxialAt(0.5);

        // Cylinder: radial 1, axial 0, offset = radius. The radius is the carrier's own
        // constant, so it is preserved EXACTLY rather than reconstructed.
        Assert.True(HelicalArcCut3d.TryBuild(band, 1, 0, rMid, band.DomainU, out var helix));
        Assert.False(helix.IsPlanar);
        for (int i = 0; i <= 32; i++)
        {
            var p = helix.PointAt(helix.Domain.ParameterAt(i / 32.0));
            Assert.Equal(rMid, Math.Sqrt(p.X * p.X + p.Y * p.Y), 1e-12);
            Assert.True(band.TryProjectPoint(p, out _, 1e-9));
        }

        // Cone through the band's mid point at 45 degrees: r − z = offset.
        double coneOffset = rMid - zMid;
        Assert.True(HelicalArcCut3d.TryBuild(band, 1, -1, coneOffset, band.DomainU, out var conical));
        for (int i = 0; i <= 32; i++)
        {
            var p = conical.PointAt(conical.Domain.ParameterAt(i / 32.0));
            Assert.Equal(coneOffset, Math.Sqrt(p.X * p.X + p.Y * p.Y) - p.Z, 1e-9);
            Assert.True(band.TryProjectPoint(p, out _, 1e-9));
        }
    }

    /// <summary>A straight generator is <see cref="SpiralArc3d"/>'s family and is declined
    /// here rather than answered twice.</summary>
    [Fact]
    public void AStraightGeneratorIsNotThisFamily()
    {
        var straight = new HelicalSurface(
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
            new Vector2d(3, 0), new Vector2d(2.5, 0.3), Pitch, new Interval(0, 10));
        Assert.True(straight.IsStraightGenerator);
        Assert.False(HelicalArcCut3d.TryBuild(straight, 0, 1, 1, straight.DomainU, out _));
    }

    // ---- the solid ----

    /// <summary>
    /// The rod itself: Validate-clean and Euler-consistent at every clearance, with the face
    /// count following the profile (an arc adds a band; a consumed crest removes one).
    /// </summary>
    [Theory]
    [InlineData(0.0, 4)]
    [InlineData(0.02, 6)]
    [InlineData(0.05, 6)]
    [InlineData(0.1, 6)]
    [InlineData(0.15, 5)]
    [InlineData(0.25, 5)]
    public void AClearanceRodIsValidAtEveryClearance(double clearance, int expectedBands)
    {
        var pieces = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -clearance);
        Assert.Equal(expectedBands, pieces.Count);

        var rod = SolidFactory.MakeThreadedRod(pieces, Pitch, Length);
        rod.Validate();
        Assert.True(rod.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(expectedBands + 2, rod.Faces.Count());
        Assert.Equal(2 * expectedBands, rod.Vertices.Count());
        Assert.Equal(3 * expectedBands, rod.Edges.Count());
    }

    /// <summary>
    /// A left-hand clearance rod is the exact MIRROR of its right-hand twin, which is the
    /// check that the arc generator carries no handedness of its own — it lives in the
    /// (radius, axial) half-plane, where a reflection acts trivially.
    /// </summary>
    [Fact]
    public void ALeftHandClearanceRodMirrorsItsRightHandTwin()
    {
        var pieces = SolidFactory.OffsetPitchProfile(Basic(), Pitch, -0.05);
        var right = SolidFactory.MakeThreadedRod(pieces, Pitch, Length);
        var left = SolidFactory.MakeThreadedRod(pieces, Pitch, Length, null, leftHand: true);
        left.Validate();

        Assert.Equal(right.Faces.Count(), left.Faces.Count());
        Assert.Equal(right.Edges.Count(), left.Edges.Count());

        // The band-by-band identity the whole construction rests on: reflecting across a
        // plane CONTAINING the axis maps phase u to −u, and it is BIT-exact — the arc
        // generator lives in the (radius, axial) half-plane, where the reflection acts
        // trivially, so it contributes nothing of its own to handedness.
        var rightBands = right.Faces.Where(f => f.Surface is HelicalSurface).ToList();
        var leftBands = left.Faces.Where(f => f.Surface is HelicalSurface).ToList();
        for (int b = 0; b < rightBands.Count; b++)
        {
            var a = (HelicalSurface)rightBands[b].Surface;
            var c = (HelicalSurface)leftBands[b].Surface;
            Assert.Equal(a.IsStraightGenerator, c.IsStraightGenerator);
            for (int i = 0; i <= 24; i++)
            {
                double u = a.DomainU.ParameterAt(i / 24.0);
                foreach (double v in (ReadOnlySpan<double>)[0, 0.5, 1])
                {
                    var p = a.PointAt(u, v);
                    Assert.Equal(new Vector3d(p.X, -p.Y, p.Z), c.PointAt(-u, v));
                }
            }
        }
    }

    /// <summary>
    /// The profile the archive writes for an arc band comes back as the same surface, and a
    /// CONICAL spiral arc keeps its axial law — which the four-argument entity used to drop,
    /// flattening a chamfered thread's cap cuts into the frame plane on reload.
    /// </summary>
    [Fact]
    public void ArcBandsAndConicalSpiralsRoundTripThroughTheArchive()
    {
        var rod = SolidFactory.MakeThreadedRod(
            SolidFactory.OffsetPitchProfile(Basic(), Pitch, -0.05), Pitch, Length);
        string text = BrepArchive.Write(rod);
        Assert.Contains("HelicalArc(", text);
        Assert.Contains("HelicalArcCut(", text);
        var restored = BrepArchive.Read(text).Solids.Single();
        restored.Validate();
        Assert.Equal(text, BrepArchive.Write(restored));

        // A conical spiral on its own: the axial law survives, where four arguments lost it.
        var frame = Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        var conical = new SpiralArc3d(frame, 3, -0.1, 0.5, 0.25, new Interval(0, 1));
        var edge = new BrepEdge(conical, conical.Domain,
            new BrepVertex(conical.PointAt(0)), new BrepVertex(conical.PointAt(1)));
        Assert.False(conical.IsPlanar);
        Assert.NotEqual(conical.PointAt(0).Z, conical.PointAt(1).Z);
        _ = edge;
    }
}
