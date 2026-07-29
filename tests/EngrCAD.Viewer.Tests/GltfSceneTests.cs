using System.Text;
using System.Text.Json;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The document → glTF bridge. What is being checked here is the STRUCTURE glTF exists to
/// carry: the assembly hierarchy survives as a node tree, a part placed N times is one
/// mesh and N nodes, and result colours travel while the deformation exaggeration
/// deliberately does not.
/// </summary>
public class GltfSceneTests
{
    [Fact]
    public void APlainSceneBecomesOneRootNodePerTab()
    {
        var scene = new Scene();
        scene.Add(new Part("block", Shape.Box(10, 10, 10)));
        var second = scene.AddTab("Fixtures");
        second.Add(new Part("jig", Shape.Cylinder(3, 8)));

        var plan = GltfScene.Plan(scene);

        Assert.Equal(2, plan.Roots.Count);
        Assert.Equal("Model", plan.Roots[0].Name);
        Assert.Equal("Fixtures", plan.Roots[1].Name);
        Assert.Equal(2, plan.Geometries.Count);
        Assert.Empty(plan.Skipped);
        // Tab nodes group; the part node under each carries the geometry.
        Assert.Null(plan.Roots[0].Geometry);
        Assert.Equal("block", plan.Roots[0].Children.Single().Name);
    }

    [Fact]
    public void TheAssemblyHierarchyIsPreservedAsNestedNodes()
    {
        var bolt = new Part("bolt", Shape.Cylinder(1, 6));
        var plate = new Part("plate", Shape.Box(40, 40, 5));

        var clamp = new Assembly("clamp");
        clamp.Add(bolt, At(5, 0, 0));
        clamp.Add(bolt, At(-5, 0, 0));

        var stack = new Assembly("stack");
        stack.Add(plate);
        stack.Add(clamp, At(0, 0, 10));

        var scene = new Scene();
        var tab = scene.AddTab("Assembly");
        tab.Add(stack);

        var plan = GltfScene.Plan(scene);

        var tabNode = Assert.Single(plan.Roots);
        var stackNode = Assert.Single(tabNode.Children);
        Assert.Equal("stack", stackNode.Name);
        Assert.Equal(2, stackNode.Children.Count);

        var clampNode = stackNode.Children.Single(n => n.Name == "clamp");
        Assert.Equal(2, clampNode.Children.Count);
        // The sub-assembly's own node carries the occurrence frame; its children are then
        // relative to it, which is what "hierarchy preserved" has to mean.
        Assert.Equal(10.0, clampNode.Transform.M34, 12);
        Assert.Equal(5.0, clampNode.Children[0].Transform.M14, 12);

        // TWO parts, two meshes — the bolt placed twice is one mesh.
        Assert.Equal(2, plan.Geometries.Count);
        var boltGeometry = clampNode.Children[0].Geometry;
        Assert.Equal(boltGeometry, clampNode.Children[1].Geometry);
    }

    [Fact]
    public void ComposedNodeTransformsAgreeWithTheFlattenedInstances()
    {
        // The bridge composes occurrence frames itself instead of consuming Flatten's
        // output, so the two must be shown to agree — otherwise an exported assembly
        // could be posed differently from the one on screen.
        var bolt = new Part("bolt", Shape.Cylinder(1, 6))
        {
            Transform = Matrix4d.CreateTranslation(new Vector3d(0, 0, 2)),
        };
        var inner = new Assembly("inner");
        inner.Add(bolt, Frame3d.FromZX(new Vector3d(3, 4, 5), (0, 1, 0), (1, 0, 0)));
        var outer = new Assembly("outer");
        outer.Add(inner, At(-7, 2, 1));

        var plan = GltfScene.Plan(outer);
        var flattened = outer.Flatten();

        var world = Compose(plan.Roots[0], Matrix4d.Identity).Single();
        var expected = flattened.Single().World;
        for (int r = 1; r <= 4; r++)
        {
            for (int c = 1; c <= 4; c++)
                Assert.Equal(Entry(expected, r, c), Entry(world, r, c), 12);
        }
    }

