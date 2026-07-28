#:project ../src/EngrCAD.Fea/EngrCAD.Fea.csproj

using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;

var box = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(4, 4, 4)));
var coarse = TetMesher.Mesh(box, null, out var r0);
Console.WriteLine($"coarse: {r0}");

var wn = new MeshWindingNumber(box.Triangulated());
Console.WriteLine($"wn at centre = {wn.FastWindingNumber(new Vector3d(2, 2, 2)):F4}  exact {wn.WindingNumber(new Vector3d(2, 2, 2)):F4}");
Console.WriteLine($"wn at (0.2,0.2,0.2) = {wn.FastWindingNumber(new Vector3d(0.2, 0.2, 0.2)):F4}");
Console.WriteLine($"wn outside = {wn.FastWindingNumber(new Vector3d(9, 9, 9)):F4}");

try
{
    var refined = TetMesher.Mesh(box, new TetMeshOptions { RefineQuality = true, RadiusEdgeRatio = 1.6, MaxElementSize = 1.5 }, out var r1);
    Console.WriteLine($"refined: {r1}");
}
catch (Exception ex)
{
    Console.WriteLine($"refined FAILED: {ex.Message}");
}
