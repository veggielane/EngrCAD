using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Inverse evaluation on the two swept surfaces that carry the exact overrides:
/// <see cref="ExtrudedSurface"/> (the section-by-section walls of every sketch pocket,
/// slot and text glyph) and <see cref="RevolvedSurface"/> (every drilled hole tool).
/// Both collapse the generic 2D grid-and-Gauss-Newton search onto ONE dimension —
/// the generator parameter — with the other parameter in closed form. These are the
/// inner loop of face pullback, so the contract they must keep is exactly the base
/// class's: on-surface points round-trip, off-surface points are rejected.
/// </summary>
public class SweptSurfaceProjectionTests
{
    /// <summary>A curved, non-planar-in-the-sweep-sense generator: a quadratic Bezier
    /// like the ones TrueType glyph outlines are made of.</summary>
    private static NurbsCurve GlyphLikeArc() => new(
        2, [(0, 0, 0), (1.5, 2.2, 0), (3, 0.4, 0)], null, [0, 0, 0, 1, 1, 1]);

    [Fact]
    public void Extruded_RoundTripsEveryParameterPair()
    {
        // A SHEARED extrusion (direction not perpendicular to the generator's plane):
        // the reduction removes the direction component, so shear must not matter.
        var surface = new ExtrudedSurface(GlyphLikeArc(), (0.4, -0.3, 5));

        var rng = new Random(7);
        for (int i = 0; i < 40; i++)
        {
            double u = rng.NextDouble();
            double v = rng.NextDouble();
            var point = surface.PointAt(u, v);
            Assert.True(surface.TryProjectPoint(point, out var uv, 1e-9));
            // The contract is positional (the 1e-9 residual the call was asked for);
            // the parameters follow to within that residual divided by the local speed.
            Assert.True(surface.PointAt(uv.X, uv.Y).AreEqual(point, new Tolerance(1e-9, 1e-9)));
            Assert.Equal(u, uv.X, 8);
            Assert.Equal(v, uv.Y, 8);
        }
    }

    [Fact]
    public void Extruded_RejectsPointsOffTheSurface()
    {
        var surface = new ExtrudedSurface(GlyphLikeArc(), (0, 0, 5));

        // Beside the wall (the generator's own normal direction).
        Assert.False(surface.TryProjectPoint(surface.PointAt(0.5, 0.5) + new Vector3d(0, 0.7, 0), out _, 1e-9));
        // Past the extrusion's ends in v.
        Assert.False(surface.TryProjectPoint(surface.PointAt(0.5, 0) - new Vector3d(0, 0, 1), out _, 1e-9));
        Assert.False(surface.TryProjectPoint(surface.PointAt(0.5, 1) + new Vector3d(0, 0, 1), out _, 1e-9));
        // Past the generator's ends in u (still on the carrier plane of the end tangent).
        Assert.False(surface.TryProjectPoint((-2, -2, 2.5), out _, 1e-9));
    }

    [Fact]
    public void Extruded_StraightGeneratorIsExactToTheLastBit()
    {
        // The overwhelmingly common case — a pocket wall — must be exact, not merely
        // converged: adjacent profile segments hand corner points over bit-for-bit.
        var surface = new ExtrudedSurface(new Line3d((1, 1, 0), (4, -2, 0)), (0, 0, 3));

        Assert.True(surface.TryProjectPoint(surface.PointAt(0.25, 0.75), out var uv, 1e-12));
        Assert.Equal(0.25, uv.X, 12);
        Assert.Equal(0.75, uv.Y, 12);
    }

    [Fact]
    public void Extruded_ClosedGeneratorWrapsInsteadOfClampingAtTheSeam()
    {
        // A closed generator makes u periodic: the solve must be able to walk across
        // the seam rather than pin at the domain end.
        var surface = new ExtrudedSurface(
            new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 2), (0, 0, 4));

        foreach (double u in new[] { 0.01, 1.0, Math.PI, 2 * Math.PI - 0.01 })
        {
            var point = surface.PointAt(u, 0.3);
            Assert.True(surface.TryProjectPoint(point, out var uv, 1e-9));
            Assert.True(surface.PointAt(uv.X, uv.Y).AreEqual(point, new Tolerance(1e-9, 1e-9)));
        }
    }

    [Fact]
    public void Revolved_RoundTripsEveryParameterPair()
    {
        // A counterbore-like generator: stepped radius, off-origin axis, tilted axis.
        var axisOrigin = new Vector3d(1, -2, 0.5);
        var axis = new Vector3d(0.2, 0.3, 1).Normalized();
        var generator = NurbsCurve.InterpolatePoints(
        [
            axisOrigin + axis * 0 + Vector3d.UnitY * 3.0,
            axisOrigin + axis * 1 + Vector3d.UnitY * 2.2,
            axisOrigin + axis * 2 + Vector3d.UnitY * 2.6,
            axisOrigin + axis * 3 + Vector3d.UnitY * 1.4,
        ]);
        var surface = new RevolvedSurface(generator, axisOrigin, axis);

        var rng = new Random(13);
        for (int i = 0; i < 40; i++)
        {
            double u = rng.NextDouble() * 2 * Math.PI;
            double v = generator.Domain.ParameterAt(rng.NextDouble());
            var point = surface.PointAt(u, v);
            Assert.True(surface.TryProjectPoint(point, out var uv, 1e-9));
            Assert.True(surface.PointAt(uv.X, uv.Y).AreEqual(point, new Tolerance(1e-8, 1e-8)));
        }
    }

    [Fact]
    public void Revolved_PartialTurnKeepsItsAngularDomainAndRejectsPointsOutsideIt()
    {
        var surface = new RevolvedSurface(
            new Line3d((2, 0, 0), (2, 0, 4)), Vector3d.Zero, Vector3d.UnitZ, Math.PI / 2);

        var inside = surface.PointAt(Math.PI / 4, 0.5);
        Assert.True(surface.TryProjectPoint(inside, out var uv, 1e-9));
        Assert.Equal(Math.PI / 4, uv.X, 9);
        Assert.InRange(uv.X, 0, Math.PI / 2);

        // The same radius and height, but a quarter turn past the swept band.
        Assert.False(surface.TryProjectPoint((0, -2, 2), out _, 1e-9));
        // Correct angle, wrong radius.
        Assert.False(surface.TryProjectPoint(new Vector3d(1, 1, 2), out _, 1e-9));
    }

    [Fact]
    public void Revolved_ConeSideRoundTripsRightUpToThePole()
    {
        // MakeCone's side: a slanted generator reaching the axis. The pole itself has no
        // azimuth (the base grid search answers there); everything above it must project.
        var surface = new RevolvedSurface(
            new Line3d((0, 0, 4), (2, 0, 0)), Vector3d.Zero, Vector3d.UnitZ);

        foreach (double v in new[] { 0.02, 0.25, 0.5, 0.9, 1.0 })
        {
            var point = surface.PointAt(2.1, v);
            Assert.True(surface.TryProjectPoint(point, out var uv, 1e-9), $"v = {v}");
            Assert.True(surface.PointAt(uv.X, uv.Y).AreEqual(point, new Tolerance(1e-8, 1e-8)));
        }
    }
}
