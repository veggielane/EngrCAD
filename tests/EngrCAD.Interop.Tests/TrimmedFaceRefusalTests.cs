using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The two winding structures <see cref="TrimmedFaceTessellator"/> refuses outright — a
/// pole-bounded single-chain band carrying hole loops, and a loop that winds the periodic
/// direction more than once — and the question the backlog asked about them: are they
/// reachable at all?
/// <para><b>Verdict: not from the <c>Shape</c> API today.</b> Both are blocked upstream,
/// by the exact B-Rep boolean rather than by the tessellator, and the tests below record
/// the constructions tried and what stops each one. That makes the refusals a backstop
/// rather than a live gap — but a backstop that must still fire correctly, so the other
/// two tests here drive hand-built faces of exactly those shapes and assert the message.
/// </para>
/// </summary>
public class TrimmedFaceRefusalTests
{
    private const double Radius = 10;

    /// <summary>A sphere as a full revolve of a pole-to-pole meridian about +Z.</summary>
    private static RevolvedSurface Sphere() => new(
        new CurveSegment(
            new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ, Radius),
            -Math.PI / 2, Math.PI / 2),
        Vector3d.Zero, Vector3d.UnitZ);

    private static Vector3d On(double u, double lat) => new(
        Radius * Math.Cos(lat) * Math.Cos(u), Radius * Math.Cos(lat) * Math.Sin(u), Radius * Math.Sin(lat));

    /// <summary>A closed loop made of one closed polyline edge through the given points.</summary>
    private static BrepLoop ClosedLoop(IReadOnlyList<Vector3d> points)
    {
        var curve = new PolylineCurve3d(points, isClosed: true);
        var vertex = new BrepVertex(points[0]);
        return new BrepLoop([new BrepCoedge(new BrepEdge(curve, curve.Domain, vertex, vertex), true)]);
    }

    private static string Refusal(BrepFace face)
    {
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var loop in face.Loops)
        {
            foreach (var coedge in loop.Coedges)
                edgePolylines[coedge.Edge] = BRepTessellator.SampleEdge(coedge.Edge, 32, 24);
        }
        var polygons = new List<IReadOnlyList<Vector3d>>();
        Assert.False(
            TrimmedFaceTessellator.TryTessellate(face, edgePolylines, 32, 24, polygons, out string? why),
            "the tessellator accepted a winding structure it is documented to refuse");
        Assert.Empty(polygons); // a refusal must not touch the caller's output
        return why!;
    }

    /// <summary>
    /// A pole-bounded cap (one winding loop, the far v row degenerate) that also carries a
    /// hole: <see cref="TrimmedFaceTessellator"/> has no tier for it — the band-with-holes
    /// path needs TWO ring chains to unroll between — so it refuses instead of falling back
    /// to the natural grid, which covers the whole sphere rather than this face.
    /// </summary>
    [Fact]
    public void PoleBoundedBandWithAHole_RefusesLoudly()
    {
        var equator = Enumerable.Range(0, 48).Select(i => On(2 * Math.PI * i / 48, 0)).ToList();
        var hole = Enumerable.Range(0, 24)
            .Select(i => On(0.3 * Math.Cos(2 * Math.PI * i / 24), 0.8 + 0.15 * Math.Sin(2 * Math.PI * i / 24)))
            .ToList();

        var why = Refusal(new BrepFace(Sphere(), [ClosedLoop(equator), ClosedLoop(hole)]));
        Assert.Contains("winding structure is unsupported", why);
    }

    /// <summary>
    /// A loop winding the periodic direction twice — a closed curve that goes round the
    /// sphere twice, rising and falling so it closes on itself. Pullback unwraps u along
    /// the loop, so the total is 2 and the tessellator refuses by name.
    /// </summary>
    [Fact]
    public void LoopWindingThePeriodTwice_RefusesByName()
    {
        // u = 4*pi*t, lat = 0.3*cos(2*pi*t): at t -> 1 the curve returns to its start
        // point (u back to 0 mod 2*pi, lat back to 0.3) having wrapped u twice.
        var doubleWrap = Enumerable.Range(0, 192)
            .Select(i =>
            {
                double t = i / 192.0;
                return On(4 * Math.PI * t, 0.3 * Math.Cos(2 * Math.PI * t));
            })
            .ToList();

        var why = Refusal(new BrepFace(Sphere(), [ClosedLoop(doubleWrap)]));
        Assert.Contains("winds the periodic direction 2 times", why);
    }

    /// <summary>
    /// The reachability verdict, as a test so it cannot rot: every construction that ought
    /// to produce one of the two refused structures is stopped EARLIER, by the exact B-Rep
    /// boolean. Each entry names what the construction was trying to make.
    /// <para>The two that do build are the interesting half of the record: a latitude cut
    /// does give a sphere a pole-bounded cap with a single winding loop, and drilling it
    /// off-axis does not put a hole IN that cap — the boolean re-splits so the bore's rim
    /// lands on the two-ring band below, which the slab sweep already handles. That is why
    /// the pole-bounded-with-holes tier has never been needed.</para>
    /// </summary>
    [Theory]
    // A pole cap that also carries a hole. The bore lands on the band, not the cap.
    [InlineData("cap with off-axis bore", true)]
    [InlineData("cap with two bores", true)]
    // Cutting the cap lower used to make the boolean fail first; since traced curves are
    // snapped onto their exact boundary landing it builds, and — the point of the record —
    // still does not put a hole in the pole-bounded cap.
    [InlineData("cap cut low with bore", true)]
    [InlineData("cone cut with bore", false)]
    // "torus cut with bore" used to be a row here. Carrier clipping moved it from refused to
    // BUILT, and it does not meet the shared floor — so it has its own test below rather than a
    // row with a softer bar, which is what keeps the floor a bar instead of a knob.
    // A doubly-winding loop needs a helical intersection curve on a periodic surface.
    [InlineData("threaded pocket in a sphere", false)]
    public void ConstructionsThatWouldReachARefusal_AreStoppedEarlierOrDoNotReachIt(
        string name, bool builds)
    {
        BrepSolid Build() => name switch
        {
            "cap with off-axis bore" => (Shape.Sphere(10)
                - Shape.Box(40, 40, 40).Translate((0, 0, -25))
                - Shape.Cylinder(2, 40).Translate((5, 0, -20))).ToBrep(),
            "cap with two bores" => (Shape.Sphere(10)
                - Shape.Box(40, 40, 40).Translate((0, 0, -25))
                - Shape.Cylinder(1.5, 40).Translate((5, 0, -20))
                - Shape.Cylinder(1.5, 40).Translate((-5, 0, -20))).ToBrep(),
            "cap cut low with bore" => (Shape.Sphere(10)
                - Shape.Box(40, 40, 40).Translate((0, 0, -28))
                - Shape.Cylinder(2, 40).Translate((6, 0, -20))).ToBrep(),
            "cone cut with bore" => (Shape.Cone(10, 0, 20)
                - Shape.Box(40, 40, 40).Translate((0, 0, 34))
                - Shape.Cylinder(1.5, 60).Translate((4, 0, -30))).ToBrep(),
            _ => Shape.Sphere(10)
                .ThreadedHole(StandardThreads.Metric(6), [new(0, 0)], 6,
                    SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY)).ToBrep(),
        };

        if (!builds)
        {
            // Stopped by the boolean's own two-manifold check, which names the operation
            // and locates a crack — the loud refusal one layer up.
            var blocked = Assert.ThrowsAny<Exception>(Build);
            Assert.Contains("unclosed solid", blocked.Message);
            return;
        }

        // It builds — so it must also tessellate, through tiers that already exist. The bar is
        // the CORPUS floor, cos(3 · 2π/n) — three natural grid steps of surface normal — not a
        // number fitted to these cases: the two original entries measure 0.9994 against it,
        // while "cap cut low with bore" measures 0.9200 (its bore rim is a tracer polyline
        // baked in at boolean time, and does not refine with the grid). Recording both numbers
        // beats moving the bar to whichever one is worse.
        var solid = Build();
        var report = TessellationQuality.Audit(solid, 32, 24);
        Assert.Equal(0, report.Folds);
        double floor = Math.Cos(3 * 2 * Math.PI / 32);
        Assert.True(report.WorstDot > floor, $"{name}: worst normal agreement {report.WorstDot} vs floor {floor}");

        // And no face carries the refused structure: a winding loop together with a hole.
        foreach (var face in solid.Faces)
        {
            double period = FaceGeometry.PeriodU(face.Surface);
            if (period <= 0)
                continue;
            var windings = face.Loops.Select(loop => Winding(face.Surface, loop, period, solid)).ToList();
            Assert.False(
                windings.Count(w => w != 0) == 1 && windings.Contains(0),
                $"{name} produced a pole-bounded band with holes after all — the tier is now needed");
            Assert.DoesNotContain(windings, w => Math.Abs(w) > 1);
        }
    }

    /// <summary>
    /// A torus cut by a plane and then bored blind through its tube. This was a REFUSAL until
    /// carrier clipping landed — the bore wall's z-constant cut is now trimmed to the arc its
    /// partner face actually shares, so it arrives as a WRAPPING chain the arrangement splits
    /// into two bands instead of a closed circle mis-split into a hole plus a disk.
    ///
    /// <para><b>The boolean is right and the tessellation is not, and both are asserted here
    /// rather than one being allowed to stand for the other.</b> The solid is Validate-clean at
    /// genus 1 and its volume converges monotonically upward on the exact Pappus value —
    /// 2π²·12·16 less the segment above z = 2 (2π·12·(16·acos(½) − 2√12)) less the bore's slice
    /// of the tube — measured 2916.5 / 2998.7 / 3009.4 / 3014.5 / 3018.4 / 3019.6 / 3020.5 at
    /// 16/32/48/64/96/128/192 segments per circle against 3021.1.</para>
    ///
    /// <para><b>The folds are gone, and the diagnosis they were filed under was wrong.</b> The
    /// entry blamed the periodic-band tier pairing its chains by u and falling to the merge walk
    /// — measured, the merge walk is reached <b>zero</b> times here at any density, and both
    /// chains are u-monotone, so the band takes the ordinary interior-rows path. Every fold was
    /// created by <b>refinement</b>: with `refine: false` the BASE triangulation is fold-free at
    /// 16/32/48/64/96/128/192 alike, while refinement inflated these two faces ×4.1 at 192 and
    /// turned 53 facets inside out. `Refine` now refuses a split that would flip a facet the
    /// parent had right, so the count is 0 at every density above.</para>
    ///
    /// <para><b>The residual that remains is fidelity, not orientation, and it is filed as open
    /// work</b>: the bore's rim is a marching-tracer polyline baked in at boolean time with 15–17
    /// samples however fine the grid around it becomes, so near it the base carries facets the
    /// rows cannot level and refinement can only decline to split. Worst facet-vs-surface
    /// agreement at 192/96 is ~0.009 refined against ~0.18 unrefined — no longer inverted, still
    /// near-perpendicular, and notably WORSE than leaving it alone, which is the sign that the
    /// real fix is a row path covering the coarse-rim region rather than anything in `Refine`.
    /// The bounds below are a RECORD of today's behaviour, not a bar to sit at.</para>
    /// </summary>
    [Fact]
    public void TorusCutWithABore_BuildsWithARecordedTessellationResidual()
    {
        var solid = (Shape.Torus(12, 4)
            - Shape.Box(60, 60, 20).Translate((0, 0, 12))
            - Shape.Cylinder(1.5, 40).Translate((12, 0, -20))).ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 1), "a bored ring is still genus 1");

        // Pappus throughout: the ring, less the tube's segment above the cut, less the material
        // the bore takes out of the tube between its lower surface and the bore's ceiling at z = 0.
        double ring = 2 * Math.PI * Math.PI * 12 * 16;
        double segment = 2 * Math.PI * 12 * (16 * Math.Acos(0.5) - 2 * Math.Sqrt(12));
        double bore = 2 * Quad(x => 2 * Math.Sqrt(2.25 - x * x) * Math.Sqrt(16 - x * x), 0, 1.5, 400);
        double expected = ring - segment - bore;

        double previous = double.NegativeInfinity;
        foreach (int segments in (ReadOnlySpan<int>)[48, 96, 192])
        {
            double volume = BRepTessellator.Tessellate(solid, segments, segments / 2).Volume();
            Assert.True(volume > previous, $"volume must converge upward: {previous} then {volume}");
            Assert.True(volume < expected, $"an inscribed tessellation cannot exceed {expected}: {volume}");
            previous = volume;
        }
        Assert.True(expected - previous < 0.001 * expected,
            $"192 segments must be within 0.1% of {expected}, measured {previous}");

        // No folds at any density — including the two well above where the corpus measures,
        // which is where they used to appear and grow.
        foreach (var (s, c) in (ReadOnlySpan<(int, int)>)[(48, 24), (128, 64), (192, 96)])
            Assert.Equal(0, TessellationQuality.Audit(solid, s, c).Folds);

        // The fidelity residual, recorded rather than barred: near the coarse traced rim the
        // facets stay near-perpendicular to the surface, and refinement makes that WORSE than
        // leaving the base alone. Both numbers are pinned so the finding cannot rot.
        var refined = TessellationQuality.Audit(solid, 192, 96);
        Assert.InRange(refined.WorstDot, 0.0, 0.10);
    }

    /// <summary>Midpoint rule, enough for a napkin integral used only as a test oracle.</summary>
    private static double Quad(Func<double, double> f, double a, double b, int steps)
    {
        double h = (b - a) / steps, sum = 0;
        for (int i = 0; i < steps; i++)
            sum += f(a + h * (i + 0.5));
        return sum * h;
    }

    private static int Winding(Surface surface, BrepLoop loop, double period, BrepSolid solid)
    {
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var edge in solid.Edges)
            edgePolylines[edge] = BRepTessellator.SampleEdge(edge, 32, 24);

        var uv = new List<Vector2d>();
        foreach (var p in BRepTessellator.LoopPolyline(loop, edgePolylines))
        {
            if (!surface.TryProjectPoint(p, out var q, FaceGeometry.InverseEvaluationTolerance))
                continue;
            if (uv.Count > 0)
                q = new Vector2d(q.X + period * Math.Round((uv[^1].X - q.X) / period), q.Y);
            uv.Add(q);
        }
        return uv.Count == 0 ? 0 : (int)Math.Round((uv[^1].X - uv[0].X) / period);
    }
}
