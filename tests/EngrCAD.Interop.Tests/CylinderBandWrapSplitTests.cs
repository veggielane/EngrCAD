using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Wrap-splitting a plain <c>CylinderSurface</c> band. Bores built by <c>Shape.Drill</c>
/// and <c>SolidFactory.Extrude</c> arrive as extruded circles, so this path went
/// unexercised and refused outright — but a hand-built or imported cylinder is an
/// ordinary thing to cut, and the periodic-band machinery is surface-generic in
/// principle.
/// <para><b>The one structural difference from the extruded and revolved cases</b>: those
/// tessellate DOMAIN-driven, so shortening a band's loops means shortening its surface
/// too. A cylinder band tessellates from its RING LOOPS, so both sub-bands keep the whole
/// cylinder and there is nothing to trim. And because a cylinder is unbounded in v, the
/// "the cut coincides with a boundary ring" test has to read the rings rather than the
/// surface domain.</para>
/// </summary>
public class CylinderBandWrapSplitTests
{
    /// <summary>A cylinder whose side really is a <see cref="CylinderSurface"/> band —
    /// built here rather than via a factory so the pose is free. Internal because the
    /// tessellation corpus builds its raw-cylinder member from the same construction:
    /// the <c>Shape</c> API lowers <c>Shape.Cylinder</c> to an EXTRUDED circle (so an
    /// affine placement bakes in exactly), which means a plain <see cref="CylinderSurface"/>
    /// only ever arrives from <see cref="SolidFactory.MakeCylinder"/>, from STEP, or from
    /// code like this — and the corpus must cover it anyway.</summary>
    internal static BrepSolid Cylinder(in Frame3d f, double radius, double height)
    {
        var full = new Interval(0, 2 * Math.PI);
        var bottomCircle = new Circle3d(f.Origin, f.X, f.Y, radius);
        var topCircle = new Circle3d(f.Origin + f.Z * height, f.X, f.Y, radius);
        var b = new BrepVertex(bottomCircle.PointAt(0));
        var t = new BrepVertex(topCircle.PointAt(0));
        var bottomEdge = new BrepEdge(bottomCircle, full, b, b);
        var topEdge = new BrepEdge(topCircle, full, t, t);
        return new BrepSolid(
        [
            new BrepShell(
            [
                new BrepFace(
                    new CylinderSurface(f.Origin, f.X, f.Y, radius),
                    [
                        new BrepLoop([new BrepCoedge(bottomEdge, sameSense: true)]),
                        new BrepLoop([new BrepCoedge(topEdge, sameSense: false)]),
                    ]),
                new BrepFace(
                    new PlaneSurface(f.Origin + f.Z * height, f.X, f.Y),
                    [new BrepLoop([new BrepCoedge(topEdge, sameSense: true)])]),
                new BrepFace(
                    new PlaneSurface(f.Origin, f.Y, f.X),
                    [new BrepLoop([new BrepCoedge(bottomEdge, sameSense: false)])]),
            ]),
        ]);
    }

