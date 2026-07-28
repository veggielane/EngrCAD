using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The base-grid <see cref="Surface.TryProjectPoint"/> now refines from every LOCAL
/// minimum of the 17×17 seed grid and its neighbours, not just the single global best —
/// the same fix <c>SweptSurface</c> got (see <c>SweptSeedSelectionTests</c>), ported to
/// the 2D grid that serves <see cref="NurbsSurface"/>. The hostile shape is a fold: two
/// branches of the surface closer together than one seed interval, so the sampled
/// distance shows one broad minimum straddling both and single-seed Newton descends to
/// whichever branch is nearer in SAMPLES, which need not be the nearer branch in space.
/// </summary>
public class NurbsSeedSelectionTests
{
    /// <summary>A flattened hairpin: a degree-3 U-shape ~14 units deep whose two branches
    /// sit at most 0.08 apart — far under the arc length of one u seed interval — swept
    /// linearly in v. Measured on the single-seed implementation this fixture fails
    /// 80 of 205 round trips; the multi-seed selection passes all of them.</summary>
    private static NurbsSurface Hairpin()
    {
        // Asymmetric on purpose: with equal x-tangents the seed grid is mirror-symmetric
        // across the fold, and every wrong-branch seed then has a same-branch mirror at
        // the identical tangential offset minus the perpendicular penalty — the argmin
        // can never land on the wrong branch and the fixture tests nothing.
        var controlPoints = new Vector3d[4, 2];
        controlPoints[0, 0] = (0, 0, 0);
        controlPoints[1, 0] = (30, 0, 0);
        controlPoints[2, 0] = (8, 0.08, 0);
        controlPoints[3, 0] = (0, 0.08, 0);
        for (int i = 0; i < 4; i++)
            controlPoints[i, 1] = controlPoints[i, 0] + new Vector3d(0, 0, 2);
        return new NurbsSurface(3, 1, controlPoints, null,
            [0, 0, 0, 0, 1, 1, 1, 1], [0, 0, 1, 1]);
    }

    [Fact]
    public void FoldedSurface_RoundTripsEveryOnSurfacePoint()
    {
        var surface = Hairpin();
        int failures = 0;
        var worst = Vector2d.Zero;
        for (int i = 0; i <= 40; i++)
        {
            for (int j = 0; j <= 4; j++)
            {
                // Off-grid parameters, so query points never coincide with seed samples.
                double u = surface.DomainU.ParameterAt((i + 0.37) / 41.0);
                double v = surface.DomainV.ParameterAt(j / 4.0);
                var point = surface.PointAt(u, v);
                if (!surface.TryProjectPoint(point, out var uv, 1e-8)
                    || surface.PointAt(uv.X, uv.Y).DistanceTo(point) > 1e-8)
                {
                    failures++;
                    worst = new Vector2d(u, v);
                }
            }
        }
        Assert.True(failures == 0,
            $"{failures}/205 on-surface points failed to round-trip (e.g. at uv {worst}); " +
            "the multi-seed selection should reach the correct branch of the fold.");
    }

    [Fact]
    public void SimpleSurface_ProjectionIsUnchangedFastPath()
    {
        // A gently curved single-sheet patch: the global-best seed converges, so the
        // multi-seed machinery must never even run (the fast path is the old path).
        var controlPoints = new Vector3d[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
                controlPoints[i, j] = (i * 2.0, j * 2.0, Math.Sin(i) * 0.5 + j * 0.1);
        }
        var surface = new NurbsSurface(2, 2, controlPoints, null,
            [0, 0, 0, 1, 1, 1], [0, 0, 0, 1, 1, 1]);
        for (int i = 0; i <= 8; i++)
        {
            for (int j = 0; j <= 8; j++)
            {
                var point = surface.PointAt(
                    surface.DomainU.ParameterAt(i / 8.0), surface.DomainV.ParameterAt(j / 8.0));
                Assert.True(surface.TryProjectPoint(point, out var uv, 1e-8));
                Assert.True(surface.PointAt(uv.X, uv.Y).DistanceTo(point) < 1e-8);
            }
        }
    }
}
