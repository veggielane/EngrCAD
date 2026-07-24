#:project ../tools/EngrCAD.DocsGen/EngrCAD.DocsGen.csproj
using EngrCAD.Modeling;

var box = Shape.Box(20, 20, 20);
var ball = Shape.Sphere(13);
Console.WriteLine($"union       {(box | ball).ToMesh().Volume():F0}  expect ~10036 (tess low)");
Console.WriteLine($"intersection {(box & ball).ToMesh().Volume():F0}  expect ~7167");
Console.WriteLine($"difference  {(box - ball).ToMesh().Volume():F0}  expect ~{8000 - 7167}");
