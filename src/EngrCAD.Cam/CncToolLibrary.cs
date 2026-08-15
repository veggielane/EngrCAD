namespace EngrCAD.Cam;

/// <summary>
/// One machinable material's cutting numbers for solid-carbide end mills:
/// <paramref name="SurfaceSpeed"/> is the cutting speed Vc (m/min) and
/// <paramref name="ChipLoadPerDiameter"/> the feed per tooth as a FRACTION of the tool diameter
/// — the shop rule of thumb that a bigger tool takes a proportionally bigger bite, so one
/// dimensionless number covers the diameter range where a fixed mm figure would be wrong at
/// both ends. ⚠ Transcribed nominal mid-range figures (the <c>StandardHoles</c>/<c>SheetMaterials</c>
/// convention): verify against the tool manufacturer's own chart before cutting metal —
/// machine rigidity, stick-out and coolant move the real numbers substantially.
/// </summary>
public sealed record MillMaterial(string Name, double SurfaceSpeed, double ChipLoadPerDiameter);

/// <summary>
/// The ⚠ verify-against-datasheet feeds-and-speeds catalogue — nominal figures for solid
/// carbide, transcribed and ASSERTED in datasheet form (Vc in m/min as a chart states it),
/// because a re-typed formula agrees with its own mistake where a transcription test does not.
/// </summary>
public static class MillMaterials
{
    /// <summary>6061 aluminium — the friendly benchmark metal. ⚠ nominal.</summary>
    public static readonly MillMaterial Aluminum6061 = new("Aluminium 6061", 250, 1.0 / 150);

    /// <summary>Free-machining brass. ⚠ nominal.</summary>
    public static readonly MillMaterial Brass = new("Brass", 200, 1.0 / 150);

    /// <summary>Mild / low-carbon steel (1018-class). ⚠ nominal.</summary>
    public static readonly MillMaterial MildSteel = new("Mild steel", 100, 1.0 / 250);

    /// <summary>304 stainless — work-hardening, so the chip load must not fall too low
    /// either; the conservative corner of the table. ⚠ nominal.</summary>
    public static readonly MillMaterial Stainless304 = new("Stainless 304", 60, 1.0 / 300);

    /// <summary>Acetal (Delrin) — the easy engineering plastic. ⚠ nominal.</summary>
    public static readonly MillMaterial Acetal = new("Acetal", 300, 1.0 / 100);

    /// <summary>Hardwood and plywood. ⚠ nominal.</summary>
    public static readonly MillMaterial Hardwood = new("Hardwood", 450, 1.0 / 80);

    /// <summary>Every published entry — the coverage CLAIM a reflection test holds this list
    /// to, so a new entry not listed here fails the build's own tests.</summary>
    public static IReadOnlyList<MillMaterial> All { get; } =
        [Aluminum6061, Brass, MildSteel, Stainless304, Acetal, Hardwood];
}

/// <summary>
/// Derives a starting <see cref="MillTool"/> from the material catalogue — the two identities a
/// feeds-and-speeds chart is built on, spelled once: <c>rpm = 1000·Vc/(π·D)</c> (the cutting
/// speed lives on the flute's own circumference) and <c>feed = rpm × flutes × chip load</c>.
///
/// <para><b>The spindle cap preserves the CHIP LOAD, not the feed</b>: a small tool in
/// aluminium asks for more rpm than a hobby spindle has, and the honest response is to run the
/// capped rpm at the SAME feed per tooth — the feed drops proportionally — because holding the
/// feed instead would thicken every chip past what the flute clears. Depth and plunge defaults
/// are stated conventions (StepDown = D/2, PlungeRate = feed/3), not physics; the returned tool
/// passes its own <see cref="MillTool.Validate"/> and every number is overridable with
/// <c>with</c>.</para>
/// </summary>
public static class CncToolLibrary
{
    /// <summary>Suggests process numbers for a tool of <paramref name="diameter"/> mm with
    /// <paramref name="flutes"/> flutes in <paramref name="material"/>, the spindle capped at
    /// <paramref name="maxRpm"/>.</summary>
    public static MillTool Suggest(
        MillMaterial material, double diameter, int flutes = 2, double maxRpm = 24000)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!(diameter > 0) || !double.IsFinite(diameter))
            throw new ArgumentException(
                $"The tool diameter must be finite and positive; got {diameter:0.###}.");
        if (flutes < 1)
            throw new ArgumentException($"A tool needs at least one flute; got {flutes}.");
        if (!(maxRpm > 0) || !double.IsFinite(maxRpm))
            throw new ArgumentException(
                $"The spindle cap must be finite and positive; got {maxRpm:0.###}.");

        double rpm = Math.Min(maxRpm, material.SurfaceSpeed * 1000 / (Math.PI * diameter));
        double chipLoad = diameter * material.ChipLoadPerDiameter;
        double feed = rpm * flutes * chipLoad;
        var tool = new MillTool(
            diameter,
            FeedRate: feed,
            PlungeRate: feed / 3,
            SpindleRpm: rpm,
            StepDown: diameter / 2);
        tool.Validate();
        return tool;
    }
}
