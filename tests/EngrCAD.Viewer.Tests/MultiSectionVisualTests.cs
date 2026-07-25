using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Viewer.Tests;

// TEMPORARY visual harness (deleted before commit).
[Collection("offscreen-gl")]
public class MultiSectionVisualTests(ITestOutputHelper output)
{
    private const string Dir =
        @"C:\Users\chris\AppData\Local\Temp\claude\C--Users-chris-projects-EngrCAD\d5bd5baf-8a48-4a56-9678-00a7cfd0de60\scratchpad";

    [SkippableFact]
    public void RenderCuts()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, "no GL");
        Directory.CreateDirectory(Dir);

        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 64 });
        scene.Add(new Part("tube", Shape.Cylinder(6, 12) - Shape.Cylinder(4, 14)
            - Shape.Cylinder(1.2, 20).Rotate(Vector3d.UnitX, 90), Palette.Brass));
        scene.PreMesh();

        EngrCad.RenderToImage(scene, Path.Combine(Dir, "cut-single.png"), 900, 620,
            sectionAxis: SectionAxis.Y, sectionOffset: 0.0);
        EngrCad.RenderToImage(scene, Path.Combine(Dir, "cut-quarter.png"), 900, 620,
            sectionPlanes: [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0)]);
        EngrCad.RenderToImage(scene, Path.Combine(Dir, "cut-octant.png"), 900, 620,
            sectionPlanes: [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0),
                SectionPlane.On(SectionAxis.Z, 0)]);
        EngrCad.RenderToImage(scene, Path.Combine(Dir, "cut-union.png"), 900, 620,
            sectionPlanes: [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0)],
            sectionCombine: SectionCombine.Union);

        var sdf = new Scene(new MeshQuality { SdfResolution = 90 });
        sdf.Add(new Part("blend", Shape.From(EngrCAD.Implicit.Sdf.Sphere(5).SmoothUnion(
            EngrCAD.Implicit.Sdf.Box((4, 4, 4)).Translate((4, 0, 0)), 2)), Palette.Teal));
        sdf.PreMesh();
        EngrCad.RenderToImage(sdf, Path.Combine(Dir, "cut-isolines-quarter.png"), 900, 620,
            sectionPlanes: [SectionPlane.On(SectionAxis.Z, 0), SectionPlane.On(SectionAxis.Y, 0)]);
        output.WriteLine("rendered");
    }
}
