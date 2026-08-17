
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

    // ---- OpenType/CFF (.otf) -------------------------------------------------

    private static readonly string[] CffDirectories =
    [
        @"C:\Windows\Fonts",
        "/usr/share/fonts/opentype",
        "/System/Library/Fonts",
    ];

    private static readonly Lazy<TrueTypeFont?> LoadedCff = new(() =>
    {
        foreach (string directory in CffDirectories)
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (string path in Directory.EnumerateFiles(directory, "*.otf", SearchOption.AllDirectories))
            {
                try
                {
                    var font = TrueTypeFont.Load(path);
                    if (font.HasPostScriptOutlines)
                        return font;
                }
                catch (FontFormatException)
                {
                    // An .otf we cannot read (CFF2, collection): keep looking.
                }
            }
        }
        return null;
    });

    /// <summary>Null when a real OpenType/CFF font was found, otherwise the reason to
    /// skip. Windows ships no .otf out of the box, so this commonly skips.</summary>
    public static string? CffSkipReason =>
        LoadedCff.Value is null ? $"no OpenType/CFF (.otf) font found (looked under {string.Join(", ", CffDirectories)})" : null;

    /// <summary>The CFF font; only valid when <see cref="CffSkipReason"/> is null.</summary>
    public static TrueTypeFont CffFont => LoadedCff.Value!;

    // ---- variable fonts ------------------------------------------------------

    /// <summary>Windows 10 1709 and later ship Bahnschrift (weight + width axes);
    /// Windows 11 adds Segoe UI Variable (weight + optical size).</summary>
    private static readonly string[] VariableCandidates =
    [
        @"C:\Windows\Fonts\bahnschrift.ttf",
        @"C:\Windows\Fonts\SegUIVar.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",           // not variable; probed and rejected below
    ];

    private static readonly Lazy<TrueTypeFont?> LoadedVariable = new(() =>
    {
        foreach (string path in VariableCandidates)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var font = TrueTypeFont.Load(path);
                if (font.IsVariable && font.VariationAxes.Any(a => a.Tag == "wght"))
                    return font;
            }
            catch (FontFormatException)
            {
                // Keep looking.
            }
        }
        return null;
    });

    /// <summary>Null when a real variable font carrying a weight axis was found,
    /// otherwise the reason to skip.</summary>
    public static string? VariableSkipReason =>
        LoadedVariable.Value is null
            ? $"no variable font with a 'wght' axis found (looked for {string.Join(", ", VariableCandidates)})"
            : null;

    /// <summary>The variable font; only valid when <see cref="VariableSkipReason"/> is
    /// null.</summary>
    public static TrueTypeFont VariableFont => LoadedVariable.Value!;
}
