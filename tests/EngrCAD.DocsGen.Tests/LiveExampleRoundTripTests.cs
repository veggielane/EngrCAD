using EngrCAD.Core;
using EngrCAD.DocsGen;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.DocsGen.Tests;

/// <summary>
/// The two halves of the live-documentation-example feature, tested against each other.
/// <c>LiveExamples</c> compiles a snippet for the browser and emits it; <c>LiveExample</c>
/// loads the bytes and runs them. What sits between them is Roslyn's script-submission
/// layout — a static <c>&lt;Factory&gt;(object[])</c> and top-level variables as fields on
/// the instance it parks in slot 1 — which no compiler checks on either side. So the round
/// trip is the test: emit, load, run, and compare the scene against the one the docs
/// harness itself would have rendered.
/// </summary>
public sealed class LiveExampleRoundTripTests
{
    private const string PlateSnippet = """
        var plate = Shape.Box(40, 24, 6) - Shape.Cylinder(4, 30);
        var scene = new Scene();
        scene.Add(new Part("plate", plate, Palette.Steel));
        """;

    [Fact]
    public async Task AnEmittedExample_RebuildsTheSameGeometry()
    {
        var built = LiveExamples.Build(PlateSnippet);
        Assert.Null(built.Refusal);
        Assert.NotNull(built.Assembly);

        var loaded = await LiveExample.RunAsync(built.Assembly!);

        // Not "a scene came back": the SAME solid. A box with a bore has a volume the
        // snippet's own arithmetic gives, and a loader that ran a different submission, or
        // read the wrong field, could not land on it.
        var part = Assert.Single(loaded.Scene.AllParts);
        Assert.Equal("plate", part.Name);
        double expected = 40 * 24 * 6 - Math.PI * 4 * 4 * 6;   // a through bore, both centred
        Assert.Equal(expected, part.GetMesh().Volume(), expected * 1e-3);
    }

    [Fact]
    public async Task DeclaredRenderInputs_ComeBackWithIt()
    {
        // The optional variables docs/writing-examples.md documents. They are read off the
        // same submission fields as `scene`, so a loader that found the scene by luck rather
        // than by the rule would come back with none of these.
        var built = LiveExamples.Build("""
            var scene = new Scene();
            scene.Add(new Part("block", Shape.Box(10, 10, 10)));
            var sectionPlanes = new[] { SectionPlane.On(SectionAxis.Z, 5) };
            var sectionCombine = SectionCombine.Union;
            var explode = 0.5;
            var shading = ShadingStyle.Clay;
            var camera = new CameraState(1.0, 0.5, 60, Vector3d.Zero);
            """);
        Assert.Null(built.Refusal);

        var loaded = await LiveExample.RunAsync(built.Assembly!);

        Assert.Equal(5, Assert.Single(loaded.SectionPlanes!).Offset);
        Assert.Equal(SectionCombine.Union, loaded.SectionCombine);
        Assert.Equal(0.5, loaded.Explode);
        Assert.Equal(ShadingStyle.Clay, loaded.Shading);
        Assert.NotNull(loaded.Camera);
        Assert.Equal(60, loaded.Camera!.Distance, 1e-12);
    }

    [Fact]
    public async Task AnAssemblyThatIsNotAnExample_IsRefusedByName()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LiveExample.RunAsync(File.ReadAllBytes(typeof(Shape).Assembly.Location)));
        Assert.Contains("not a documentation example", error.Message);
    }

    /// <summary>
    /// The reference set IS the rule: the browser compilation sees exactly the assemblies
    /// <c>EngrCAD.Web</c> ships, so a snippet reaching past them is refused by the compiler
    /// rather than by a list somebody has to maintain. Each of these is a real refusal from
    /// the live manifest.
    /// </summary>
    [Theory]
    // The simulation layer is not in the browser payload.
    [InlineData("var mesh = TetMesher.Mesh(Shape.Box(4, 4, 4).ToMesh());", "TetMesher")]
    // The desktop viewer is not either -- only its UI-free half is.
    [InlineData("EngrCad.RenderToImage(new Scene(), \"x.png\");", "EngrCad")]
    // `Scratch` is a docs-harness global, and the browser build supplies no globals.
    [InlineData("var path = Path.Combine(Scratch, \"x.stl\");", "Scratch")]
    public void ASnippetReachingPastTheBrowsersAssemblies_IsRefusedNamingWhatIsMissing(
        string code, string named)
    {
        var built = LiveExamples.Build(code);
        Assert.False(built.Live);
        Assert.Contains("does not compile against the browser's assemblies", built.Refusal);
        Assert.Contains(named, built.Refusal);
    }

    /// <summary>
    /// The one check a reference set cannot make, and the reason it is SEMANTIC. The
    /// browser's filesystem holds only the app's own assets, so a snippet reading one fails
    /// where a reader can see it — but `heightmaps.md` names `Heightmap.ReadPng` in a comment
    /// while computing its grid, and a text scan refuses that page wrongly.
    /// </summary>
    [Fact]
    public void ReadingTheBuildMachine_IsRefused_ButNamingItInAComment_IsNot()
    {
        var reads = LiveExamples.Build("""
            var text = File.ReadAllText("terrain.dat");
            var scene = new Scene();
            """);
        Assert.False(reads.Live);
        Assert.Contains("System.IO.File", reads.Refusal);

        var mentions = LiveExamples.Build("""
            // Any double[,] works - computed, or read from a file with File.ReadAllText.
            var heights = new double[4, 4];
            var scene = new Scene();
            scene.Add(new Part("terrain", Shape.Heightmap(heights, cellSize: 2)));
            """);
        Assert.Null(mentions.Refusal);
    }

    /// <summary>
    /// A finding worth pinning, because it looks like a capability refusal and is a SCOPE
    /// one. Roslyn puts a script's globals type's members in scope, inherited ones included,
    /// so a submission compiled with no globals at all cannot see <c>object</c>'s statics and
    /// a bare <c>ReferenceEquals</c> — legal in every ordinary C# class, and used by
    /// chamfer-fillet.md — fails to compile. Handing over <c>object</c> restores exactly that
    /// scope and nothing else, which is why `Scratch` above is still refused.
    /// </summary>
    [Fact]
    public void BareObjectStatics_AreInScope()
    {
        var built = LiveExamples.Build("""
            var a = new Part("a", Shape.Box(1, 1, 1));
            var same = ReferenceEquals(a, a);
            var scene = new Scene();
            """);
        Assert.Null(built.Refusal);
    }
}
