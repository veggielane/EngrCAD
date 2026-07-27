using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The construction tree: how a part's build history maps to rows, how a row maps back
/// to the graph node it came from, and how per-node previews are produced and cached.
/// All headless — the viewer only walks this model.
/// </summary>
public class ConstructionTreeTests
{
    // ---- shape graph -> rows ----

    [Fact]
    public void BooleanGraphBecomesNestedRows()
    {
        var box = Shape.Box(2, 2, 2);
        var pin = Shape.Cylinder(0.5, 3);
        var root = ConstructionTree.FromShape(box - pin);

        Assert.Equal("Difference", root.Label);
        Assert.Equal(ConstructionNodeKind.Operation, root.Kind);
        Assert.Equal(2, root.Children.Count);
        // Labels come from Shape.Describe() — the same text Explain prints.
        Assert.StartsWith("Box(", root.Children[0].Label);
        Assert.StartsWith("Cylinder(", root.Children[1].Label);
        Assert.Equal(ConstructionNodeKind.Primitive, root.Children[0].Kind);
    }

    [Fact]
    public void RowsCarryTheGraphNodeByReference()
    {
        var box = Shape.Box(2, 2, 2);
        var pin = Shape.Cylinder(0.5, 3);
        var root = ConstructionTree.FromShape(box | pin);

        Assert.Same(box, root.Children[0].Target);
        Assert.Same(pin, root.Children[1].Target);
    }

    [Fact]
    public void PathsAreUniqueAndAddressable()
    {
        var root = ConstructionTree.FromShape(
            (Shape.Box(2, 2, 2) - Shape.Cylinder(0.5, 3).Translate(0.5, 0, 0)) | Shape.Sphere(0.4));

        var all = root.Flatten().ToList();
        Assert.Equal(all.Count, all.Select(n => n.Path).Distinct().Count());
        Assert.Equal("", root.Path);
        foreach (var node in all)
            Assert.Same(node, root.Find(node.Path));
        Assert.Null(root.Find("nope"));
    }

    [Fact]
    public void TransformsAppearAsRowsOverTheirChild()
    {
        var cylinder = Shape.Cylinder(0.5, 3);
        var root = ConstructionTree.FromShape(cylinder.Translate(1, 0, 0));

        Assert.Equal("Transform", root.Label);
        var child = Assert.Single(root.Children);
        Assert.Same(cylinder, child.Target);
    }

    [Fact]
    public void SketchExtrudeExposesItsSketchAndPlane()
    {
        var sketch = Sketch.Rectangle(4, 3);
        var plane = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
        var root = ConstructionTree.FromShape(Shape.Extrude(sketch, 2, plane));

        Assert.Equal("Extrude(sketch)", root.Label);
        var row = Assert.Single(root.Children);
        Assert.Equal(ConstructionNodeKind.Sketch, row.Kind);
        Assert.Same(sketch, row.Sketch);
        Assert.Null(row.Target);
        Assert.True(row.CanPreview);
        // The placement is the sketch plane, so the preview lands on it.
        Assert.Equal(5, row.Placement.TransformPoint(Vector3d.Zero).Z, 12);
    }

    [Fact]
    public void SketchRowCountsCurvesAndHoles()
    {
        var sketch = Sketch.Rectangle(4, 3).WithHole(Sketch.Circle(0.5));
        var root = ConstructionTree.FromShape(Shape.Extrude(sketch, 2));
        var row = Assert.Single(root.Children);
        Assert.Contains("4 curves", row.Label);
        Assert.Contains("1 holes", row.Label);
    }

    [Fact]
    public void DrillShowsTheBodyItCutsAsItsChild()
    {
        var body = Shape.Box(30, 20, 12);
        var plane = SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);
        var root = ConstructionTree.FromShape(
            body.Drill(StandardHoles.Clearance(5), [new(0, 0), new(10, 0)], depth: 14, plane));

