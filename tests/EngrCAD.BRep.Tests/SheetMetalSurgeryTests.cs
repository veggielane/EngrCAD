using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Sheet-metal edge flanges as topology surgery: the bend is welded straight into the
/// parent's loops, never unioned, because it meets both sheets tangentially. Structure
/// and refusals live here; the volume oracles (folded versus flat) need mass properties
/// and live in EngrCAD.Modeling.Tests.
/// </summary>
public class SheetMetalSurgeryTests
{
    private const double Thickness = 1.5;
    private const double Radius = 2.0;
    private const double Length = 60;
    private const double Width = 40;

    /// <summary>A plate spanning x in [0, 60], y in [0, 40], z in [0, T].</summary>
    private static BrepSolid Plate() =>
        SolidFactory.Extrude(
            Profile.FromPoints([(0, 0, 0), (Length, 0, 0), (Length, Width, 0), (0, Width, 0)]),
            (0, 0, Thickness));

    /// <summary>A bend on the plate's +X wall, folding up (so the top face is the inside
    /// of the bend, per the "a flange folds toward the face you named" rule).</summary>
    private static SheetBendSection AtPlusX(double angleDegrees) => new(
        BendLinePoint: (Length, 0, Thickness),
        Inside: Vector3d.UnitZ,
        Outward: Vector3d.UnitX,
        Thickness: Thickness,
        BendRadius: Radius,
        AngleRadians: angleDegrees * Math.PI / 180);

