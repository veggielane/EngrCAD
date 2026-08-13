using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// VRML97 (.wrl) import (<see cref="VrmlReader"/>). The oracles are geometric — a closed unit
/// cube's exact volume through every code path (the transform stack, DEF/USE instancing, the
/// ccw/mirror winding XOR) — because a scene-graph reader's classic failure is a plausible mesh
/// under the wrong transform. Coordinates read VERBATIM (no unit factor); dirt is reported, never
/// thrown; the version/PROTO/truncation refusals are by name.
/// </summary>
public sealed class VrmlReaderTests
{
    /// <summary>A unit cube (corners 0..1) as an IndexedFaceSet body, outward CCW quads.</summary>
    private const string CubeGeometry = """
        geometry IndexedFaceSet {
          coord Coordinate { point [ 0 0 0, 1 0 0, 1 1 0, 0 1 0, 0 0 1, 1 0 1, 1 1 1, 0 1 1 ] }
          coordIndex [ 0 3 2 1 -1, 4 5 6 7 -1, 0 1 5 4 -1, 1 2 6 5 -1, 2 3 7 6 -1, 3 0 4 7 -1 ]
        }
        """;

    private static string Wrl(string body) => "#VRML V2.0 utf8\n" + body;

    private static HalfEdgeMesh ReadMesh(string body)
    {
        var result = VrmlReader.Read(Wrl(body));
        return result.RequireMesh();
    }

    [Fact]
    public void AUnitCube_ReadsClosed_WithExactVolume_AndVerbatimCoordinates()
    {
        var mesh = ReadMesh($"Shape {{ {CubeGeometry} }}");
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(12, mesh.FaceCount);                    // quads triangulated
        Assert.Equal(1.0, mesh.Volume(), 12);                // outward winding, no unit factor
    }

    [Fact]
    public void TheTransformStack_Composes_TranslationRotationScaleAndCenter()
    {
        // A nested Transform: translate (5, 0, 0), then scale 2 about the origin — the cube
        // lands at x ∈ [5, 7] with volume 8. Rotation about z by 90° (an exact quarter turn of
        // the SAME cube elsewhere) preserves the volume and swaps the footprint.
        var mesh = ReadMesh($$"""
            Transform {
              translation 5 0 0
              children [
                Transform { scale 2 2 2 children [ Shape { {{CubeGeometry}} } ] }
              ]
            }
            """);
        Assert.Equal(8.0, mesh.Volume(), 12);
        var bounds = Aabb.FromPoints(mesh.Vertices.Select(v => v.Position).ToArray());
        Assert.Equal(5.0, bounds.Min.X, 12);
        Assert.Equal(7.0, bounds.Max.X, 12);
        Assert.Equal(0.0, bounds.Min.Y, 12);
        Assert.Equal(2.0, bounds.Max.Y, 12);

        // center: scaling 2× about (1, 1, 1) sends corner (0,0,0) to (−1,−1,−1) and (1,1,1) to
        // itself — the spec's T·C·S·C⁻¹ composition, asserted through the bounds.
        var centred = ReadMesh($$"""
            Transform { center 1 1 1 scale 2 2 2 children [ Shape { {{CubeGeometry}} } ] }
            """);
        var cb = Aabb.FromPoints(centred.Vertices.Select(v => v.Position).ToArray());
        Assert.Equal(-1.0, cb.Min.X, 12);
        Assert.Equal(1.0, cb.Max.X, 12);
        Assert.Equal(8.0, centred.Volume(), 12);

        var rotated = ReadMesh($$"""
            Transform {
              rotation 0 0 1 1.5707963267948966
              children [ Shape { {{CubeGeometry}} } ]
            }
            """);
        Assert.Equal(1.0, rotated.Volume(), 12);
        var rb = Aabb.FromPoints(rotated.Vertices.Select(v => v.Position).ToArray());
        Assert.Equal(-1.0, rb.Min.X, 12);                    // the cube swings into −x
        Assert.Equal(0.0, rb.Max.X, 9);
    }

    [Fact]
    public void DefUse_InstancesTheSharedNode_UnderEachUsesOwnTransform()
    {
        // One DEF'd shape, used twice more under translations: three cubes, one soup.
        var mesh = ReadMesh($$"""
            DEF BODY Shape { {{CubeGeometry}} }
            Transform { translation 10 0 0 children [ USE BODY ] }
            Transform { translation 20 0 0 children [ USE BODY ] }
            """);
        Assert.Equal(3.0, mesh.Volume(), 12);
        Assert.Equal(24, mesh.VertexCount);
    }

