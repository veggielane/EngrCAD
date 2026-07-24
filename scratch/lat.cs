#:project ../tools/EngrCAD.DocsGen/EngrCAD.DocsGen.csproj
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

var scene = new Scene(new MeshQuality { SdfResolution = 110 });
scene.Add(new Part("gyroid lattice",
    Shape.Sphere(16).Lattice(Sdf.Gyroid(cellSize: 12, thickness: 1.2)),
    Palette.Slate, Matrix4d.CreateTranslation((0, 0, 16))));
EngrCad.RenderToImage(scene, @"scratch\lattice-thin.png", 1600, 1120);
Console.WriteLine("ok");
