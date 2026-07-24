#:project ../tools/EngrCAD.DocsGen/EngrCAD.DocsGen.csproj
using EngrCAD.Modeling;

var box = Shape.Box(30, 22, 14).ToMesh();
Console.WriteLine($"box: {box.VertexCount} verts, {box.FaceCount} faces");
var cyl = Shape.Cylinder(11, 26).ToMesh();
Console.WriteLine($"cyl: {cyl.VertexCount} verts, {cyl.FaceCount} faces");
var sph = Shape.Sphere(13).ToMesh();
Console.WriteLine($"sph: {sph.VertexCount} verts, {sph.FaceCount} faces");
