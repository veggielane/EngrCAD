using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Passive (non-design) regions — material pinned SOLID (it must stay) or VOID (it must go),
/// verified by identities and by the feasible-set argument rather than by a picture.
///
/// <para><b>Two claims carry it.</b> Pinning is EXACT: with no filter in the way, a pinned-solid
/// element ends at density 1 and a pinned-void one at the floor, at every element, whatever the
/// optimiser would otherwise have done — and the volume constraint still holds to round-off,
/// because the passive material counts toward it and the bisection shares only the remaining
/// budget among the free elements. And a passive constraint can only RAISE compliance, never
/// lower it, because it shrinks the feasible set: any passive-constrained design is also a valid
/// free design at the same volume, so the free optimum is at least as good — the mutation a bug
/// that quietly ignored the pinning would fail.</para>
/// </summary>
public sealed class TopologyPassiveRegionTests(ITestOutputHelper output)
{
    /// <summary>The centroid of one element over its four corner nodes.</summary>
    private static Vector3d Centroid(AnalysisMesh mesh, int e)
    {
        var n = mesh.Element(e);
        return 0.25 * (mesh.Position(n[0]) + mesh.Position(n[1]) + mesh.Position(n[2]) + mesh.Position(n[3]));
    }

    [Fact]
    public void PinnedElementsHoldTheirDensityExactly_AndTheConstraintStillHolds()
    {
        // The bar, with NO filter so physical == design and pinning is exact: the first quarter
        // is pinned solid, the last quarter pinned void, the middle half free.
        var model = TopologyFixtures.Bar();
        const double min = 1e-3;
        var result = TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = 0.5,
            FilterRadius = 6.0,
            Filter = TopologyFilter.None,
            MinimumDensity = min,
            SolidRegion = c => c.X < TopologyFixtures.BarLength * 0.25,
            VoidRegion = c => c.X > TopologyFixtures.BarLength * 0.75,
        });

        int solidChecked = 0, voidChecked = 0;
        for (int e = 0; e < model.Mesh.ElementCount; e++)
        {
            double x = Centroid(model.Mesh, e).X;
            if (x < TopologyFixtures.BarLength * 0.25)
            {
                Assert.Equal(1.0, result.Density[e], 12);
                solidChecked++;
            }
            else if (x > TopologyFixtures.BarLength * 0.75)
            {
                Assert.Equal(min, result.Density[e], 12);
                voidChecked++;
            }
        }
        Assert.True(solidChecked > 0 && voidChecked > 0, "the fixture must carry both kinds");
        output.WriteLine(
            $"pinned {solidChecked} solid at 1, {voidChecked} void at {min}, "
            + $"volume fraction {result.VolumeFraction:G8}");

        // The passive material counts toward the budget, so the whole-domain fraction is still
        // the constraint met to round-off.
        Assert.Equal(0.5, result.VolumeFraction, 6);
    }

    [Fact]
    public void PinningRaisesCompliance_TheFeasibleSetShrinks()
    {
        // A free run, then the SAME problem with a void forced through the tension corner where
        // the optimiser would have anchored a chord. A passive-void design is a valid free
        // design at the same volume, so the free optimum is at least as good — forcing a hole
        // where material is wanted must cost stiffness.
        var free = TopologyFixtures.Cantilever(0, out var mesh);
        var options = new TopologyOptions { VolumeFraction = 0.4, FilterRadius = 4.0 };
        double cFree = TopologyOptimizer.Minimize(free, options).Compliance;

        var constrained = TopologyFixtures.Cantilever(0, out _);
        var voided = TopologyOptimizer.Minimize(constrained, options with
        {
            // The clamped root's lower corner (small x, small z) — where a cantilever anchors
            // its bottom chord and strain energy is highest.
            VoidRegion = c => c.X < TopologyFixtures.CantileverLength * 0.25
                && c.Z < TopologyFixtures.CantileverThickness * 0.5,
        });

        output.WriteLine($"free compliance {cFree:G8}, forced-void {voided.Compliance:G8} "
            + $"({voided.Compliance / cFree - 1:P2} higher)");
        // The void region is genuinely emptied: its interior elements sit at the floor.
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var c = Centroid(mesh, e);
            if (c.X < TopologyFixtures.CantileverLength * 0.12 && c.Z < TopologyFixtures.CantileverThickness * 0.25)
                Assert.True(voided.Density[e] < 0.05, $"element {e} not emptied: {voided.Density[e]:G4}");
        }
        // Compliance strictly higher — the constraint bites. (Feasible-set inclusion makes it
        // >= exactly; the margin is real because a corner chord matters.)
        Assert.True(voided.Compliance > cFree * 1.02,
            $"forced void {voided.Compliance:G8} not clear of free {cFree:G8}");
    }

    [Fact]
    public void PinnedSolidAlsoRaisesCompliance_MaterialForcedWhereItIsNotWanted()
    {
        // The other direction: force material into the low-stress tip corner, which the free run
        // would leave empty. It is a valid free design at the same volume, so it too can only
        // cost stiffness.
        var free = TopologyFixtures.Cantilever(0, out _);
        var options = new TopologyOptions { VolumeFraction = 0.4, FilterRadius = 4.0 };
        double cFree = TopologyOptimizer.Minimize(free, options).Compliance;

        var constrained = TopologyFixtures.Cantilever(0, out var mesh);
        var solid = TopologyOptimizer.Minimize(constrained, options with
        {
            // The far tip's top corner — remote from both the clamp and the load path.
            SolidRegion = c => c.X > TopologyFixtures.CantileverLength * 0.85
                && c.Z > TopologyFixtures.CantileverThickness * 0.5,
        });
        output.WriteLine($"free {cFree:G8}, forced-solid {solid.Compliance:G8} "
            + $"({solid.Compliance / cFree - 1:P2} higher)");
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var c = Centroid(mesh, e);
            if (c.X > TopologyFixtures.CantileverLength * 0.9 && c.Z > TopologyFixtures.CantileverThickness * 0.75)
                Assert.True(solid.Density[e] > 0.95, $"element {e} not solid: {solid.Density[e]:G4}");
        }
        Assert.True(solid.Compliance > cFree * 1.02,
            $"forced solid {solid.Compliance:G8} not clear of free {cFree:G8}");
    }

    [Fact]
    public void OverlappingAndOversizedRegionsRefuseByName()
    {
        var model = TopologyFixtures.Bar();

        // An element accepted by both selectors is a contradiction.
        var overlap = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(model,
            new TopologyOptions
            {
                VolumeFraction = 0.5,
                FilterRadius = 6.0,
                SolidRegion = _ => true,
                VoidRegion = _ => true,
            }));
        Assert.Contains("BOTH", overlap.Message);

        // A solid region larger than the budget can never be feasible.
        var oversize = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(model,
            new TopologyOptions
            {
                VolumeFraction = 0.3,
                FilterRadius = 6.0,
                SolidRegion = c => c.X < TopologyFixtures.BarLength * 0.5,  // half the domain, > 0.3
            }));
        Assert.Contains("exceeds the budget", oversize.Message);
    }

    [Fact]
    public void NoPassiveRegions_IsTheIncumbentPathBitForBit()
    {
        // The safety statement: a run stating no passive regions is byte-for-byte the run that
        // predates the feature — the passive array is all-free and every branch reduces to it.
        var a = TopologyOptimizer.Minimize(TopologyFixtures.Bar(),
            new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 6.0, MaxIterations = 20 });
        var b = TopologyOptimizer.Minimize(TopologyFixtures.Bar(),
            new TopologyOptions
            {
                VolumeFraction = 0.5, FilterRadius = 6.0, MaxIterations = 20,
                SolidRegion = null, VoidRegion = null,
            });
        for (int e = 0; e < a.Density.Count; e++)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(a.Density[e]),
                BitConverter.DoubleToInt64Bits(b.Density[e]));
    }
}
