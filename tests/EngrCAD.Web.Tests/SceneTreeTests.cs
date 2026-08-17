using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// The model tree as a value, and the one property everything downstream rests on:
/// <b>row N's instance index addresses the same occurrence the viewport draws at index
/// N</b>. That is checked against <see cref="Tab.Instances"/> itself rather than against
/// a second hand-written walk — a copy of the traversal would agree with a broken
/// implementation just as happily as with a correct one.
/// </summary>
public class SceneTreeTests
{
    private static Part MakePart(string name) => new(name, Shape.Box(2, 2, 2));

    private static Frame3d At(double x) =>
        Frame3d.FromOrthonormal((x, 0, 0), Vector3d.UnitX, Vector3d.UnitY);

    private static Scene SceneWith(out Tab tab)
    {
        var scene = new Scene();
        tab = scene.AddTab("Model");
        return scene;
    }

    [Fact]
    public void LoosePartsBecomeRowsInInstanceOrder()
    {
        SceneWith(out var tab);
        tab.Add(MakePart("base"));
        tab.Add(MakePart("lid"));

        var tree = SceneTree.Build(tab);

        Assert.Equal(2, tree.Rows.Count);
        Assert.Equal(2, tree.InstanceCount);
        Assert.All(tree.Rows, row => Assert.Equal(SceneTreeRowKind.Part, row.Kind));
        AssertMatchesInstances(tree, tab);
    }

    [Fact]
    public void AnAssemblyGetsAHeaderRowAndIndentedOccurrences()
    {
        SceneWith(out var tab);
        var bolt = MakePart("bolt");
        var stack = new Assembly("stack");
        stack.Add(bolt);
        stack.Add(bolt, At(5));   // auto-named "bolt.2"
        tab.Add(stack);

        var tree = SceneTree.Build(tab);

        Assert.Equal(3, tree.Rows.Count);
        Assert.Equal(SceneTreeRowKind.Assembly, tree.Rows[0].Kind);
        Assert.Equal(0, tree.Rows[0].Depth);
        // A header addresses no instance: it is a group, and giving it one would make
        // every row after it point at the wrong geometry.
        Assert.Equal(-1, tree.Rows[0].InstanceIndex);
        Assert.Equal(1, tree.Rows[1].Depth);
        Assert.Equal(1, tree.Rows[2].Depth);
        Assert.Equal(2, tree.InstanceCount);
        AssertMatchesInstances(tree, tab);
    }

    [Fact]
    public void NestedAssembliesIndentAndKeepTheirPaths()
    {
        SceneWith(out var tab);
        var bolt = MakePart("bolt");
        var clamp = new Assembly("clamp");
        clamp.Add(bolt);
        var rig = new Assembly("rig");
        rig.Add(clamp);
        rig.Add(clamp, At(10));
        tab.Add(rig);

        var tree = SceneTree.Build(tab);

        // The paths are the occurrence paths, so a tree row and a PartInstance name the
        // same thing in the same words as well as by the same index.
        Assert.Equal(
            ["rig", "rig/clamp", "rig/clamp/bolt", "rig/clamp.2", "rig/clamp.2/bolt"],
            tree.Rows.Select(r => r.Path));
        Assert.Equal([0, 1, 2, 1, 2], tree.Rows.Select(r => r.Depth));
        AssertMatchesInstances(tree, tab);
    }

    [Fact]
    public void LoosePartsComeBeforeAssembliesJustAsTabInstancesDoes()
    {
        SceneWith(out var tab);
        var stack = new Assembly("stack");
        stack.Add(MakePart("bolt"));
        tab.Add(stack);
        tab.Add(MakePart("plate"));   // added AFTER the assembly

        var tree = SceneTree.Build(tab);

        // Tab.Instances() emits loose parts first whatever the add order, so the tree
        // must too — otherwise every index is off by one for exactly this scene.
        Assert.Equal("plate", tree.Rows[0].Path);
        Assert.Equal(0, tree.Rows[0].InstanceIndex);
        AssertMatchesInstances(tree, tab);
    }

    [Fact]
    public void RowForInstanceIsTheInverseOfTheRowsIndex()
    {
        SceneWith(out var tab);
        tab.Add(MakePart("plate"));
        var stack = new Assembly("stack");
        stack.Add(MakePart("bolt"));
        tab.Add(stack);

        var tree = SceneTree.Build(tab);

        for (int i = 0; i < tree.InstanceCount; i++)
            Assert.Equal(i, tree.Rows[tree.RowForInstance(i)].InstanceIndex);
        Assert.Equal(-1, tree.RowForInstance(-1));
        Assert.Equal(-1, tree.RowForInstance(tree.InstanceCount));
    }

    // ---- visibility ----

    [Fact]
    public void EverythingIsVisibleWithNothingUnchecked()
    {
        SceneWith(out var tab);
        tab.Add(MakePart("a"));
        tab.Add(MakePart("b"));

        Assert.Equal([true, true], SceneTree.Build(tab).EffectiveVisibility());
    }

    [Fact]
    public void HidingAPartHidesOnlyThatInstance()
    {
        SceneWith(out var tab);
        tab.Add(MakePart("a"));
        tab.Add(MakePart("b"));
        var tree = SceneTree.Build(tab);

        var visible = tree.EffectiveVisibility(new HashSet<string> { tree.Rows[1].Key });

        Assert.Equal([true, false], visible);
    }

