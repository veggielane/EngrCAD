using System.Globalization;
using System.Text;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>One part's row in a <see cref="SceneReport"/>.</summary>
public sealed record PartCheck(
    string Tab,
    string Name,
    string Kind,
    bool Meshed,
    bool Closed,
    int FaceCount,
    double? Volume,
    double? Area,
    Aabb Bounds,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when nothing about this part needs a second look.</summary>
    public bool Clean => Notes.Count == 0;
}

/// <summary>
/// The model-validation report — the OpenSCAD <c>assert</c>/<c>echo</c> analog for a
/// whole document: per distinct part, its geometry kind, mesh facts (closed?, faces,
/// volume, surface area, bounds) and a NOTES column naming anything suspicious — an
/// open display mesh (with its boundary-loop count), a failed mesh/lowering (the
/// exception, not a crash), a degenerate volume, active debug modifiers. Viewers
/// surface it behind a Check button; scripts call <see cref="Create"/> +
/// <see cref="ToText"/> and assert on <see cref="AllClean"/>.
/// <para>Creating a report meshes every part at the scene's resolved quality (cached —
/// a part already displayed costs nothing). Volume is reported only for closed
/// meshes: a signed volume of an open shell is not a volume.</para>
/// </summary>
public sealed class SceneReport
{
    private SceneReport(IReadOnlyList<PartCheck> parts)
    {
        Parts = parts;
        WarningCount = parts.Sum(p => p.Notes.Count);
    }

    public IReadOnlyList<PartCheck> Parts { get; }

    /// <summary>Total note count across all parts.</summary>
    public int WarningCount { get; }

    /// <summary>True when every part meshed and carries no notes.</summary>
    public bool AllClean => WarningCount == 0 && Parts.All(p => p.Meshed);

    public static SceneReport Create(Scene scene, MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var resolved = quality ?? scene.ResolveQuality();
        var checks = new List<PartCheck>();
        foreach (var tab in scene.Tabs)
        {
            foreach (var part in tab.AllParts)
                checks.Add(Check(tab.Name, part, resolved));
        }
        return new SceneReport(checks);
    }

    private static PartCheck Check(string tab, Part part, MeshQuality quality)
    {
        var notes = new List<string>();
        if (part.Hidden)
            notes.Add("hidden (debug modifier: excluded from display and export)");
        if (part.Ghost)
            notes.Add("ghost (debug modifier: displayed translucent, excluded from export)");
        if (part.Isolated)
            notes.Add("isolated (debug modifier: only isolated parts show)");

        string kind = part.Geometry switch
        {
            Shape => "Shape",
            BrepSolid => "B-Rep",
            HalfEdgeMesh => "Mesh",
            Sdf => "SDF",
            var other => other.GetType().Name,
        };

        HalfEdgeMesh mesh;
        try
        {
            mesh = part.GetMesh(quality);
        }
        catch (Exception exception)
        {
            notes.Add($"failed to mesh: {exception.GetType().Name}: {FirstLine(exception.Message)}");
            return new PartCheck(tab, part.Name, kind, Meshed: false, Closed: false,
                FaceCount: 0, Volume: null, Area: null, Aabb.Empty, notes);
        }

        bool closed = mesh.IsClosed;
        double? volume = null;
        if (!closed)
        {
            notes.Add($"open mesh: {mesh.BoundaryLoops().Count} boundary loop(s) — not watertight, no volume");
        }
        else
        {
            volume = mesh.Volume();
            if (volume <= 0)
                notes.Add($"non-positive volume {volume:G6} — inverted or degenerate geometry");
        }
        if (mesh.FaceCount == 0)
            notes.Add("empty mesh: no faces");

        return new PartCheck(
            tab, part.Name, kind, Meshed: true, closed, mesh.FaceCount,
            volume, mesh.SurfaceArea(), part.Bounds(quality), notes);
    }

    private static string FirstLine(string message)
    {
        int newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].TrimEnd('\r');
    }

    /// <summary>The report as an aligned monospace table plus per-part notes.</summary>
    public string ToText()
    {
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();
        var rows = new List<string[]>
        {
            new[] { "TAB", "PART", "KIND", "FACES", "CLOSED", "VOLUME", "AREA", "SIZE" },
        };
        foreach (var check in Parts)
        {
            var size = check.Bounds.IsEmpty ? Vector3d.Zero : check.Bounds.Size;
            rows.Add(
            [
                check.Tab,
                check.Name,
                check.Kind,
                check.Meshed ? check.FaceCount.ToString("N0", culture) : "—",
                !check.Meshed ? "—" : check.Closed ? "yes" : "NO",
                check.Volume is { } volume ? volume.ToString("G6", culture) : "—",
                check.Area is { } area ? area.ToString("G6", culture) : "—",
                check.Meshed
                    ? string.Create(culture, $"{size.X:G4} x {size.Y:G4} x {size.Z:G4}")
                    : "—",
            ]);
        }

        var widths = new int[rows[0].Length];
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length; i++)
                widths[i] = Math.Max(widths[i], row[i].Length);
        }
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length; i++)
                builder.Append(row[i].PadRight(widths[i] + 2));
            builder.AppendLine();
        }

        foreach (var check in Parts.Where(c => c.Notes.Count > 0))
        {
            foreach (var note in check.Notes)
                builder.AppendLine(string.Create(culture, $"! {check.Tab}/{check.Name}: {note}"));
        }
        builder.AppendLine();
        builder.AppendLine(AllClean
            ? $"{Parts.Count} part(s), all clean."
            : $"{Parts.Count} part(s), {WarningCount} note(s).");
        return builder.ToString();
    }

    public override string ToString() => ToText();
}
