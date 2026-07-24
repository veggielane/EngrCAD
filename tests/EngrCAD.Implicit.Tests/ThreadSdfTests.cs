using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Implicit.Tests;

public class ThreadSdfTests
{
    // M8×1.25 external thread, ISO 68-1 basic profile: H = (√3/2)·P, crest flat P/8 at
    // the major radius, root flat P/4 at the minor radius d1/2 = 4 − (5/8)H.
    private const double Pitch = 1.25;
    private static readonly double H = Math.Sqrt(3) / 2 * Pitch;
    private const double MajorR = 4.0;
    private static readonly double MinorR = MajorR - 0.625 * H;
    private static readonly double CrestWidth = Pitch / 8;
    private static readonly double RootWidth = Pitch / 4;

    private static Sdf M8(double length, double profileOffset = 0, double startChamfer = 0, double endChamfer = 0) =>
        Sdf.Thread(MajorR, MinorR, Pitch, CrestWidth, RootWidth, length,
            profileOffset, startChamfer, endChamfer);

    [Fact]
    public void Thread_SignIsExactAtCharacteristicPoints()
    {
        var thread = M8(10);

        // Deep in the core.
        Assert.True(thread.Evaluate((0, 0, 5)) < 0);
        // Outside the major cylinder.
        Assert.True(thread.Evaluate((4.5, 0, 5)) > 0.3);
        // Above / below the ends.
        Assert.True(thread.Evaluate((2, 0, 11)) > 0);
        Assert.True(thread.Evaluate((2, 0, -1)) > 0);

        // On the +x half-plane (θ = 0) the helical phase is just z: at z = 5·P the
        // crest is centered (u = 0) — mid-flank radius is inside the ridge; half a
        // pitch later the same radius sits in the groove (root flat region).
        double zRidge = 5 * Pitch, zGroove = zRidge + Pitch / 2;
        double midFlankR = (MajorR + MinorR) / 2;
        Assert.True(thread.Evaluate((midFlankR, 0, zRidge)) < 0);
        Assert.True(thread.Evaluate((midFlankR, 0, zGroove)) > 0);
        // Below the root the groove is still material.
        Assert.True(thread.Evaluate((MinorR - 0.1, 0, zGroove)) < 0);
    }

    [Fact]
    public void Thread_CrestCenterHelixLiesOnTheSurface()
    {
        // The helical line (r = majorR, θ, z = P·θ/2π + k·P) has phase u = 0 — the
        // center of the crest flat — so it lies exactly on the boundary.
        var thread = M8(10);
        for (int i = 0; i < 24; i++)
        {
            double theta = 2 * Math.PI * i / 24.0 - Math.PI;
            for (int k = 3; k <= 5; k++)
            {
                double z = Pitch * theta / (2 * Math.PI) + k * Pitch;
                var p = new Vector3d(MajorR * Math.Cos(theta), MajorR * Math.Sin(theta), z);
                Assert.True(Math.Abs(thread.Evaluate(p)) < 1e-9,
                    $"crest helix point at θ={theta:g4}, k={k} gave {thread.Evaluate(p):g4}");
            }
        }
    }

    [Fact]
    public void Thread_RadialDistanceAboveCrestIsBracketedByTheCosLeadScale()
    {
        // Directly above the crest center the true distance is exactly the radial gap;
        // the field reports it scaled by cos λ(root) ∈ (0.998, 1) for M8 — the
        // documented lower-bound contract.
        var thread = M8(10);
        double gap = 0.5;
        double d = thread.Evaluate((MajorR + gap, 0, 5 * Pitch));
        double circumference = 2 * Math.PI * MinorR;
        double cosLead = circumference / Math.Sqrt(circumference * circumference + Pitch * Pitch);
        Assert.InRange(d, gap * cosLead - 1e-12, gap + 1e-12);
        Assert.True(cosLead > 0.99, "M8 lead angle sanity");
    }

    [Fact]
    public void Thread_PolygonizedVolumeMatchesThePappusEstimate()
    {
        // Core cylinder + one trapezoidal ridge per pitch revolved at its centroid
        // radius (Pappus). End effects clip at most one partial ridge (~2%), and
        // Surface Nets at this resolution adds a few % — 8% total headroom.
        double length = 10;
        var thread = M8(length);
        double volume = SurfaceNets.Polygonize(thread, resolution: 96).Volume();

        double core = Math.PI * MinorR * MinorR * length;
        double major = Math.PI * MajorR * MajorR * length;
        Assert.InRange(volume, core, major); // nominal bounds — threads add to the core, stay under the major cylinder

        double depth = MajorR - MinorR;
        double rootSpan = Pitch - RootWidth; // ridge width at the root
        double area = (CrestWidth + rootSpan) / 2 * depth;
        double centroidUp = depth / 3 * (rootSpan + 2 * CrestWidth) / (rootSpan + CrestWidth);
        double ridgePerLength = area * 2 * Math.PI * (MinorR + centroidUp) / Pitch;
        double estimate = core + ridgePerLength * length;
        Assert.True(Math.Abs(volume - estimate) / estimate < 0.08,
            $"volume {volume:g6} vs Pappus estimate {estimate:g6}");
    }

