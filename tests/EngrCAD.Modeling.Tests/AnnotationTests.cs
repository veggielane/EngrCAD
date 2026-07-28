using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// 3D annotations (PMI): measurement correctness against analytic ground truth
/// (exactly-known box/cylinder dimensions), selector survival through feature-history
/// regeneration (the topological-naming story), resolution caching and error paths on
/// <see cref="Part"/>, and dimension text formatting.
/// </summary>
public class AnnotationTests
{
    private const double Exact = 1e-9;   // measured values come from exact construction

    // ---- point-to-point ----

    [Fact]
    public void PointToPoint_MeasuresDistance()
    {
        var dimension = new LinearDimension((0, 0, 0), (3, 4, 0));
        var resolved = dimension.Resolve();

        Assert.Equal(AnnotationKind.LinearDimension, resolved.Kind);
        Assert.Equal(5.0, resolved.Value, Exact);
        Assert.Equal("5", resolved.Text);
        Assert.Equal(new Vector3d(0, 0, 0), resolved.AnchorA);
        Assert.Equal(new Vector3d(3, 4, 0), resolved.AnchorB);
    }

    [Fact]
    public void Label_OverridesFormattedValue()
    {
        var dimension = new LinearDimension((0, 0, 0), (10, 0, 0)) { Label = "10 REF" };
        Assert.Equal("10 REF", dimension.Resolve().Text);
    }

    // ---- face-selector linear dimensions ----

    private static Func<BrepSolid, BrepFace> FaceToward(Vector3d direction) =>
        s => s.PlanarFacesWithNormal(direction).First();

    [Fact]
    public void BetweenFaces_MeasuresBoxHeight()
    {
        var solid = Shape.Box(40, 20, 10).ToBrep();
        var dimension = LinearDimension.BetweenFaces(
            FaceToward(Vector3d.UnitZ), FaceToward(-Vector3d.UnitZ));
        var resolved = dimension.Resolve(solid);

        Assert.Equal(10.0, resolved.Value, Exact);
        Assert.Equal("10", resolved.Text);
        // The anchors differ by exactly the measured distance.
        Assert.Equal(10.0, resolved.AnchorA.DistanceTo(resolved.AnchorB), Exact);
    }

    [Fact]
    public void BetweenFaces_MeasuresBoxWidthAcrossXFaces()
    {
        var solid = Shape.Box(40, 20, 10).ToBrep();
        var dimension = LinearDimension.BetweenFaces(
            FaceToward(-Vector3d.UnitX), FaceToward(Vector3d.UnitX));

        Assert.Equal(40.0, dimension.Resolve(solid).Value, Exact);
    }

    [Fact]
    public void BetweenFaces_NonParallelFaces_Throws()
    {
        var solid = Shape.Box(10, 10, 10).ToBrep();
        var dimension = LinearDimension.BetweenFaces(
            FaceToward(Vector3d.UnitZ), FaceToward(Vector3d.UnitX));

        var e = Assert.Throws<InvalidOperationException>(() => dimension.Resolve(solid));
        Assert.Contains("parallel", e.Message);
    }

    [Fact]
    public void SelectorDimension_WithoutSolid_Throws()
    {
        var dimension = LinearDimension.BetweenFaces(
            FaceToward(Vector3d.UnitZ), FaceToward(-Vector3d.UnitZ));
        Assert.Throws<InvalidOperationException>(() => dimension.Resolve());
    }

    // ---- radial dimensions ----

    private static Func<BrepSolid, BrepEdge> FirstCircularEdge(double? radius = null) =>
        s => s.Faces.SelectMany(f => f.Edges()).Distinct().First(e =>
            e.IsCircular(out _, out _, out double r) && (radius is null || Math.Abs(r - radius.Value) < 1e-9));

    [Fact]
    public void RadialDimension_ReadsCylinderRadius()
    {
        var solid = Shape.Cylinder(5, 10).ToBrep();
        var resolved = RadialDimension.OnEdge(FirstCircularEdge()).Resolve(solid);

        Assert.Equal(AnnotationKind.RadialDimension, resolved.Kind);
        Assert.Equal(5.0, resolved.Value, Exact);
        Assert.Equal("R5", resolved.Text);
        // AnchorA lies on the circle, AnchorB is its center.
        Assert.Equal(5.0, resolved.AnchorA.DistanceTo(resolved.AnchorB), Exact);
    }

    [Fact]
    public void RadialDimension_DiameterMode_UsesDiameterSignAndValue()
    {
        var solid = Shape.Cylinder(5, 10).ToBrep();
        var resolved = RadialDimension.OnEdge(FirstCircularEdge(), diameter: true).Resolve(solid);

        Assert.Equal(10.0, resolved.Value, Exact);
        Assert.Equal("\u230010", resolved.Text);
    }

