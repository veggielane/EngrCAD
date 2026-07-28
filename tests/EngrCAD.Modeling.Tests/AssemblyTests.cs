using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class AssemblyTests
{
    private static Part BoxPart(string name = "box") => new(name, MeshPrimitives.Box(1, 1, 1));

    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    // ---- naming ----

    [Fact]
    public void OccurrenceNames_DeriveFromItem_AutoSuffix()
    {
        var bolt = BoxPart("bolt");
        var assembly = new Assembly("clamp");

        Assert.Equal("bolt", assembly.Add(bolt).Name);
        Assert.Equal("bolt.2", assembly.Add(bolt).Name);
        Assert.Equal("bolt.3", assembly.Add(bolt).Name);

        var sub = new Assembly("bolt"); // same base name as the part occurrences
        Assert.Equal("bolt.4", assembly.Add(sub).Name);
    }

    [Fact]
    public void ExplicitOccurrenceNames_MustBeUnique_NoSlash()
    {
        var assembly = new Assembly("a");
        var part = BoxPart();
        assembly.Add(part, name: "left");

        Assert.Throws<ArgumentException>(() => assembly.Add(part, name: "left"));
        Assert.Throws<ArgumentException>(() => assembly.Add(part, name: "a/b"));
        Assert.Throws<ArgumentException>(() => assembly.Add(part, name: " "));
        Assert.Equal("right", assembly.Add(part, name: "right").Name);
    }

    [Fact]
    public void AssemblyCycles_AreRejected()
    {
        var a = new Assembly("a");
        var b = new Assembly("b");
        var c = new Assembly("c");
        a.Add(b);
        b.Add(c);

        Assert.Throws<ArgumentException>(() => a.Add(a));   // self
        Assert.Throws<ArgumentException>(() => b.Add(a));   // direct cycle
        Assert.Throws<ArgumentException>(() => c.Add(a));   // transitive cycle
    }

    // ---- pose composition & flattening ----

    [Fact]
    public void Flatten_ComposesFramesDownTheTree()
    {
        // outer(F1) -> sub(F2) -> part: world = F2.Then(F1) as a matrix.
        var part = BoxPart("bolt");
        var f1 = Frame3d.FromZX((10, 0, 0), Vector3d.UnitZ, Vector3d.UnitY);   // rotate about Z, translate
        var f2 = Frame3d.FromZX((0, 2, 1), Vector3d.UnitX, Vector3d.UnitZ);    // tilt, translate

        var sub = new Assembly("sub");
        sub.Add(part, f2);
        var outer = new Assembly("outer");
        outer.Add(sub, f1);

        var instance = Assert.Single(outer.Flatten());
        var expected = f2.Then(f1);
        foreach (var p in new Vector3d[] { (0, 0, 0), (1, 0, 0), (0.3, -0.7, 2.1) })
        {
            var viaFrames = expected.ToWorld(p);
            var viaMatrix = instance.World.TransformPoint(p);
            Assert.True((viaFrames - viaMatrix).Length < 1e-12,
                $"instance world disagrees with frame composition at {p}");
        }
    }

    [Fact]
    public void InstanceWorld_AppliesPartTransform_ThenOccurrenceFrame()
    {
        // The part's own Transform is the innermost map; the occurrence frame poses it.
        var part = new Part("p", MeshPrimitives.Box(1, 1, 1),
            transform: Matrix4d.CreateTranslation((1, 0, 0)));
        var frame = Frame3d.FromZX(Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitY); // 90° about Z

        var assembly = new Assembly("a");
        assembly.Add(part, frame);

        var instance = Assert.Single(assembly.Flatten());
        var world = instance.World.TransformPoint(Vector3d.Zero); // part-local origin
        // Transform moves it to (1,0,0); the frame's X axis is world Y, so it lands at (0,1,0).
        Assert.True((world - new Vector3d(0, 1, 0)).Length < 1e-12, $"got {world}");
    }

    [Fact]
    public void Flatten_Paths_DepthFirst_RootedAtAssemblyName()
    {
        var bolt = BoxPart("bolt");
        var plate = BoxPart("plate");

        var stack = new Assembly("stack");
        stack.Add(bolt);
        stack.Add(bolt);

        var gearbox = new Assembly("gearbox");
        gearbox.Add(plate);
        gearbox.Add(stack);
        gearbox.Add(stack);

        var paths = gearbox.Flatten().Select(i => i.Path).ToArray();
        Assert.Equal(
            ["gearbox/plate", "gearbox/stack/bolt", "gearbox/stack/bolt.2",
             "gearbox/stack.2/bolt", "gearbox/stack.2/bolt.2"],
            paths);
    }

    // ---- shared parts & caching ----

    [Fact]
    public void SharedPart_MeshedOnce_ListedOnceInAllParts()
    {
        var bolt = new Part("bolt", Sdf.Sphere(0.5));
        var assembly = new Assembly("a");
        assembly.Add(bolt, At(-2, 0, 0));
        assembly.Add(bolt, At(2, 0, 0));

        var scene = new Scene(new MeshQuality { SdfResolution = 24 });
        scene.AddTab("t").Add(assembly);

        Assert.Single(scene.AllParts);          // instanced twice, one distinct part
        scene.PreMesh();
        Assert.Same(bolt.GetMesh(), bolt.GetMesh()); // cached — both instances share it

        var instances = scene.Tabs[0].Instances();
        Assert.Equal(2, instances.Count);
        Assert.Same(instances[0].Part, instances[1].Part);
        Assert.NotEqual(instances[0].World, instances[1].World);
    }

    [Fact]
    public void DistinctParts_WalksNestedAssembliesOnce()
    {
        var bolt = BoxPart("bolt");
        var nut = BoxPart("nut");
        var sub = new Assembly("sub");
        sub.Add(bolt);
        sub.Add(nut);
        var top = new Assembly("top");
        top.Add(sub);
        top.Add(sub);
        top.Add(bolt);

        var parts = top.DistinctParts();
        Assert.Equal(2, parts.Count);
        Assert.Contains(bolt, parts);
        Assert.Contains(nut, parts);
    }

    // ---- tabs ----

    [Fact]
    public void TabAdd_Assembly_AssignsColors_EnforcesUniqueNames()
    {
        var scene = new Scene();
        var tab = scene.AddTab("t");
        tab.Add(BoxPart("clamp"));

        var assembly = new Assembly("clamp"); // clashes with the part name
        Assert.Throws<ArgumentException>(() => tab.Add(assembly));

        var ok = new Assembly("vise");
        var jaw = BoxPart("jaw");
        ok.Add(jaw);
        tab.Add(ok);
        Assert.NotNull(jaw.Color);                            // palette-assigned on add
        Assert.Throws<ArgumentException>(() => tab.Add(new Assembly("vise")));
        Assert.Throws<ArgumentException>(() => tab.Add(BoxPart("vise"))); // part vs assembly clash
    }

    [Fact]
    public void TabInstances_RetroAssignsPaletteColors_WithoutReshuffling()
    {
        var scene = new Scene();
        var tab = scene.AddTab("t");
        var assembly = new Assembly("vise");
        var jaw = BoxPart("jaw");
        assembly.Add(jaw);
        tab.Add(assembly);
        var jawColor = jaw.Color;
        Assert.NotNull(jawColor);

        // A part added AFTER the assembly joined the tab has no color until the tab
        // next flattens; the sweep hands it the NEXT palette entry and moves nothing
        // else (the color-stability rule: assignment is ??= and the cursor only
        // advances, so latecomers can never reshuffle earlier parts).
        var screw = BoxPart("screw");
        assembly.Add(screw);
        Assert.Null(screw.Color);
        tab.Instances();
        Assert.NotNull(screw.Color);
        Assert.Equal(jawColor, jaw.Color);
        Assert.NotEqual(jawColor, screw.Color);

        // Idempotent: a second flatten changes nothing.
        var assigned = screw.Color;
        tab.Instances();
        Assert.Equal(assigned, screw.Color);
    }

    [Fact]
    public void TabInstances_LoosePartsFirst_UsePartTransformAndName()
    {
        var scene = new Scene();
        var tab = scene.AddTab("t");
        var loose = new Part("loose", MeshPrimitives.Box(1, 1, 1),
            transform: Matrix4d.CreateTranslation((7, 0, 0)));
        tab.Add(loose);
        var assembly = new Assembly("asm");
        assembly.Add(BoxPart("inner"), At(0, 5, 0));
        tab.Add(assembly);

        var instances = tab.Instances();
        Assert.Equal(2, instances.Count);
        Assert.Equal("loose", instances[0].Path);
        Assert.Equal(loose.Transform, instances[0].World);
        Assert.Equal("asm/inner", instances[1].Path);
    }

    [Fact]
    public void TabBounds_IncludeAssemblyInstances()
    {
        var scene = new Scene();
        var tab = scene.AddTab("t");
        var part = BoxPart();
        var assembly = new Assembly("row");
        assembly.Add(part, At(-5, 0, 0));
        assembly.Add(part, At(5, 0, 0));
        tab.Add(assembly);

        var bounds = tab.Bounds();
        Assert.True(bounds.Min.X < -5.4 && bounds.Max.X > 5.4);
    }

    [Fact]
    public void PartBounds_WorldOverload_UsesGivenPlacement()
    {
        var part = BoxPart();
        var bounds = part.Bounds(Matrix4d.CreateTranslation((0, 0, 10)));
        Assert.True(Math.Abs(bounds.Center.Z - 10) < 1e-12);
        Assert.True(Math.Abs(part.Bounds().Center.Z) < 1e-12); // untouched default
    }

    [Fact]
    public void AssemblyBounds_UnionOfInstanceBounds()
    {
        var part = BoxPart();
        var assembly = new Assembly("pair");
        assembly.Add(part, At(0, -3, 0));
        assembly.Add(part, At(0, 3, 0));

        var bounds = assembly.Bounds();
        Assert.True(bounds.Min.Y < -3.4 && bounds.Max.Y > 3.4);
        Assert.True(bounds.Max.X < 0.6); // unit box, centered
    }
}
