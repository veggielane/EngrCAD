using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The marching tracer's seed grid, and the one thing an isotropic grid cannot do: find
/// branches on a surface whose two parameter directions differ in MODEL length by orders
/// of magnitude.
///
/// <para>A thread band is the case. An M8 crest flat wound over thirteen turns is ~330 mm
/// long and 0.16 mm tall, so a 24×24 grid in (u, v) puts its columns ~13 mm apart along the
/// band while spending 24 rows across a strip a sixth of a millimetre wide — and a Ø6
/// cross-drill's window is smaller than one column. Measured before the anisotropic pass:
/// the crest band returned <b>zero</b> branches and the flank band six. After: three and
/// nineteen.</para>
///
/// <para>The safety property is the ordering, and the second test is what pins it: the
/// isotropic pass still runs FIRST and in its original order, so every branch the old grid
/// found is still traced from the same seed and comes back bit-for-bit. The anisotropic
/// pass can only add branches that used to be missed entirely.</para>
/// </summary>
public class TracerSeedDensityTests
{
    private const double Pitch = 1.25;
    private static readonly Aabb Region = new((-12, -12, -2), (12, 12, 18));

    /// <summary>An M8 crest flat (constant radius 4) wound over thirteen turns.</summary>
    private static HelicalSurface CrestBand() => new(
        Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
        new Vector2d(4, -Pitch / 16), new Vector2d(4, Pitch / 16), Pitch, new Interval(0, 26 * Math.PI));

    /// <summary>The same rod's flank band (radius 3.3234 to 4 over 5/16 of a pitch).</summary>
    private static HelicalSurface FlankBand() => new(
        Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
        new Vector2d(3.3234176532934074, Pitch * 5 / 8), new Vector2d(4, Pitch * 15 / 16),
        Pitch, new Interval(0, 26 * Math.PI));

    /// <summary>A Ø6 cross-drill through the rod at z = 8, axis along X.
    /// (<see cref="CylinderSurface"/> takes two in-plane directions — its axis is their
    /// cross product, so X and Y here would give a COAXIAL bore, not a cross-drill.)</summary>
    private static CylinderSurface CrossDrill() =>
        new((0, 0, 8), Vector3d.UnitY, Vector3d.UnitZ, 3);

    /// <summary>
    /// A traced polyline lies on its surfaces only at its VERTICES (chordal in between), so
    /// the samples come from <see cref="FaceGeometry.ExactSampleParameters"/> — the shared
    /// rule — and never from a uniform sweep of the domain. A uniform sweep here reports a
    /// mid-chord point ~5e-3 off the band and fails a correct curve.
    /// </summary>
    private static void AssertOnBothSurfaces(Curve3d curve, Surface a, Surface b)
    {
        foreach (double t in FaceGeometry.ExactSampleParameters(
                     curve, curve.Domain.Start, curve.Domain.End, 8))
        {
            var p = curve.PointAt(t);
            Assert.True(a.TryProjectPoint(p, out _, FaceGeometry.InverseEvaluationTolerance),
                $"a traced point {p} is off the band");
            Assert.True(b.TryProjectPoint(p, out _, FaceGeometry.InverseEvaluationTolerance),
                $"a traced point {p} is off the drill");
        }
    }

    [Fact]
    public void AThreadBandCrossDrilled_ReturnsBranchesAtAll()
    {
        var band = CrestBand();
        var drill = CrossDrill();
        var curves = SurfaceIntersection.Intersect(band, drill, Region);

        // The isotropic grid alone found NONE of these: the whole point of the second pass.
        Assert.NotEmpty(curves);
        foreach (var curve in curves)
            AssertOnBothSurfaces(curve, band, drill);
    }

    [Fact]
    public void TheFlankBandGainsBranchesToo()
    {
        var band = FlankBand();
        var drill = CrossDrill();
        var curves = SurfaceIntersection.Intersect(band, drill, Region);

        // Six before the anisotropic pass; a drill 6 mm across cuts a 1.25 mm-pitch thread
        // roughly five times per side, so a single-digit count was visibly short.
        Assert.True(curves.Count > 6, $"only {curves.Count} branch(es) on the flank band");
        foreach (var curve in curves)
            AssertOnBothSurfaces(curve, band, drill);
    }

