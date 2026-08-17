using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Auto-dimensioning and BOM-linked balloons — the two passes that read something the model
/// already knows and put it on the paper.
///
/// <para>The dimensions are checked against CLOSED FORMS (a plate's overall width IS its width,
/// a bolt circle's diameter IS twice its radius), and the placement rule is checked as the
/// property it claims: nothing the pass adds may overlap anything else it adds, or the view.</para>
/// </summary>
public class AutoDimensionTests
{
    private static SketchPlane Top(double z) => SketchPlane.At((0, 0, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoltedPlate() => new("plate", Shape.Box(90, 60, 12)
        .Drill(HoleSpec.Simple(6.6), LocationSet.Polar(6, 25), depth: 14, Top(6)));

    private static DrawingView PlanOf(Part part) =>
        new(part, StandardViews.DirectionFor("top")!.Value, "TOP") { Scale = 1, Center = (150, 150) };

    // ---------------------------------------------------------------- overall extents

    /// <summary>A rectangle's overall-extent dimensions read its exact size — the value is the
    /// anchors' own separation in model coordinates, so there is nothing to round.</summary>
    [Fact]
    public void TheOverallExtentsReadThePartsExactSize()
    {
        var view = PlanOf(new Part("plate", Shape.Box(90, 60, 12)));
        var added = AutoDimension.Apply(view, new AutoDimensionOptions { Holes = false });

        var linear = added.OfType<SheetLinearDimension>().Select(d => d.Value).OrderBy(v => v).ToList();
        Assert.Equal(2, linear.Count);
        Assert.Equal(60, linear[0], 9);
        Assert.Equal(90, linear[1], 9);
    }

    /// <summary>The pass returns what it placed and places it ON the view, so a caller keeps,
    /// moves or deletes any of it — explicit placement stays the contract.</summary>
    [Fact]
    public void EverythingThePassAddsIsAnOrdinaryAnnotationOnTheView()
    {
        var view = PlanOf(BoltedPlate());
        var added = AutoDimension.Apply(view);
        Assert.NotEmpty(added);
        foreach (var annotation in added)
            Assert.Contains(annotation, view.Annotations);
        Assert.Equal(added.Count, view.Annotations.Count);
    }

    // ---------------------------------------------------------------- hole families

    /// <summary>
    /// A hole family is read from the CONSTRUCTION GRAPH, so the callout carries the spec that
    /// cut the holes — and the bolt circle it names is exactly the one the LocationSet drew,
    /// because "every point the same distance from their centroid" is what Polar constructs.
    /// </summary>
    [Fact]
    public void ABoltCircleIsRecognisedAtExactlyItsOwnDiameter()
    {
        var view = PlanOf(BoltedPlate());
        var families = AutoDimension.Families(view);
        var family = Assert.Single(families);
        Assert.Equal(6, family.Points.Count);
        Assert.Equal(6.6, family.Row.DrillDiameter, 9);

        string pattern = Assert.IsType<string>(AutoDimension.PatternOf(family.Points, 90));
        Assert.Equal("ON \u230050 B.C.", pattern);
    }

    /// <summary>A grid's pitches come back exactly as stated, both axes at once.</summary>
    [Fact]
    public void AGridIsRecognisedAtExactlyItsOwnPitch()
    {
        var plate = new Part("plate", Shape.Box(90, 60, 12)
            .Drill(HoleSpec.Simple(5), LocationSet.Grid(3, 2, 20, 16), depth: 14, Top(6)));
        var family = Assert.Single(AutoDimension.Families(PlanOf(plate)));
        Assert.Equal("3\u00D72 PITCH 20 \u00D7 16", AutoDimension.PatternOf(family.Points, 90));
    }

    /// <summary>Points forming neither pattern are reported as none rather than as an
    /// approximate one — the recognition is exact or absent.</summary>
    [Fact]
    public void ScatteredHolesCarryNoPattern()
    {
        IReadOnlyList<Vector2d> scattered = [new(0, 0), new(13, 4), new(31, 19), new(2, 27)];
        Assert.Null(AutoDimension.PatternOf(scattered, 90));
    }

    /// <summary>
    /// A hole reads as a CIRCLE only where its axis runs along the line of sight, so the plan
    /// view carries the callout and the front view — where the same holes are a rectangle —
    /// carries none. A callout on an edge-on hole would be pointing at the wrong feature.
    /// </summary>
    [Fact]
    public void OnlyAViewLookingDownTheHoleAxisCallsItOut()
    {
        var part = BoltedPlate();
        Assert.Single(AutoDimension.Families(PlanOf(part)));

        var front = new DrawingView(part, StandardViews.DirectionFor("front")!.Value, "FRONT")
        {
            Scale = 1, Center = (150, 60),
        };
        Assert.Empty(AutoDimension.Families(front));

        // ... so the front view gets its extents and nothing else.
        var added = AutoDimension.Apply(front);
        Assert.All(added, a => Assert.IsType<SheetLinearDimension>(a));
    }

    /// <summary>The callout carries the graph's own spec text with an "N x" count prefix and the
    /// pattern beneath it.</summary>
    [Fact]
    public void TheCalloutIsTheGraphsOwnSpecTextWithItsCountAndPattern()
    {
        var view = PlanOf(BoltedPlate());
        var note = Assert.Single(AutoDimension.Apply(view).OfType<SheetNote>());
        string[] lines = note.Text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("6\u00D7 ", lines[0]);
        Assert.Contains("6.6", lines[0]);
        Assert.Equal("ON \u230050 B.C.", lines[1]);
    }

    // ---------------------------------------------------------------- the placement rule

    /// <summary>
    /// THE placement claim, asserted rather than trusted: the overall width goes below, the
    /// height to the left and the callouts out to a column on the right, so no two pieces of
    /// text the pass places can overlap each other or the view's own line work.
    /// </summary>
    [Fact]
    public void NothingThePassPlacesOverlapsAnythingElseOrTheView()
    {
        var plate = new Part("plate", Shape.Box(90, 60, 12)
            .Drill(HoleSpec.Simple(6.6), LocationSet.Polar(6, 25), depth: 14, Top(6))
            .Drill(HoleSpec.Counterbore(6.6, 11, 6.8), [new Vector2d(0, 0)], depth: 14, Top(6)));
        var view = PlanOf(plate);
        AutoDimension.Apply(view);

        var content = view.Compute();
        var boxes = content.Texts
            .Where(t => t.Layer == SheetLayers.Dimensions)
            .Select(TextBox)
            .ToList();
        Assert.True(boxes.Count >= 4, $"expected several placed texts, got {boxes.Count}");

        for (int i = 0; i < boxes.Count; i++)
        {
            Assert.False(Overlaps(boxes[i], content.Bounds),
                $"text {i} lands on the view's line work");
            for (int j = i + 1; j < boxes.Count; j++)
                Assert.False(Overlaps(boxes[i], boxes[j]), $"texts {i} and {j} overlap");
        }
    }

    /// <summary>The pass is a deterministic function of the view — a placement heuristic keyed
    /// on iteration order would show here.</summary>
    [Fact]
    public void TheAutoDimensionedSheetIsAByteIdenticalFunctionOfItself()
    {
        static string Svg()
        {
            var sheet = new DrawingSheet(SheetFormat.A3);
            var view = PlanOf(BoltedPlate());
            AutoDimension.Apply(view);
            return sheet.Add(view).ToSvg();
        }
        Assert.Equal(Svg(), Svg());
    }

    // ---------------------------------------------------------------- BOM balloons

    /// <summary>
    /// The fixture the balloon claim needs, built so BOTH halves of it bite. Seen from the
    /// front, the post's extreme corner along the leader direction (its top right) sits BEHIND
    /// the plate, while its bottom stub stands clear — so an anchor rule that ignored visibility
    /// would point at a corner the reader cannot see, and one that ignored which instance a run
    /// came from would put both balloons on the same part.
    /// </summary>
    private static Scene OccludedScene()
    {
        var scene = new Scene();
        scene.Add(new Part("plate", Shape.Box(120, 10, 80)));
        scene.Add(new Part("post", Shape.Box(30, 10, 90).Translate(30, 20, -15)));
        return scene;
    }

    private static DrawingView FrontOf(Scene scene) =>
        new(scene, StandardViews.DirectionFor("front")!.Value, "FRONT") { Scale = 1, Center = (200, 150) };

    /// <summary>
    /// THE balloon claim: every balloon's anchor is a VISIBLE point of the line work of the
    /// occurrence it labels. A balloon on a hidden edge points through the material at
    /// something the reader cannot see; one on the neighbour's outline labels the wrong part.
    /// </summary>
    [Fact]
    public void EveryBalloonAnchorsOnVisibleLineWorkOfThePartItLabels()
    {
        var scene = OccludedScene();
        var view = FrontOf(scene);
        var list = new SheetPartsList(Bom.For(scene));
        var placed = BomBalloons.Attach(view, list);
        Assert.Equal(2, placed.Count);

        var runs = view.Content.Runs;
        foreach (var balloon in placed)
        {
            var line = list.Bom.Lines[int.Parse(balloon.Item) - 1];
            Assert.Contains(balloon.Instance, line.Paths);

            bool onOwnVisibleRun = runs.Any(r =>
                r.Visibility == EdgeVisibility.Visible && r.Instance == balloon.Instance
                && r.Points.Any(p => (p - balloon.Anchor).Length < 1e-9));
            Assert.True(onOwnVisibleRun, $"balloon {balloon.Item} is not on {balloon.Instance}'s visible line work");
        }

        // The two balloons landed on DIFFERENT parts — a pass that anchored both on whatever was
        // nearest would pass every assertion above and fail this one.
        Assert.Equal(2, placed.Select(b => b.Instance).Distinct().Count());

        // And the fixture really does hide the post's extreme corner, or the visibility half of
        // the claim above would hold for free: its furthest point along the leader direction is
        // NOT the furthest point of its visible line work.
        var outward = BomBalloons.DefaultLeader.Normalized(Tolerance.Default);
        var post = placed.Single(b => b.Instance == "post");
        double bestAnywhere = runs
            .Where(r => r.Instance == "post")
            .SelectMany(r => r.Points)
            .Max(p => p.Dot(outward));
        Assert.True(bestAnywhere > post.Anchor.Dot(outward) + 1,
            "the fixture no longer hides the post's extreme corner");
    }

    /// <summary>A part with no visible line work in this view gets NO balloon — there is
    /// nothing to point at, and an honest absence beats a leader into the dark.</summary>
    [Fact]
    public void APartWithNoVisibleLineWorkGetsNoBalloon()
    {
        var scene = new Scene();
        scene.Add(new Part("shell", Shape.Box(120, 10, 80)));
        scene.Add(new Part("buried", Shape.Box(20, 10, 20).Translate(0, 30, 0)));
        var view = FrontOf(scene);
        var placed = BomBalloons.Attach(view, new SheetPartsList(Bom.For(scene)));

        Assert.DoesNotContain(placed, b => b.Instance == "buried");
        Assert.Contains(placed, b => b.Instance == "shell");
    }

    /// <summary>The balloons and the parts list read ONE bill of materials, so a drawing cannot
    /// label a part with a number its own list does not carry.</summary>
    [Fact]
    public void TheBalloonNumbersAreThePartsListsOwn()
    {
        var scene = OccludedScene();
        var sheet = new DrawingSheet(SheetFormat.A3);
        var view = FrontOf(scene);
        var list = new SheetPartsList(Bom.For(scene));
        var placed = BomBalloons.Attach(view, list);
        sheet.PartsList = list;
        sheet.Add(view);

        var texts = sheet.Compute().Texts.Select(t => t.Text).ToList();
        foreach (var balloon in placed)
        {
            Assert.Contains(balloon.Item, texts);   // the balloon
            Assert.Equal(balloon.Item, list.NumberOf(list.Bom.Lines[int.Parse(balloon.Item) - 1].Part));
        }
        foreach (var line in list.Bom.Lines)
            Assert.Contains(line.Item, texts);      // the row that number indexes
        Assert.Contains("QTY", texts);
    }

    /// <summary>A sheet stating no parts list draws none — the addition is opt-in, so an
    /// existing sheet is untouched.</summary>
    [Fact]
    public void ASheetWithNoPartsListIsUnchanged()
    {
        var scene = OccludedScene();
        var sheet = new DrawingSheet(SheetFormat.A3);
        sheet.Add(FrontOf(scene));
        string before = sheet.ToSvg();
        sheet.PartsList = new SheetPartsList(Bom.For(scene));
        Assert.NotEqual(before, sheet.ToSvg());
        sheet.PartsList = null;
        Assert.Equal(before, sheet.ToSvg());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A conservative box around a placed text: the stroke advance of a proportional
    /// face is under 0.62 em per character, so this OVER-states the ink and an overlap test
    /// built on it cannot pass by measuring too little.</summary>
    private static Aabb TextBox(SheetText text)
    {
        double width = text.Text.Length * text.Height * 0.62;
        double x = text.Anchor switch
        {
            SheetTextAnchor.Center => text.Position.X - width / 2,
            SheetTextAnchor.Right => text.Position.X - width,
            _ => text.Position.X,
        };
        return new Aabb(
            new Vector3d(x, text.Position.Y, 0),
            new Vector3d(x + width, text.Position.Y + text.Height, 0));
    }

    private static bool Overlaps(in Aabb a, in Aabb b) =>
        a.Min.X < b.Max.X && b.Min.X < a.Max.X && a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;
}
