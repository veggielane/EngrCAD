using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class ThreadedRodTessellationTests
{
    private const double Pitch = 1.25;
    private static readonly double H = Math.Sqrt(3) / 2 * Pitch;
    private const double MajorRadius = 4.0;
    private static readonly double MinorRadius = MajorRadius - 0.625 * H;

    private static IReadOnlyList<Vector2d> IsoProfile() =>
    [
        new(MajorRadius, -Pitch / 16),
        new(MajorRadius, Pitch / 16),
        new(MinorRadius, 3 * Pitch / 8),
        new(MinorRadius, 5 * Pitch / 8),
    ];

    /// <summary>
    /// Exact volume of the threaded rod. In cylindrical coordinates the solid is
    /// {(r, θ, z) : 0 ≤ z ≤ L, r ≤ R((z − P·θ/2π) mod P)}, so
    /// V = ∫₀^{2π}∫₀^L ½R²((z − P·θ/2π) mod P) dz dθ. At fixed z the θ-integral sweeps
    /// the phase over exactly one period regardless of z, so it equals
    /// (2π/P)·∫₀^P ½R²(s) ds — independent of z — and V = L·(2π/P)·∫₀^P ½R²(s) ds
    /// EXACTLY, for any length (whole turns or not: the full angular sweep washes out
    /// the phase). This is Pappus per pitch: core π·rMin²·P plus the ridge trapezoid's
    /// area times 2π times its centroid radius (the helical shear along z is
    /// volume-preserving), times L/P turns.
    /// </summary>
    private static double AnalyticVolume(double length)
    {
        double crest = 0.5 * MajorRadius * MajorRadius * (Pitch / 8);
        double root = 0.5 * MinorRadius * MinorRadius * (Pitch / 4);
        // ∫₀^1 ½(rMin + Δr·t)² dt = (rMaj² + rMaj·rMin + rMin²)/6 per unit axial width.
        double flank = (5 * Pitch / 16)
            * (MajorRadius * MajorRadius + MajorRadius * MinorRadius + MinorRadius * MinorRadius) / 6;
        double perPitch = crest + root + 2 * flank;
        return length * (2 * Math.PI / Pitch) * perPitch;
    }

    [Fact]
    public void ThreadedRod_TessellatesClosedWithAnalyticVolume()
    {
        var rod = SolidFactory.MakeThreadedRod(IsoProfile(), Pitch, 10);
        var mesh = BRepTessellator.Tessellate(rod);

        Assert.True(mesh.IsClosed, "threaded rod tessellation must weld closed");
        // Chordal tessellation inscribes the surface: at 32 segments/circle the area
        // deficit is ~(2π/32)²/6 ≈ 0.64%, so 1% covers it with margin.
        double expected = AnalyticVolume(10);
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 0.01,
            $"volume {mesh.Volume():g6} vs analytic {expected:g6}");
    }

    [Fact]
    public void ThreadedRod_FractionalTurnsTessellateClosed()
    {
        // 8.24 turns — the analytic volume formula is exact for ANY length (the full
        // angular sweep at each z covers one whole phase period).
        var rod = SolidFactory.MakeThreadedRod(IsoProfile(), Pitch, 10.3);
        var mesh = BRepTessellator.Tessellate(rod);
        Assert.True(mesh.IsClosed);
        double expected = AnalyticVolume(10.3);
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 0.01,
            $"volume {mesh.Volume():g6} vs analytic {expected:g6}");
    }

    [Fact]
    public void ThreadedRod_WeldsAtNonAlignedSegmentCounts()
    {
        // 48 segments/circle is not a multiple of 16, so profile corner phases do NOT
        // land on a shared global grid — welding must come from the band grids reusing
        // the edge polylines, not from lucky alignment.
        var rod = SolidFactory.MakeThreadedRod(IsoProfile(), Pitch, 5);
        var mesh = BRepTessellator.Tessellate(rod, segmentsPerCircle: 48, curveSamples: 24);
        Assert.True(mesh.IsClosed);
        double expected = AnalyticVolume(5);
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 0.005,
            $"volume {mesh.Volume():g6} vs analytic {expected:g6}");
    }

    [Fact]
    public void ThreadedRod_PlacedFrameTessellatesClosedWithSameVolume()
    {
        var axis = new Vector3d(2, -1, 3).Normalized();
        var x = axis.ArbitraryPerpendicular(Tolerance.Default);
        var frame = Frame3d.FromOrthonormal(new Vector3d(-4, 2, 7), x, axis.Cross(x));
        var rod = SolidFactory.MakeThreadedRod(IsoProfile(), Pitch, 10, frame);
        var mesh = BRepTessellator.Tessellate(rod);
        Assert.True(mesh.IsClosed);
        double expected = AnalyticVolume(10);
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 0.01,
            $"volume {mesh.Volume():g6} vs analytic {expected:g6}");
    }
}
