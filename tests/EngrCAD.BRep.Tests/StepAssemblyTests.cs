using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>STEP product structure: NEXT_ASSEMBLY_USAGE_OCCURRENCE export and import.</summary>
public class StepAssemblyTests
{
    private static Matrix4d Pose(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY).ToMatrix();

    private static Matrix4d Turned(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitY, -Vector3d.UnitX).ToMatrix();

    private static int Count(string text, string entity) =>
        text.Split(entity).Length - 1;

    [Fact]
    public void SharedSolidsBecomeOneProductWithSeveralOccurrences()
    {
        var plate = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 10, 2)));
        var bolt = SolidFactory.MakeCylinder(1, 6);

        string step = StepWriter.WriteAssembly(
        [
            new StepInstance("plate", "rig/plate", plate, Matrix4d.Identity),
            new StepInstance("bolt", "rig/bolt", bolt, Pose(5, 0, 2)),
            new StepInstance("bolt", "rig/bolt.2", bolt, Pose(-5, 0, 2)),
        ], "rig");

        // Two geometric products (plate, bolt) plus the assembly product.
        Assert.Equal(2, Count(step, "MANIFOLD_SOLID_BREP("));
        Assert.Equal(3, Count(step, "\nPRODUCT('") + Count(step, "=PRODUCT('"));
        Assert.Equal(3, Count(step, "NEXT_ASSEMBLY_USAGE_OCCURRENCE("));
        Assert.Equal(3, Count(step, "CONTEXT_DEPENDENT_SHAPE_REPRESENTATION("));
        Assert.Equal(3, Count(step, "ITEM_DEFINED_TRANSFORMATION("));
        Assert.Contains("'rig/bolt.2'", step);
        Assert.Contains("SHAPE_REPRESENTATION('rig'", step);
    }

    [Fact]
    public void RoundTripsThroughTheReaderWithPosesIntact()
    {
        var plate = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 10, 2)));
        var bolt = SolidFactory.MakeCylinder(1, 6);
        var poses = new[] { Matrix4d.Identity, Pose(5, 1, 2), Turned(-5, -1, 2) };

        string step = StepWriter.WriteAssembly(
        [
            new StepInstance("plate", "rig/plate", plate, poses[0]),
            new StepInstance("bolt", "rig/bolt", bolt, poses[1]),
            new StepInstance("bolt", "rig/bolt.2", bolt, poses[2]),
        ], "rig");

        var read = StepReader.Read(step);

        Assert.True(read.HasAssemblyStructure);
        Assert.Equal(2, read.Solids.Count);                 // one solid per distinct product
        Assert.Equal(3, read.Instances.Count);              // three placements
        Assert.Equal(["plate", "bolt", "bolt"], read.Instances.Select(i => i.PartName));
        Assert.Equal(["rig/plate", "rig/bolt", "rig/bolt.2"],
            read.Instances.Select(i => i.OccurrenceName));

        // The two bolt occurrences share ONE imported solid, as they shared one product.
        Assert.Same(read.Instances[1].Solid, read.Instances[2].Solid);

        for (int i = 0; i < 3; i++)
        {
            foreach (var probe in new Vector3d[] { (0, 0, 0), (1, 2, 3), (-4, 5, -6) })
            {
                var expected = poses[i].TransformPoint(probe);
                var actual = read.Instances[i].World.TransformPoint(probe);
                Assert.Equal(expected.X, actual.X, 9);
                Assert.Equal(expected.Y, actual.Y, 9);
                Assert.Equal(expected.Z, actual.Z, 9);
            }
        }
    }

    [Fact]
    public void NestedSubAssembliesComposeOnImport()
    {
        // Our writer takes a FLAT instance list, so it never emits two levels — but other
        // CAD systems do, and the reader walks the tree. Build the two-level file by
        // splicing the product structure onto a real single-product file:
        //   root → mid (at z = 10) → block (at x = 3)   ⇒   block at (3, 0, 10).
        string leaf = StepWriter.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2))), "block");
        var parsed = StepParser.Parse(leaf);
        int blockDefinition = parsed.Entities.Values
            .Select(e => e.Find("PRODUCT_DEFINITION_SHAPE"))
            .First(r => r is not null)!.Args[2].AsReference();
        int productContext = parsed.Entities.Values.First(e => e.Find("PRODUCT_CONTEXT") is not null).Id;
        int definitionContext = parsed.Entities.Values
            .First(e => e.Find("PRODUCT_DEFINITION_CONTEXT") is not null).Id;
        int geometricContext = parsed.Entities.Values
            .First(e => e.Find("GEOMETRIC_REPRESENTATION_CONTEXT") is not null).Id;
        int blockRepresentation = parsed.Entities.Values
            .First(e => e.Find("ADVANCED_BREP_SHAPE_REPRESENTATION") is not null).Id;

        var extra = new System.Text.StringBuilder();
        int next = parsed.Entities.Keys.Max() + 1;
        int Emit(string entity)
        {
            extra.Append('#').Append(next).Append('=').Append(entity).Append(";\n");
            return next++;
        }

        (int Definition, int Shape, int Representation) Level(string name)
        {
            int product = Emit($"PRODUCT('{name}','{name}','',(#{productContext}))");
            int formation = Emit($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
            int definition = Emit($"PRODUCT_DEFINITION('design','',#{formation},#{definitionContext})");
            int shape = Emit($"PRODUCT_DEFINITION_SHAPE('','',#{definition})");
            int representation = Emit($"SHAPE_REPRESENTATION('{name}',(),#{geometricContext})");
            Emit($"SHAPE_DEFINITION_REPRESENTATION(#{shape},#{representation})");
            return (definition, shape, representation);
        }

        int Placement(double x, double y, double z)
        {
            int point = Emit($"CARTESIAN_POINT('',({x}.,{y}.,{z}.))");
            int zDir = Emit("DIRECTION('',(0.,0.,1.))");
            int xDir = Emit("DIRECTION('',(1.,0.,0.))");
            return Emit($"AXIS2_PLACEMENT_3D('',#{point},#{zDir},#{xDir})");
        }

        void Occurrence(string name, int parentDefinition, int parentRepresentation,
                        int childDefinition, int childRepresentation, double x, double y, double z)
        {
            int nauo = Emit($"NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','{name}','',"
                          + $"#{parentDefinition},#{childDefinition},$)");
            int nauoShape = Emit($"PRODUCT_DEFINITION_SHAPE('','',#{nauo})");
            int from = Placement(0, 0, 0);
            int to = Placement(x, y, z);
            int transformation = Emit($"ITEM_DEFINED_TRANSFORMATION('','',#{from},#{to})");
            int relationship = Emit(
                $"(REPRESENTATION_RELATIONSHIP('','',#{childRepresentation},#{parentRepresentation})"
                + $"REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#{transformation})"
                + "SHAPE_REPRESENTATION_RELATIONSHIP())");
            Emit($"CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#{relationship},#{nauoShape})");
        }

        var mid = Level("mid");
        var root = Level("root");
        Occurrence("mid", root.Definition, root.Representation, mid.Definition, mid.Representation, 0, 0, 10);
        Occurrence("block", mid.Definition, mid.Representation, blockDefinition, blockRepresentation, 3, 0, 0);

        string nested = leaf.Replace("ENDSEC;\nEND-ISO-10303-21;", extra + "ENDSEC;\nEND-ISO-10303-21;");
        var read = StepReader.Read(nested);

        Assert.True(read.HasAssemblyStructure);
        var placed = Assert.Single(read.Instances);
        // Paths start below the root product, so two levels give "mid/block".
        Assert.Equal("mid/block", placed.OccurrenceName);
        var origin = placed.World.TransformPoint(Vector3d.Zero);
        Assert.Equal(3, origin.X, 9);
        Assert.Equal(0, origin.Y, 9);
        Assert.Equal(10, origin.Z, 9);
    }

    [Fact]
    public void ImportedSolidsAreGeometricallyTheOnesExported()
    {
        var plate = SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 10, 2)));
        string step = StepWriter.WriteAssembly(
            [new StepInstance("plate", "rig/plate", plate, Pose(1, 2, 3))], "rig");

        var read = StepReader.Read(step);
        var imported = Assert.Single(read.Solids);

        Assert.Equal(plate.Faces.Count(), imported.Faces.Count());
        Assert.Equal(plate.Edges.Count(), imported.Edges.Count());
        imported.Validate();
        // The solid comes back in its OWN coordinates: the pose lives on the instance.
        var bounds = BoundsOf(imported);
        Assert.Equal(0, bounds.Min.X, 9);
        Assert.Equal(20, bounds.Max.X, 9);

        static Aabb BoundsOf(BrepSolid solid)
        {
            var box = Aabb.Empty;
            foreach (var face in solid.Faces)
                box = box.Union(face.Bounds());
            return box;
        }
    }

    [Fact]
    public void APlainSingleSolidFileStillReadsAsOneIdentityInstance()
    {
        string step = StepWriter.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (4, 4, 4))), "block");
        var read = StepReader.Read(step);

        Assert.False(read.HasAssemblyStructure);
        var instance = Assert.Single(read.Instances);
        Assert.Equal(Matrix4d.Identity, instance.World);
        Assert.Equal("block", instance.PartName);
    }

    [Fact]
    public void NonRigidPlacementsAreRefusedByName()
    {
        var block = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        var scaled = Matrix4d.Identity;
        scaled = Matrix4d.CreateScale(2) * scaled;

        var exception = Assert.Throws<NotSupportedException>(() => StepWriter.WriteAssembly(
            [new StepInstance("block", "rig/block", block, scaled)], "rig"));

        Assert.Contains("rig/block", exception.Message);
        Assert.Contains("non-rigid", exception.Message);
    }

    [Fact]
    public void MirroredPlacementsAreRefusedByName()
    {
        // A reflection passes every orthonormality test (unit, perpendicular axes) but
        // is improper: AXIS2 axes are right-handed by definition, so writing it would
        // silently re-pose the part un-mirrored on the way back in.
        var block = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        var mirrored = Matrix4d.CreateScale(new Vector3d(-1, 1, 1));

        var exception = Assert.Throws<NotSupportedException>(() => StepWriter.WriteAssembly(
            [new StepInstance("block", "rig/block", block, mirrored)], "rig"));

        Assert.Contains("rig/block", exception.Message);
        Assert.Contains("mirrored", exception.Message);
    }

    [Fact]
    public void NamesWithApostrophesAreEscaped()
    {
        var block = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        string step = StepWriter.WriteAssembly(
            [new StepInstance("O'Brien plate", "rig/O'Brien plate", block, Matrix4d.Identity)], "rig");

        Assert.Contains("'O''Brien plate'", step);
        // ...and it survives the parser, which decodes the doubling.
        var read = StepReader.Read(step);
        Assert.Equal("O'Brien plate", read.Instances[0].PartName);
    }

    [Fact]
    public void AnEmptyInstanceListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => StepWriter.WriteAssembly([], "rig"));
    }
}
