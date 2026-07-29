using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="SketchRegion"/> flattens its segments into structure-of-arrays form, runs
/// lane-wise kernels for lines and full circles, rejects far segments by bounding box, and
/// indexes the ray-parity pieces by y. Every one of those is meant to be a pure
/// restructuring — the field's magnitude is exact and its even–odd sign drives boolean
/// classification, so "close enough" is not a thing here. These tests hold the line:
/// golden bit-hashes against the plain segment loop that preceded the restructuring, and
/// bit equality between the batch and scalar entry points on both the region and the
/// extruded/revolved SDF nodes built on it.
/// </summary>
public class SketchRegionKernelTests
{
    private static Sketch Plate() => Sketch.Rectangle(40, 24)
        .WithHole(Sketch.Circle(new Vector2d(-12, 0), 4))
        .WithHole(Sketch.Circle(new Vector2d(12, 0), 4))
        .WithHole(Sketch.RoundedRectangle(10, 6, 1.5));

    private static Sketch Curvy() => Sketch.Start(0, 0)
        .LineTo(10, 0)
        .BezierTo(new Vector2d(14, 3), new Vector2d(14, 9), new Vector2d(10, 12))
        .ArcTo(new Vector2d(2, 12), 5, clockwise: false)
        .QuadraticTo(new Vector2d(-3, 6), new Vector2d(0, 0))
        .Close();

    /// <summary>A rounded rectangle (quarter turns, axis-aligned boundary rays) with a
    /// major-arc hole — a sweep wider than π, which inverts the wedge test's branch.</summary>
    private static Sketch Arcs() => Sketch.RoundedRectangle(20, 12, 3)
        .WithHole(Sketch.Start(-2, 0)
            .ArcTo(new Vector2d(2, 0), 2.5, clockwise: false, largeArc: true)
            .Close());

    /// <summary>A cubic with a genuine cusp (B′ vanishes at t = ½, which needs
    /// p₃ + c₂ = c₁ + p₀) — the Newton stage's hostile input.</summary>
    private static Sketch Cusp() => Sketch.Start(0, 0)
        .BezierTo(new Vector2d(9, 0), new Vector2d(0, 9), new Vector2d(9, -9))
        .LineTo(12, -12)
        .LineTo(12, 6)
        .LineTo(0, 6)
        .Close();

    /// <summary>A rotated full ellipse with a second, counter-rotated one as a hole — the
    /// segment kind whose distance is a scan-and-Newton rather than a closed form.</summary>
    private static Sketch Ellipses() => Sketch.Ellipse(new Vector2d(0, 0), 18, 11, 27)
        .WithHole(Sketch.Ellipse(new Vector2d(3, 1), 6, 3.5, -40));

    /// <summary>
    /// Elliptical ARCS rather than full ellipses: a closed outline of four SVG-style
    /// elliptical arcs, so every one carries a partial sweep (both signs, both largeArc
    /// branches) and the scan's endpoints are real segment joints shared with a neighbour.
    /// </summary>
    private static Sketch EllipseArcs() => Sketch.Start(-14, 0)
        .EllipticalArcTo(new Vector2d(0, 9), 14, 9, 0, largeArc: false, clockwise: false)
        .EllipticalArcTo(new Vector2d(14, 0), 16, 11, 20, largeArc: false, clockwise: false)
        .EllipticalArcTo(new Vector2d(0, -7), 14, 7, 0, largeArc: false, clockwise: false)
        .EllipticalArcTo(new Vector2d(-14, 0), 20, 13, -15, largeArc: false, clockwise: false)
        .Close();