        Assert.Equal("Drill(2 holes)", root.Label);
        var child = Assert.Single(root.Children);
        Assert.Same(body, child.Target);
    }

    [Fact]
    public void PatternsShowTheirBalancedUnionTree()
    {
        var unit = Shape.Cylinder(0.3, 1);
        var root = ConstructionTree.FromShape(unit.PatternLinear(4, (2, 0, 0)));

        // 4 copies -> a balanced union tree whose leaves are the original plus three
        // transforms of it; every leaf resolves back to the same cylinder node.
        var leaves = root.Flatten().Where(n => n.Children.Count == 0).ToList();
        Assert.Equal(4, leaves.Count);
        Assert.All(leaves, leaf => Assert.Same(unit, leaf.Target));
    }

    [Fact]
    public void RawGeometryPartsHaveNoConstructionTree()
    {
        var part = new Part("mesh", MeshPrimitives.Box(1, 1, 1));
        Assert.Null(part.ConstructionTree());
    }

    [Fact]
    public void PartCachesItsConstructionTree()
    {
        var part = new Part("body", Shape.Box(2, 2, 2) | Shape.Sphere(1));
        var first = part.ConstructionTree();
        Assert.NotNull(first);
        Assert.Same(first, part.ConstructionTree());
    }

    // ---- feature history -> rows ----

    private static FeatureHistory PlateHistory(bool suppressChamfer = false)
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(40, 30)) { Height = 10, Name = "plate" });
        history.Add(new ChamferRimFeature { Setback = 2, Name = "bevel", Suppressed = suppressChamfer });
        return history;
    }

    [Fact]
    public void FeatureHistoryBecomesAnOrderedFeatureList()
    {
        var history = PlateHistory();
        history.Regenerate();
        var root = ConstructionTree.FromHistory(history);

        Assert.Equal("Features", root.Label);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("plate", root.Children[0].Label);
        Assert.Equal("bevel", root.Children[1].Label);
        Assert.All(root.Children, c => Assert.Equal(ConstructionNodeKind.Feature, c.Kind));
        Assert.Same(history.Features[0], root.Children[0].Feature);
    }

    [Fact]
    public void FeatureRowsListTheirParamValues()
    {
        var history = PlateHistory();
        history.Regenerate();
        var root = ConstructionTree.FromHistory(history);

        var height = root.Children[0].Children.Single(c => c.Label == "Height");
        Assert.Equal(ConstructionNodeKind.Parameter, height.Kind);
        Assert.Equal("10", height.Detail);
        Assert.False(height.CanPreview);   // a value row draws nothing

        // Geometry inputs are parameters too, and print their descriptive query.
        var plane = root.Children[0].Children.Single(c => c.Label == "Plane");
        Assert.Equal(ConstructionNodeKind.Parameter, plane.Kind);
        Assert.Equal(PlaneRef.WorldXY.Descriptor, plane.Detail);
    }

    [Fact]
    public void SuppressedFeaturesAreFlagged()
    {
        var history = PlateHistory(suppressChamfer: true);
        history.Regenerate();
        var root = ConstructionTree.FromHistory(history);

        Assert.False(root.Children[0].Suppressed);
        Assert.True(root.Children[1].Suppressed);
        Assert.Equal("suppressed", root.Children[1].Detail);
    }

    [Fact]
    public void FeatureRowsTargetTheBodyAsOfThatStep()
    {
        var history = PlateHistory();
        history.Regenerate();
        var root = ConstructionTree.FromHistory(history);

        // Rollback view: step 0 is the un-chamfered plate, step 1 the chamfered one.
        var plate = root.Children[0].Target;
        var beveled = root.Children[1].Target;
        Assert.NotNull(plate);
        Assert.NotNull(beveled);
        Assert.NotSame(plate, beveled);
        Assert.Equal(40 * 30 * 10, plate.ToMesh().Volume(), 6);
        Assert.True(beveled.ToMesh().Volume() < plate.ToMesh().Volume());
        Assert.Same(history.Result, beveled);
    }

    [Fact]
    public void SuppressedFeatureRowShowsTheBodyItPassedThrough()
    {
        var history = PlateHistory(suppressChamfer: true);
        history.Regenerate();
        var root = ConstructionTree.FromHistory(history);
        Assert.Same(root.Children[0].Target, root.Children[1].Target);
    }

    [Fact]
    public void HistoryBackedPartsShowFeaturesNotTheShapeGraph()
    {
        var history = PlateHistory();
        var part = history.ToPart("plate");

        Assert.Same(history, part.History);
        var root = part.ConstructionTree();
        Assert.NotNull(root);
        Assert.Equal("Features", root.Label);
        Assert.Equal(2, root.Children.Count);
    }

    [Fact]
    public void BodyAfterIsBoundsChecked()
    {
        var history = PlateHistory();
        history.Regenerate();
        Assert.Null(history.BodyAfter(-1));
        Assert.Null(history.BodyAfter(99));
        Assert.NotNull(history.BodyAfter(0));
    }

    // ---- previews ----

    [Fact]
    public void SketchPreviewDrawsTheSketchOnItsPlane()
    {
        var plane = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
        var root = ConstructionTree.FromShape(Shape.Extrude(Sketch.Circle(2), 1, plane));
        var preview = ConstructionPreview.Build(root.Children[0]);

        Assert.Null(preview.Error);
        Assert.NotEmpty(preview.Segments);
        foreach (var (a, b) in preview.Segments)
        {
            // Chorded circle at display resolution: on the plane, within the sagitta.
            Assert.Equal(5, a.Z, 9);
            Assert.Equal(5, b.Z, 9);
            Assert.InRange(new Vector2d(a.X, a.Y).Length, 1.98, 2.0 + 1e-9);
        }
        // A closed chain: each chord starts where the previous one ended.
        for (int i = 1; i < preview.Segments.Count; i++)
            Assert.True(preview.Segments[i - 1].B.DistanceTo(preview.Segments[i].A) < 1e-9);
        Assert.True(preview.Segments[^1].B.DistanceTo(preview.Segments[0].A) < 1e-9);
    }

    [Fact]
    public void SketchPreviewIncludesHoleLoops()
    {
        var outer = Sketch.Rectangle(10, 6);
        var withHole = outer.WithHole(Sketch.Circle(new(0, 0), 1.5));
        var plain = ConstructionPreview.Build(
            ConstructionTree.FromShape(Shape.Extrude(outer, 1)).Children[0]);
        var holed = ConstructionPreview.Build(
            ConstructionTree.FromShape(Shape.Extrude(withHole, 1)).Children[0]);

        Assert.Equal(4, plain.Segments.Count);                    // one chord per rectangle side
        Assert.True(holed.Segments.Count > plain.Segments.Count); // plus the hole circle
    }

    [Fact]
    public void SketchPreviewHonorsBezierAndArcSegments()
    {
        var sketch = Sketch.Start(-2, -1)
            .LineTo(2, -1)
            .ArcTo(new(2, 1), 1.4, clockwise: false)
            .BezierTo(new(0, 2), new(-2, 2), new(-2, 1))
            .Close();
        var preview = ConstructionPreview.Build(
            ConstructionTree.FromShape(Shape.Extrude(sketch, 1)).Children[0]);

        Assert.Null(preview.Error);
        // Curves are chorded, so there are many more chords than the four segments.
        Assert.True(preview.Segments.Count > 20);
    }

    [Fact]
    public void SubShapePreviewShowsThatSubShapesGeometry()
    {
        var box = Shape.Box(4, 3, 2);
        var root = ConstructionTree.FromShape(box - Shape.Cylinder(0.5, 5));
        var preview = ConstructionPreview.Build(root.Children[0]);

        Assert.Null(preview.Error);
        Assert.Equal(12, preview.Segments.Count);   // the un-drilled box's 12 edges
        Assert.Equal(-2, preview.Bounds.Min.X, 9);
        Assert.Equal(2, preview.Bounds.Max.X, 9);
        Assert.Equal(1, preview.Bounds.Max.Z, 9);
    }

    [Fact]
    public void PreviewOfTheRootMatchesTheFinishedPart()
    {
        var shape = Shape.Box(4, 3, 2) - Shape.Cylinder(0.5, 5);
        var root = ConstructionTree.FromShape(shape);
        var preview = ConstructionPreview.Build(root);

        Assert.Null(preview.Error);
        // The drilled box has the box's 12 edges plus the two bore rims.
        Assert.True(preview.Segments.Count > 12);
    }

    [Fact]
    public void PreviewOfAValueRowReportsInsteadOfThrowing()
    {
        var history = PlateHistory();
        history.Regenerate();
        var parameter = ConstructionTree.FromHistory(history).Children[0].Children[0];
        var preview = ConstructionPreview.Build(parameter);

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Segments);
        Assert.True(preview.IsEmpty);
    }

    [Fact]
    public void ImplicitOnlyOperationsStillPreview()
    {
        // No B-Rep lowering exists for a smooth union: the preview falls back to mesh
        // dihedral edges rather than failing.
        var root = ConstructionTree.FromShape(
            Shape.Box(2, 2, 2).SmoothUnion(Shape.Sphere(1.2), 0.4));
        var preview = ConstructionPreview.Build(root, new MeshQuality { SdfResolution = 32 });

        Assert.Null(preview.Error);
        Assert.NotEmpty(preview.Segments);
    }

    // ---- preview cache ----

    [Fact]
    public void PreviewCacheReturnsTheSameInstanceForARow()
    {
        var cache = new ConstructionPreviewCache();
        var root = ConstructionTree.FromShape(Shape.Box(2, 2, 2) - Shape.Cylinder(0.5, 5));

        Assert.False(cache.TryGet(root, out _));
        var first = cache.Get(root);
        Assert.True(cache.TryGet(root, out var cached));
        Assert.Same(first, cached);
        Assert.Same(first, cache.Get(root));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RepeatedSubShapesAreLoweredOnce()
    {
        // A pattern places ONE cylinder node under four rows: they share a cache entry,
        // so the preview is built once however many rows reference it.
        var unit = Shape.Cylinder(0.3, 1);
        var root = ConstructionTree.FromShape(unit.PatternLinear(4, (2, 0, 0)));
        var leaves = root.Flatten().Where(n => ReferenceEquals(n.Target, unit)).ToList();
        Assert.Equal(4, leaves.Count);

        var cache = new ConstructionPreviewCache();
        var first = cache.Get(leaves[0]);
        foreach (var leaf in leaves)
            Assert.Same(first, cache.Get(leaf));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void SketchRowsWithTheSameSketchOnDifferentPlanesDoNotCollide()
    {
        var sketch = Sketch.Rectangle(4, 3);
        var low = ConstructionTree.FromShape(Shape.Extrude(sketch, 1)).Children[0];
        var high = ConstructionTree.FromShape(
            Shape.Extrude(sketch, 1, SketchPlane.At((0, 0, 9), Vector3d.UnitX, Vector3d.UnitY))).Children[0];

        var cache = new ConstructionPreviewCache();
        var lowPreview = cache.Get(low);
        var highPreview = cache.Get(high);

        Assert.Equal(2, cache.Count);
        Assert.Equal(0, lowPreview.Bounds.Max.Z, 9);
        Assert.Equal(9, highPreview.Bounds.Max.Z, 9);
    }

    [Fact]
    public void PreviewCacheClears()
    {
        var cache = new ConstructionPreviewCache();
        cache.Get(ConstructionTree.FromShape(Shape.Box(1, 1, 1)));
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