    [Fact]
    public void PerRowHiddenReadsOwnAndAncestorCheckboxes()
    {
        SceneWith(out var tab);
        var bolt = MakePart("bolt");
        var clamp = new Assembly("clamp");
        clamp.Add(bolt);
        var rig = new Assembly("rig");
        rig.Add(clamp);
        tab.Add(rig);
        var tree = SceneTree.Build(tab);
        var boltRow = tree.Rows.Single(r => r.Path == "rig/clamp/bolt");
        var clampRow = tree.Rows.Single(r => r.Path == "rig/clamp");

        // Nothing hidden: every row reads shown (the empty set is the identity).
        Assert.False(tree.IsEffectivelyHidden(boltRow, null));
        Assert.False(tree.IsEffectivelyHidden(boltRow, new HashSet<string>()));

        // The row's own checkbox.
        Assert.True(tree.IsEffectivelyHidden(boltRow, new HashSet<string> { boltRow.Key }));

        // An ANCESTOR's checkbox reaches every row beneath it — the same own-AND-
        // ancestors chain EffectiveVisibility folds, exposed per row so the tree can
        // gray what it hides.
        var hidden = new HashSet<string> { clampRow.Key };
        Assert.True(tree.IsEffectivelyHidden(boltRow, hidden));
        Assert.True(tree.IsEffectivelyHidden(clampRow, hidden));
        var rigRow = tree.Rows.Single(r => r.Path == "rig");
        Assert.False(tree.IsEffectivelyHidden(rigRow, hidden));
    }

    [Fact]
    public void HidingAnAssemblyHidesItsWholeSubtree()
    {
        SceneWith(out var tab);
        tab.Add(MakePart("plate"));
        var bolt = MakePart("bolt");
        var clamp = new Assembly("clamp");
        clamp.Add(bolt);
        clamp.Add(bolt, At(5));
        var rig = new Assembly("rig");
        rig.Add(clamp);
        rig.Add(MakePart("pin"));
        tab.Add(rig);

        var tree = SceneTree.Build(tab);
        int clampRow = tree.Rows.Single(r => r.Path == "rig/clamp").Index;

        var visible = tree.EffectiveVisibility(new HashSet<string> { tree.Rows[clampRow].Key });

        // Both bolts under the clamp go; the loose plate and the sibling pin stay.
        var byPath = tree.Rows
            .Where(r => r.InstanceIndex >= 0)
            .ToDictionary(r => r.Path, r => visible[r.InstanceIndex]);
        Assert.True(byPath["plate"]);
        Assert.False(byPath["rig/clamp/bolt"]);
        Assert.False(byPath["rig/clamp/bolt.2"]);
        Assert.True(byPath["rig/pin"]);
    }

    [Fact]
    public void AnAncestorHidesAChildWithoutTouchingItsOwnState()
    {
        SceneWith(out var tab);
        var rig = new Assembly("rig");
        rig.Add(MakePart("bolt"));
        tab.Add(rig);
        var tree = SceneTree.Build(tab);
        int header = tree.Rows[0].Index;

        // Hide the group, then un-hide it: the child's own (unchecked) state is what
        // decides, unchanged — which is what makes re-checking a group restore exactly
        // what was showing before.
        var hidden = new HashSet<string> { tree.Rows[header].Key };
        Assert.Equal([false], tree.EffectiveVisibility(hidden));
        hidden.Remove(tree.Rows[header].Key);
        Assert.Equal([true], tree.EffectiveVisibility(hidden));
    }

    [Fact]
    public void VisibilityKeysSurviveARebuild()
    {
        // The hidden set is remembered by key, not by row index, because a tree is
        // rebuilt whenever a part fails to mesh or a tab is revisited.
        SceneWith(out var tab);
        tab.Add(MakePart("a"));
        tab.Add(MakePart("b"));

        var first = SceneTree.Build(tab);
        var hidden = new HashSet<string> { first.Rows[1].Key };
        var second = SceneTree.Build(tab);

        Assert.Equal([true, false], second.EffectiveVisibility(hidden));
    }

    // ---- failed parts ----

    [Fact]
    public void AFailedPartTakesNoInstanceIndexAndShiftsNothingAfterIt()
    {
        SceneWith(out var tab);
        var a = MakePart("a");
        var bad = MakePart("bad");
        var c = MakePart("c");
        tab.Add(a);
        tab.Add(bad);
        tab.Add(c);

        var tree = SceneTree.Build(tab, new Dictionary<Part, string> { [bad] = "boom" });

        Assert.Equal(0, tree.Rows[0].InstanceIndex);
        Assert.Equal(-1, tree.Rows[1].InstanceIndex);
        Assert.Equal("boom", tree.Rows[1].Failure);
        // The instance AFTER the failure takes index 1, because the viewport's list has
        // no entry for the part that threw. Off-by-one here silently hides, selects and
        // highlights the wrong part.
        Assert.Equal(1, tree.Rows[2].InstanceIndex);
        Assert.Equal(2, tree.InstanceCount);
        // Every instance the tree claims to address is addressable.
        Assert.Equal(2, tree.EffectiveVisibility().Length);
    }

    [Fact]
    public void TheEmptyTreeIsUsable()
    {
        Assert.Empty(SceneTree.Empty.Rows);
        Assert.Equal(0, SceneTree.Empty.InstanceCount);
        Assert.Empty(SceneTree.Empty.EffectiveVisibility());
        Assert.Equal(-1, SceneTree.Empty.RowForInstance(0));
    }

    /// <summary>
    /// The load-bearing check: every part row's index and path agree with
    /// <see cref="Tab.Instances"/>, the very list the viewport is handed.
    /// </summary>
    private static void AssertMatchesInstances(SceneTree tree, Tab tab)
    {
        var instances = tab.Instances();
        Assert.Equal(instances.Count, tree.InstanceCount);
        foreach (var row in tree.Rows)
        {
            if (row.InstanceIndex < 0)
                continue;
            Assert.Equal(instances[row.InstanceIndex].Path, row.Path);
            Assert.Same(instances[row.InstanceIndex].Part, row.Part);
        }
    }
}
