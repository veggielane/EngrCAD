using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class CrossedHelicalGearTests
{
    // A deliberately lopsided pair: different helix angles, so every quantity that
    // COINCIDES on parallel axes (normal vs transverse module, teeth ratio vs radius
    // ratio) separates and a test can tell them apart.
    private static CrossedHelicalPair Lopsided() =>
        CrossedHelicalPair.Create(normalModule: 2, teeth1: 20, teeth2: 30,
            helixAngle1Degrees: 20, helixAngle2Degrees: 50);

    // ---- the meshing condition: one normal module, two transverse ones ----

    [Fact]
    public void NormalModuleIsShared_AndTheTransverseModulesDiffer()
    {
        var pair = Lopsided();
        double c1 = Math.Cos(20 * Math.PI / 180), c2 = Math.Cos(50 * Math.PI / 180);

        // Route one: the members' own transverse modules, back through m_n = m_t·cos β.
        Assert.True(Math.Abs(pair.First.Module * c1 - 2) < 1e-14, $"{pair.First.Module * c1}");
        Assert.True(Math.Abs(pair.Second.Module * c2 - 2) < 1e-14, $"{pair.Second.Module * c2}");

        // Route two: HelicalGearGeometry, which the factory delegates to.
        Assert.True(Math.Abs(HelicalGearGeometry.NormalModule(pair.First.Module, 20) - 2) < 1e-14);
        Assert.True(Math.Abs(HelicalGearGeometry.NormalModule(pair.Second.Module, 50) - 2) < 1e-14);

        // And they are genuinely different transverse gears: 2.128 against 3.111.
        Assert.True(Math.Abs(pair.First.Module - pair.Second.Module) > 0.9,
            $"transverse modules {pair.First.Module:0.####} and {pair.Second.Module:0.####}");
    }

    [Fact]
    public void NormalPressureAngleIsShared_AndTheTransverseAnglesDiffer()
    {
        var pair = Lopsided();
        Assert.True(Math.Abs(
            HelicalGearGeometry.NormalPressureAngleDegrees(pair.First.PressureAngleDegrees, 20) - 20) < 1e-12);
        Assert.True(Math.Abs(
            HelicalGearGeometry.NormalPressureAngleDegrees(pair.Second.PressureAngleDegrees, 50) - 20) < 1e-12);
        // tan α_t = tan α_n/cos β, so the 50-degree member's transverse angle is much larger.
        Assert.True(pair.Second.PressureAngleDegrees - pair.First.PressureAngleDegrees > 8,
            $"transverse pressure angles {pair.First.PressureAngleDegrees:0.###} and "
            + $"{pair.Second.PressureAngleDegrees:0.###}");
    }

    [Fact]
    public void NormalRackCoefficients_AreRadialLengths_SoTheyScaleByCosBeta()
    {
        // The whole tooth-height family is quoted against the NORMAL module because a hob
        // cuts it, while GearSpec reads it against the TRANSVERSE one — so every
        // coefficient, the profile shift included, carries a cos β. Asserted where a
        // human can check it: in MILLIMETRES against the normal module.
        const double mn = 2, beta = 45;
        double cos = Math.Cos(beta * Math.PI / 180);
        var spec = HelicalGearGeometry.FromNormal(mn, 18, beta, profileShift: 0.3);

        double addendum = (spec.TipDiameter - spec.PitchDiameter) / 2;
        double dedendum = (spec.PitchDiameter - spec.RootDiameter) / 2;
        Assert.True(Math.Abs(addendum - mn * (1.00 + 0.3)) < 1e-12, $"addendum {addendum} mm");
        Assert.True(Math.Abs(dedendum - mn * (1.25 - 0.3)) < 1e-12, $"dedendum {dedendum} mm");
        Assert.True(Math.Abs(spec.RootFilletCoefficient * spec.Module - 0.38 * mn) < 1e-12);

        // The transverse tooth thickness is the normal one over cos β — the identity that
        // only holds if the shift scaled too.
        double normalThickness = mn * (Math.PI / 2 + 2 * 0.3 * Math.Tan(20 * Math.PI / 180));
        Assert.True(Math.Abs(spec.ToothThicknessAtPitch - normalThickness / cos) < 1e-12,
            $"{spec.ToothThicknessAtPitch} vs {normalThickness / cos}");

        // And it is not cosmetic: unscaled, the 0.38 fillet reads 1.34x too big and a
        // 24-tooth member at 45 degrees cannot be drawn at all — adjacent root fillets
        // overlap, because a high helix angle raises the TRANSVERSE pressure angle to
        // 27.2 degrees and a tooth that flares that fast leaves a narrow root gap.
        var unscaled = new GearSpec(
            HelicalGearGeometry.TransverseModule(mn, beta), 24,
            HelicalGearGeometry.TransversePressureAngleDegrees(20, beta));
        var ex = Assert.Throws<ArgumentException>(() => Gears.Spur(unscaled));
        Assert.Contains("root gap", ex.Message);
        _ = Gears.Spur(HelicalGearGeometry.FromNormal(mn, 24, beta));   // the scaled one draws
    }

    // ---- the shaft angle, from the arithmetic and from the placed axes ----

    [Theory]
    [InlineData(20, 50, 70)]     // same hand: Σ = β₁ + β₂
    [InlineData(45, 45, 90)]     // the classic right-angle screw pair
    [InlineData(50, -20, 30)]    // opposite hands: Σ = β₁ − |β₂|
    [InlineData(20, -50, 30)]    // and the other way round (the sign of Σ flips, not its size)
    [InlineData(30, 0, 30)]      // a spur gear crossed with a helical is a legitimate pair
    public void ShaftAngle_IsTheSignedSumAndTheAxesAgree(double beta1, double beta2, double expected)
    {
        var pair = CrossedHelicalPair.Create(2, 20, 30, beta1, beta2);
        Assert.True(Math.Abs(pair.ShaftAngleDegrees - expected) < 1e-12,
            $"{pair.ShaftAngleDegrees} vs {expected}");
        Assert.Equal(beta1 + beta2, pair.SignedShaftAngleDegrees);
        Assert.Equal(beta1 * beta2 > 0, pair.SameHand);

        // The independent route: measure the angle between the two PLACED axes.
        double measured = Math.Acos(Math.Clamp(
            pair.FirstAxis.Direction.Dot(pair.SecondAxis.Direction), -1, 1)) * 180 / Math.PI;
        Assert.True(Math.Abs(measured - expected) < 1e-9, $"axes measure {measured}, arithmetic says {expected}");
    }

    // ---- centre distance and tangency ----

    [Fact]
    public void CentreDistance_IsTheSkewAxisSeparation()
    {
        var pair = Lopsided();

        // Route one: the closed form, m_n/2·(z₁/cos β₁ + z₂/cos β₂).
        double c1 = Math.Cos(20 * Math.PI / 180), c2 = Math.Cos(50 * Math.PI / 180);
        double expected = 2.0 / 2 * (20 / c1 + 30 / c2);
        Assert.True(Math.Abs(pair.CentreDistance - expected) < 1e-12,
            $"{pair.CentreDistance} vs {expected}");

        // Route two: the distance between the two placed SKEW lines, by the standard
        // |(p₂ − p₁)·(d₁ × d₂)|/|d₁ × d₂| — nothing in common with the formula above.
        var a = pair.FirstAxis;
        var b = pair.SecondAxis;
        var n = a.Direction.Cross(b.Direction);
        double separation = Math.Abs((b.Origin - a.Origin).Dot(n)) / n.Length;
        Assert.True(Math.Abs(separation - pair.CentreDistance) < 1e-12,
            $"skew separation {separation} vs centre distance {pair.CentreDistance}");
    }

    [Fact]
    public void ContactPoint_LiesOnBothPitchCylinders()
    {
        var pair = Lopsided();
        Assert.True(Math.Abs(DistanceToAxis(pair.ContactPoint, pair.FirstAxis) - pair.FirstPitchRadius) < 1e-12);
        Assert.True(Math.Abs(DistanceToAxis(pair.ContactPoint, pair.SecondAxis) - pair.SecondPitchRadius) < 1e-12);

        // Tangency at ONE point: the two cylinders touch, so the sum of the radii is the
        // separation exactly — anything less would be interference, anything more a gap.
        Assert.True(Math.Abs(
            pair.FirstPitchRadius + pair.SecondPitchRadius - pair.CentreDistance) < 1e-12);
    }

    // ---- the trap: speed follows the TEETH ----

    [Fact]
    public void Ratio_FollowsTheTeethAndNotThePitchRadii()
    {
        var pair = Lopsided();
        Assert.Equal(30.0 / 20.0, pair.Ratio);

        // On parallel axes r₂/r₁ would equal z₂/z₁ and the habit would be harmless. Here
        // r = m_n·z/(2·cos β), so the radii are out by cos β₁/cos β₂ = 1.4619 — a 46%
        // error in the transmission ratio for anyone who reads the radii.
        double radiusRatio = pair.SecondPitchRadius / pair.FirstPitchRadius;
        double expectedSkew = Math.Cos(20 * Math.PI / 180) / Math.Cos(50 * Math.PI / 180);
        Assert.True(Math.Abs(radiusRatio - pair.Ratio * expectedSkew) < 1e-12,
            $"radius ratio {radiusRatio}, teeth ratio {pair.Ratio}");
        Assert.True(Math.Abs(radiusRatio - pair.Ratio) / pair.Ratio > 0.4,
            $"the fixture does not separate the two ratios: {radiusRatio} vs {pair.Ratio}");
    }

    // ---- the meshing condition, geometrically: one tooth trace ----

    [Fact]
    public void ToothTraces_CoincideAtTheContactPoint()
    {
        foreach (var (beta1, beta2) in new[] { (20.0, 50.0), (45.0, 45.0), (50.0, -20.0), (30.0, 0.0) })
        {
            var pair = CrossedHelicalPair.Create(2, 20, 30, beta1, beta2);

            // Recomputed from the RETURNED frames rather than read off the pair, so this
            // is a check on the placement and not a restatement of it.
            var t1 = HelixTangent(pair.FirstAxis, Vector3d.Zero, pair.ContactPoint, beta1);
            var t2 = HelixTangent(pair.SecondAxis, pair.SecondAxis.Origin, pair.ContactPoint, beta2);
            double agreement = Math.Abs(t1.Dot(t2));
            Assert.True(Math.Abs(agreement - 1) < 1e-12,
                $"at beta {beta1}/{beta2} the tooth traces disagree: |dot| = {agreement:0.############}");
        }
    }

    [Fact]
    public void ToothTraceCheck_SeesAWrongShaftAngle()
    {
        // The mutation check for the instrument above: turn the second shaft by 5 degrees
        // off the pairing arithmetic and the traces must visibly part company, or the
        // agreement measured there is telling us nothing.
        var pair = Lopsided();
        double sigma = (pair.SignedShaftAngleDegrees + 5) * Math.PI / 180;
        var wrongAxis = new Ray3d(
            new Vector3d(pair.CentreDistance, 0, 0),
            new Vector3d(0, Math.Sin(sigma), Math.Cos(sigma)));

        var t1 = HelixTangent(pair.FirstAxis, Vector3d.Zero, pair.ContactPoint, 20);
        var t2 = HelixTangent(wrongAxis, wrongAxis.Origin, pair.ContactPoint, 50);
        double agreement = Math.Abs(t1.Dot(t2));
        Assert.True(agreement < 0.9997, $"a 5 degree shaft error still reads |dot| = {agreement:0.######}");
    }

    // ---- the placed solids ----

    [Fact]
    public void PlacedSolids_SitOnTheAxesThePairReports()
    {
        var pair = CrossedHelicalPair.Create(2, 18, 24, 45, 45);
        const double width = 8;

        foreach (var (shape, spec, axis) in new (Shape, GearSpec, Ray3d)[]
        {
            (pair.FirstGear(width, boreDiameter: 8), pair.First, pair.FirstAxis),
            (pair.SecondGear(width, boreDiameter: 8), pair.Second, pair.SecondAxis),
        })
        {
            var mesh = shape.ToMesh();
            Assert.True(mesh.IsClosed);
            double maxRadial = 0, maxAxial = 0, minRadial = double.PositiveInfinity;
            foreach (var vertex in mesh.Vertices)
            {
                var d = vertex.Position - axis.Origin;
                double along = d.Dot(axis.Direction);
                double across = (d - axis.Direction * along).Length;
                maxRadial = Math.Max(maxRadial, across);
                minRadial = Math.Min(minRadial, across);
                maxAxial = Math.Max(maxAxial, Math.Abs(along));
            }
            // The solid is a gear about THAT axis, centred on the contact plane: nothing
            // reaches past the tip circle, nothing sits inside the bore, and the face
            // width straddles the contact plane. A placement that missed the pose would
            // fail all three at once.
            Assert.True(maxRadial <= spec.TipDiameter / 2 + 1e-9, $"radial reach {maxRadial}");
            Assert.True(maxRadial > spec.TipDiameter / 2 - 0.2, $"radial reach {maxRadial} is short of the tip");
            Assert.True(Math.Abs(minRadial - 4) < 1e-9, $"bore radius reads {minRadial}");
            Assert.True(Math.Abs(maxAxial - width / 2) < 1e-9, $"axial half-width {maxAxial}");
        }
    }

    // ---- refusals, by name ----

    [Fact]
    public void ParallelShafts_RefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => CrossedHelicalPair.Create(2, 20, 30, 25, -25));
        Assert.Contains("PARALLEL", ex.Message);
        Assert.Contains("LINE contact", ex.Message);
        Assert.Contains("HelicalGear", ex.Message);
    }

    [Fact]
    public void OutOfRangeInputs_Refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CrossedHelicalPair.Create(0, 20, 30, 20, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrossedHelicalPair.Create(2, 20, 30, 70, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrossedHelicalPair.Create(2, 20, 30, 20, 50, 50));
    }

    // ------------------------------------------------------------------ helpers

    private static double DistanceToAxis(in Vector3d point, in Ray3d axis)
    {
        var d = point - axis.Origin;
        return (d - axis.Direction * d.Dot(axis.Direction)).Length;
    }

    /// <summary>The pitch-helix tangent at <paramref name="point"/> for a gear on
    /// <paramref name="axis"/>: cos β along the axis, sin β around it.</summary>
    private static Vector3d HelixTangent(
        in Ray3d axis, in Vector3d axisPoint, in Vector3d point, double helixAngleDegrees)
    {
        var d = point - axisPoint;
        var radial = (d - axis.Direction * d.Dot(axis.Direction)).Normalized();
        var around = axis.Direction.Cross(radial);
        double beta = helixAngleDegrees * Math.PI / 180;
        return (axis.Direction * Math.Cos(beta) + around * Math.Sin(beta)).Normalized();
    }
}
