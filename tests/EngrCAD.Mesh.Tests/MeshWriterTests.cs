using System.IO.Compression;
using System.Xml.Linq;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The 3MF / AMF / OFF writers: package/XML structure, transform baking with winding
/// flips under mirroring, color materials — and OFF round-trips through
/// <see cref="OffReader"/> exactly, which is the strongest check available (the other
/// two have no reader here, so they are verified structurally against their specs).
/// </summary>
public class MeshWriterTests
{
    private static readonly XNamespace Core3mf =
        "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    // -------------------------------------------------------------------------- OFF

    [Fact]
    public void Off_RoundTripsThroughTheReader()
    {
        var mesh = MeshPrimitives.Cylinder(1.5, 4, 16);
        using var writer = new StringWriter();
        OffWriter.Write(mesh, writer);

        var result = OffReader.Read(new StringReader(writer.ToString()));
        Assert.NotNull(result.Mesh);
        Assert.Equal(mesh.VertexCount, result.Mesh!.VertexCount);
        // The reader fan-triangulates n-gon faces, so it comes back as sum(degree - 2).
        Assert.Equal(mesh.Faces.Sum(f => f.Degree - 2), result.Mesh.FaceCount);
        Assert.Equal(mesh.Volume(), result.Mesh.Volume(), 12);   // R-format doubles: exact
    }

    [Fact]
    public void Off_MergesPartsAndMirrorKeepsOutwardVolume()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        var mirror = Matrix4d.CreateScale(new Vector3d(-1, 1, 1));
        using var writer = new StringWriter();
        OffWriter.Write([(box, Matrix4d.CreateTranslation((5, 0, 0))), (box, mirror)], writer);

        var result = OffReader.Read(new StringReader(writer.ToString()));
        Assert.NotNull(result.Mesh);
        // Two disjoint unit boxes, both outward: total volume 2 (a wrong winding on the
        // mirrored copy would cancel its volume to -1 + 1 = 0).
        Assert.Equal(2.0, result.Mesh!.Volume(), 9);
    }

    // -------------------------------------------------------------------------- 3MF

    [Fact]
    public void ThreeMf_PackageStructureAndCounts()
    {
        var mesh = MeshPrimitives.Box(2, 1, 1);
        using var stream = new MemoryStream();
        ThreeMfWriter.Write(
            [new MeshExportPart(mesh, Matrix4d.CreateTranslation((10, 0, 0)), "brick", (1f, 0f, 0f))],
            stream);

        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("_rels/.rels"));
        var entry = archive.GetEntry("3D/3dmodel.model");
        Assert.NotNull(entry);

        using var model = entry!.Open();
        var document = XDocument.Load(model);
        var obj = Assert.Single(document.Root!.Element(Core3mf + "resources")!.Elements(Core3mf + "object"));
        Assert.Equal("brick", obj.Attribute("name")!.Value);

        var vertices = obj.Descendants(Core3mf + "vertex").ToList();
        Assert.Equal(8, vertices.Count);
        Assert.Equal(12, obj.Descendants(Core3mf + "triangle").Count());

        // The transform is BAKED: every x sits at 10 +/- 1.
        foreach (var vertex in vertices)
        {
            double x = double.Parse(vertex.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(x, 9, 11);
        }

        // Color: one basematerials with the red base, referenced by the object.
        var materials = document.Root!.Element(Core3mf + "resources")!.Element(Core3mf + "basematerials");
        Assert.NotNull(materials);
        Assert.Equal("#FF0000FF", materials!.Element(Core3mf + "base")!.Attribute("displaycolor")!.Value);
        Assert.Equal(materials.Attribute("id")!.Value, obj.Attribute("pid")!.Value);

        // One build item per part.
        Assert.Single(document.Root!.Element(Core3mf + "build")!.Elements(Core3mf + "item"));
    }

    [Fact]
    public void ThreeMf_MirrorFlipsTriangleWinding()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        using var straight = new MemoryStream();
        using var mirrored = new MemoryStream();
        ThreeMfWriter.Write([new MeshExportPart(box, "a")], straight);
        ThreeMfWriter.Write(
            [new MeshExportPart(box, Matrix4d.CreateScale(new Vector3d(-1, 1, 1)), "a")], mirrored);

        // Signed volume from the written triangles: both must be positive (outward).
        Assert.True(SignedVolumeOf3mf(straight) > 0);
        Assert.True(SignedVolumeOf3mf(mirrored) > 0);
    }

    private static double SignedVolumeOf3mf(MemoryStream stream)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var model = archive.GetEntry("3D/3dmodel.model")!.Open();
        var document = XDocument.Load(model);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var mesh = document.Descendants(Core3mf + "mesh").Single();
        var vertices = mesh.Descendants(Core3mf + "vertex")
            .Select(v => new Vector3d(
                double.Parse(v.Attribute("x")!.Value, culture),
                double.Parse(v.Attribute("y")!.Value, culture),
                double.Parse(v.Attribute("z")!.Value, culture)))
            .ToArray();
        double volume = 0;
        foreach (var t in mesh.Descendants(Core3mf + "triangle"))
        {
            var a = vertices[int.Parse(t.Attribute("v1")!.Value, culture)];
            var b = vertices[int.Parse(t.Attribute("v2")!.Value, culture)];
            var c = vertices[int.Parse(t.Attribute("v3")!.Value, culture)];
            volume += a.Dot(b.Cross(c)) / 6.0;
        }
        return volume;
    }

    // -------------------------------------------------------------------------- AMF

    [Fact]
    public void Amf_ObjectsMaterialsAndBakedTransforms()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        using var stream = new MemoryStream();
        AmfWriter.Write(
            [
                new MeshExportPart(box, Matrix4d.CreateTranslation((5, 0, 0)), "left", (1f, 0f, 0f)),
                new MeshExportPart(box, Matrix4d.Identity, "right", (1f, 0f, 0f)),   // same color: shared material
            ],
            stream);

        stream.Position = 0;
        var document = XDocument.Load(stream);
        Assert.Equal("millimeter", document.Root!.Attribute("unit")!.Value);

        var objects = document.Root.Elements("object").ToList();
        Assert.Equal(2, objects.Count);
        Assert.Equal("left", objects[0].Element("metadata")!.Value);
        Assert.Equal(8, objects[0].Descendants("vertex").Count());
        Assert.Equal(12, objects[0].Descendants("triangle").Count());

        // One material shared by both volumes (distinct colors dedupe).
        var material = Assert.Single(document.Root.Elements("material"));
        foreach (var obj in objects)
            Assert.Equal(material.Attribute("id")!.Value, obj.Descendants("volume").Single().Attribute("materialid")!.Value);

        // Baked translation on the first object's x coordinates.
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var x in objects[0].Descendants("x"))
            Assert.InRange(double.Parse(x.Value, culture), 4, 6);
    }

    [Fact]
    public void EmptyPartLists_AreRefused()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => ThreeMfWriter.Write([], stream));
        Assert.Throws<ArgumentException>(() => AmfWriter.Write([], stream));
    }
}