    private static Sketch Named(string name) => name switch
    {
        "plate" => Plate(),
        "curvy" => Curvy(),
        "slot" => Sketch.Slot(20, 8),
        "arcs" => Arcs(),
        "cusp" => Cusp(),
        "ellipses" => Ellipses(),
        "ellipsearcs" => EllipseArcs(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// A grid straddling the sketch (so vertices, tangencies and exterior points all
    /// appear) plus 20 000 seeded random points, hashed on the exact bits of every result.
    /// </summary>
    private static long Fingerprint(SketchRegion region, in Aabb bounds)
    {
        unchecked
        {
            long hash = (long)14695981039346656037UL;
            void Mix(double value)
            {
                hash ^= BitConverter.DoubleToInt64Bits(value);
                hash *= 1099511628211L;
            }

            double x0 = bounds.Min.X - 3, x1 = bounds.Max.X + 3;
            double y0 = bounds.Min.Y - 3, y1 = bounds.Max.Y + 3;
            const int n = 61;
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                    Mix(region.SignedDistance(new Vector2d(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * j / n)));

            var random = new Random(20260725);
            for (int i = 0; i < 20000; i++)
                Mix(region.SignedDistance(new Vector2d(
                    x0 + (x1 - x0) * random.NextDouble(), y0 + (y1 - y0) * random.NextDouble())));
            return hash;
        }
    }

    /// <summary>
    /// Every one of these is the plain segment loop's answer, not this code's: "plate",
    /// "curvy" and "slot" predate the kernels entirely, and "arcs"/"cusp" — added with the
    /// arc and bézier kernels to reach the sweep-wider-than-π branch and a cusped Newton
    /// solve — were taken by routing the region back through <c>SketchSegment.Distance</c>
    /// and reading the hash off THAT. So they prove the transcriptions are term for term,
    /// which is the only reason the lane-wise forms are allowed to exist.
    /// <para>
    /// The two ellipse rows are the constants
    /// <see cref="EllipseKernel_IsBitIdenticalToTheSharedCurveSolve"/> reads off the
    /// <c>General</c> path. That test asserts the equality continuously, so these stay the
    /// shared curve solve's answer by construction rather than only at the moment they were
    /// taken — which is the difference the ellipse needed, its source living in another
    /// project.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("plate", -20992552982407019L)]
    [InlineData("curvy", -1804961155336630648L)]
    [InlineData("slot", 9097049425223680080L)]
    [InlineData("arcs", 4040765462217916895L)]
    [InlineData("cusp", 4167966295857103130L)]
    [InlineData("ellipses", 9065144379352060846L)]
    [InlineData("ellipsearcs", -5214840631977865301L)]
    public void SignedDistance_MatchesTheGoldenBitPattern(string name, long fingerprint)
    {
        var sketch = Named(name);
        Assert.Equal(fingerprint, Fingerprint(new SketchRegion(sketch), sketch.Bounds));
    }

    // ------------------------------------------------------------------ elliptical arcs

    /// <summary>
    /// The ellipse kernel is the one transcription whose source lives in another project —
    /// <c>EllipseSeg.Distance</c> is <c>EngrCAD.BRep</c>'s shared <c>Curve2d.NearestPoint</c>
    /// — so the two paths are held bit-equal on the same binary rather than only against a
    /// committed constant. That is what makes a future edit to the shared curve solve fail
    /// HERE, naming the drift, instead of somewhere downstream as a moved number.
    /// </summary>
    [Theory]
    [InlineData("ellipses")]
    [InlineData("ellipsearcs")]
    public void EllipseKernel_IsBitIdenticalToTheSharedCurveSolve(string name)
    {
        var sketch = Named(name);
        Assert.Equal(
            Fingerprint(new SketchRegion(sketch, forRevolution: false, ellipseKernel: false), sketch.Bounds),
            Fingerprint(new SketchRegion(sketch), sketch.Bounds));
    }

    /// <summary>
    /// The kernel's hostile points: the 65 scan parameters themselves (where the scan lands
    /// exactly on the curve and Newton starts already converged), the bracket ends bestT ±
    /// 1/64, the axis extremes, the shared segment joints, and the CENTRE — where the
    /// nearest point is genuinely ambiguous and the Newton derivative is at its smallest.
    /// </summary>
    [Theory]
    [InlineData("ellipses")]
    [InlineData("ellipsearcs")]
    public void EllipseStructuredPoints_AreBitIdenticalToScalar(string name)
    {
        var sketch = Named(name);
        var region = new SketchRegion(sketch);
        var reference = new SketchRegion(sketch, forRevolution: false, ellipseKernel: false);
        var probes = new List<Vector2d>();
        foreach (var (centre, a, b, start, sweep) in EllipseParameters(name))
        {
            probes.Add(centre);
            // Every scan parameter, and the two bracket ends around it, on the curve and on
            // a tight ring about it (the sign flips across the boundary).
            for (int i = 0; i <= 64; i++)
            {
                foreach (double t in new[] { (double)i / 64, (i - 1.0) / 64, (i + 1.0) / 64 })
                {
                    if (t is < 0 or > 1)
                        continue;
                    double angle = start + sweep * t;
                    var on = centre + a * Math.Cos(angle) + b * Math.Sin(angle);
                    probes.Add(on);
                    for (int k = 0; k < 4; k++)
                    {
                        double ring = 2 * Math.PI * k / 4;
                        foreach (double radius in new[] { 1e-12, 1e-6, 1e-2, 0.5 })
                            probes.Add(on + new Vector2d(Math.Cos(ring), Math.Sin(ring)) * radius);
                    }
                }
            }
        }

        AssertProbesAgree(region, probes);
        // And the kernel agrees with the shared curve solve at every one of them.
        foreach (var probe in probes)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(reference.SignedDistance(probe)),
                BitConverter.DoubleToInt64Bits(region.SignedDistance(probe)));
    }

    /// <summary>The ellipses each fixture builds, as (centre, semi-axis A, semi-axis B,
    /// start, sweep) — the same parameters <c>Sketch.Ellipse</c> and
    /// <c>EllipticalArcTo</c> put on the segments.</summary>
    private static IEnumerable<(Vector2d Centre, Vector2d A, Vector2d B, double Start, double Sweep)>
        EllipseParameters(string name)
    {
        if (name == "ellipses")
        {
            foreach (var (centre, semiX, semiY, degrees) in new[]
                     {
                         (new Vector2d(0, 0), 18.0, 11.0, 27.0),
                         (new Vector2d(3, 1), 6.0, 3.5, -40.0),
                     })
            {
                double radians = degrees * Math.PI / 180;
                double cos = Math.Cos(radians), sin = Math.Sin(radians);
                yield return (
                    centre, new Vector2d(cos, sin) * semiX, new Vector2d(-sin, cos) * semiY, 0, 2 * Math.PI);
            }
            yield break;
        }
        // The arc fixture's segments carry solved centres, so read them off the sketch's own
        // exact curves rather than restating the SVG endpoint solve here.
        foreach (var curve in Named(name).ToCurves())
        {
            if (curve is Ellipse2d ellipse)
                yield return (
                    ellipse.Center, ellipse.SemiAxisX, ellipse.SemiAxisY,
                    ellipse.StartAngle, ellipse.SweepAngle);
        }
    }

    /// <summary>
    /// A revolved elliptical profile: the kernel must survive the on-axis segment dropping
    /// that <c>forRevolution</c> does, and the revolved node's batch entry.
    /// </summary>
    [Fact]
    public void RevolvedEllipseRegion_IsBitIdenticalToScalar()
    {
        var profile = Sketch.Start(0, 0)
            .LineTo(9, 0)
            .EllipticalArcTo(new Vector2d(4, 14), 7, 9, 12, largeArc: false, clockwise: false)
            .LineTo(0, 14)
            .Close();
        var field = Sdf.RevolvedRegion(new SketchRegion(profile, forRevolution: true));
        AssertBatchMatchesScalar(field, profile.Bounds, runLength: 19);
    }

    private static (double[] X, double[] Y) SamplePoints(in Aabb bounds, int count, int seed)
    {
        var random = new Random(seed);
        var x = new double[count];
        var y = new double[count];
        for (int i = 0; i < count; i++)
        {
            x[i] = bounds.Min.X - 3 + (bounds.Size.X + 6) * random.NextDouble();
            y[i] = bounds.Min.Y - 3 + (bounds.Size.Y + 6) * random.NextDouble();
        }
        return (x, y);
    }

    /// <summary>
    /// Batch and scalar must agree to the bit, at every length around the SIMD register
    /// width and the batch chunk boundary (the ragged tail is where a lane-wise kernel and
    /// its scalar fallback would diverge if they disagreed).
    /// </summary>
    [Theory]
    [InlineData("plate")]
    [InlineData("curvy")]
    [InlineData("slot")]
    [InlineData("arcs")]
    [InlineData("cusp")]
    [InlineData("ellipses")]
    [InlineData("ellipsearcs")]
    public void BatchSignedDistance_IsBitIdenticalToScalar(string name)
    {
        var sketch = Named(name);
        var region = new SketchRegion(sketch);
        foreach (int count in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 1023, 1024, 1025, 2049 })
        {
            var (x, y) = SamplePoints(sketch.Bounds, count, 4242 + count);
            var batched = new double[count];
            region.SignedDistance(x, y, batched);
            for (int i = 0; i < count; i++)
            {
                double expected = region.SignedDistance(new Vector2d(x[i], y[i]));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(batched[i]));
            }
        }
    }

    /// <summary>
    /// Points that land exactly on the boundary, on shared vertices and on the ray-parity
    /// piece breaks are where the half-open endpoint rule and the bounding-box reject earn
    /// their keep — an index or a reject that lost a segment would show up as a sign flip
    /// or a nonzero distance here.
    /// </summary>
    [Fact]
    public void OnBoundaryPoints_AreExactlyZeroAndBatchAgrees()
    {
        var sketch = Plate();
        var region = new SketchRegion(sketch);
        // Rectangle corners and edge midpoints, plus points on both bores' circles.
        var probes = new List<Vector2d>
        {
            new(-20, -12), new(20, -12), new(20, 12), new(-20, 12),
            new(0, -12), new(20, 0), new(0, 12), new(-20, 0),
        };
        for (int i = 0; i < 16; i++)
        {
            double a = 2 * Math.PI * i / 16;
            probes.Add(new Vector2d(-12 + 4 * Math.Cos(a), 4 * Math.Sin(a)));
            probes.Add(new Vector2d(12 + 4 * Math.Cos(a), 4 * Math.Sin(a)));
        }

        var x = probes.Select(p => p.X).ToArray();
        var y = probes.Select(p => p.Y).ToArray();
        var batched = new double[probes.Count];
        region.SignedDistance(x, y, batched);

        for (int i = 0; i < probes.Count; i++)
        {
            // Boundary points sit at distance zero up to the arithmetic that placed them.
            Assert.Equal(0, Math.Abs(region.SignedDistance(probes[i])), 12);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(region.SignedDistance(probes[i])),
                BitConverter.DoubleToInt64Bits(batched[i]));
        }
    }

    // ------------------------------------------------------- sweep boundaries and béziers

    /// <summary>
    /// Asserts the batch and scalar entry points agree to the bit over an arbitrary probe
    /// list, at eight leading offsets so every probe visits every SIMD lane — a lane-wise
    /// kernel that only disagreed in, say, lane 0 of a block would otherwise hide behind a
    /// fixed alignment.
    /// </summary>
    private static void AssertProbesAgree(SketchRegion region, IReadOnlyList<Vector2d> probes)
    {
        for (int lead = 0; lead < 8; lead++)
        {
            var x = new double[probes.Count + lead];
            var y = new double[probes.Count + lead];
            for (int i = 0; i < lead; i++)
                (x[i], y[i]) = (1e4 + i, -1e4 - i);
            for (int i = 0; i < probes.Count; i++)
                (x[lead + i], y[lead + i]) = (probes[i].X, probes[i].Y);

            var batched = new double[x.Length];
            region.SignedDistance(x, y, batched);
            for (int i = 0; i < x.Length; i++)
            {
                double expected = region.SignedDistance(new Vector2d(x[i], y[i]));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(batched[i]));
            }
        }
    }

    /// <summary>Probes strung along a sweep boundary ray and along rays rotated off it by a
    /// ladder of angles, each at radii spanning twelve decades.</summary>
    private static void AddRayProbes(
        List<Vector2d> into, Vector2d centre, Vector2d endpoint, bool onRayOnly)
    {
        var arm = endpoint - centre;
        double[] scales = [0, 1e-6, 1e-3, 0.5, 1 - 1e-12, 1, 1 + 1e-12, 2, 1e3, 1e6];
        if (onRayOnly)
        {
            // EXACTLY on the boundary ray: the cross products vanish, so every one of these
            // lands inside the certainty band and is answered by the scalar Atan2 path.
            foreach (double scale in scales)
                into.Add(centre + arm * scale);
            return;
        }
        // Rotated off the ray by more than the band's half-width (1e-9 rad), so the lane-wise
        // wedge test answers these itself — this is where it must agree with Atan2.
        foreach (double delta in new[] { -1e-2, -1e-4, -1e-6, -1e-8, 1e-8, 1e-6, 1e-4, 1e-2 })
        {
            double cos = Math.Cos(delta), sin = Math.Sin(delta);
            var turned = new Vector2d(arm.X * cos - arm.Y * sin, arm.X * sin + arm.Y * cos);
            foreach (double scale in scales)
                into.Add(centre + turned * scale);
        }
    }

    /// <summary>
    /// The wedge test replaces <c>ArcSeg.Distance</c>'s <c>Atan2</c>, so the sweep boundary
    /// rays are where it has to earn its keep. Points rotated off a boundary by 1e-8 rad and
    /// more are decided by the lane-wise test itself, so any disagreement with <c>Atan2</c>
    /// shows up here; points sitting exactly ON a boundary — which is where an arc's shared
    /// endpoint lands — fall inside the certainty band and are handed back to the scalar
    /// path. Both must be bit-identical, and both are checked at every model scale from
    /// microns to kilometres, because the guard is angular and must not care.
    /// </summary>
    [Theory]
    [InlineData(1e-5)]
    [InlineData(1)]
    [InlineData(1e5)]
    public void ArcSweepBoundaries_AreBitIdenticalToScalar(double scale)
    {
        // Corner-arc centres and one endpoint each, plus the major-arc hole's, in the
        // geometry Arcs() builds (10 x 6 half-extents, radius 3; hole centred (0, -1.5)).
        var region = new SketchRegion(ScaledArcs(scale));
        var onRay = new List<Vector2d>();
        var offRay = new List<Vector2d>();
        foreach (var (centre, first, second) in ArcRays(scale))
        {
            AddRayProbes(onRay, centre, first, onRayOnly: true);
            AddRayProbes(onRay, centre, second, onRayOnly: true);
            AddRayProbes(offRay, centre, first, onRayOnly: false);
            AddRayProbes(offRay, centre, second, onRayOnly: false);
            onRay.Add(centre);
        }

        AssertProbesAgree(region, onRay);
        AssertProbesAgree(region, offRay);
    }

    private static Sketch ScaledArcs(double scale) => Sketch.RoundedRectangle(20 * scale, 12 * scale, 3 * scale)
        .WithHole(Sketch.Start(-2 * scale, 0)
            .ArcTo(new Vector2d(2 * scale, 0), 2.5 * scale, clockwise: false, largeArc: true)
            .Close());

    /// <summary>Each arc of <see cref="ScaledArcs"/> as (centre, endpoint, endpoint).</summary>
    private static IEnumerable<(Vector2d Centre, Vector2d First, Vector2d Second)> ArcRays(double scale)
    {
        double w = 10 * scale, h = 6 * scale, r = 3 * scale;
        foreach (int sx in new[] { -1, 1 })
            foreach (int sy in new[] { -1, 1 })
            {
                var centre = new Vector2d(sx * (w - r), sy * (h - r));
                yield return (centre, centre + new Vector2d(sx * r, 0), centre + new Vector2d(0, sy * r));
            }
        var hole = new Vector2d(0, -1.5 * scale);
        yield return (hole, new Vector2d(-2 * scale, 0), new Vector2d(2 * scale, 0));
    }

    /// <summary>
    /// A degenerate arc — a sweep of a few nanoradians at a millimetre radius, so its two
    /// boundary rays are separated by less than the certainty band itself. The kernel must
    /// still answer exactly (it does, by refusing: every probe is uncertain against one ray
    /// or the other and takes the scalar path).
    /// </summary>
    [Fact]
    public void ANearDegenerateArc_IsStillBitIdenticalToScalar()
    {
        // A chord of 1e-8 across a radius of 1 sweeps 1e-8 rad.
        var sketch = Sketch.Start(0, 0)
            .LineTo(1, 0)
            .ArcTo(new Vector2d(1, 1e-8), 1, clockwise: false)
            .LineTo(0, 1e-8)
            .Close();
        var region = new SketchRegion(sketch);
        var probes = new List<Vector2d>();
        for (int i = -20; i <= 20; i++)
            for (int k = 0; k < 4; k++)
                probes.Add(new Vector2d(1 + i * 1e-9 * Math.Pow(10, k), i * 1e-9));
        AssertProbesAgree(region, probes);
    }

    /// <summary>
    /// The bézier kernel's hostile points: the sixteen scan parameters themselves (where the
    /// scan finds distance zero and Newton starts already converged), the control points, the
    /// endpoints shared with neighbouring segments, and the neighbourhood of a cusp, where
    /// B′ vanishes and the Newton step's divergent <c>break</c> — reproduced by masking the
    /// write to the refined parameter — is the only thing keeping the two paths together.
    /// </summary>
    [Fact]
    public void BezierStructuredPoints_AreBitIdenticalToScalar()
    {
        var region = new SketchRegion(Cusp());
        Vector2d p0 = new(0, 0), c1 = new(9, 0), c2 = new(0, 9), p3 = new(9, -9);
        var probes = new List<Vector2d> { p0, c1, c2, p3 };
        for (int i = 0; i <= 64; i++)
        {
            double t = (double)i / 64;
            double u = 1 - t;
            var on = p0 * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + p3 * (t * t * t);
            probes.Add(on);
            // A tight ring around each curve point: the sign flips across the boundary, and
            // the nearest-point solve is at its most ill-conditioned right at a cusp.
            for (int k = 0; k < 8; k++)
            {
                double angle = 2 * Math.PI * k / 8;
                foreach (double radius in new[] { 1e-12, 1e-7, 1e-3, 0.25 })
                    probes.Add(on + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * radius);
            }
        }
        AssertProbesAgree(region, probes);
    }

    /// <summary>
    /// The extruded node answers the region once per distinct (x, y) instead of once per
    /// sample. That memoization must be invisible: identical results whether the batch is
    /// long constant-xy runs (how the polygonizer samples), all-distinct points, or a
    /// single point.
    /// </summary>
    [Theory]
    [InlineData("plate")]
    [InlineData("curvy")]
    public void ExtrudedRegionBatch_IsBitIdenticalToScalar(string name)
    {
        var sketch = Named(name);
        var field = Sdf.ExtrudedRegion(new SketchRegion(sketch), 6);
        AssertBatchMatchesScalar(field, sketch.Bounds, runLength: 37);
        AssertBatchMatchesScalar(field, sketch.Bounds, runLength: 1);
    }

    [Fact]
    public void RevolvedRegionBatch_IsBitIdenticalToScalar()
    {
        var profile = Sketch.Start(0, 0)
            .LineTo(8, 0)
            .BezierTo(new Vector2d(10, 4), new Vector2d(5, 8), new Vector2d(6, 14))
            .LineTo(0, 14)
            .Close();
        var field = Sdf.RevolvedRegion(new SketchRegion(profile, forRevolution: true));
        AssertBatchMatchesScalar(field, profile.Bounds, runLength: 23);
    }

    /// <summary>An <see cref="IPlanarRegion"/> that does NOT override the batch method must
    /// still work — the interface's default implementation is part of the contract.</summary>
    [Fact]
    public void ADefaultBatchRegion_StillLowersToACorrectPrism()
    {
        var field = Sdf.ExtrudedRegion(new UnitDisk(), 4);
        AssertBatchMatchesScalar(field, new Aabb((-1, -1, 0), (1, 1, 0)), runLength: 11);
        Assert.Equal(-0.5, field.Evaluate(new Vector3d(0.5, 0, 2)), 12);
    }

    private static void AssertBatchMatchesScalar(Sdf field, in Aabb planarBounds, int runLength)
    {
        var random = new Random(90210 + runLength);
        const int count = 2100;
        var x = new double[count];
        var y = new double[count];
        var z = new double[count];
        double px = 0, py = 0;
        for (int i = 0; i < count; i++)
        {
            if (i % runLength == 0)
            {
                px = planarBounds.Min.X - 2 + (planarBounds.Size.X + 4) * random.NextDouble();
                py = planarBounds.Min.Y - 2 + (planarBounds.Size.Y + 4) * random.NextDouble();
            }
            x[i] = px;
            y[i] = py;
            z[i] = -4 + 16 * random.NextDouble();
        }

        var batched = new double[count];
        var points = new Vector3d[count];
        for (int i = 0; i < count; i++)
            points[i] = new Vector3d(x[i], y[i], z[i]);
        field.Evaluate(points, batched);

        for (int i = 0; i < count; i++)
        {
            double expected = field.Evaluate(points[i]);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(batched[i]));
        }
    }

    private sealed class UnitDisk : IPlanarRegion
    {
        public double SignedDistance(in Vector2d point) => point.Length - 1;

        public Aabb Bounds => new((-1, -1, 0), (1, 1, 0));
    }
}
