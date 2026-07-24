using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The raw kernel pipeline behind B-Rep threaded holes: plate − clipped-profile helical
/// tool. The tool's pilot volume is part of the same rod (its "root" flat sits at the
/// pilot radius), so the only face pairs the boolean sees are helical-band ∩ drilled
/// plane — exact spiral arcs that chain into a closed loop the plane face splits along.
/// </summary>
public class ThreadedHoleBooleanTests
{
    // M6-like internal thread clipped at the tap-drill radius.
    private const double Pitch = 1.0;
    private static readonly double H = Math.Sqrt(3) / 2 * Pitch;
    private const double MajorRadius = 3.0;
    private static readonly double MinorRadius = MajorRadius - 0.625 * H;
    private const double PilotRadius = 2.5;

    private static IReadOnlyList<Vector2d> ClippedProfile()
    {
        double flankDrop = 5 * Pitch / 16 * (MajorRadius - PilotRadius) / (MajorRadius - MinorRadius);
        return
        [
            new(MajorRadius, -Pitch / 16),
            new(MajorRadius, Pitch / 16),
            new(PilotRadius, Pitch / 16 + flankDrop),
            new(PilotRadius, 15 * Pitch / 16 - flankDrop),
        ];
    }

    /// <summary>Void volume for a hole of the given depth: depth·(2π/P)·∫₀^P ½R′² over
    /// the clipped profile (exact for any depth — the angular sweep washes out the
    /// phase, as for the rod).</summary>
    private static double VoidVolume(double depth)
    {
        double flankDrop = 5 * Pitch / 16 * (MajorRadius - PilotRadius) / (MajorRadius - MinorRadius);
        double za = Pitch / 16 + flankDrop, zb = 15 * Pitch / 16 - flankDrop;
        double perPitch = 0.5 * MajorRadius * MajorRadius * (Pitch / 8)
            + 0.5 * PilotRadius * PilotRadius * (zb - za)
            + 2 * (za - Pitch / 16) * (MajorRadius * MajorRadius + MajorRadius * PilotRadius + PilotRadius * PilotRadius) / 6;
        return depth * (2 * Math.PI / Pitch) * perPitch;
    }

    private static BrepSolid Tool(double length, double overshoot) =>
        // Advancing DOWN from z = +overshoot: frame (X, −Y, −Z), a π-rotation about X.
        SolidFactory.MakeThreadedRod(ClippedProfile(), Pitch, length,
            Frame3d.FromOrthonormal(new Vector3d(0, 0, overshoot), Vector3d.UnitX, -Vector3d.UnitY));

    [Fact]
    public void BlindThreadedHole_TessellatesClosedWithAnalyticVolume()
    {
        var plate = SolidFactory.MakeBox(new Aabb((-8, -6, -8), (8, 6, 0)));
        var result = BrepBoolean.Difference(plate, Tool(6.4, 0.4)); // bottom at −6, blind

        // 5 intact plate faces + top-with-hole + 4 band fragments + tool bottom cap.
        Assert.Equal(11, result.Faces.Count());
        var mesh = BRepTessellator.Tessellate(result);
        Assert.True(mesh.IsClosed, "threaded-hole boolean must tessellate closed");

        double expected = 16.0 * 12 * 8 - VoidVolume(6);
        // Chordal void deficit ~2% of the void at 32 segments/circle (O(h²)).
        Assert.True(Math.Abs(mesh.Volume() - expected) < 0.025 * VoidVolume(6),
            $"volume {mesh.Volume():g8} vs analytic {expected:g8}");
    }

    [Fact]
    public void ThroughThreadedHole_SplitsBothFacesAndTessellatesClosed()
    {
        var plate = SolidFactory.MakeBox(new Aabb((-8, -6, -8), (8, 6, 0)));
        var result = BrepBoolean.Difference(plate, Tool(8.8, 0.4)); // pierces both faces

        var mesh = BRepTessellator.Tessellate(result);
        Assert.True(mesh.IsClosed);
        double expected = 16.0 * 12 * 8 - VoidVolume(8);
        Assert.True(Math.Abs(mesh.Volume() - expected) < 0.025 * VoidVolume(8),
            $"volume {mesh.Volume():g8} vs analytic {expected:g8}");
    }
}
