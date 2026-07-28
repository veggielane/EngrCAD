using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The PMI follow-up vocabulary: angular dimensions (three points and face selectors,
/// against analytically-known angles), tolerance text sugar, chain/ordinate linear
/// dimension factories, multi-line callout continuations, and hole tables / auto
/// callouts generated from the Drill data already in the Shape graph.
/// </summary>
public class AnnotationExtrasTests
{
    private const double Exact = 1e-9;

    // ---- angular dimensions ----

    [Fact]
    public void AngularDimension_ThreePoints_MeasuresTheAngleAtTheVertex()
    {
        var dimension = new AngularDimension((0, 0, 0), (10, 0, 0), (0, 10, 0));
        var resolved = dimension.Resolve();

        Assert.Equal(AnnotationKind.AngularDimension, resolved.Kind);
        Assert.Equal(90.0, resolved.Value, Exact);
        Assert.Equal("90°", resolved.Text);
        Assert.Equal(new Vector3d(0, 0, 0), resolved.AnchorC);   // vertex
        Assert.Equal(new Vector3d(10, 0, 0), resolved.AnchorA);
        Assert.Equal(new Vector3d(0, 10, 0), resolved.AnchorB);
    }

    [Fact]
    public void AngularDimension_ObtuseThreePoints_Measures135()
    {
        var dimension = new AngularDimension((0, 0, 0), (10, 0, 0), (-7, 7, 0));
        Assert.Equal(135.0, dimension.Resolve().Value, Exact);
    }

    [Fact]
    public void AngularDimension_RayOnVertex_FailsLoudly()
    {
        var dimension = new AngularDimension((1, 1, 1), (1, 1, 1), (0, 10, 0));
        Assert.Throws<InvalidOperationException>(() => dimension.Resolve());
    }

    [Fact]
    public void AngularDimension_BetweenBoxFaces_MeasuresTheIncludedRightAngle()
    {
        // A box's top face and one side face meet at 90 degrees — the included angle
        // between the surfaces, not the 90 degrees their normals happen to span too;
        // the wedge test below separates the two conventions.
        var solid = Shape.Box(20, 20, 10).ToBrep();
        var dimension = AngularDimension.BetweenFaces(
            s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First(),
            s => s.PlanarFacesWithNormal(Vector3d.UnitX).First());
        var resolved = dimension.Resolve(solid);

        Assert.Equal(90.0, resolved.Value, Exact);
        Assert.Equal("90°", resolved.Text);
        // The vertex sits on the faces' shared edge line (x = 10, z = 5).
        Assert.Equal(10.0, resolved.AnchorC.X, Exact);
        Assert.Equal(5.0, resolved.AnchorC.Z, Exact);
    }

    [Fact]
    public void AngularDimension_DraftedFace_MeasuresTheIncludedAngle()
    {
        // A 10-degree drafted side against the bottom face: the INCLUDED angle the
        // drafter dimensions is 90 - 10 = 80 degrees (the normals span 100).
        var solid = Shape.Box(30, 20, 10)
            .Draft(10, neutralOrigin: (0, 0, -5), pullDirection: Vector3d.UnitZ,
                faces: s => s.PlanarFacesWithNormal(Vector3d.UnitX))
            .ToBrep();
        var dimension = AngularDimension.BetweenFaces(
            s => s.Faces.First(f => f.IsPlanar(out _, out var n) && n.Dot(Vector3d.UnitX) > 0.9),
            s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First());
        Assert.Equal(80.0, dimension.Resolve(solid).Value, 1e-6);
    }

    [Fact]
    public void AngularDimension_ParallelFaces_FailLoudly()
    {
        var solid = Shape.Box(10, 10, 10).ToBrep();
        var dimension = AngularDimension.BetweenFaces(
            s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First(),
            s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First());
        var exception = Assert.Throws<InvalidOperationException>(() => dimension.Resolve(solid));
        Assert.Contains("parallel", exception.Message);
    }

    // ---- tolerance sugar ----

    [Fact]
    public void Tolerance_Symmetric_AppendsPlusMinus()
    {
        var dimension = new LinearDimension((0, 0, 0), (40, 0, 0))
        {
            Tolerance = ToleranceSpec.Symmetric(0.1),
        };
        Assert.Equal("40 ±0.1", dimension.Resolve().Text);
    }

    [Fact]
    public void Tolerance_Limits_AppendsBothMagnitudes()
    {
        var dimension = new LinearDimension((0, 0, 0), (40, 0, 0))
        {
            Tolerance = ToleranceSpec.Limits(0.2, 0.1),
        };
        Assert.Equal("40 +0.2/-0.1", dimension.Resolve().Text);
    }

    [Fact]
    public void Tolerance_LabelOverrideWins()
    {
        var dimension = new LinearDimension((0, 0, 0), (40, 0, 0))
        {
            Label = "40 REF",
            Tolerance = ToleranceSpec.Symmetric(0.1),
        };
        Assert.Equal("40 REF", dimension.Resolve().Text);
    }

    [Fact]
    public void Tolerance_OnAngularDimension_AppendsAfterDegrees()
    {
        var dimension = new AngularDimension((0, 0, 0), (10, 0, 0), (0, 10, 0))
        {
            Tolerance = ToleranceSpec.Symmetric(0.5),
        };
        Assert.Equal("90° ±0.5", dimension.Resolve().Text);
    }

    [Fact]
    public void Tolerance_DegenerateSpecs_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToleranceSpec.Symmetric(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToleranceSpec.Limits(-0.1, 0.1));
        Assert.Throws<ArgumentException>(() => ToleranceSpec.Limits(0, 0));
    }

    // ---- chain / ordinate factories ----

    [Fact]
    public void Chain_DimensionsConsecutivePairsOnOneOffset()
    {
        var offset = new Vector3d(0, -8, 0);
        var chain = LinearDimension.Chain(
            [new Vector3d(0, 0, 0), new Vector3d(10, 0, 0), new Vector3d(25, 0, 0)], offset);

        Assert.Equal(2, chain.Count);
        var first = chain[0].Resolve();
        var second = chain[1].Resolve();
        Assert.Equal(10.0, first.Value, Exact);
        Assert.Equal(15.0, second.Value, Exact);
        Assert.Equal(offset, first.Offset);
        Assert.Equal(offset, second.Offset);   // one shared line of dimensions
        Assert.Equal(first.AnchorB, second.AnchorA);   // end-to-end
    }

    [Fact]
    public void Ordinate_DimensionsEveryPointFromTheDatum_StackedOutward()
    {
        var ordinates = LinearDimension.Ordinate(
            [new Vector3d(0, 0, 0), new Vector3d(10, 0, 0), new Vector3d(25, 0, 0)],
            new Vector3d(0, -10, 0), spacing: 5);

        Assert.Equal(2, ordinates.Count);
        var first = ordinates[0].Resolve();
        var second = ordinates[1].Resolve();
        // Every dimension measures from the datum (the first point).
        Assert.Equal(new Vector3d(0, 0, 0), first.AnchorA);
        Assert.Equal(new Vector3d(0, 0, 0), second.AnchorA);
        Assert.Equal(10.0, first.Value, Exact);
        Assert.Equal(25.0, second.Value, Exact);
        // Successive lines stack outward so they never overlap.
        Assert.Equal(new Vector3d(0, -10, 0), first.Offset);
        Assert.Equal(new Vector3d(0, -15, 0), second.Offset);
    }

    [Fact]
    public void ChainAndOrdinate_RefuseDegenerateInput()
    {
        Assert.Throws<ArgumentException>(() =>
            LinearDimension.Chain([new Vector3d(0, 0, 0)], new Vector3d(0, -5, 0)));
        Assert.Throws<ArgumentException>(() =>
            LinearDimension.Ordinate(
                [new Vector3d(0, 0, 0), new Vector3d(5, 0, 0)], Vector3d.Zero));
    }

    // ---- multi-line callouts ----

    [Fact]
    public void HoleCallout_CounterboreContinuation_IsItsOwnLine()
    {
        string text = HoleCallout.Text(StandardHoles.Counterbored(5), 12);
        var lines = text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("⌀", lines[0]);       // the hole line
        Assert.StartsWith("⌴", lines[1]);       // the counterbore continuation
    }

    [Fact]
    public void HoleCallout_SimpleHole_StaysSingleLine()
    {
        Assert.DoesNotContain('\n', HoleCallout.Text(StandardHoles.Clearance(5), 12));
    }

    // ---- hole table + auto-attached callouts ----

    /// <summary>The plate's top face as a placement plane (box centered: z = +4).</summary>
    private static readonly SketchPlane TopPlane =
        SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);

    private static Shape DrilledPlate() =>
        Shape.Box(60, 40, 8)
            .Drill(StandardHoles.Clearance(5),
                [new Vector2d(-20, 0), new Vector2d(20, 0)], 20, TopPlane)
            .Drill(StandardHoles.Counterbored(4),
                [new Vector2d(0, 10)], 20, TopPlane);

    [Fact]
    public void HoleTable_OneRowPerDrillCall_LetteredInCallOrder()
    {
        var table = HoleTable.For(DrilledPlate());

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(3, table.HoleCount);
        // "A" is the FIRST call (the clearance pair), even though the graph nests it
        // deepest; positions land on the placement plane (z = 4, the top face).
        Assert.Equal("A", table.Rows[0].Letter);
        Assert.Equal(2, table.Rows[0].Positions.Count);
        Assert.Equal(new Vector3d(-20, 0, 4), table.Rows[0].Positions[0]);
        Assert.Equal("B", table.Rows[1].Letter);
        Assert.Contains("⌴", table.Rows[1].Callout);   // the counterbore row

        string text = table.ToText();
        Assert.Contains("A ", text);
        Assert.Contains("(2×)", text);
        // One line per row: callout continuations are folded into the row.
        Assert.Equal(2, text.Split('\n').Length);
    }

    [Fact]
    public void HoleTable_Annotate_AttachesBalloonsAndTheTableNote()
    {
        var part = new Part("plate", DrilledPlate());
        int attached = HoleTable.For(part).Annotate(part, tableAnchor: (0, -30, 4));

        Assert.Equal(4, attached);   // 3 balloons + 1 table note
        Assert.Equal(4, part.Annotations.Count);
        var resolved = part.ResolveAnnotations();
        Assert.Contains(resolved, r => r.Kind == AnnotationKind.DatumLabel && r.Text == "A1");
        Assert.Contains(resolved, r => r.Kind == AnnotationKind.DatumLabel && r.Text == "A2");
        Assert.Contains(resolved, r => r.Kind == AnnotationKind.DatumLabel && r.Text == "B1");
        Assert.Contains(resolved, r => r.Kind == AnnotationKind.LeaderNote && r.Text.Contains('\n'));
    }

    [Fact]
    public void AutoAttach_OneCalloutPerCall_WithCountPrefix()
    {
        var part = new Part("plate", DrilledPlate());
        int attached = HoleAnnotations.AutoAttach(part);

        Assert.Equal(2, attached);
        var resolved = part.ResolveAnnotations();
        Assert.Equal(2, resolved.Count);
        Assert.Contains(resolved, r => r.Text.StartsWith("2× ⌀", StringComparison.Ordinal));
        // The single counterbore gets no count prefix, and keeps its continuation line.
        Assert.Contains(resolved, r => r.Text.StartsWith("⌀", StringComparison.Ordinal)
            && r.Text.Contains('\n'));
    }

    [Fact]
    public void HoleTable_EmptyForUndrilledParts()
    {
        Assert.Empty(HoleTable.For(Shape.Box(10, 10, 10)).Rows);
        var part = new Part("box", Shape.Box(10, 10, 10));
        Assert.Equal(0, HoleTable.For(part).Annotate(part, Vector3d.Zero));
        Assert.Empty(part.Annotations);
    }

    [Fact]
    public void LetterFor_SpreadsheetLettering()
    {
        Assert.Equal("A", HoleTable.LetterFor(0));
        Assert.Equal("Z", HoleTable.LetterFor(25));
        Assert.Equal("AA", HoleTable.LetterFor(26));
        Assert.Equal("AB", HoleTable.LetterFor(27));
    }
}
