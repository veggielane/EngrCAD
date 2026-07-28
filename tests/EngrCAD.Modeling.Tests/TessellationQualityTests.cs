using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="TessellationQuality"/>: adaptive curvature-driven segment counts
/// (OpenSCAD $fa / OCCT deflection), opt-in via <see cref="MeshQuality.Tessellation"/>.
/// The contract under test: (a) the criterion math is exact, (b) the default path is
/// bit-identical to the fixed counts, and (c) ONE criterion drives the display mesh AND
/// the feature-edge overlay so they agree by construction — the documented fix for the
/// overlay visibly detaching from the fill on large rims.
/// </summary>
public class TessellationQualityTests
{
    // ---------------------------------------------------------------- criterion math

    [Fact]
    public void AngleCriterion_IsRadiusFree()
    {
        var quality = new TessellationQuality { MaxAngleDegrees = 11.25 };   // 360/32
        Assert.Equal(32, quality.SegmentsFor(0.001));
        Assert.Equal(32, quality.SegmentsFor(1));
        Assert.Equal(32, quality.SegmentsFor(1000));
    }

    [Fact]
    public void ChordCriterion_MatchesTheSagittaFormula()
    {
        // Sagitta of an inscribed n-gon chord: s = r(1 - cos(pi/n)). For r = 10 and
        // n = 64 that is s = 10*(1 - cos(pi/64)); asking for a hair MORE deviation
        // must give exactly 64 segments, and a hair less must give 65.
        double radius = 10;
        double sagitta = radius * (1 - Math.Cos(Math.PI / 64));
        var justLoose = new TessellationQuality { MaxChordDeviation = sagitta * (1 + 1e-9), MinSegments = 3 };
        var justTight = new TessellationQuality { MaxChordDeviation = sagitta * (1 - 1e-9), MinSegments = 3 };
        Assert.Equal(64, justLoose.SegmentsFor(radius));
        Assert.Equal(65, justTight.SegmentsFor(radius));
    }

    [Fact]
    public void ChordCriterion_GrowsWithRadius()
    {
        // Fixed absolute deviation: a larger circle needs more segments (n ~ sqrt(r/d)).
        var quality = new TessellationQuality { MaxChordDeviation = 0.05, MinSegments = 3 };
        int small = quality.SegmentsFor(5);
        int large = quality.SegmentsFor(500);
        Assert.True(large > small, $"expected growth, got {small} -> {large}");
        // Quadrupling the radius should roughly double the count.
        int quadrupled = quality.SegmentsFor(20);
        Assert.InRange(quadrupled, 2 * small - 2, 2 * small + 2);
    }

    [Fact]
    public void Clamps_AndDegenerateRadii()
    {
        var quality = new TessellationQuality
        {
            MaxChordDeviation = 1e-9,
            MinSegments = 12,
            MaxSegments = 64,
        };
        Assert.Equal(64, quality.SegmentsFor(100));      // clamped high
        Assert.Equal(12, quality.SegmentsFor(0));        // no curvature: floor
        Assert.Equal(12, quality.SegmentsFor(1e-12));    // deviation >= radius: floor
    }

