using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// 3MF export (the modern 3D-printing interchange format) — dependency-free over the
/// BCL's <see cref="ZipArchive"/> and LINQ-to-XML: an OPC package containing
/// `[Content_Types].xml`, `_rels/.rels`, and `3D/3dmodel.model` (the 3MF core-spec
/// model XML, unit = millimeter). Each <see cref="MeshExportPart"/> becomes one
/// `object` with its transform BAKED into the vertices (the same flattening the
/// merged STL/OBJ exports do — always valid, at the cost of not sharing geometry
/// between instances; a negative-determinant transform flips triangle winding so
/// facets stay outward, which 3MF requires). Part colors become one `basematerials`
/// resource with a base per distinct color; parts without a color reference no
/// material. N-gon faces are fan-triangulated — 3MF meshes are triangles.
/// </summary>
public static class ThreeMfWriter
{
    private static readonly XNamespace Core =
        "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly XNamespace ContentTypes =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Relationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    public static void Write(HalfEdgeMesh mesh, Stream stream, string name = "part") =>
        Write([new MeshExportPart(mesh, name)], stream);

    public static void Write(IReadOnlyList<MeshExportPart> parts, Stream stream)
    {
        if (parts.Count == 0)
            throw new ArgumentException("3MF export needs at least one part.", nameof(parts));

        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, "[Content_Types].xml", new XElement(ContentTypes + "Types",
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "model"),
                new XAttribute("ContentType", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml"))));
        WriteEntry(archive, "_rels/.rels", new XElement(Relationships + "Relationships",
            new XElement(Relationships + "Relationship",
                new XAttribute("Id", "rel0"),
                new XAttribute("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"),
                new XAttribute("Target", "/3D/3dmodel.model"))));
        WriteEntry(archive, "3D/3dmodel.model", BuildModel(parts));
    }

    public static void WriteFile(HalfEdgeMesh mesh, string path, string name = "part")
    {
        using var stream = File.Create(path);
        Write(mesh, stream, name);
    }

    public static void WriteFile(IReadOnlyList<MeshExportPart> parts, string path)
    {
        using var stream = File.Create(path);
        Write(parts, stream);
    }

    private static XElement BuildModel(IReadOnlyList<MeshExportPart> parts)
    {
        var culture = CultureInfo.InvariantCulture;
        var resources = new XElement(Core + "resources");
        var build = new XElement(Core + "build");

        // One basematerials resource; a base per DISTINCT color, in first-use order.
        var colorIndex = new Dictionary<(float, float, float), int>();
        XElement? materials = null;
        const int MaterialsId = 1;
        foreach (var part in parts)
        {
            if (part.Color is not { } color || colorIndex.ContainsKey(color))
                continue;
            materials ??= new XElement(Core + "basematerials",
                new XAttribute("id", MaterialsId.ToString(culture)));
            colorIndex[color] = colorIndex.Count;
            materials.Add(new XElement(Core + "base",
                new XAttribute("name", $"color{colorIndex[color]}"),
                new XAttribute("displaycolor", ToHex(color))));
        }
        if (materials is not null)
            resources.Add(materials);

        int nextId = MaterialsId + 1;
        foreach (var part in parts)
        {
            var (positions, faces) = part.Mesh.ToIndexed();
            bool flip = part.Transform.Determinant < 0;

            var world = new Vector3d[positions.Length];
            var vertices = new XElement(Core + "vertices");
            for (int v = 0; v < positions.Length; v++)
            {
                var p = world[v] = part.Transform.TransformPoint(positions[v]);
                vertices.Add(new XElement(Core + "vertex",
                    new XAttribute("x", p.X.ToString("R", culture)),
                    new XAttribute("y", p.Y.ToString("R", culture)),
                    new XAttribute("z", p.Z.ToString("R", culture))));
            }

            var triangles = new XElement(Core + "triangles");
            foreach (var face in faces)
            {
                // The shared fan rule, read on the transformed points (see StlWriter).
                int apex = PolygonFan.Apex(face, world);
                for (int i = 1; i + 1 < face.Length; i++)
                {
                    triangles.Add(new XElement(Core + "triangle",
                        new XAttribute("v1", face[apex].ToString(culture)),
                        new XAttribute("v2",
                            face[PolygonFan.Corner(apex, face.Length, flip ? i + 1 : i)].ToString(culture)),
                        new XAttribute("v3",
                            face[PolygonFan.Corner(apex, face.Length, flip ? i : i + 1)].ToString(culture))));
                }
            }

            var obj = new XElement(Core + "object",
                new XAttribute("id", nextId.ToString(culture)),
                new XAttribute("type", "model"),
                new XAttribute("name", part.Name));
            if (part.Color is { } c)
            {
                obj.Add(new XAttribute("pid", MaterialsId.ToString(culture)));
                obj.Add(new XAttribute("pindex", colorIndex[c].ToString(culture)));
            }
            obj.Add(new XElement(Core + "mesh", vertices, triangles));
            resources.Add(obj);
            build.Add(new XElement(Core + "item", new XAttribute("objectid", nextId.ToString(culture))));
            nextId++;
        }

        return new XElement(Core + "model",
            new XAttribute("unit", "millimeter"),
            new XAttribute(XNamespace.Xml + "lang", "en-US"),
            resources, build);
    }

    private static string ToHex((float R, float G, float B) color)
    {
        static int Channel(float value) => Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        return $"#{Channel(color.R):X2}{Channel(color.G):X2}{Channel(color.B):X2}FF";
    }

    private static void WriteEntry(ZipArchive archive, string path, XElement root)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        new XDocument(root).Save(stream);
    }
}