    [Fact]
    public void FullWidthNinetyDegreeFlange_IsAValidClosedSolid()
    {
        var solid = SheetMetalSurgery.AddEdgeFlange(
            Plate(), AtPlusX(90), (Length, 0, Thickness), (Length, Width, Thickness),
            wallLength: 20 - (Radius + Thickness));

        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula());
        // The wall goes, five faces arrive; the two end walls absorb the cross-section.
        Assert.Equal(10, solid.Faces.Count());
        Assert.Equal(24, solid.Edges.Count());
        Assert.Equal(16, solid.Vertices.Count());
    }

    [Fact]
    public void ANinetyDegreeFlangesTipLandsWhereTheClosedFormPutsIt()
    {
        // "Bend outside": the bend's tangent line IS the named edge, so the material
        // continues outboard through the bend and the flange's outer face lands at
        // x = 60 + (R + T). The outer virtual sharp is the corner where that plane meets
        // the plate's own bottom plane, at (63.5, y, 0), and a 25 mm flange measured from
        // it reaches exactly z = 25.
        const double flangeLength = 25;
        double setback = Radius + Thickness;   // tan(45 degrees) = 1
        var solid = SheetMetalSurgery.AddEdgeFlange(
            Plate(), AtPlusX(90), (Length, 0, Thickness), (Length, Width, Thickness),
            wallLength: flangeLength - setback);

        var bounds = Aabb.Empty;
        foreach (var vertex in solid.Vertices)
            bounds = bounds.Union(vertex.Position);

        Assert.Equal(Length + Radius + Thickness, bounds.Max.X, 9);
        Assert.Equal(flangeLength, bounds.Max.Z, 9);
        Assert.Equal(0.0, bounds.Min.Z, 9);
    }

    [Fact]
    public void InsetFlange_SplitsTheWallIntoTwoStubsAndStaysValid()
    {
        var solid = SheetMetalSurgery.AddEdgeFlange(
            Plate(), AtPlusX(90), (Length, 8, Thickness), (Length, 32, Thickness),
            wallLength: 20 - (Radius + Thickness));

        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula());
        // Two wall stubs plus two end caps replace the one wall, beside the five bend and
        // flange faces.
        Assert.Equal(14, solid.Faces.Count());
    }

    [Fact]
    public void TwoOppositeFullWidthFlanges_MakeAChannel()
    {
        var one = SheetMetalSurgery.AddEdgeFlange(
            Plate(), AtPlusX(90), (Length, 0, Thickness), (Length, Width, Thickness),
            wallLength: 20 - (Radius + Thickness));

        var other = new SheetBendSection(
            BendLinePoint: (0, 0, Thickness),
            Inside: Vector3d.UnitZ,
            Outward: -Vector3d.UnitX,
            Thickness: Thickness,
            BendRadius: Radius,
            AngleRadians: Math.PI / 2);
        var channel = SheetMetalSurgery.AddEdgeFlange(
            one, other, (0, 0, Thickness), (0, Width, Thickness),
            wallLength: 20 - (Radius + Thickness));

        channel.Validate();
        Assert.True(channel.SatisfiesEulerFormula());
        Assert.Equal(14, channel.Faces.Count());
    }

    [Fact]
    public void AFlangeOnAFlangesTip_ChainsThroughTheSameSurgery()
    {
        var first = SheetMetalSurgery.AddEdgeFlange(
            Plate(), AtPlusX(90), (Length, 0, Thickness), (Length, Width, Thickness),
            wallLength: 20 - (Radius + Thickness));

        // The first flange's inside face is the x = 62 plane looking back along -X, and
        // its tip is at z = 20; a second bend on that edge folds a return lip back over
        // the plate.
        double insideX = Length + Radius;
        var second = new SheetBendSection(
            BendLinePoint: (insideX, 0, 20),
            Inside: -Vector3d.UnitX,
            Outward: Vector3d.UnitZ,
            Thickness: Thickness,
            BendRadius: Radius,
            AngleRadians: Math.PI / 2);
        var lipped = SheetMetalSurgery.AddEdgeFlange(
            first, second, (insideX, 0, 20), (insideX, Width, 20),
            wallLength: 10 - (Radius + Thickness));

        lipped.Validate();
        Assert.True(lipped.SatisfiesEulerFormula());
    }

    [Fact]
    public void BendAngleOfOneEighty_IsRefusedAsAHem()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SheetMetalSurgery.AddEdgeFlange(
                Plate(), AtPlusX(180), (Length, 0, Thickness), (Length, Width, Thickness), 10));
        Assert.Contains("HEM", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LengthShorterThanTheSetback_IsRefusedNamingTheVirtualSharp()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SheetMetalSurgery.AddEdgeFlange(
                Plate(), AtPlusX(90), (Length, 0, Thickness), (Length, Width, Thickness), wallLength: -1));
        Assert.Contains("OUTER VIRTUAL SHARP", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlangeFlushAtOneEndOnly_IsRefusedAsACornerInteraction()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            SheetMetalSurgery.AddEdgeFlange(
                Plate(), AtPlusX(90), (Length, 0, Thickness), (Length, 30, Thickness),
                wallLength: 20 - (Radius + Thickness)));
        Assert.Contains("inset from BOTH ends", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFlangesMeetingAtACorner_AreRefusedByName()
    {
        var one = SheetMetalSurgery.AddEdgeFlange(
            Plate(), AtPlusX(90), (Length, 0, Thickness), (Length, Width, Thickness),
            wallLength: 20 - (Radius + Thickness));

        // The +Y wall absorbed the first flange's cross-section, so a flange there would
        // have to close a corner.
        var adjacent = new SheetBendSection(
            BendLinePoint: (0, Width, Thickness),
            Inside: Vector3d.UnitZ,
            Outward: Vector3d.UnitY,
            Thickness: Thickness,
            BendRadius: Radius,
            AngleRadians: Math.PI / 2);
        var exception = Assert.Throws<NotSupportedException>(() =>
            SheetMetalSurgery.AddEdgeFlange(
                one, adjacent, (0, Width, Thickness), (Length, Width, Thickness),
                wallLength: 20 - (Radius + Thickness)));
        Assert.Contains("CORNER", exception.Message, StringComparison.Ordinal);
    }
}