    [Fact]
    public void MisconfiguredQuality_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new TessellationQuality().SegmentsFor(1));
        Assert.Throws<InvalidOperationException>(
            () => new TessellationQuality { MaxAngleDegrees = -5 }.SegmentsFor(1));
        Assert.Throws<InvalidOperationException>(
            () => new TessellationQuality { MaxChordDeviation = 0 }.SegmentsFor(1));
        Assert.Throws<InvalidOperationException>(
            () => new TessellationQuality { MaxAngleDegrees = 10, MinSegments = 2 }.SegmentsFor(1));
        Assert.Throws<InvalidOperationException>(
            () => new TessellationQuality { MaxAngleDegrees = 10, MinSegments = 16, MaxSegments = 8 }.SegmentsFor(1));
    }

    // ------------------------------------------------------------ solid resolution

    [Fact]
    public void ResolveFor_BindsAtTheLargestRadius()
    {
        // A cone from radius 20 down to radius 2: the chord criterion must size the
        // count from the LARGE rim (the small rim inherits more segments than it
        // needs, never fewer than the criterion demands).
        var cone = Shape.Cone(20, 2, 8).ToBrep();
        var quality = new TessellationQuality { MaxChordDeviation = 0.05, MinSegments = 3 };
        var (segments, curveSamples) = quality.ResolveFor(cone);

        Assert.Equal(quality.SegmentsFor(20), segments);
        Assert.True(segments > quality.SegmentsFor(2));
        Assert.True(curveSamples >= 8);
    }

    [Fact]
    public void ResolveFor_PlanarSolid_UsesTheFloor()
    {
        var box = Shape.Box(10, 10, 10).ToBrep();
        var quality = new TessellationQuality { MaxChordDeviation = 0.01, MinSegments = 8 };
        var (segments, _) = quality.ResolveFor(box);
        Assert.Equal(8, segments);   // no curvature anywhere: the floor, and it is moot
    }

    // ------------------------------------------------- default-path bit-compatibility

    [Fact]
    public void DefaultQuality_IsBitIdenticalToTheFixedCounts()
    {
        // Tessellation == null must reproduce the incumbent path exactly (the docs PNG
        // oracle depends on it): same mesh as calling the tessellator with the fixed
        // counts directly.
        var shape = Shape.Cylinder(5, 10);
        var viaPart = new Part("cyl", shape).GetMesh(new MeshQuality());
        var direct = Interop.BRepTessellator.Tessellate(shape.ToBrep(), 32, 24);

        Assert.Equal(direct.VertexCount, viaPart.VertexCount);
        Assert.Equal(direct.FaceCount, viaPart.FaceCount);
        var (expected, _) = direct.ToIndexed();
        var (actual, _) = viaPart.ToIndexed();
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);   // exact bits, not a tolerance
    }

    // ---------------------------------------------- mesh/overlay agreement (the fix)

    [Fact]
    public void AdaptiveQuality_MeshAndFeatureEdgesAgreeByConstruction()
    {
        // Under an adaptive quality the rim circle in the OVERLAY and the rim polygon
        // in the MESH must have the same segment count — the whole point of the type.
        var quality = new MeshQuality
        {
            Tessellation = new TessellationQuality { MaxChordDeviation = 0.02, MinSegments = 3 },
        };
        double radius = 40;
        var part = new Part("disc", Shape.Cylinder(radius, 8));
        int expected = quality.Tessellation.SegmentsFor(radius);

        // Overlay: two sharp rims, one segment per sample step each.
        var edges = part.GetFeatureEdges(quality);
        Assert.Equal(2 * expected, edges.Count);

        // Mesh: the top rim polygon has exactly that many vertices.
        var mesh = part.GetMesh(quality);
        var (positions, _) = mesh.ToIndexed();
        Assert.Equal(expected, positions.Count(p =>
            Math.Abs(p.Z - 4) < 1e-9 && Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - radius) < 1e-6));

        // And the fixed-count path would NOT have agreed (96 vs 32) — the detachment.
        var fixedPart = new Part("disc2", Shape.Cylinder(radius, 8));
        var fixedEdges = fixedPart.GetFeatureEdges(new MeshQuality());
        Assert.Equal(2 * 96, fixedEdges.Count);
    }

    [Fact]
    public void AdaptiveQuality_FlowsThroughShapeToMesh()
    {
        var quality = new MeshQuality
        {
            Tessellation = new TessellationQuality { MaxAngleDegrees = 5, MinSegments = 3 },   // 72 segments
        };
        var mesh = Shape.Cylinder(3, 6).ToMesh(quality);
        var (positions, _) = mesh.ToIndexed();
        int rim = positions.Count(p =>
            Math.Abs(p.Z - 3) < 1e-9 && Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 3) < 1e-6);
        Assert.Equal(72, rim);
    }
}
