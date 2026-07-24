#:project ../tools/EngrCAD.DocsGen/EngrCAD.DocsGen.csproj
using EngrCAD.Modeling;

var q = new MeshQuality { SegmentsPerCircle = 128 };
var block = Shape.Box(24, 24, 12);
var post = Shape.Cylinder(7, 28).Translate(4, 4, 0);
double cyl = Math.PI * 49 * 28, cylIn = Math.PI * 49 * 12, box = 24 * 24 * 12;
Console.WriteLine($"union  {(block | post).ToMesh(q).Volume():F0} expect {box + cyl - cylIn:F0}");
Console.WriteLine($"inter  {(block & post).ToMesh(q).Volume():F0} expect {cylIn:F0}");
Console.WriteLine($"diff   {(block - post).ToMesh(q).Volume():F0} expect {box - cylIn:F0}");

var machined = Shape.Box(60, 36, 16)
    - Shape.Cylinder(6, 30).Translate(-19, 0, 0)
    - Shape.Cylinder(6, 30).Translate(19, 0, 0)
    - Shape.Box(20, 40, 12).Translate(0, 0, 6);
double exp = 60*36*16 - 2 * Math.PI * 36 * 16 - 20*36*8;
Console.WriteLine($"machined {machined.ToMesh(q).Volume():F0} expect {exp:F0}");
