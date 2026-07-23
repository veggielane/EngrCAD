// The live-modeling loop. Run with:
//
//     dotnet watch --project samples/EngrCAD.LiveDemo
//
// then edit the dimensions below and SAVE — the viewer updates in place, camera
// untouched. Break the code and the last good scene stays with the error in the
// overlay. Headless export (no window): dotnet run ... -- --export bracket.step
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

return EngrCad.Run(args, BuildScene, "EngrCAD live bracket");

static Scene BuildScene()
{
    // ---- parameters: edit + save to see the change ----
    double width = 40, depth = 30, thickness = 10;
    double boreRadius = 4;
    double bossRadius = 7, bossHeight = 8;
    double blend = 2.5;

    var body = Shape.Box(width, depth, thickness)
        .SmoothUnion(
            Shape.Cylinder(bossRadius, bossHeight).Translate(width / 4, 0, thickness / 2 + bossHeight / 2 - 1),
            blend)
        - Shape.Cylinder(boreRadius, thickness + bossHeight + 4).Translate(width / 4, 0, 4)
        - Shape.Cylinder(3, thickness + 2).Translate(-width / 3, depth / 4, 0)
        - Shape.Cylinder(3, thickness + 2).Translate(-width / 3, -depth / 4, 0);

    var scene = new Scene(new SceneOptions { SdfResolution = 96 });
    scene.Add("bracket", body, Palette.Steel);
    return scene;
}
