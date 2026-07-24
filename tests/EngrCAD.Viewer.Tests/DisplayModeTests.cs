using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>Per-part display modes (state layer; rendering is verified visually).</summary>
public class DisplayModeTests
{
    [Fact]
    public void Part_DefaultsToShaded()
    {
        var part = new Part("box", MeshPrimitives.Box(1, 1, 1));
        Assert.Equal(DisplayMode.Shaded, part.DisplayMode);
    }

    [Fact]
    public void Part_ModeIsSettable_AndSurvivesSceneAdd()
    {
        var scene = new Scene();
        var part = scene.Add(new Part("glass", MeshPrimitives.Box(1, 1, 1)));
        part.DisplayMode = DisplayMode.Translucent;
        Assert.Equal(DisplayMode.Translucent, scene.AllParts.Single().DisplayMode);
    }

    [Fact]
    public void WireframeEdges_ExtractsEveryUniqueEdgeOnce()
    {
        // A quad-faced box has 12 edges; triangulated it gains one diagonal per face: 18.
        Assert.Equal(12, WireframeEdges.Extract(MeshPrimitives.Box(1, 1, 1)).Count);
        Assert.Equal(18, WireframeEdges.Extract(MeshPrimitives.Box(1, 1, 1).Triangulated()).Count);
    }

    [Fact]
    public void WireframeEdges_CountMatchesEulerFormula()
    {
        // Closed genus-0 mesh: V - E + F = 2, so E = V + F - 2.
        var sphere = MeshPrimitives.UvSphere(1, segments: 16, rings: 8);
        var edges = WireframeEdges.Extract(sphere);
        Assert.Equal(sphere.VertexCount + sphere.FaceCount - 2, edges.Count);
    }
}
