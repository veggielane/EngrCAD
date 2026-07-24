#:project ../tools/EngrCAD.DocsGen/EngrCAD.DocsGen.csproj
using EngrCAD.Modeling;
using EngrCAD.Viewer;

var scene = new Scene();
scene.Add(new Part("box", Shape.Box(24, 18, 12).Translate(0, 0, 6), Palette.Steel));
EngrCad.RenderToImage(scene, @"scratch\box-lifted.png", 1600, 1120);
Console.WriteLine("ok");
