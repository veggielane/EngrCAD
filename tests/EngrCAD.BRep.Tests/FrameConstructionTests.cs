using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>Frame-based construction overloads must be pure sugar: bit-identical to the
/// classic vector constructors fed the frame's own axes.</summary>
public class FrameConstructionTests
{
    private static readonly Frame3d Skew =
        Frame3d.FromXY(new Vector3d(1, -2, 3), new Vector3d(0.3, -0.4, 0.5), new Vector3d(-0.2, 0.9, 0.1));

    [Fact]
    public void PlaneSurface_FromFrame_MatchesClassicConstructor()
    {
        var fromFrame = new PlaneSurface(Skew);
        var classic = new PlaneSurface(Skew.Origin, Skew.X, Skew.Y);
        Assert.Equal(classic.Origin, fromFrame.Origin);
        Assert.Equal(classic.XDirection, fromFrame.XDirection);
        Assert.Equal(classic.YDirection, fromFrame.YDirection);
        Assert.Equal(Skew.Z, fromFrame.Normal); // plane normal is exactly the frame Z
        Assert.Equal(classic.PointAt(0.7, -1.3), fromFrame.PointAt(0.7, -1.3));
    }

    [Fact]
    public void Circle3d_FromFrame_MatchesClassicConstructor()
    {
        var fromFrame = new Circle3d(Skew, 2.5);
        var classic = new Circle3d(Skew.Origin, Skew.X, Skew.Y, 2.5);
        Assert.Equal(classic.Center, fromFrame.Center);
        Assert.Equal(classic.PointAt(1.234), fromFrame.PointAt(1.234));
        Assert.Equal(Skew.Z, fromFrame.Axis);
    }

    [Fact]
    public void Ellipse3d_FromFrame_MatchesClassicConstructor()
    {
        var fromFrame = new Ellipse3d(Skew, 3, 1.5);
        var classic = new Ellipse3d(Skew.Origin, Skew.X * 3, Skew.Y * 1.5);
        Assert.Equal(classic.Center, fromFrame.Center);
        Assert.Equal(classic.SemiAxisX, fromFrame.SemiAxisX);
        Assert.Equal(classic.SemiAxisY, fromFrame.SemiAxisY);
        Assert.Equal(classic.PointAt(0.9), fromFrame.PointAt(0.9));
    }

    [Fact]
    public void Parabola3d_FromFrame_MatchesClassicConstructor()
    {
        var domain = new Interval(-2, 2);
        var fromFrame = new Parabola3d(Skew, 0.75, domain);
        var classic = new Parabola3d(Skew.Origin, Skew.X, Skew.Y, 0.75, domain);
        Assert.Equal(classic.Apex, fromFrame.Apex);
        Assert.Equal(classic.Focus, fromFrame.Focus);
        Assert.Equal(classic.PointAt(1.1), fromFrame.PointAt(1.1));
    }

    [Fact]
    public void Hyperbola3d_FromFrame_MatchesClassicConstructor()
    {
        var domain = new Interval(-1, 1);
        var fromFrame = new Hyperbola3d(Skew, 2, 0.5, domain);
        var classic = new Hyperbola3d(Skew.Origin, Skew.X * 2, Skew.Y * 0.5, domain);
        Assert.Equal(classic.Center, fromFrame.Center);
        Assert.Equal(classic.SemiAxisX, fromFrame.SemiAxisX);
        Assert.Equal(classic.SemiAxisY, fromFrame.SemiAxisY);
        Assert.Equal(classic.PointAt(0.4), fromFrame.PointAt(0.4));
    }
}
