using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>SketchPlane is a veneer over Frame3d: the historical surface (Origin/XAxis/
/// YAxis/Normal, statics, At) must be unchanged, with the frame now exposed.</summary>
public class SketchPlaneFrameTests
{
    [Fact]
    public void Statics_KeepTheirHistoricalAxes()
    {
        Assert.Equal(Vector3d.Zero, SketchPlane.XY.Origin);
        Assert.Equal(Vector3d.UnitX, SketchPlane.XY.XAxis);
        Assert.Equal(Vector3d.UnitY, SketchPlane.XY.YAxis);
        Assert.Equal(Vector3d.UnitZ, SketchPlane.XY.Normal);

        Assert.Equal(Vector3d.UnitX, SketchPlane.XZ.XAxis);
        Assert.Equal(Vector3d.UnitZ, SketchPlane.XZ.YAxis);
        Assert.Equal(new Vector3d(0, -1, 0), SketchPlane.XZ.Normal); // X × Z

        Assert.Equal(Vector3d.UnitY, SketchPlane.YZ.XAxis);
        Assert.Equal(Vector3d.UnitZ, SketchPlane.YZ.YAxis);
        Assert.Equal(Vector3d.UnitX, SketchPlane.YZ.Normal); // Y × Z
    }

    [Fact]
    public void At_MatchesFrameFromXYBitForBit()
    {
        var origin = new Vector3d(1, 2, 3);
        var xAxis = new Vector3d(0.3, -0.4, 0.5);
        var yAxis = new Vector3d(-0.2, 0.9, 0.1);
        var plane = SketchPlane.At(origin, xAxis, yAxis);
        var frame = Frame3d.FromXY(origin, xAxis, yAxis);
        Assert.Equal(frame, plane.Frame);
        Assert.Equal(frame.X, plane.XAxis);
        Assert.Equal(frame.Y, plane.YAxis);
        Assert.Equal(frame.Z, plane.Normal);
    }

    [Fact]
    public void FrameConstructor_RoundTrips()
    {
        var frame = Frame3d.FromNormal(new Vector3d(0, 0, 6), new Vector3d(0.1, 0.2, 1));
        var plane = new SketchPlane(frame);
        Assert.Equal(frame, plane.Frame);
        Assert.Equal(frame.Origin, plane.Origin);

        // ToWorld agrees with the frame's in-plane map.
        var p = new Vector2d(1.5, -0.5);
        Assert.Equal(plane.Origin + plane.XAxis * p.X + plane.YAxis * p.Y, plane.ToWorld(p));
    }
}
