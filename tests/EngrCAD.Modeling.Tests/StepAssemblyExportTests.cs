using EngrCAD.Core;
using EngrCAD.Implicit;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class StepAssemblyExportTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Assembly Rig()
    {
        var plate = new Part("plate", Shape.Box(20, 12, 3));
        var pin = new Part("pin", Shape.Cylinder(1.5, 6));
        var rig = new Assembly("rig");
        rig.Add(plate);
        rig.Add(pin, At(-5, 0, 1.5));
        rig.Add(pin, At(5, 0, 1.5));
        return rig;
    }

    [Fact]
    public void SharedPartsBecomeOneProductWithSeveralOccurrences()
    {
        var plan = StepAssembly.Plan(Rig());

        Assert.Equal(3, plan.Instances.Count);
        Assert.Equal(2, plan.ProductCount);            // plate + pin
        Assert.Empty(plan.Skipped);
        Assert.Equal(["rig/plate", "rig/pin", "rig/pin.2"], plan.Instances.Select(i => i.OccurrenceName));
        // The two pin occurrences hand the writer the SAME solid — Part.TryGetSolid's cache.
        Assert.Same(plan.Instances[1].Solid, plan.Instances[2].Solid);
    }

    [Fact]
    public void PosesComeFromTheFlatteningAndRoundTrip()
    {
        var rig = Rig();
        string step = StepAssembly.Write(rig.Flatten(), "rig");
        var read = EngrCAD.BRep.StepReader.Read(step);

        Assert.True(read.HasAssemblyStructure);
        Assert.Equal(3, read.Instances.Count);
        var expected = rig.Flatten();
        for (int i = 0; i < 3; i++)
        {
            var a = expected[i].World.TransformPoint(Vector3d.Zero);
            var b = read.Instances[i].World.TransformPoint(Vector3d.Zero);
            Assert.Equal(a.X, b.X, 9);
            Assert.Equal(a.Y, b.Y, 9);
            Assert.Equal(a.Z, b.Z, 9);
        }
    }

    [Fact]
    public void AnExplodedFlatteningExportsExploded()
    {
        var rig = Rig();
        rig.AutoExplode(distance: 20);

        var assembled = StepAssembly.Plan(rig).Instances;
        var exploded = StepAssembly.Plan(rig, explode: 1).Instances;

        Assert.Equal(assembled.Count, exploded.Count);
        Assert.NotEqual(
            assembled[1].World.TransformPoint(Vector3d.Zero).Z,
            exploded[1].World.TransformPoint(Vector3d.Zero).Z);
    }

    [Fact]
    public void PartsWithNoExactSolidAreNamedNotSilentlyDropped()
    {
        var rig = new Assembly("rig");
        rig.Add(new Part("plate", Shape.Box(10, 10, 2)));
        var blob = new Part("blob", Sdf.Sphere(3));
        rig.Add(blob);
        rig.Add(blob, At(8, 0, 0));

        var plan = StepAssembly.Plan(rig);

        Assert.Single(plan.Instances);
        var (part, paths) = Assert.Single(plan.Skipped);
        Assert.Same(blob, part);
        Assert.Equal(["rig/blob", "rig/blob.2"], paths);
    }

    [Fact]
    public void NothingExportableIsRefusedWithAReason()
    {
        var rig = new Assembly("rig");
        rig.Add(new Part("blob", Sdf.Sphere(3)));

        var exception = Assert.Throws<InvalidOperationException>(() => StepAssembly.Write(rig.Flatten()));
        Assert.Contains("no part in this assembly has an exact B-Rep", exception.Message);
    }

    [Fact]
    public void TabsAndScenesExportToo()
    {
        var scene = new Scene();
        var tab = scene.AddTab("model");
        tab.Add(new Part("jig", Shape.Box(4, 4, 4)));
        tab.Add(Rig());

        Assert.Equal(4, StepAssembly.Plan(tab).Instances.Count);      // jig + plate + 2 pins
        Assert.Equal(3, StepAssembly.Plan(scene).ProductCount);       // jig, plate, pin
    }

    [Fact]
    public void WritesAFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-assembly-{Guid.NewGuid():N}.step");
        try
        {
            var plan = StepAssembly.WriteFile(Rig(), path);
            Assert.Equal(3, plan.Instances.Count);
            string text = File.ReadAllText(path);
            Assert.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", text);
            Assert.StartsWith("ISO-10303-21;", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
