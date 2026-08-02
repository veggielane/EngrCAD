using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The analytic matcap selector (<see cref="ShadingStyle"/>), through the offscreen
/// pass. The default's byte-identity to the pre-feature look is carried by the
/// committed docs PNGs (the oracle a shader change actually answers to); what is
/// pinned here is the selector's contract — Lit IS the parameterless render, the
/// styles genuinely differ from each other, and a matcap changes how fills are LIT
/// and nothing else (section cut faces keep their flat cut material, byte for byte).
/// </summary>
[Collection("offscreen-gl")]
public class ShadingRenderTests
{
    private const int W = 240, H = 180;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    private static byte[] Render(ShadingStyle? shading, double? sectionOffset = null)
    {
        // Two separate parts (no boolean in the fixture): a box and a sphere, both
        // straddling the z = 0.5 section plane when one is asked for. As raw MESHES,
        // deliberately: a Shape part has an implicit route, so a sectioned render
        // would draw the automatic SDF isolines on the cut — constant-coloured LINES
        // whose supersample blends with the shading-dependent surface behind them,
        // which is exactly the pixel class the cut-material assertion must not
        // include (measured: 3 of 2784 interior warm pixels differed through them).
        var scene = new Scene();
        scene.Add(new Part("box", Shape.Box(6, 4, 3).ToMesh(), Palette.Steel));
        scene.Add(new Part("dome", Shape.Sphere(2).ToMesh(), Palette.Steel,
            Matrix4d.CreateTranslation((0, 0, 1))));
        scene.PreMesh();
        var parts = scene.AllParts.ToList();
        return shading is { } s
            ? OffscreenRenderer.Render(parts, W, H, camera: null, furniture: false,
                sectionAxis: SectionAxis.Z, sectionOffset: sectionOffset,
                ambientOcclusion: false, shading: s)
            : OffscreenRenderer.Render(parts, W, H, camera: null, furniture: false,
                sectionAxis: SectionAxis.Z, sectionOffset: sectionOffset,
                ambientOcclusion: false);
    }

    [Fact]
    public void Lit_IsZero_TheValueALinkedProgramInitializesUniformsTo()
    {
        // The whole default-off safety argument: a front end that says nothing gets
        // uMatcap = 0 from GL's own uniform initialization, which must BE Lit.
        Assert.Equal(0, (int)ShadingStyle.Lit);
    }

    [SkippableFact]
    public void ExplicitLit_IsByteIdenticalToTheParameterlessRender()
    {
        Skip.If(SkipReason is not null, SkipReason);
        Assert.Equal(Render(shading: null), Render(ShadingStyle.Lit));
    }

    [SkippableFact]
    public void EachShadingStyleProducesADifferentRender()
    {
        Skip.If(SkipReason is not null, SkipReason);
        byte[] lit = Render(ShadingStyle.Lit);
        byte[] clay = Render(ShadingStyle.Clay);
        byte[] metal = Render(ShadingStyle.Metal);

        Assert.NotEqual(lit, clay);
        Assert.NotEqual(lit, metal);
        Assert.NotEqual(clay, metal);
    }

    [SkippableFact]
    public void SectionCutFacesKeepTheirFlatCutMaterialUnderAMatcap()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // The cut-face branch returns BEFORE the lighting model, so a matcap must not
        // move a single pure-cut-material pixel. Steel's cut cue sits at r ~ b + 30
        // while every outward steel pixel is bluish under every shading (a matcap
        // scales the bluish surface; Metal's additive highlight leans blue itself), so
        // r > b + 25 in the Lit frame classifies cut material — then ERODED by one
        // pixel, because a boundary pixel's 2x2 supersample can blend one outward
        // sub-pixel in (whose shading legitimately differs by a rounding step; the
        // first run failed on exactly one such 181-vs-180 byte). An INTERIOR cut
        // pixel's supersamples are all cut material, so its bytes must be IDENTICAL
        // under Clay, while the frames as a whole differ.
        byte[] lit = Render(ShadingStyle.Lit, sectionOffset: 0.5);
        byte[] clay = Render(ShadingStyle.Clay, sectionOffset: 0.5);
        Assert.NotEqual(lit, clay);

        var warm = new bool[W * H];
        for (int p = 0; p < warm.Length; p++)
            warm[p] = lit[p * 4] > lit[p * 4 + 2] + 25;

        int cut = 0;
        for (int y = 1; y < H - 1; y++)
        {
            for (int x = 1; x < W - 1; x++)
            {
                int p = y * W + x;
                if (!warm[p] || !warm[p - 1] || !warm[p + 1] || !warm[p - W] || !warm[p + W])
                    continue;
                cut++;
                int i = p * 4;
                Assert.Equal(lit[i], clay[i]);
                Assert.Equal(lit[i + 1], clay[i + 1]);
                Assert.Equal(lit[i + 2], clay[i + 2]);
            }
        }
        Assert.True(cut > 100, $"the section should expose a visible cut face ({cut} pixels found)");
    }
}
