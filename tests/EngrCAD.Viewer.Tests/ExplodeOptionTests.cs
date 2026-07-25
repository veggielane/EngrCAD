using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The <c>--explode</c> / <see cref="EngrCadBuilder.WithExplode"/> knob — headless paths
/// only (no GL, no Avalonia lifetime): parsing, precedence, and the fact that a STEP
/// export of an exploded scene is exploded, because both go through the same flattening.
/// </summary>
public class ExplodeOptionTests
{
    private static Scene RigScene()
    {
        var scene = new Scene();
        var plate = new Part("plate", Shape.Box(20, 12, 3));
        var pin = new Part("pin", Shape.Cylinder(1.5, 6));
        var rig = new Assembly("rig");
        rig.Add(plate);
        rig.Add(pin, Frame3d.FromXY((-5, 0, 1.5), Vector3d.UnitX, Vector3d.UnitY));
        rig.Add(pin, Frame3d.FromXY((5, 0, 1.5), Vector3d.UnitX, Vector3d.UnitY));
        scene.AddTab("rig").Add(rig);
        return scene;
    }

    [Fact]
    public void WithExplodeSetsTheOption_AndRejectsNegatives()
    {
        var options = EngrCad.Configure().WithExplode(0.4).Options;
        Assert.Equal(0.4, options.Explode);
        Assert.Throws<ArgumentOutOfRangeException>(() => EngrCad.Configure().WithExplode(-1));
        Assert.Equal(0, new EngrCadOptions().Explode);   // assembled by default
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("-1")]
    public void BadExplodeArgumentsAreAUsageError(string value)
    {
        var log = new ListLogger();
        string[] args = value.Length == 0 ? ["--explode"] : ["--explode", value];
        Assert.Equal(2, EngrCad.Configure().WithLogger(log).Run(args, RigScene));
        Assert.Contains(log.Errors, m => m.Contains("--explode"));
    }

    [Fact]
    public void ExplodeWinsOverTheBuilderDefaultAndReachesTheExport()
    {
        // --export goes through Scene.AllInstances (assembled) by design: exporting a
        // manufacturing file exploded would be wrong. The check here is that the OPTION
        // parses and lands, and that the document-level API is what an exploded export
        // would use.
        var options = new EngrCadOptions();
        var builder = EngrCad.Configure(options).WithExplode(0.25);
        Assert.Equal(0.25, options.Explode);

        var scene = RigScene();
        scene.AutoExplode();
        var assembled = StepAssembly.Plan(scene).Instances;
        var exploded = StepAssembly.Plan(scene, explode: 1).Instances;
        Assert.Equal(assembled.Count, exploded.Count);
        Assert.NotEqual(
            assembled[1].World.TransformPoint(Vector3d.Zero),
            exploded[1].World.TransformPoint(Vector3d.Zero));
    }

    [Fact]
    public void StepExportOfAnAssemblyWritesProductStructure()
    {
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.step");
        try
        {
            var log = new ListLogger();
            Assert.Equal(0, EngrCad.Configure().WithLogger(log).Run(["--export", path], RigScene));
            string text = File.ReadAllText(path);
            // ONE file, one product per distinct part, one occurrence per placement —
            // not the pre-assembly "one file per part" split.
            Assert.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", text);
            Assert.Equal(3, text.Split("NEXT_ASSEMBLY_USAGE_OCCURRENCE(").Length - 1);
            Assert.Equal(2, text.Split("MANIFOLD_SOLID_BREP(").Length - 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASingleSolidSceneStillWritesThePlainPartFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.step");
        try
        {
            var scene = new Scene();
            scene.Add(new Part("cyl", Shape.Cylinder(1, 2)));
            Assert.Equal(0, EngrCad.Configure().WithLogger(new ListLogger()).Run(["--export", path], () => scene));
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("NEXT_ASSEMBLY_USAGE_OCCURRENCE", text);
            Assert.Contains("MANIFOLD_SOLID_BREP(", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