    [Fact]
    public void ExplodeOffsetsCompose_AndFactorZeroLeavesFramesUntouched()
    {
        var lid = new Part("lid", Shape.Box(10, 10, 2));
        var box = new Part("box", Shape.Box(10, 10, 8));
        var assembly = new Assembly("case");
        assembly.Add(box);
        var lidOccurrence = assembly.Add(lid, At(0, 0, 5));
        lidOccurrence.ExplodeOffset = new Vector3d(0, 0, 20);

        var assembled = GltfScene.Plan(assembly);
        var exploded = GltfScene.Plan(assembly, explode: 1);

        var assembledLid = assembled.Roots[0].Children.Single(n => n.Name == "lid");
        var explodedLid = exploded.Roots[0].Children.Single(n => n.Name == "lid");
        Assert.Equal(5.0, assembledLid.Transform.M34, 12);
        Assert.Equal(25.0, explodedLid.Transform.M34, 12);
    }

    [Fact]
    public void PartColorsAndTranslucencyTravel()
    {
        var scene = new Scene();
        scene.Add(new Part("brass", Shape.Box(4, 4, 4), Palette.Brass));
        scene.Add(new Part("glass", Shape.Box(4, 4, 4)) { DisplayMode = DisplayMode.Translucent });

        var plan = GltfScene.Plan(scene);

        var brass = plan.Geometries.Single(g => g.Name == "brass");
        Assert.Equal(Palette.Brass.R, brass.Color!.Value.R);
        Assert.Equal(1f, brass.Opacity);

        var glass = plan.Geometries.Single(g => g.Name == "glass");
        Assert.Equal(GltfScene.TranslucentAlpha, glass.Opacity);
    }

    [Fact]
    public void ResultColorsTravelAndAgreeWithWhatTheViewerDraws()
    {
        var part = new Part("plate", Shape.Box(20, 10, 2));
        var mesh = part.GetMesh();
        part.AddResult(MeshField.Sample(mesh, "stress", "MPa", p => p.X));
        part.FieldDisplay = new FieldDisplay { Field = "stress" };
        var scene = new Scene();
        scene.Add(part);

        var plan = GltfScene.Plan(scene);
        var geometry = Assert.Single(plan.Geometries);
        Assert.NotNull(geometry.VertexColors);
        Assert.Equal(mesh.VertexCount, geometry.VertexColors!.Count);

        // The exported colours ARE the rendered ones: both come from
        // FieldRendering.SourceColors, so a plot in a browser and a plot in the viewport
        // cannot disagree about what a value looks like.
        Assert.True(part.TryResolveFieldDisplay(out var display, out _));
        var expected = FieldRendering.SourceColors(display.Field, display.Range, display.ColorMap);
        Assert.Equal(expected, geometry.VertexColors);
    }

    [Fact]
    public void ADeformedDisplayExportsColorsButNotTheExaggeratedShape()
    {
        // An exaggeration factor is a viewing parameter with nowhere to live in a glTF
        // file, so a file carrying 50x-displaced geometry would be indistinguishable from
        // a model that really is that shape. Colours travel; the displacement does not.
        var part = new Part("beam", Shape.Box(40, 4, 4));
        var mesh = part.GetMesh();
        part.AddResult(MeshField.Sample(mesh, "stress", "MPa", p => p.X));
        part.AddResult(MeshField.SampleVector(mesh, "u", "mm", p => new Vector3d(0, 0, p.X * 0.01)));
        part.FieldDisplay = new FieldDisplay { Field = "stress", Deform = "u", DeformScale = 50 };
        var scene = new Scene();
        scene.Add(part);

        var plan = GltfScene.Plan(scene);
        var geometry = Assert.Single(plan.Geometries);
        Assert.NotNull(geometry.VertexColors);
        // Same mesh object the part meshes to — no displaced copy anywhere.
        Assert.Same(mesh, geometry.Mesh);
    }

    [Fact]
    public void ADisplayNamingARemovedResultStillExportsThePartUncolored()
    {
        var part = new Part("plate", Shape.Box(10, 10, 2))
        {
            FieldDisplay = new FieldDisplay { Field = "gone" },
        };
        var scene = new Scene();
        scene.Add(part);

        var plan = GltfScene.Plan(scene);
        Assert.Null(Assert.Single(plan.Geometries).VertexColors);
    }