    private static readonly Frame3d Upright =
        Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>The tessellator's inscribed n-gon area — the discrete truth a band covers.</summary>
    private static double NgonArea(int n, double r) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    [Theory]
    [InlineData(48)]
    [InlineData(96)]
    public void APlaneCutAcrossACylinderBandSplitsItExactly(int segments)
    {
        // The canonical constant-v wrap cut: a box lops the top off a cylinder, and its
        // bottom plane meets the band in a full circle. Exact as an identity against the
        // n-gon prism, at every density — the whole point of splitting rather than
        // re-tracing.
        var body = Cylinder(Upright, 5, 20);
        var lid = SolidFactory.MakeBox(new Aabb((-10, -10, 15), (10, 10, 25)));

        var lopped = BrepBoolean.Difference(body, lid);
        lopped.Validate();
        Assert.True(lopped.SatisfiesEulerFormula(genus: 0));
        // Still a cylinder band plus two caps: the split consumed the upper sub-band.
        Assert.Equal(3, lopped.Faces.Count());

        var mesh = BRepTessellator.Tessellate(lopped, segments, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(NgonArea(segments, 5) * 15, mesh.Volume(), 6);
    }

    [Fact]
    public void BothSubBandsKeepTheWholeCylinder()
    {
        // A slab through the middle leaves a band above and below, and the surfaces are
        // NOT shortened — a cylinder's tessellation reads its loops, so shortening would
        // be meaningless work. (The extruded and revolved paths must shorten theirs.)
        var body = Cylinder(Upright, 4, 20);
        var slab = SolidFactory.MakeBox(new Aabb((-10, -10, 8), (10, 10, 12)));

        var cut = BrepBoolean.Difference(body, slab);
        cut.Validate();
        var bands = cut.Faces.Where(f => f.Surface is CylinderSurface).ToList();
        Assert.Equal(2, bands.Count);
        foreach (var band in bands)
        {
            var cylinder = Assert.IsType<CylinderSurface>(band.Surface);
            Assert.Equal(4, cylinder.Radius, 12);
            Assert.Equal(0, cylinder.Origin.DistanceTo(Vector3d.Zero), 12);
        }

        var mesh = BRepTessellator.Tessellate(cut, 96, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(NgonArea(96, 4) * 16, mesh.Volume(), 6);
    }

    [Fact]
    public void ACutFlushWithARingLeavesTheBandAlone()
    {
        // The check that cannot be made against the surface DOMAIN, a cylinder being
        // unbounded in v: a cut exactly on the top ring must be recognized as a boundary
        // and split nothing (which the boolean then reports as an unchanged body or a
        // coplanar refusal — either way it must not fabricate a zero-height sub-band).
        var body = Cylinder(Upright, 5, 20);
        var faces = FaceSplitter.SplitByCurve(
            body.Faces.First(f => f.Surface is CylinderSurface),
            new Circle3d((0, 0, 20), Vector3d.UnitX, Vector3d.UnitY, 5));
        Assert.Single(faces);
    }

    /// <summary>
    /// The volume a cylinder of radius <paramref name="r"/> bored straight through a
    /// cylinder of radius <paramref name="big"/> removes, their axes perpendicular and
    /// INTERSECTING. Each point of the small cylinder's disc section at offset a from its
    /// own axis plane sits on a chord of length 2*sqrt(R^2 - a^2), so
    /// V = 4 * integral over [-r, r] of sqrt((R^2 - a^2)(r^2 - a^2)) da; substituting
    /// a = r*sin t turns it into 4r^2 * integral over one PERIOD of the smooth periodic
    /// integrand cos^2 t * sqrt(R^2 - r^2 sin^2 t), where the midpoint rule converges
    /// EXPONENTIALLY — so 512 nodes is the closed form to machine precision rather than a
    /// quadrature with an error budget. (It is an elliptic integral: there is no
    /// elementary antiderivative, which is exactly why the reference is written this way
    /// instead of being copied from a table.)
    /// </summary>
    private static double CrossBoreVolume(double big, double r)
    {
        const int nodes = 512;
        double sum = 0;
        for (int k = 0; k < nodes; k++)
        {
            double t = -Math.PI / 2 + Math.PI * (k + 0.5) / nodes;
            double c = Math.Cos(t), s = Math.Sin(t);
            sum += c * c * Math.Sqrt(big * big - r * r * s * s);
        }
        return 4 * r * r * sum * Math.PI / nodes;
    }

    [Fact]
    public void ACrossDrillPiercingACylinderWallIsExactAndConverges()
    {
        // A cross-drill piercing a cylinder's wall makes a wrapping cut whose v VARIES,
        // so both sub-bands keep the whole surface and rely on the trimmed path. This
        // used to be REFUSED, on the reading that trimmed cylindrical tessellation with
        // wrapping loops did not exist. It did: the defect was one stage later, in the
        // tessellator's ROUTING — a face with two closed-edge loops went to the
        // index-pairing ring path whatever those loops were, and two independently traced
        // cuts have unrelated sample phases, so 18 of the tool band's 40 quads faced
        // inward (worst normal agreement -0.0000) and the weld reported a duplicated
        // directed edge. See BRepTessellator.IsRingPairedBand.
        var body = Cylinder(Upright, 5, 20);
        var across = Frame3d.FromOrthonormal((0, -15, 10), Vector3d.UnitZ, Vector3d.UnitX);
        var tool = Cylinder(across, 1.5, 30);

        var cut = BrepBoolean.Difference(body, tool);
        cut.Validate();
        Assert.True(cut.SatisfiesEulerFormula(genus: 1));

        // Both cylinders are now trimmed by wrapping cuts: the body's band carries the two
        // breakout holes, the tool's is a band between the two of them.
        var bands = cut.Faces.Where(f => f.Surface is CylinderSurface).ToList();
        Assert.Equal(2, bands.Count);
        Assert.Equal(4, bands.Single(f => !f.IsReversed).Loops.Count);   // 2 rings + 2 holes
        Assert.Equal(2, bands.Single(f => f.IsReversed).Loops.Count);    // both wrapping cuts

        double exact = Math.PI * 25 * 20 - CrossBoreVolume(5, 1.5);
        double coarse = BRepTessellator.Tessellate(cut, 32, 24).Volume() - exact;
        double medium = BRepTessellator.Tessellate(cut, 64, 48).Volume() - exact;
        double fine = BRepTessellator.Tessellate(cut, 128, 96).Volume() - exact;

        // Inscribed on both cylinders, so the error is one-sided and chordal.
        Assert.True(coarse < 0 && medium < 0 && fine < 0,
            $"expected a one-sided inscribed deficit, got {coarse:E3} / {medium:E3} / {fine:E3}");
        Assert.True(Math.Abs(coarse / exact) < 1e-2, $"coarse relative error {coarse / exact:E2}");
        // The tool's rim is a marching-tracer polyline baked at boolean time, so its
        // sample count does NOT refine with segmentsPerCircle and puts a fixed floor under
        // the total — the same effect the corpus convergence gate allows 2.5 for. That
        // floor is also why this sharp version is not a corpus member: the tracer steps by
        // ARC LENGTH, so this Ø3 rim gets 40 samples per turn (du = 0.157) where the
        // corpus's Ø10 one gets 66 (du = 0.095), and facet-vs-surface agreement therefore
        // reads 0.974 / 0.949 / 0.565 at 32/96/192 against 0.858 / 0.9995 / 0.9998. No
        // folds at any density either way — the volume is what this fixture is for.
        double first = coarse / medium, second = medium / fine;
        Assert.True(first > 2.5 && second > 2.5,
            $"expected near-quadratic convergence, got ratios {first:F2} then {second:F2} " +
            $"(errors {coarse:E3} / {medium:E3} / {fine:E3})");
    }

    [Fact]
    public void ATiltedPlaneCutAcrossACylinderBandConverges()
    {
        // The OTHER wrapping cut whose v varies, and a different carrier: a tilted plane
        // meets the band in an exact Ellipse3d rather than a traced polyline, so this
        // covers the analytic half of the same routing change. The remaining solid is a
        // cylinder with a slanted top, whose exact volume is pi*R^2 times the height ON
        // THE AXIS — the tilt contributes the section's first moment about the axis,
        // which is zero.
        const double tilt = 0.3, height = 12;
        var plane = SketchPlane.At(
            (0, 0, height), (Math.Cos(tilt), 0, -Math.Sin(tilt)), Vector3d.UnitY);
        var lid = Shape.Extrude(Sketch.Rectangle(60, 60), 30, plane).ToBrep();
        double exact = Math.PI * 36 * height;

        double Error(int segments)
        {
            var lopped = BrepBoolean.Difference(Cylinder(Upright, 6, 20), lid.Clone());
            lopped.Validate();
            Assert.True(lopped.SatisfiesEulerFormula(genus: 0));
            var mesh = BRepTessellator.Tessellate(lopped, segments, segments / 2);
            Assert.True(mesh.IsClosed);
            return mesh.Volume() - exact;
        }

        double coarse = Error(32), medium = Error(64), fine = Error(128);
        Assert.True(coarse < 0 && medium < 0 && fine < 0,
            $"expected a one-sided inscribed deficit, got {coarse:E3} / {medium:E3} / {fine:E3}");
        double first = coarse / medium, second = medium / fine;
        Assert.True(first > 2.5 && second > 2.5,
            $"expected near-quadratic convergence, got ratios {first:F2} then {second:F2} " +
            $"(errors {coarse:E3} / {medium:E3} / {fine:E3})");
    }
}