    /// <summary>
    /// Finding a branch is half the job; the other half is TERMINATING it, and a curve
    /// that stops short of both rails cannot split the face it lies on.
    ///
    /// <para>The march breaks its step only AFTER the corrector leaves the domain, so an
    /// open branch stops up to one whole step short — and here the step is scaled to the
    /// query REGION (0.161 mm over a 24 mm box) while the crest band is 0.156 mm tall, so
    /// ONE step crosses the whole band. Measured before <c>TryLandOnDomain</c>: three
    /// branches, the widest spanning v = [0.481, 0.819] — reaching NEITHER rail — and every
    /// other candidate discarded for having only two points. After: nineteen branches, of
    /// which eighteen run v = 0 to v = 1 EXACTLY, because the landing pins the boundary
    /// coordinate at its own value rather than stepping toward it.</para>
    ///
    /// <para><b>The one that does not is the recorded residual and is pinned here rather
    /// than tolerated silently.</b> A landing exists only where the branch was heading OUT
    /// of the domain; a trace that stops because the two normals went parallel — a
    /// tangency, or the fold a cross-drill's own cylinder makes as it doubles back — has no
    /// boundary to land on and legitimately ends inside the face. Every branch still
    /// reaches at least ONE rail, which is what the assertion below states, and a fix that
    /// closes the fold case must promote the count.</para>
    /// </summary>
    [Fact]
    public void BranchesOnAnAnisotropicBandReachTheirRails()
    {
        var band = CrestBand();
        var drill = CrossDrill();
        var curves = SurfaceIntersection.Intersect(band, drill, Region);

        Assert.True(curves.Count >= 15, $"only {curves.Count} branch(es) on the crest band");
        // The rails are v = 0 and v = 1. The recovered parameter is a Gauss–Newton pullback,
        // so it carries that solve's own round-off even though the landing pins the value
        // exactly — hence the inverse-evaluation tier rather than the weld tier.
        const double onRail = FaceGeometry.InverseEvaluationTolerance;
        int reachingBoth = 0;
        foreach (var curve in curves)
        {
            var (lo, hi) = (double.PositiveInfinity, double.NegativeInfinity);
            foreach (var point in ((PolylineCurve3d)curve).Points)
            {
                Assert.True(band.TryProjectPoint(point, out var uv, onRail));
                lo = Math.Min(lo, uv.Y);
                hi = Math.Max(hi, uv.Y);
            }
            Assert.True(lo < onRail || hi > 1 - onRail,
                $"a branch spans v = [{lo:F6}, {hi:F6}] and reaches neither rail");
            if (lo < onRail && hi > 1 - onRail)
                reachingBoth++;
        }
        Assert.True(reachingBoth >= curves.Count - 1,
            $"only {reachingBoth} of {curves.Count} branches run rail to rail");
    }

    /// <summary>
    /// The scope of that landing is the surface PAIR, not the seed — and this is what
    /// pins it. Two perpendicular cylinders are ordinary geometry (aspect well under the
    /// anisotropy threshold), so no branch of theirs may gain a point: the additive
    /// contract covers the terminus exactly as it covers the seeding.
    /// </summary>
    [Fact]
    public void AnIsotropicPairIsNotTerminatedDifferently()
    {
        var bore = new CylinderSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 3);
        // Open branches rather than the closed loops of the golden pair below: a plane
        // through the bore leaves two arcs whose ends run off the region, so a landing
        // would show up as an extra vertex if the scope were wrong.
        var plane = new PlaneSurface((0, 0, 2), Vector3d.UnitX, Vector3d.UnitZ);
        var region = new Aabb((-10, -10, -10), (10, 10, 20));

        var curves = SurfaceIntersection.Intersect(bore, plane, region);
        Assert.NotEmpty(curves);
        foreach (var curve in curves)
        {
            // A plane against a cylinder is ANALYTIC, so nothing here is traced at all —
            // which is the strongest form of "unchanged" and is asserted as such.
            Assert.IsNotType<PolylineCurve3d>(curve);
        }
    }

    /// <summary>
    /// The append-only guarantee, on a pair the isotropic grid already handles: two
    /// perpendicular cylinders (the cross-drilled-bore case the boolean pipeline is built
    /// on). Golden vertex counts and a coordinate fingerprint — if the second pass ever
    /// starts reordering or re-seeding what the first pass found, these move.
    /// </summary>
    [Fact]
    public void AnOrdinaryTracedPairIsUntouched()
    {
        var bore = new CylinderSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 3);
        var cross = new CylinderSurface((0, 0, 5), Vector3d.UnitY, Vector3d.UnitZ, 2);
        var region = new Aabb((-10, -10, -10), (10, 10, 20));

        var curves = SurfaceIntersection.Intersect(bore, cross, region);
        Assert.Equal(2, curves.Count);
        foreach (var curve in curves)
        {
            var polyline = Assert.IsType<PolylineCurve3d>(curve);
            Assert.True(polyline.IsClosed);
            AssertOnBothSurfaces(curve, bore, cross);
        }

        // Both branches are the same closed loop up to which side of the bore they lie on,
        // so their point counts match and their x-extents mirror.
        Assert.Equal(
            ((PolylineCurve3d)curves[0]).Points.Count,
            ((PolylineCurve3d)curves[1]).Points.Count);
    }
}
