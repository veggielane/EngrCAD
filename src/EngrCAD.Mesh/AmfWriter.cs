using System.Globalization;
using System.Xml.Linq;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// AMF (Additive Manufacturing File Format, ISO/ASTM 52915) export — plain XML over
/// LINQ-to-XML, unit = millimeter. Each <see cref="MeshExportPart"/> becomes one
/// `object` (name carried as `metadata type="name"`) whose transform is BAKED into the
/// vertex coordinates, matching the merged STL/OBJ flattening; a negative-determinant
/// transform flips triangle winding so facets stay outward. Part colors become one
/// `material` per distinct color, referenced by the object's `volume`. N-gon faces are
/// fan-triangulated — AMF volumes are triangles.
/// </summary>
public static class AmfWriter
{
    public static void Write(HalfEdgeMesh mesh, Stream stream, string name = "part") =>
        Write([new MeshExportPart(mesh, name)], stream);

    public static void Write(IReadOnlyList<MeshExportPart> parts, Stream stream)
    {
        if (parts.Count == 0)
            throw new ArgumentException("AMF export needs at least one part.", nameof(parts));
        var culture = CultureInfo.InvariantCulture;

        var amf = new XElement("amf",
            new XAttribute("unit", "millimeter"),
            new XAttribute("version", "1.1"));

        // A material per DISTINCT color, ids after the objects' so both stay stable.
        var colorId = new Dictionary<(float, float, float), int>();
        foreach (var part in parts)
        {
            if (part.Color is { } color && !colorId.ContainsKey(color))
                colorId[color] = parts.Count + colorId.Count;   // ids follow the object ids
        }

        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var (positions, faces) = part.Mesh.ToIndexed();
            bool flip = part.Transform.Determinant < 0;

            var world = new Vector3d[positions.Length];
            var vertices = new XElement("vertices");
            for (int v = 0; v < positions.Length; v++)
            {
                var w = world[v] = part.Transform.TransformPoint(positions[v]);
                vertices.Add(new XElement("vertex", new XElement("coordinates",
                    new XElement("x", w.X.ToString("R", culture)),
                    new XElement("y", w.Y.ToString("R", culture)),
                    new XElement("z", w.Z.ToString("R", culture)))));
            }

            var volume = new XElement("volume");
            if (part.Color is { } c)
                volume.Add(new XAttribute("materialid", colorId[c].ToString(culture)));
            foreach (var face in faces)
            {
                // The shared fan rule, read on the transformed points (see StlWriter).
                int apex = PolygonFan.Apex(face, world);
                for (int i = 1; i + 1 < face.Length; i++)
                {
                    volume.Add(new XElement("triangle",
                        new XElement("v1", face[apex].ToString(culture)),
                        new XElement("v2",
                            face[PolygonFan.Corner(apex, face.Length, flip ? i + 1 : i)].ToString(culture)),
                        new XElement("v3",
                            face[PolygonFan.Corner(apex, face.Length, flip ? i : i + 1)].ToString(culture))));
                }
            }

            amf.Add(new XElement("object",
                new XAttribute("id", p.ToString(culture)),
                new XElement("metadata", new XAttribute("type", "name"), part.Name),
                new XElement("mesh", vertices, volume)));
        }

        foreach (var (color, id) in colorId.OrderBy(entry => entry.Value))
        {
            amf.Add(new XElement("material",
                new XAttribute("id", id.ToString(culture)),
                new XElement("color",
                    new XElement("r", color.Item1.ToString("R", culture)),
                    new XElement("g", color.Item2.ToString("R", culture)),
                    new XElement("b", color.Item3.ToString("R", culture)))));
        }

        new XDocument(amf).Save(stream);
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
}
