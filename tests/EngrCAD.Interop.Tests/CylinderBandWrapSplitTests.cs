using EngrCAD.BRep;
using EngrCAD.Core;
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
    /// built here rather than via a factory so the pose is free.</summary>
    private static BrepSolid Cylinder(in Frame3d f, double radius, double height)
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

    [Fact]
    public void ANonPlanarWrappingCutIsRefusedByName()
    {
        // A cross-drill piercing a cylinder's wall makes a wrapping cut whose v VARIES.
        // Its sub-bands would keep the whole surface and need trimmed cylindrical
        // tessellation with wrapping loops, which does not exist — so it refuses here
        // rather than surfacing three stages later as a non-manifold mesh edge.
        var body = Cylinder(Upright, 5, 20);
        var across = Frame3d.FromOrthonormal((0, -15, 10), Vector3d.UnitZ, Vector3d.UnitX);
        var tool = Cylinder(across, 1.5, 30);

        var error = Assert.Throws<NotSupportedException>(() => BrepBoolean.Difference(body, tool));
        Assert.Contains("CylinderSurface", error.Message);
        Assert.Contains("extruded circle", error.Message);
    }
}
