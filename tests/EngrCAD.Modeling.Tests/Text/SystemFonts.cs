using EngrCAD.Modeling.Text;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// Locates a real TrueType font already installed on the machine. No font binary is
/// committed to this repository (licensing), so tests that want genuine production
/// outlines find one here and <c>Skip.If</c> out when none exists — the same
/// skip-gracefully policy the offscreen-GL tests use for GPU-less CI.
/// <para>Such tests assert <b>structural</b> facts only (glyph counts, closed solids,
/// positive volume, 'O' has one counter): the exact outline of Arial is not ours to
/// pin down, and every exact-geometry expectation lives in the synthetic-font tests.</para>
/// </summary>
internal static class SystemFonts
{
    private static readonly string[] Candidates =
    [
        @"C:\Windows\Fonts\arial.ttf",
        @"C:\Windows\Fonts\verdana.ttf",
        @"C:\Windows\Fonts\segoeui.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
    ];

    private static readonly Lazy<TrueTypeFont?> Loaded = new(() =>
    {
        string? path = Candidates.FirstOrDefault(File.Exists);
        return path is null ? null : TrueTypeFont.Load(path);
    });

    /// <summary>Null when a font was found, otherwise the reason to skip.</summary>
    public static string? SkipReason =>
        Loaded.Value is null ? $"no system TrueType font found (looked for {string.Join(", ", Candidates)})" : null;

    /// <summary>The font; only valid when <see cref="SkipReason"/> is null.</summary>
    public static TrueTypeFont Font => Loaded.Value!;
}
