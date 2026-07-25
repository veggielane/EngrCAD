using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Per-part section opt-out (<see cref="Part.ClippedBySection"/>): a part marked exempt
/// renders — and picks — whole inside a cutaway, the drafting convention that shafts,
/// bolts, nuts, keys, pins and ribs are never sectioned lengthwise. Pixel-level, because
/// the switch IS a render-state change (the shader's master switch per draw group), and
/// the offscreen pass must apply it exactly where the window does. Shares the
/// "offscreen-gl" collection (no concurrent EGL contexts).
/// </summary>
[Collection("offscreen-gl")]
public class SectionExemptTests
{
    private const int W = 320, H = 240;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A housing with a pin through it, viewed down -Y so the z = 0 cut is
    /// horizontal across the middle of the image.</summary>
    private static (Part Housing, Part Pin) Parts()
    {
        var housing = new Part("housing", Shape.Box(8, 8, 6) - Shape.Cylinder(1.2, 10), Palette.Steel);
        var pin = new Part("pin", Shape.Cylinder(1.0, 12), Palette.Brass);
        return (housing, pin);
    }

    private static byte[] Render(IReadOnlyList<Part> parts, double? offset) =>
        OffscreenRenderer.Render(parts, W, H, new CameraState(Math.PI / 2, 0, 24, (0, 0, 0)),
            furniture: false, ViewStyle.ShadedWithEdges, SectionAxis.Z, offset,
            ambientOcclusion: false);

    /// <summary>Non-background pixels above the image's vertical centre — the part of the
    /// model a z = 0 cut removes.</summary>
    private static int ModelPixelsAbove(byte[] rgba, int rows)
    {
        int count = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int x = 0; x < W; x++)
            {
                int p = (row * W + x) * 4;
                // The background gradient tops out around (46, 51, 61) and is always
                // blue-leaning; any brighter or warmer pixel is geometry.
                if (rgba[p] > 70 || rgba[p] >= rgba[p + 2])
                    count++;
            }
        }
        return count;
    }

    [SkippableFact]
    public void AnExemptPartSurvivesACutThatRemovesTheRest()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var (housing, pin) = Parts();

        // Both parts cut: the z > 0 half of the image loses essentially all geometry.
        int bothCut = ModelPixelsAbove(Render([housing, pin], 0.0), H / 2 - 8);
        Assert.True(bothCut < 200, $"the z = 0 cut left {bothCut} pixels above the plane");

        // Exempt the pin: it stays whole, so geometry reappears above the plane — but
        // far less than an uncut render, because only the pin is left up there.
        pin.ClippedBySection = false;
        int pinKept = ModelPixelsAbove(Render([housing, pin], 0.0), H / 2 - 8);
        int uncut = ModelPixelsAbove(Render([housing, pin], null), H / 2 - 8);
        Assert.True(pinKept > bothCut + 200,
            $"the exempt pin was still cut ({pinKept} vs {bothCut} pixels above the plane)");
        Assert.True(pinKept < uncut / 2,
            $"the housing stopped being cut ({pinKept} of {uncut} pixels above the plane)");
    }

    [SkippableFact]
    public void ExemptingAPartChangesNothingWithoutASection()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // The flag is a section-mode concept only: with no section plane the render must
        // be byte-identical, so it can be set unconditionally in design code.
        var (housing, pin) = Parts();
        var before = Render([housing, pin], null);
        pin.ClippedBySection = false;
        Assert.Equal(before, Render([housing, pin], null));
    }

    [Fact]
    public void ExemptPartsContributeNoSectionIsolines()
    {
        // No cut face means nothing to draw contours ON — the CPU half of the rule, so
        // it needs no GL.
        var part = new Part("blend", Shape.Box(4, 4, 4).SmoothUnion(Shape.Sphere(2.4), 0.5));
        var plane = SectionContours.PlaneFrame(Vector3d.UnitZ, 0);
        var instances = new[] { new PartInstance(part, Matrix4d.Identity, part.Name) };

        Assert.Equal(1, SectionContours.Build(instances, [true], plane).PartCount);
        part.ClippedBySection = false;
        Assert.Equal(0, SectionContours.Build(instances, [true], plane).PartCount);
    }
}
