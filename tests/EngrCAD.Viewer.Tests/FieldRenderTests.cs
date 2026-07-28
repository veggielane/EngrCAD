using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Pixel-level verification of field display in the headless pass — the same shader,
/// the same colour floats and the same legend geometry the window uploads, so these
/// assertions cover both paths. Statistical (pixel classes and counts), not golden
/// images.
/// </summary>
[Collection("offscreen-gl")]
public class FieldRenderTests
{
    // Big enough for the legend to fit at the offscreen pass's 2x supersample
    // (FieldLegend.Fits: 100 x 210 final pixels is the floor).
    private const int W = 320, H = 260;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A plate whose result ramps along X, so a colour map shows both ends.</summary>
    private static IReadOnlyList<Part> Plate(Action<Part>? configure = null)
    {
        var scene = new Scene();
        var part = new Part("plate", Shape.Box(20, 10, 2), Palette.Steel);
        scene.Add(part);
        scene.PreMesh();
        var mesh = part.GetMesh();
        part.AddResult(MeshField.Sample(mesh, "stress", "MPa", p => p.X));
        part.AddResult(MeshField.SampleVector(mesh, "u", "mm", p => new Vector3d(0, 0, 0.02 * p.X * p.X)));
        configure?.Invoke(part);
        return [.. scene.AllParts];
    }

    private static byte[] Render(IReadOnlyList<Part> parts, bool fields = true, bool furniture = false) =>
        OffscreenRenderer.Render(parts, W, H, camera: null, furniture: furniture,
            ViewStyle.ShadedWithEdges, SectionAxis.Z, sectionOffset: null,
            ambientOcclusion: false, sectionPlanes: null,
            sectionCombine: SectionCombine.Intersection, preview: null, previewWorld: null, fields: fields);

    /// <summary>Pixels at the warm/bright end of viridis (its yellow extreme). The part
    /// colour is a cool steel blue, so a yellow pixel can only come from the map.</summary>
    private static int Yellowish(byte[] rgba)
    {
        int count = 0;
        for (int p = 0; p < rgba.Length; p += 4)
        {
            if (rgba[p] > 150 && rgba[p + 1] > 150 && rgba[p + 2] < 100)
                count++;
        }
        return count;
    }

    private static int DifferingPixels(byte[] a, byte[] b)
    {
        int count = 0;
        for (int p = 0; p < a.Length; p += 4)
        {
            if (a[p] != b[p] || a[p + 1] != b[p + 1] || a[p + 2] != b[p + 2])
                count++;
        }
        return count;
    }

    /// <summary>Differing pixels inside the legend's screen strip (left margin, the full
    /// height), in final-image pixels — the legend is laid out at the supersample scale,
    /// so it comes out the same DIP size in the downsampled image.</summary>
    private static int DifferingInLegendStrip(byte[] a, byte[] b)
    {
        int x0 = (int)FieldLegend.MarginDip;
        int x1 = (int)(FieldLegend.MarginDip + FieldLegend.BarWidthDip);
        int count = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int p = (y * W + x) * 4;
                if (a[p] != b[p] || a[p + 1] != b[p + 1] || a[p + 2] != b[p + 2])
                    count++;
            }
        }
        return count;
    }

    [SkippableFact]
    public void PartsWithoutAFieldDisplay_RenderByteIdentically()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // THE oracle for the constant-when-absent rule. A part that merely CARRIES
        // results but displays none must produce exactly the pixels a part with no
        // results at all produces: the colour attribute reads its context constant and
        // uFieldColor is 0, so mix(uColor, vFieldColor, 0.0) is uColor bit for bit.
        // (The docs suite is the wider oracle: all 87 rendered PNGs were byte-identical
        // across the shader change that added the attribute.)
        var plain = new Scene();
        plain.Add(new Part("plate", Shape.Box(20, 10, 2), Palette.Steel));
        plain.PreMesh();

        Assert.Equal(Render([.. plain.AllParts]), Render(Plate()));
    }

    [SkippableFact]
    public void ShowingAField_RepaintsThePartThroughTheColorMap()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var off = Render(Plate());
        var on = Render(Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" }));

        // The part colour is a cool steel blue; viridis's high end is yellow, so a
        // yellow pixel can only be the map.
        Assert.Equal(0, Yellowish(off));
        Assert.True(Yellowish(on) > 200, $"expected the map's warm end, got {Yellowish(on)} pixels");
    }

    [SkippableFact]
    public void ShowingAField_DrawsTheLegend()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var off = Render(Plate());
        var on = Render(Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" }));

        int strip = DifferingInLegendStrip(off, on);
        Assert.True(strip > 500,
            $"expected the colour bar in the left margin, only {strip} pixels there changed");
    }

    [SkippableFact]
    public void FieldsOff_ReturnsThePartToItsOwnColour()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // RenderToImage's `fields: false` is how a geometry figure is taken of a model
        // that also carries results — it must reproduce the no-display render exactly,
        // legend included.
        var displayed = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" });
        Assert.Equal(Render(Plate()), Render(displayed, fields: false));
    }

    [SkippableFact]
    public void TheColorMapChoiceChangesThePixels()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var viridis = Render(Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" }));
        var diverging = Render(Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            ColorMap = FieldColorMap.Diverging,
        }));
        Assert.True(DifferingPixels(viridis, diverging) > 1000,
            "the two colour maps must not render the same");
    }

    [SkippableFact]
    public void DeformedShape_MovesGeometryAndGhostsTheOriginal()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var flat = Render(Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" }));
        var bent = Render(Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 10,
        }));
        var bentNoGhost = Render(Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 10,
            ShowUndeformed = false,
        }));

        // A displaced shape is different geometry, so a large share of the image moves.
        Assert.True(DifferingPixels(flat, bent) > 2000,
            "the deformed shape must not render like the undeformed one");
        // The ghost is an extra translucent body: turning it off changes the picture.
        Assert.True(DifferingPixels(bent, bentNoGhost) > 200,
            "the undeformed ghost must be visible behind the deformed shape");
    }

    [SkippableFact]
    public void ABrokenFieldDisplay_RendersThePartPlainRatherThanFailing()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // A display naming a result an edit removed becomes a status message in the
        // window; headlessly it must still produce an image of the geometry.
        var broken = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "temperature" });
        Assert.Equal(Render(Plate()), Render(broken));
    }
}
