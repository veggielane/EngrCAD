using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Dogleg explode paths. A screw comes straight OUT of its bore before it moves aside,
/// because a diagonal path reads as "insert it at an angle" and a fitter will try — so
/// an exploded view that is a drawing rather than a demo needs waypoints.
/// <para>The two properties that make it usable are asserted here: the factor maps to
/// ARC LENGTH (so a part moves at constant speed through the corner instead of
/// lingering on the shorter leg), and the endpoints are exact by DECISION rather than by
/// arithmetic that happens to land there — an un-exploded flatten must stay bit-for-bit
/// what it always was.</para>
/// </summary>
public class ExplodePathTests
{
    private static (Scene Scene, Occurrence Lid) Stack()
    {
        var scene = new Scene();
        var body = new Part("body", Shape.Box(10, 10, 4));
        var lid = new Part("lid", Shape.Box(10, 10, 2).Translate(0, 0, 4));
        var stack = new Assembly("stack");
        stack.Add(body);
        var occurrence = stack.Add(lid);
        occurrence.ExplodeOffset = new Vector3d(30, 0, 40);
        scene.AddTab("stack").Add(stack);
        return (scene, occurrence);
    }

    [Fact]
    public void NoPathIsTheStraightLineItAlwaysWas()
    {
        var (_, lid) = Stack();
        Assert.Equal(new Vector3d(15, 0, 20), lid.ExplodeDisplacement(0.5));
        Assert.Equal(Vector3d.Zero, lid.ExplodeDisplacement(0));
        Assert.Equal(new Vector3d(30, 0, 40), lid.ExplodeDisplacement(1));
    }

    [Fact]
    public void AWaypointMakesTheMoveGoUpThenOver()
    {
        var (_, lid) = Stack();
        // Straight up 40, then across 30: legs of 40 and 30, total 70.
        lid.ExplodePath.Add(new Vector3d(0, 0, 40));

        // Halfway by ARC LENGTH is 35 along, i.e. 5 short of the corner.
        var half = lid.ExplodeDisplacement(0.5);
        Assert.Equal(0, half.X, 12);
        Assert.Equal(35, half.Z, 12);

        // At the corner's own fraction (40/70) it is exactly the waypoint.
        var corner = lid.ExplodeDisplacement(40.0 / 70.0);
        Assert.Equal(0, corner.X, 9);
        Assert.Equal(40, corner.Z, 9);

        // ... and past it the motion is purely lateral.
        var later = lid.ExplodeDisplacement(0.9);
        Assert.Equal(40, later.Z, 9);
        Assert.True(later.X > corner.X);
    }

    [Fact]
    public void SpeedIsConstantAlongTheWholePath()
    {
        var (_, lid) = Stack();
        lid.ExplodePath.Add(new Vector3d(0, 0, 40));

        // Equal steps in the factor must travel equal DISTANCE, which is what separates
        // arc-length parameterization from one leg per half of the slider.
        double? step = null;
        var previous = lid.ExplodeDisplacement(0);
        for (int i = 1; i <= 20; i++)
        {
            var next = lid.ExplodeDisplacement(i / 20.0);
            double travelled = (next - previous).Length;
            step ??= travelled;
            // The corner segment is the only one that can differ, and only because a
            // sample straddles it; 70/20 = 3.5 per step, so allow one corner rounding.
            Assert.InRange(travelled, step.Value * 0.7, step.Value * 1.05);
            previous = next;
        }
    }

    [Fact]
    public void TheEndsAreExactWhateverThePath()
    {
        var (_, lid) = Stack();
        lid.ExplodePath.Add(new Vector3d(0, 0, 40));
        lid.ExplodePath.Add(new Vector3d(30, 0, 40));

        Assert.Equal(Vector3d.Zero, lid.ExplodeDisplacement(0));
        Assert.Equal(new Vector3d(30, 0, 40), lid.ExplodeDisplacement(1));
    }

    [Fact]
    public void ADegeneratePathFallsBackToTheStraightLineRatherThanNaN()
    {
        var (_, lid) = Stack();
        lid.ExplodeOffset = Vector3d.Zero;         // a path with no length at all
        lid.ExplodePath.Add(Vector3d.Zero);
        var at = lid.ExplodeDisplacement(0.5);
        Assert.True(double.IsFinite(at.X) && double.IsFinite(at.Y) && double.IsFinite(at.Z));
        Assert.Equal(Vector3d.Zero, at);
    }

    [Fact]
    public void TheFlattenWalkAndAnExplodeTrackBothTakeThePath()
    {
        var (scene, lid) = Stack();
        lid.ExplodePath.Add(new Vector3d(0, 0, 40));

        // The flatten walk goes through the same ExplodeDisplacement, so a dogleg is not
        // something a viewer or an exporter has to know about.
        var posed = scene.Tabs[0].Instances(0.5).Single(i => i.Part.Name == "lid");
        Assert.Equal(35, posed.World.M34, 9);      // still on the vertical leg
        Assert.Equal(0, posed.World.M14, 9);

        // Factor exactly 0 is bit-identical to the un-exploded flatten.
        var assembled = scene.Tabs[0].Instances(0).Single(i => i.Part.Name == "lid");
        var plain = scene.Tabs[0].Instances().Single(i => i.Part.Name == "lid");
        Assert.Equal(plain.World, assembled.World);
    }

    [Fact]
    public void APathRoundTripsThroughTheDocumentFormat()
    {
        var (scene, lid) = Stack();
        lid.ExplodePath.Add(new Vector3d(0, 0, 40));

        var document = new Document(scene);
        string json = document.Save();
        var loaded = Document.Load(json);
        var reloaded = loaded.Scene.Tabs[0].Assemblies[0].Occurrences
            .Single(o => o.Part?.Name == "lid");

        Assert.Equal([new Vector3d(0, 0, 40)], reloaded.ExplodePath);
        Assert.Equal(lid.ExplodeDisplacement(0.5), reloaded.ExplodeDisplacement(0.5));
        // save -> load -> save stays a fixed point, the document format's own contract.
        Assert.Equal(json, loaded.Document.Save());
    }

    [Fact]
    public void AnAssemblyWithNoPathWritesNoPathField()
    {
        // An additive field must be ABSENT when unused, or every existing document file
        // changes and the byte-comparison oracle stops meaning anything.
        var (scene, _) = Stack();
        Assert.DoesNotContain("explodePath", new Document(scene).Save());
    }
}