    [Fact]
    public void APartThatWillNotMeshIsNamedAndDropped()
    {
        var scene = new Scene();
        scene.Add(new Part("good", Shape.Box(5, 5, 5)));
        scene.Add(new Part("bad", Shape.From(new ThrowingSdf())));

        var plan = GltfScene.Plan(scene);

        Assert.Equal("good", Assert.Single(plan.Geometries).Name);
        var (part, reason) = Assert.Single(plan.Skipped);
        Assert.Equal("bad", part.Name);
        Assert.Contains("deliberate", reason);
        // The rest of the scene still exports.
        Assert.Single(plan.Roots[0].Children);
    }

    [Fact]
    public void TheFlatInstanceOverloadKeepsInstancingWithoutHierarchy()
    {
        var bolt = new Part("bolt", Shape.Cylinder(1, 6));
        var assembly = new Assembly("rack");
        for (int i = 0; i < 5; i++)
            assembly.Add(bolt, At(i * 4, 0, 0));

        var plan = GltfScene.Plan(assembly.Flatten());

        Assert.Single(plan.Geometries);      // one mesh...
        Assert.Equal(5, plan.Roots.Count);   // ...five placements
        Assert.All(plan.Roots, n => Assert.Equal(0, n.Geometry));
        Assert.Equal("rack/bolt", plan.Roots[0].Name);
    }

    [Fact]
    public void APartFilterDecidesWhatExportsWithoutChangingTheStructure()
    {
        var keep = new Part("keep", Shape.Box(3, 3, 3));
        var drop = new Part("drop", Shape.Box(3, 3, 3));
        var scene = new Scene();
        scene.Add(keep);
        scene.Add(drop);

        var plan = GltfScene.Plan(scene, parts: [keep]);

        Assert.Equal("keep", Assert.Single(plan.Geometries).Name);
        Assert.Equal("keep", Assert.Single(plan.Roots[0].Children).Name);
    }

    [Fact]
    public void WriteFileProducesAReadableGlbForAWholeAssembly()
    {
        var bolt = new Part("bolt", Shape.Cylinder(1, 6), Palette.Steel);
        var plate = new Part("plate", Shape.Box(40, 40, 5), Palette.Brass);
        var stack = new Assembly("stack");
        stack.Add(plate);
        stack.Add(bolt, At(10, 10, 5));
        stack.Add(bolt, At(-10, 10, 5));

        var scene = new Scene();
        scene.AddTab("Assembly").Add(stack);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".glb");
        try
        {
            var plan = GltfScene.WriteFile(scene, path);
            Assert.Equal(2, plan.Geometries.Count);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal("glTF", Encoding.ASCII.GetString(bytes, 0, 4));

            // Parse the JSON chunk and check the shape of what came out.
            int jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
            using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength).ToArray());
            var root = document.RootElement;
            Assert.Equal(2, root.GetProperty("meshes").GetArrayLength());
            // Three placement nodes (plate + two bolts) + stack + tab + the unit root.
            Assert.Equal(6, root.GetProperty("nodes").GetArrayLength());
            Assert.Equal("Assembly", root.GetProperty("scenes")[0].GetProperty("name").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- helpers ----

    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromOrthonormal(new Vector3d(x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static double Entry(in Matrix4d m, int row, int column) => (row, column) switch
    {
        (1, 1) => m.M11, (1, 2) => m.M12, (1, 3) => m.M13, (1, 4) => m.M14,
        (2, 1) => m.M21, (2, 2) => m.M22, (2, 3) => m.M23, (2, 4) => m.M24,
        (3, 1) => m.M31, (3, 2) => m.M32, (3, 3) => m.M33, (3, 4) => m.M34,
        _ => (row, column) switch
        {
            (4, 1) => m.M41, (4, 2) => m.M42, (4, 3) => m.M43, _ => m.M44,
        },
    };

    private static List<Matrix4d> Compose(GltfNode node, in Matrix4d parent)
    {
        var world = parent * node.Transform;
        var results = new List<Matrix4d>();
        if (node.Geometry is not null)
            results.Add(world);
        foreach (var child in node.Children)
            results.AddRange(Compose(child, world));
        return results;
    }

    private sealed class ThrowingSdf : Implicit.Sdf
    {
        public override double Evaluate(in Vector3d point) =>
            throw new InvalidOperationException("a deliberate meshing failure");

        public override Aabb Bounds => new((-1, -1, -1), (1, 1, 1));
    }
}
