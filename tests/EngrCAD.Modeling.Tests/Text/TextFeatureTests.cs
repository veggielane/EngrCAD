using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// <see cref="TextFeature"/> — modeled text as a parametric feature. The synthetic font's
/// 'I' is a 200×700 bar with a 100-unit left bearing; at em size S the scale is S/1000, so
/// the bar is 0.2S wide × 0.7S tall and its footprint area is 0.14·S². Every expectation is
/// arithmetic on that.
/// </summary>
public class TextFeatureTests
{
    private static readonly TrueTypeFont Font = TrueTypeFont.Load(SyntheticFont.Build());

    /// <summary>Footprint area of the 'I' bar at em size <paramref name="size"/>.</summary>
    private static double IArea(double size) => 0.14 * size * size;

    // ---- the feature applies, and Size drives it ----------------------------

    /// <summary>
    /// A first-in-history text feature produces the bare text solid, and its <c>[Param]</c>
    /// Size drives the geometry THROUGH the same JSON seam a design study / configuration /
    /// properties panel uses. This is the item's real point: the font is a CONSTRUCTOR
    /// input, so a parameter edit re-runs the same instance (which keeps its font) — the
    /// snapshot never has to carry the font, and yet regeneration is correct.
    /// </summary>
    [Fact]
    public void Standalone_ProducesTheTextSolid_AndSizeDrivesTheVolumeThroughTheParamSeam()
    {
        var history = new FeatureHistory();
        history.Add(new TextFeature("I", Font) { Size = 10, Height = 3, Name = "Label" });

        var first = history.Regenerate();
        Assert.True(first.Succeeded, first.ToString());
        Assert.Equal(IArea(10) * 3, first.Body!.ToMesh().Volume(), 6);   // 14 × 3 = 42

        // Drive Size through the parameter seam on the SAME instance (its font is retained).
        var warnings = history.LoadParameters("{\"Label\":{\"Size\":20}}");
        Assert.Empty(warnings);

        var second = history.Regenerate();
        Assert.True(second.Succeeded, second.ToString());
        Assert.Equal(IArea(20) * 3, second.Body!.ToMesh().Volume(), 6);  // 56 × 3 = 168
    }

    // ---- emboss / engrave on a body -----------------------------------------

    private static FeatureHistory PlateWith(TextFeature label)
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(40, 30)) { Height = 8, Name = "Plate" });
        history.Add(label);
        return history;
    }

    /// <summary>Emboss adds a proud label of exactly footprint × Height (its base is sunk
    /// into the body, so only the proud part is new material).</summary>
    [Fact]
    public void Emboss_AddsAProudLabelOfTheRightVolume()
    {
        var history = PlateWith(new TextFeature("I", Font) { Size = 10, Height = 3, Plane = PlaneRef.TopPlane });
        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());

        var mesh = result.Body!.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 30 * 8 + IArea(10) * 3, mesh.Volume(), 3);     // 9600 + 42
    }

    /// <summary>Engrave cuts a recess of exactly footprint × Height out of the body.</summary>
    [Fact]
    public void Engrave_CutsARecessOfTheRightVolume()
    {
        var history = PlateWith(new TextFeature("I", Font)
        {
            Size = 10,
            Height = 3,
            Engrave = true,
            Plane = PlaneRef.TopPlane,
        });
        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());

        var mesh = result.Body!.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 30 * 8 - IArea(10) * 3, mesh.Volume(), 3);     // 9600 − 42
    }

    // ---- persistence is honest, not complete --------------------------------

    /// <summary>
    /// A font is a binary blob with no data form, so the feature is OPAQUE to whole-history
    /// persistence: SaveHistory does not throw (the record is written honestly with its
    /// type/name/params), and LoadHistory skips it with a warning naming it — exactly the
    /// contract a ComponentFeature over a non-catalogue component follows.
    /// </summary>
    [Fact]
    public void IsOpaqueToHistoryPersistence_NamedNotCrashed()
    {
        var history = new FeatureHistory();
        history.Add(new TextFeature("SN-001", Font) { Size = 4, Height = 1, Name = "Serial" });

        string json = history.SaveHistory();     // must not throw
        Assert.Contains(nameof(TextFeature), json);

        var loaded = FeatureHistory.LoadHistory(json);
        Assert.False(loaded.Complete);
        Assert.Contains(loaded.Warnings, w => w.Contains(nameof(TextFeature)));
    }

    // ---- registry ------------------------------------------------------------

    /// <summary>The registry lists the type with honest un-creatability: a font is not
    /// data, so the reason names the constructor's demands rather than pretending it can be
    /// rebuilt from JSON.</summary>
    [Fact]
    public void Registry_ListsItAsNotDataConstructible()
    {
        var entry = FeatureRegistry.Default.Find(nameof(TextFeature));
        Assert.NotNull(entry);
        Assert.False(entry!.CanCreate);
        Assert.Contains(nameof(TrueTypeFont), entry.Reason);

        // Its [Param] metadata is still described without an instance.
        Assert.Contains(entry.Parameters, p => p.Name == "Size" && p.Type == typeof(double));
        Assert.Contains(entry.Parameters, p => p.Name == "Engrave" && p.Type == typeof(bool));
    }
}
