#:project ../tools/EngrCAD.DocsGen/EngrCAD.DocsGen.csproj
using EngrCAD.Modeling;
using EngrCAD.Viewer;

var scene = new Scene();
scene.Add(new Part("box", Shape.Box(24, 18, 12), Palette.Steel));
EngrCad.RenderToImage(scene, @"scratch\box-only.png", 1600, 1120);
Console.WriteLine("ok");
