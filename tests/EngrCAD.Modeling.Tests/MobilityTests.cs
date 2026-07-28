using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Grübler/Kutzbach as a cross-check, not a source of truth: the formula and the
/// solver's measured rank side by side, disagreement informative.
/// </summary>
public class MobilityTests
{
    private static Frame3d Posed(double x, double y, double angle) =>
        Frame3d.FromXY(
            (x, y, 0),
            (Math.Cos(angle), Math.Sin(angle), 0),
            (-Math.Sin(angle), Math.Cos(angle), 0));

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    [Fact]
    public void ASimpleHinge_AgreesWithTheFormula()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("door"));
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(Joint.Revolute(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ)));
        mechanism.Assemble();

        var mobility = mechanism.Mobility();

        Assert.Equal(1, mobility.MovingBodies);
        Assert.Equal(1, mobility.PredictedDegreesOfFreedom);   // 6·1 − 5
        Assert.Equal(1, mobility.MeasuredDegreesOfFreedom);
        Assert.True(mobility.Agrees);
    }

    [Fact]
    public void AGearPair_CountsTheCouplingAndAgrees()
    {
        var rig = new Assembly("gearbox");
        var housing = rig.Add(BoxPart("housing"));
        var gearA = rig.Add(BoxPart("gearA"));
        var gearB = rig.Add(BoxPart("gearB"), Posed(30, 0, 0));
        var z = Vector3d.UnitZ;
        var pinA = Joint.Revolute(
            MateGeometry.Axis(housing, (0, 0, 0), z), MateGeometry.Axis(gearA, (0, 0, 0), z));
        var pinB = Joint.Revolute(
            MateGeometry.Axis(housing, (30, 0, 0), z), MateGeometry.Axis(gearB, (0, 0, 0), z));
        var mechanism = new Mechanism(rig).Ground(housing).Add(pinA).Add(pinB)
            .Add(Coupling.Gear(pinA, pinB, 20, 40));
        mechanism.Assemble();

        var mobility = mechanism.Mobility();

        // 6·2 − 2·5 − 1 coupling = 1, and the rank agrees.
        Assert.Equal(1, mobility.CouplingCount);
        Assert.Equal(1, mobility.PredictedDegreesOfFreedom);
        Assert.Equal(1, mobility.MeasuredDegreesOfFreedom);
        Assert.True(mobility.Agrees);
    }

    [Fact]
    public void ThePlanarFourBar_IsWhereTheFormulaLies_AndTheReportSaysSo()
    {
        // A planar four-bar built in space: spatial Kutzbach predicts 6·3 − 4·5 = −2,
        // the measured rank says 1 — the classic overconstrained-but-mobile case.
        const double crank = 10, coupler = 35, rocker = 25, span = 40;
        var rig = new Assembly("linkage");
        var frame = rig.Add(BoxPart("frame"));
        var crankLink = rig.Add(BoxPart("crank"));
        var couplerLink = rig.Add(BoxPart("coupler"));
        var rockerLink = rig.Add(BoxPart("rocker"));

        // Elbow-up authoring at crank angle 0 (same construction as MechanismTests).
        var tip = new Vector3d(crank, 0, 0);
        var o2 = new Vector3d(span, 0, 0);
        var toPivot = o2 - tip;
        double d = toPivot.Length;
        var u = toPivot / d;
        double along = (d * d + coupler * coupler - rocker * rocker) / (2 * d);
        double h = Math.Sqrt(coupler * coupler - along * along);
        var elbow = tip + u * along + new Vector3d(-u.Y, u.X, 0) * h;
        crankLink.Frame = Posed(0, 0, 0);
        couplerLink.Frame = Posed(tip.X, tip.Y, Math.Atan2(elbow.Y - tip.Y, elbow.X - tip.X));
        rockerLink.Frame = Posed(span, 0, Math.Atan2(elbow.Y, elbow.X - span));

        var z = Vector3d.UnitZ;
        var mechanism = new Mechanism(rig)
            .Ground(frame)
            .Add(Joint.Revolute(MateGeometry.Axis(frame, (0, 0, 0), z), MateGeometry.Axis(crankLink, (0, 0, 0), z)))
            .Add(Joint.Revolute(MateGeometry.Axis(crankLink, (crank, 0, 0), z), MateGeometry.Axis(couplerLink, (0, 0, 0), z)))
            .Add(Joint.Revolute(MateGeometry.Axis(couplerLink, (coupler, 0, 0), z), MateGeometry.Axis(rockerLink, (rocker, 0, 0), z)))
            .Add(Joint.Revolute(MateGeometry.Axis(frame, (span, 0, 0), z), MateGeometry.Axis(rockerLink, (0, 0, 0), z)));
        mechanism.Assemble();

        var mobility = mechanism.Mobility();

        Assert.Equal(-2, mobility.PredictedDegreesOfFreedom);
        Assert.Equal(1, mobility.MeasuredDegreesOfFreedom);
        Assert.False(mobility.Agrees);
        Assert.Contains(mobility.Notes, n => n.Contains("Bennett"));
        Assert.Contains("the RANK is the truth", mobility.ToString());
    }

    [Fact]
    public void RawMates_AreFlaggedAsInvisibleToTheFormula()
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("door"));
        var mechanism = new Mechanism(rig).Ground(fixedOne)
            .Add(Joint.Revolute(
                MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
                MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ)))
            .Add(Mate.Parallel(
                MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitX),
                MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitX)));
        mechanism.Assemble();

        var mobility = mechanism.Mobility();
        Assert.Contains(mobility.Notes, n => n.Contains("raw mate"));
    }
}
