using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// <c>BrepBoolean.ProbePoint</c> decides "this fragment is a band" by whether its outer loop
/// WRAPS the surface's periodic direction, and the band path then probes halfway toward the
/// surface's own v domain edge. A loop that merely SPANS most of the period without wrapping
/// — a contractible facet, of which a threaded rod's end-chamfer facet (272°) is the case that
/// found this — sends the probe clean outside the fragment, and the boolean classifies the
/// facet away. The decision is net u drift; see <c>FaceGeometry.LoopWrapsPeriod</c>.
/// </summary>
public class ProbePointWrapTests
{
    /// <summary>
    /// A contractible facet spanning <paramref name="degrees"/> of a closed extruded band,
    /// confined to v ∈ [0.25, 0.5] so a probe walking toward a v domain edge leaves it.
    /// </summary>
    private static BrepFace Facet(double degrees)
    {
        const double radius = 10, height = 20;
        double span = degrees * Math.PI / 180;
        var generator = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, radius);
        var surface = new ExtrudedSurface(generator, new Vector3d(0, 0, height));

        Circle3d RingAt(double v) =>
            new(new Vector3d(0, 0, height * v), Vector3d.UnitX, Vector3d.UnitY, radius);
        var bottom = RingAt(0.25);
        var top = RingAt(0.5);

        var a = new BrepVertex(surface.PointAt(0, 0.25));
        var b = new BrepVertex(surface.PointAt(span, 0.25));
        var c = new BrepVertex(surface.PointAt(span, 0.5));
        var d = new BrepVertex(surface.PointAt(0, 0.5));

        var bottomEdge = new BrepEdge(bottom, new Interval(0, span), a, b);
        var rightEdge = new BrepEdge(new Line3d(b.Position, c.Position), Interval.Unit, b, c);
        var topEdge = new BrepEdge(top, new Interval(0, span), d, c);
        var leftEdge = new BrepEdge(new Line3d(d.Position, a.Position), Interval.Unit, d, a);

        var loop = new BrepLoop(
        [
            new BrepCoedge(bottomEdge, true),
            new BrepCoedge(rightEdge, true),
            new BrepCoedge(topEdge, false),
            new BrepCoedge(leftEdge, true),
        ]);
        return new BrepFace(surface, [loop], isReversed: false);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(200)]
    [InlineData(272)]   // the measured chamfer-facet span
    [InlineData(350)]
    public void ContractibleFacet_IsProbedInsideItself(double degrees)
    {
        var face = Facet(degrees);

        // The fixture must actually carry the configuration: one loop, spanning more than
        // three quarters of the period once past 270 degrees, and NOT wrapping.
        var pulled = FaceGeometry.PullLoops(face)[0];
        double period = FaceGeometry.PeriodU(face.Surface);
        Assert.Equal(degrees > 270, pulled.Max(p => p.X) - pulled.Min(p => p.X) > 0.75 * period);
        Assert.False(FaceGeometry.LoopWrapsPeriod(pulled, period));

        var probe = BrepBoolean.ProbePoint(face);
        Assert.True(FaceGeometry.Contains(face, probe),
            $"probe {probe} is not inside the {degrees}-degree facet");

        // ...and specifically it stays inside the facet's own v band, where the band path
        // (halfway to the surface's v domain edge, i.e. v = 0.6875) would not.
        Assert.True(face.Surface.TryProjectPoint(probe, out var uv, FaceGeometry.InverseEvaluationTolerance));
        Assert.InRange(uv.Y, 0.25, 0.5);
    }

    // ---- the pole rule, at face level ----

    private const double CapRadius = 3, ToolHeight = 10;

    /// <summary>An axis-touching revolve's flat bottom, cut by the exact chord at
    /// perpendicular <paramref name="offset"/> — the shape a blind drill's cap takes when the
    /// face it breaks out of slices it. Returns the fragment containing the POLE, which is
    /// the one whose single loop WRAPS: a loop encircling the pole must, since in parameter
    /// space the pole is the whole v = 0 line.</summary>
    private static BrepFace CutCap(double offset, double azimuth)
    {
        var frame = Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
        var profile = Profile.FromLoop(
            [new(0, 0), new(CapRadius, 0), new(CapRadius, ToolHeight), new(0, ToolHeight)], frame);
        var tool = SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ);
        var cap = tool.Faces.First(f =>
            f.Surface is RevolvedSurface && Math.Abs(f.Bounds().Center.Z) < 1e-9);

        double half = Math.Sqrt(CapRadius * CapRadius - offset * offset);
        var n = new Vector3d(Math.Cos(azimuth), Math.Sin(azimuth), 0);
        var t = new Vector3d(-Math.Sin(azimuth), Math.Cos(azimuth), 0);
        var pieces = FaceSplitter.SplitByCurve(cap, new Line3d(n * offset - t * half, n * offset + t * half));
        Assert.Equal(2, pieces.Count);
        return pieces.Single(p => FaceGeometry.LoopWrapsPeriod(
            FaceGeometry.PullLoops(p)[0], FaceGeometry.PeriodU(p.Surface)));
    }

    /// <summary>
    /// <c>ProbePoint</c>'s pole path, measured at FACE level rather than only end to end,
    /// which is what a bare pole cap declining to split by a chord used to make impossible.
    ///
    /// <para>A single loop that WRAPS the period separates the pole from everything else, so
    /// the face is the pole's side and every v strictly between the pole and the loop is
    /// inside at every u — which is why the probe may skip the parity check. It must read
    /// the loop's CLOSEST APPROACH to the pole, though, and here that is a closed form: the
    /// closest point of a chord at perpendicular offset d is at radius exactly d, so the
    /// probe lands at radius exactly d/2. Asserting the VALUE pins the rule; asserting only
    /// "inside" would pass for any rule that happens to land in the major segment.</para>
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, Math.PI)]
    [InlineData(2.9, -0.7)]
    public void ACutPoleCap_IsProbedHalfwayToItsPole(double offset, double azimuth)
    {
        var face = CutCap(offset, azimuth);
        var probe = BrepBoolean.ProbePoint(face);

        Assert.True(FaceGeometry.Contains(face, probe), $"probe {probe} is outside the cut cap");
        Assert.Equal(offset / 2, new Vector3d(probe.X, probe.Y, 0).Length, 12);
        Assert.Equal(0.0, probe.Z, 12);
    }

    /// <summary>
    /// The fixture carries the configuration, which for this rule means the loop is NOT
    /// level: measuring it by its AVERAGE v instead of its closest approach puts the probe
    /// somewhere the face does not reach. That is exactly what cracked a blind drill breaking
    /// out of a plate's top face — the average landed 0.106 above the plate — and without
    /// this row the value above could be satisfied by an average that happened to agree.
    /// </summary>
    [Fact]
    public void MeasuringTheCutCapsLoopByItsAVERAGE_ProbesOutsideIt()
    {
        var face = CutCap(1.0, Math.PI);
        var loop = FaceGeometry.PullLoops(face)[0];
        var average = face.Surface.PointAt(loop.Average(p => p.X), loop.Average(p => p.Y) / 2);

        Assert.False(FaceGeometry.Contains(face, average),
            $"the average-v probe {average} is inside after all — the fixture no longer carries the case");
        Assert.True(FaceGeometry.Contains(face, BrepBoolean.ProbePoint(face)));
    }
}
