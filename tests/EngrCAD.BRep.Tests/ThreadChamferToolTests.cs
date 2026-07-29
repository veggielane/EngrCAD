using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="SolidFactory.MakeThreadEndChamferTool"/>: the solid a 45-degree lead-in
/// chamfer removes from a threaded rod. What has to hold is that the CONE is the only
/// face of it that can reach the rod — every other face is pushed clear by the overshoot,
/// which is what keeps every intersecting pair transversal.
/// </summary>
public class ThreadChamferToolTests
{
    private const double MajorRadius = 4;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheConeReachesTheMajorRadiusExactlyOneChamferFromTheEndFace(bool atMaxAxial)
    {
        const double chamfer = 0.5, endAxial = 6;
        var tool = SolidFactory.MakeThreadEndChamferTool(MajorRadius, chamfer, endAxial, atMaxAxial);
        double inward = atMaxAxial ? -1 : 1;

        // The one slanted face is the chamfer cone; sample its generator and check the
        // 45-degree law r = MajorRadius + inward-distance-from-the-chamfer-start.
        var cone = tool.Faces
            .Select(f => (RevolvedSurface)f.Surface)
            .Single(s => Math.Abs(Radius(s, 0) - Radius(s, 1)) > 1e-9
                      && Math.Abs(Axial(s, 0) - Axial(s, 1)) > 1e-9);
        for (int i = 0; i <= 8; i++)
        {
            double t = i / 8.0;
            double z = Axial(cone, t), r = Radius(cone, t);
            // r = MajorRadius at z = endAxial + inward*chamfer, sloping 1:1 from there.
            double expected = MajorRadius + inward * (z - (endAxial + inward * chamfer));
            Assert.Equal(expected, r, 9);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryOtherFaceClearsTheRodsEnvelope(bool atMaxAxial)
    {
        const double chamfer = 0.5, endAxial = 6, length = 6;
        var tool = SolidFactory.MakeThreadEndChamferTool(
            MajorRadius, chamfer, atMaxAxial ? endAxial : 0, atMaxAxial);

        foreach (var face in tool.Faces)
        {
            var surface = (RevolvedSurface)face.Surface;
            bool slanted = Math.Abs(Radius(surface, 0) - Radius(surface, 1)) > 1e-9
                        && Math.Abs(Axial(surface, 0) - Axial(surface, 1)) > 1e-9;
            if (slanted)
                continue;
            // A flat is clear if every sample is outside the rod's cylinder of radius
            // MajorRadius, or outside its axial extent [0, length].
            for (int i = 0; i <= 8; i++)
            {
                double t = i / 8.0;
                double z = Axial(surface, t), r = Radius(surface, t);
                Assert.True(r > MajorRadius + 1e-9 || z < -1e-9 || z > length + 1e-9,
                    $"a non-cone face sample at (r={r:F4}, z={z:F4}) touches the rod's envelope");
            }
        }
    }

    [Fact]
    public void ARefusedChamferNamesTheAxis()
    {
        // A chamfer deep enough to swallow the axis has no cone at all.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SolidFactory.MakeThreadEndChamferTool(MajorRadius, MajorRadius, 6, true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SolidFactory.MakeThreadEndChamferTool(MajorRadius, 0, 6, true));
    }

    private static double Radius(RevolvedSurface s, double t)
    {
        var p = s.Generator.PointAt(s.Generator.Domain.ParameterAt(t));
        return Math.Sqrt(p.X * p.X + p.Y * p.Y);
    }

    private static double Axial(RevolvedSurface s, double t) =>
        s.Generator.PointAt(s.Generator.Domain.ParameterAt(t)).Z;
}