    [Fact]
    public void TheWindingRule_IsCcwXorMirror()
    {
        // ccw FALSE: the same cube with every loop written clockwise still reads OUTWARD.
        var clockwise = ReadMesh("""
            Shape {
              geometry IndexedFaceSet {
                ccw FALSE
                coord Coordinate { point [ 0 0 0, 1 0 0, 1 1 0, 0 1 0, 0 0 1, 1 0 1, 1 1 1, 0 1 1 ] }
                coordIndex [ 1 2 3 0 -1, 7 6 5 4 -1, 4 5 1 0 -1, 5 6 2 1 -1, 6 7 3 2 -1, 7 4 0 3 -1 ]
              }
            }
            """);
        Assert.Equal(1.0, clockwise.Volume(), 12);

        // A mirroring transform (negative determinant) flips winding; the reader flips it back,
        // exactly as HalfEdgeMesh.Transformed would — so the mirrored instance is still outward.
        var mirrored = ReadMesh($$"""
            Transform { scale -1 1 1 children [ Shape { {{CubeGeometry}} } ] }
            """);
        Assert.Equal(1.0, mirrored.Volume(), 12);
    }

    [Fact]
    public void SwitchAndLod_TakeTheChoiceAndTheMostDetailedLevel()
    {
        // Switch: whichChoice 1 takes the SECOND child only (the default −1 takes none).
        var chosen = ReadMesh($$"""
            Switch {
              whichChoice 1
              choice [
                Transform { translation 100 0 0 children [ Shape { {{CubeGeometry}} } ] }
                Shape { {{CubeGeometry}} }
              ]
            }
            """);
        Assert.Equal(1.0, chosen.Volume(), 12);
        var bounds = Aabb.FromPoints(chosen.Vertices.Select(v => v.Position).ToArray());
        Assert.Equal(1.0, bounds.Max.X, 12);                 // the un-translated one

        var defaulted = VrmlReader.Read(Wrl($$"""
            Switch { choice [ Shape { {{CubeGeometry}} } ] }
            """));
        Assert.Empty(defaulted.Faces);

        // LOD: the first level is the most detailed — the right one for an import.
        var lod = ReadMesh($$"""
            LOD { level [ Shape { {{CubeGeometry}} } Group { } ] }
            """);
        Assert.Equal(1.0, lod.Volume(), 12);
    }

    [Fact]
    public void Dirt_IsReportedNotThrown_AndAppearanceIsIgnored()
    {
        // KiCad-style content: appearance with a DEF'd material, commas, comments, a WorldInfo
        // whose string contains a '#', an unknown geometry, an Inline, a USE of nothing, and an
        // out-of-range coordIndex — the cube still reads, everything else is a named note.
        var result = VrmlReader.Read(Wrl($$"""
            WorldInfo { title "a # inside a string is not a comment" }
            Shape {
              appearance Appearance {
                material DEF MAT Material { diffuseColor 0.8, 0.8, 0.8 shininess 0.5 }
              }
              {{CubeGeometry}}
            }
            # a comment between nodes
            Shape { appearance Appearance { material USE MAT } geometry Box { size 2 2 2 } }
            Shape { geometry IndexedFaceSet {
              coord Coordinate { point [ 0 0 0, 1 0 0 ] }
              coordIndex [ 0 1 99 -1 ]
            } }
            Inline { url "other.wrl" }
            USE NEVER_DEFINED
            """));
        Assert.NotNull(result.Mesh);
        Assert.Equal(1.0, result.Mesh!.Volume(), 12);
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("'Box'"));
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("Inline"));
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("NEVER_DEFINED"));
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("coordIndex"));
    }

    [Fact]
    public void TheRefusals_AreByName()
    {
        // Missing header, a V1.0 file (a different grammar), an unsupported version, a PROTO,
        // and a truncated file (unbalanced braces).
        Assert.Contains("header",
            Assert.Throws<FormatException>(() => VrmlReader.Read("Shape { }")).Message);
        Assert.Contains("VRML 1.0",
            Assert.Throws<FormatException>(
                () => VrmlReader.Read("#VRML V1.0 ascii\nSeparator { }")).Message);
        Assert.Contains("version",
            Assert.Throws<FormatException>(
                () => VrmlReader.Read("#VRML V3.5 utf8\nShape { }")).Message);
        Assert.Contains("PROTO",
            Assert.Throws<FormatException>(
                () => VrmlReader.Read(Wrl("PROTO Thing [ ] { }"))).Message);
        Assert.Contains("Truncated",
            Assert.Throws<FormatException>(
                () => VrmlReader.Read(Wrl("Shape { geometry IndexedFaceSet {"))).Message);
    }

    [Fact]
    public void TheMeshReaderFacade_AndShapeFrom_SpeakWrl()
    {
        Assert.True(MeshReader.SupportsExtension(".wrl"));
        Assert.True(MeshReader.SupportsExtension("WRL"));
        var path = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.wrl");
        File.WriteAllText(path, Wrl($"Shape {{ {CubeGeometry} }}"));
        try
        {
            var result = MeshReader.ReadFile(path);
            Assert.Equal(1.0, result.RequireMesh().Volume(), 12);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
