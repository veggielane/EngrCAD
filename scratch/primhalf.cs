#:project ../tools/EngrCAD.DocsGen/EngrCAD.DocsGen.csproj
using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

var scene = new Scene();
scene.Add(new Part("box", Shape.Box(12, 9, 6), Palette.Steel, Matrix4d.CreateTranslation((-20, 0, 3))));
scene.Add(new Part("cylinder", Shape.Cylinder(4.5, 10), Palette.Brass, Matrix4d.CreateTranslation((-6.5, 0, 5))));
scene.Add(new Part("sphere", Shape.Sphere(5.25), Palette.Coral, Matrix4d.CreateTranslation((6.5, 0, 5.25))));
scene.Add(new Part("torus", Shape.Torus(5, 2), Palette.Teal, Matrix4d.CreateTranslation((20, 0, 2))));
EngrCad.RenderToImage(scene, @"scratch\prim-half.png", 1600, 1120);
Console.WriteLine("ok");