    [Fact]
    public void RadialDimension_OnDrilledBore_ReadsSpecRadius()
    {
        // A drilled plate: the bore rim's radius is the spec's clearance radius.
        var spec = StandardHoles.Clearance(5);   // ISO 273 normal fit: 5.5 mm bore
        var plate = Shape.Box(30, 30, 8)
            .Drill(spec, [new Vector2d(0, 0)], 10, SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY));
        var solid = plate.ToBrep();

        var resolved = RadialDimension.OnEdge(FirstCircularEdge(2.75), diameter: true).Resolve(solid);
        Assert.Equal(5.5, resolved.Value, Exact);
        Assert.Equal("\u23005.5", resolved.Text);
    }

    [Fact]
    public void RadialDimension_NonCircularEdge_Throws()
    {
        var solid = Shape.Box(10, 10, 10).ToBrep();
        var dimension = RadialDimension.OnEdge(
            s => s.Faces.SelectMany(f => f.Edges()).First());
        var e = Assert.Throws<InvalidOperationException>(() => dimension.Resolve(solid));
        Assert.Contains("not circular", e.Message);
    }

    // ---- notes and datums ----

    [Fact]
    public void LeaderNote_CarriesTextAndAnchor()
    {
        var note = new LeaderNote((1, 2, 3), "DEBURR");
        var resolved = note.Resolve();

        Assert.Equal(AnnotationKind.LeaderNote, resolved.Kind);
        Assert.Equal("DEBURR", resolved.Text);
        Assert.Equal(new Vector3d(1, 2, 3), resolved.AnchorA);
        Assert.Equal(resolved.AnchorA, resolved.AnchorB);
    }

    [Fact]
    public void DatumLabel_CarriesLetter()
    {
        var resolved = new DatumLabel((0, 0, 5), "A").Resolve();
        Assert.Equal(AnnotationKind.DatumLabel, resolved.Kind);
        Assert.Equal("A", resolved.Text);
    }

    [Fact]
    public void EmptyNoteText_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LeaderNote((0, 0, 0), "  "));
        Assert.Throws<ArgumentException>(() => new DatumLabel((0, 0, 0), ""));
    }

    // ---- the selector-survival story: re-measure through regeneration ----

    [Fact]
    public void SelectorDimension_SurvivesParameterEdit()
    {
        // A parametric block: extrude a rectangle, then change its height. The SAME
        // dimension object re-measures the regenerated body via its selectors — no
        // persisted indices, no stale values (the topological-naming story).
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(40, 20)) { Height = 20 });
        var dimension = LinearDimension.BetweenFaces(
            FaceToward(Vector3d.UnitZ), FaceToward(-Vector3d.UnitZ));

        var first = history.Regenerate();
        Assert.True(first.Succeeded);
        Assert.Equal(20.0, dimension.Resolve(first.Body!.ToBrep()).Value, Exact);

        history.Replace(0, new ExtrudeSketchFeature(Sketch.Rectangle(40, 20)) { Height = 30 });
        var second = history.Regenerate();
        Assert.True(second.Succeeded);
        Assert.Equal(30.0, dimension.Resolve(second.Body!.ToBrep()).Value, Exact);
    }

    // ---- Part attachment, caching, error paths ----

    [Fact]
    public void Part_ResolvesAttachedAnnotations()
    {
        var part = new Part("block", Shape.Box(40, 20, 10))
            .Annotate(LinearDimension.BetweenFaces(
                FaceToward(Vector3d.UnitZ), FaceToward(-Vector3d.UnitZ)))
            .Annotate(new LeaderNote((0, 0, 5), "TOP"));

        var resolved = part.ResolveAnnotations();
        Assert.Equal(2, resolved.Count);
        Assert.Equal(10.0, resolved[0].Value, Exact);
        Assert.Equal("TOP", resolved[1].Text);
    }

    [Fact]
    public void Part_ResolutionIsCached_AndInvalidatedByAnnotate()
    {
        var part = new Part("block", Shape.Box(4, 4, 4))
            .Annotate(new LeaderNote((0, 0, 2), "A"));
        var first = part.ResolveAnnotations();
        Assert.Same(first, part.ResolveAnnotations());   // cached list identity

        part.Annotate(new LeaderNote((0, 0, -2), "B"));
        var second = part.ResolveAnnotations();
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void Part_SelectorAnnotationOnMeshGeometry_ReportsError()
    {
        var mesh = Shape.Box(2, 2, 2).ToMesh();
        var part = new Part("meshpart", mesh)
            .Annotate(LinearDimension.BetweenFaces(
                FaceToward(Vector3d.UnitZ), FaceToward(-Vector3d.UnitZ)));

        Assert.False(part.TryResolveAnnotations(out var resolved, out string? error));
        Assert.Empty(resolved);
        Assert.Contains("meshpart", error);
        Assert.Throws<InvalidOperationException>(() => part.ResolveAnnotations());
    }

    [Fact]
    public void Part_PointAnnotationsNeverLower()
    {
        // Point-anchored annotations resolve without a B-Rep — even on mesh parts.
        var part = new Part("meshpart", Shape.Box(2, 2, 2).ToMesh())
            .Annotate(new LinearDimension((0, 0, -1), (0, 0, 1)));
        var resolved = part.ResolveAnnotations();
        Assert.Equal(2.0, resolved[0].Value, Exact);
    }

    [Fact]
    public void PreMesh_ResolvesAnnotationsWithoutThrowing()
    {
        var scene = new Scene();
        var good = scene.Add(new Part("good", Shape.Box(10, 10, 10))
            .Annotate(LinearDimension.BetweenFaces(
                FaceToward(Vector3d.UnitZ), FaceToward(-Vector3d.UnitZ))));
        var bad = scene.Add(new Part("bad", Shape.Box(2, 2, 2).ToMesh())
            .Annotate(LinearDimension.BetweenFaces(
                FaceToward(Vector3d.UnitZ), FaceToward(-Vector3d.UnitZ))));

        scene.PreMesh();   // must not throw despite the bad part

        Assert.True(good.TryResolveAnnotations(out var resolved, out _));
        Assert.Equal(10.0, resolved[0].Value, Exact);
        Assert.False(bad.TryResolveAnnotations(out _, out string? error));
        Assert.NotNull(error);
    }

    // ---- formatting ----

    [Theory]
    [InlineData(40.0, "40")]
    [InlineData(5.5, "5.5")]
    [InlineData(0.125, "0.125")]
    [InlineData(1.0 / 3.0 * 100, "33.333")]
    public void ValueFormatting_TrimsTrailingZeros(double value, string expected)
    {
        var resolved = new LinearDimension((0, 0, 0), (value, 0, 0)).Resolve();
        Assert.Equal(expected, resolved.Text);
    }

    // ---- callout generators ----

    [Fact]
    public void HoleCallout_Simple_DiameterAndDepth()
    {
        // M5 normal clearance = 5.5; symbols: \u2300 diameter, \u21A7 depth.
        Assert.Equal("\u23005.5 \u21A714", HoleCallout.Text(StandardHoles.Clearance(5), 14));
    }

    [Fact]
    public void HoleCallout_Counterbore_AppendsRecess()
    {
        // DIN 974 M5 cbore: bore 5.5, recess diameter 10, recess depth 5.5. The
        // counterbore is a CONTINUATION LINE (drawing convention; the stroke-font
        // layout stacks '\n'-separated lines).
        Assert.Equal("\u23005.5 \u21A714\n\u2334\u230010 \u21A75.5",
            HoleCallout.Text(StandardHoles.Counterbored(5), 14));
    }

    [Fact]
    public void HoleCallout_Countersink_AppendsConeAndAngle()
    {
        // ISO 10642 M5 csk: bore 5.5, cone diameter 11.6, 90 degrees; the cone is a
        // continuation line like the counterbore's.
        Assert.Equal("\u23005.5 \u21A714\n\u2335\u230011.6 \u00D790\u00B0",
            HoleCallout.Text(StandardHoles.Countersunk(5), 14));
    }

    [Fact]
    public void HoleCallout_From_ProducesLeaderNote()
    {
        var note = HoleCallout.From(HoleSpec.Simple(6.6), (10, 0, 4), 12);
        var resolved = note.Resolve();
        Assert.Equal(AnnotationKind.LeaderNote, resolved.Kind);
        Assert.Equal("\u23006.6 \u21A712", resolved.Text);
        Assert.Equal(new Vector3d(10, 0, 4), resolved.AnchorA);
    }

    [Fact]
    public void ThreadCallout_UsesDesignationAndDepth()
    {
        var spec = StandardThreads.Metric(6);
        Assert.Equal("M6\u00D71", ThreadCallout.Text(spec));
        Assert.Equal("M6\u00D71 \u21A712", ThreadCallout.Text(spec, 12));
        Assert.Equal("M6\u00D71 \u21A712", ThreadCallout.From(spec, (0, 0, 0), 12).Resolve().Text);
    }

    [Fact]
    public void Callouts_RejectNonPositiveDepth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HoleCallout.Text(HoleSpec.Simple(5), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThreadCallout.Text(StandardThreads.Metric(6), -1));
    }
}