    [Fact]
    public void Thread_ProfileOffsetIsPointwiseAndVolumeMonotone()
    {
        // Positive offset dilates the profile (internal-void use), negative erodes it
        // (external clearance): the field must decrease pointwise as offset grows, and
        // polygonized volume must grow with it.
        var eroded = M8(10, profileOffset: -0.15);
        var nominal = M8(10);
        var dilated = M8(10, profileOffset: +0.15);

        var samples = new Vector3d[]
        {
            (MajorR, 0, 5 * Pitch), (midFlank(), 0, 5 * Pitch), (midFlank(), 0, 5.5 * Pitch),
            (MinorR, 0.3, 4), (0, 4.2, 6.1), (3.9, -0.7, 2.2),
        };
        foreach (var p in samples)
        {
            Assert.True(eroded.Evaluate(p) >= nominal.Evaluate(p) - 1e-12);
            Assert.True(nominal.Evaluate(p) >= dilated.Evaluate(p) - 1e-12);
        }

        double vEroded = SurfaceNets.Polygonize(eroded, 64).Volume();
        double vNominal = SurfaceNets.Polygonize(nominal, 64).Volume();
        double vDilated = SurfaceNets.Polygonize(dilated, 64).Volume();
        Assert.True(vEroded < vNominal && vNominal < vDilated,
            $"volumes must grow with offset: {vEroded:g6}, {vNominal:g6}, {vDilated:g6}");

        static double midFlank() => (MajorR + MinorR) / 2;
    }

    [Fact]
    public void Thread_EndChamferCutsTheTip()
    {
        double length = 10;
        double depth = MajorR - MinorR;
        var plain = M8(length);
        var chamfered = M8(length, endChamfer: depth);

        // A crest point just below the top end survives unchamfered but is cut by the
        // 45° cone (allowed radius there is barely above the minor radius).
        var tipCrest = new Vector3d(MajorR - 0.05, 0, 8 * Pitch - 0.05);
        Assert.True(plain.Evaluate(tipCrest) < 0);
        Assert.True(chamfered.Evaluate(tipCrest) > 0);

        // The chamfer only removes material.
        double vPlain = SurfaceNets.Polygonize(plain, 64).Volume();
        double vChamfered = SurfaceNets.Polygonize(chamfered, 64).Volume();
        Assert.True(vChamfered < vPlain, $"{vChamfered:g6} vs {vPlain:g6}");

        // Start chamfer mirrors at z = 0.
        var startChamfered = M8(length, startChamfer: depth);
        Assert.True(startChamfered.Evaluate((MajorR - 0.05, 0, 0.05)) > 0);
    }

    [Fact]
    public void Thread_UnequalChamfers_EachEndCutsExactlyItsOwnDepth()
    {
        // Regression (code-quality review): a shared max-based cone anchor cut the
        // smaller-chamfer end to the LARGER chamfer's depth. Each end-face radius must
        // be majorRadius − itsOwnChamfer: probe just inside/outside each face circle.
        double length = 10;
        double small = 0.3, large = 1.0;
        var thread = M8(length, startChamfer: small, endChamfer: large);

        double startFaceR = MajorR - small; // allowed radius at z = 0
        double endFaceR = MajorR - large;   // allowed radius at z = length

        // Start end: material survives between (MajorR − large) and (MajorR − small)
        // — exactly the band the old shared anchor wrongly removed.
        Assert.True(thread.Evaluate((startFaceR - 0.05, 0, 0.02)) < 0);
        Assert.True(thread.Evaluate((startFaceR + 0.05, 0, 0.02)) > 0);

        // End face honors its own (larger) chamfer.
        Assert.True(thread.Evaluate((endFaceR - 0.05, 0, length - 0.02)) < 0);
        Assert.True(thread.Evaluate((endFaceR + 0.05, 0, length - 0.02)) > 0);
    }

    [Fact]
    public void Thread_BoundsAreConservative()
    {
        var thread = M8(10, profileOffset: 0.1);
        var bounds = thread.Bounds;
        Assert.Equal(MajorR + 0.1, bounds.Max.X, 12);
        Assert.Equal(0, bounds.Min.Z, 12);
        Assert.Equal(10, bounds.Max.Z, 12);

        // Everything strictly outside the bounds is outside the solid.
        Assert.True(thread.Evaluate((0, 0, 10.2)) > 0);
        Assert.True(thread.Evaluate((MajorR + 0.2, 0, 5)) > 0);
    }

    [Fact]
    public void Thread_ValidatesInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(3, 4, 1, 0.1, 0.2, 10));  // radii swapped
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(4, 0, 1, 0.1, 0.2, 10));  // minor ≤ 0
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(4, 3.3, 0, 0.1, 0.2, 10)); // pitch
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(4, 3.3, 1, 0.6, 0.5, 10)); // flats eat the pitch
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(4, 3.3, 1, 0.1, 0.2, 0));  // length
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(4, 3.3, 1, 0.1, 0.2, 10, profileOffset: -4)); // offset erases the thread
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(4, 3.3, 1, 0.1, 0.2, 10, endChamfer: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Thread(4, 3.3, 1, 0.1, 0.2, 10, endChamfer: 5)); // chamfer past the axis
    }
}
