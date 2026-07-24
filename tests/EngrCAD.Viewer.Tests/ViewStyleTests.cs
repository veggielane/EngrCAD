using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The global-view-style x per-part-display-mode precedence (RenderModes.Resolve is
/// the single rule both render passes use) and the shared back-to-front sort.
/// </summary>
public class ViewStyleTests
{
    [Fact]
    public void Resolve_DefaultShadedParts_FollowTheGlobalStyle()
    {
        Assert.Equal(EffectiveMode.Points, RenderModes.Resolve(ViewStyle.Points, DisplayMode.Shaded));
        Assert.Equal(EffectiveMode.Wireframe, RenderModes.Resolve(ViewStyle.Wireframe, DisplayMode.Shaded));
        Assert.Equal(EffectiveMode.Shaded, RenderModes.Resolve(ViewStyle.Shaded, DisplayMode.Shaded));
        Assert.Equal(EffectiveMode.ShadedWithEdges, RenderModes.Resolve(ViewStyle.ShadedWithEdges, DisplayMode.Shaded));
    }

    [Fact]
    public void Resolve_ExplicitPartModes_OverrideEveryGlobalStyle()
    {
        foreach (var style in Enum.GetValues<ViewStyle>())
        {
            Assert.Equal(EffectiveMode.Wireframe, RenderModes.Resolve(style, DisplayMode.Wireframe));
            Assert.Equal(EffectiveMode.Translucent, RenderModes.Resolve(style, DisplayMode.Translucent));
        }
    }

    [Fact]
    public void SortBackToFront_OrdersDescendingByDepth()
    {
        int[] order = [0, 1, 2, 3];
        double[] depth = [2.0, 9.0, 1.0, 5.0];
        RenderModes.SortBackToFront(order, depth, 4);
        Assert.Equal([1, 3, 0, 2], order);
        Assert.Equal([9.0, 5.0, 2.0, 1.0], depth);
    }

    [Fact]
    public void SortBackToFront_HonorsCount_LeavingTheTailUntouched()
    {
        int[] order = [0, 1, 2, 9];
        double[] depth = [1.0, 3.0, 2.0, 99.0];
        RenderModes.SortBackToFront(order, depth, 3);
        Assert.Equal([1, 2, 0, 9], order);
        Assert.Equal(99.0, depth[3]);
    }

    [Fact]
    public void SectionAxis_DirectionsAreTheWorldUnitVectors()
    {
        Assert.Equal(EngrCAD.Core.Vector3d.UnitX, SectionAxis.X.Direction());
        Assert.Equal(EngrCAD.Core.Vector3d.UnitY, SectionAxis.Y.Direction());
        Assert.Equal(EngrCAD.Core.Vector3d.UnitZ, SectionAxis.Z.Direction());
    }
}
