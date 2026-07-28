using System.Globalization;
using System.Text;
using System.Xml.Linq;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>
/// Drawing-convention line classes for 2D output — build123d's lesson that what makes
/// an exported drawing usable is line-type control driven by edge CLASSIFICATION, not
/// a flat soup of curves: <see cref="Visible"/> outlines are solid, <see cref="Hidden"/>
/// lines dashed, <see cref="Section"/> cuts dash-dot (the ISO 128 vocabulary).
/// </summary>
public enum SvgLineClass
{
    /// <summary>Visible outline: solid, thick.</summary>
    Visible,

    /// <summary>Hidden (occluded) edge: dashed, thin.</summary>
    Hidden,

    /// <summary>Section cut: dash-dot — the classic cutting-plane convention.</summary>
    Section,
}

/// <summary>
/// SVG export for 2D profiles and drawing views — the natural sink for
/// <c>Shape.Section</c> / <c>Shape.Silhouette</c> output and for sketches. Content is
/// added in model coordinates (y up, millimetres); the writer flips to SVG's y-down
/// space via one root transform and sizes the viewBox from the content's bounds, so
/// 1 SVG user unit = 1 mm. Each (layer, line-class) pair becomes one <c>&lt;g&gt;</c>
/// with the class's stroke/dash preset (overridable per add), so a CAM or editor
/// downstream can toggle whole layers.
/// <para>Regions are polygonal paths (their fidelity is set where they were made —
/// e.g. a section's chord tolerance); sketches are written EXACTLY — lines, arcs
/// (SVG <c>A</c> commands carry circular arcs exactly) and cubic béziers
/// (<c>C</c> commands) with nothing flattened.</para>
/// </summary>
public sealed class SvgDrawing
{
    /// <summary>Stroke preset: color, width (mm), dash pattern (null = solid).</summary>
    public sealed record SvgPen(string Stroke, double Width, string? DashArray)
    {
        public static SvgPen For(SvgLineClass lineClass) => lineClass switch
        {
            SvgLineClass.Hidden => new SvgPen("#666666", 0.25, "1.2 0.8"),
            SvgLineClass.Section => new SvgPen("#aa2222", 0.5, "3 0.7 0.7 0.7"),
            _ => new SvgPen("#111111", 0.5, null),
        };
    }

    private sealed record Group(string Layer, SvgLineClass LineClass, SvgPen Pen, List<string> Paths);

    private readonly List<Group> _groups = [];
    private Aabb _bounds = Aabb.Empty;

    /// <summary>Whitespace around the content, in millimetres (default 5).</summary>
    public double Margin { get; set; } = 5;

    /// <summary>Adds a region's loops (outer + holes) as one path.</summary>
    public void Add(
        Region2d region, SvgLineClass lineClass = SvgLineClass.Visible,
        string? layer = null, SvgPen? pen = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        var path = new StringBuilder();
        AppendLoop(path, region.Outer);
        foreach (var hole in region.Holes)
            AppendLoop(path, hole);
        GroupFor(layer, lineClass, pen).Paths.Add(path.ToString());
        _bounds = _bounds.Union(region.Bounds);
    }

    /// <summary>Adds several regions (e.g. a whole <c>Shape.Section</c> result).</summary>
    public void Add(
        IEnumerable<Region2d> regions, SvgLineClass lineClass = SvgLineClass.Visible,
        string? layer = null, SvgPen? pen = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        foreach (var region in regions)
            Add(region, lineClass, layer, pen);
    }

    /// <summary>Adds a sketch EXACTLY: lines, arcs (<c>A</c>), cubics (<c>C</c>);
    /// holes as further subpaths.</summary>
    public void Add(
        Sketch sketch, SvgLineClass lineClass = SvgLineClass.Visible,
        string? layer = null, SvgPen? pen = null)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var path = new StringBuilder();
        AppendSketchLoop(path, sketch);
        foreach (var hole in sketch.Holes)
            AppendSketchLoop(path, hole);
        GroupFor(layer, lineClass, pen).Paths.Add(path.ToString());
        _bounds = _bounds.Union(sketch.Bounds);
    }

    // ------------------------------------------------------------------------ write

    public string ToSvg()
    {
        if (_groups.Count == 0 || _bounds.IsEmpty)
            throw new InvalidOperationException("The drawing has no content; add regions or sketches first.");

        var culture = CultureInfo.InvariantCulture;
        XNamespace ns = "http://www.w3.org/2000/svg";

        double minX = _bounds.Min.X - Margin, maxX = _bounds.Max.X + Margin;
        double minY = _bounds.Min.Y - Margin, maxY = _bounds.Max.Y + Margin;
        double width = maxX - minX, height = maxY - minY;

        // Model space is y-up; SVG is y-down. One root flip keeps every coordinate in
        // the file identical to the model's, with the viewBox shifted to the flipped
        // range [-maxY, -minY].
        var flipped = new XElement(ns + "g", new XAttribute("transform", "scale(1,-1)"));
        foreach (var group in _groups)
        {
            var g = new XElement(ns + "g",
                new XAttribute("id", SanitizeId(group.Layer)),
                new XAttribute("class", group.LineClass.ToString().ToLowerInvariant()),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", group.Pen.Stroke),
                new XAttribute("stroke-width", group.Pen.Width.ToString("R", culture)),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"));
            if (group.Pen.DashArray is { } dash)
                g.Add(new XAttribute("stroke-dasharray", dash));
            foreach (var path in group.Paths)
                g.Add(new XElement(ns + "path", new XAttribute("d", path)));
            flipped.Add(g);
        }

        var svg = new XElement(ns + "svg",
            new XAttribute("viewBox", string.Create(culture, $"{minX:R} {-maxY:R} {width:R} {height:R}")),
            new XAttribute("width", string.Create(culture, $"{width:R}mm")),
            new XAttribute("height", string.Create(culture, $"{height:R}mm")),
            flipped);
        return new XDocument(svg).ToString();
    }

    public void Save(TextWriter writer) => writer.Write(ToSvg());

    public void SaveFile(string path) => File.WriteAllText(path, ToSvg());

    // ---------------------------------------------------------------------- helpers

    private Group GroupFor(string? layer, SvgLineClass lineClass, SvgPen? pen)
    {
        string name = layer ?? lineClass.ToString().ToLowerInvariant();
        var resolved = pen ?? SvgPen.For(lineClass);
        var existing = _groups.FirstOrDefault(
            g => g.Layer == name && g.LineClass == lineClass && g.Pen == resolved);
        if (existing is null)
        {
            existing = new Group(name, lineClass, resolved, []);
            _groups.Add(existing);
        }
        return existing;
    }

    private static void AppendLoop(StringBuilder path, IReadOnlyList<Vector2d> loop)
    {
        var culture = CultureInfo.InvariantCulture;
        for (int i = 0; i < loop.Count; i++)
        {
            path.Append(i == 0 ? "M" : " L");
            path.Append(string.Create(culture, $"{loop[i].X:R} {loop[i].Y:R}"));
        }
        path.Append(" Z ");
    }

    private static void AppendSketchLoop(StringBuilder path, Sketch loop)
    {
        var culture = CultureInfo.InvariantCulture;
        var segments = loop.Segments;
        var start = segments[0].Start;
        path.Append(string.Create(culture, $"M{start.X:R} {start.Y:R}"));
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case LineSeg line:
                    path.Append(string.Create(culture, $" L{line.End.X:R} {line.End.Y:R}"));
                    break;
                case ArcSeg arc:
                    AppendArc(path, arc, culture);
                    break;
                case CubicSeg cubic:
                    path.Append(string.Create(culture,
                        $" C{cubic.Control1.X:R} {cubic.Control1.Y:R} {cubic.Control2.X:R} {cubic.Control2.Y:R} {cubic.P3.X:R} {cubic.P3.Y:R}"));
                    break;
                default:
                    throw new NotSupportedException($"No SVG form for a {segment.GetType().Name}.");
            }
        }
        path.Append(" Z ");
    }

    /// <summary>An arc as SVG <c>A</c> commands — exact (endpoint parameterization of
    /// the same circle). A full circle has coincident endpoints, which the <c>A</c>
    /// form cannot express in one command, so sweeps over half a turn split at the
    /// midpoint angle.</summary>
    private static void AppendArc(StringBuilder path, ArcSeg arc, CultureInfo culture)
    {
        // sweep > 0 is CCW in model coordinates; flags are evaluated in the path's own
        // (pre-flip) space, so they carry over directly.
        int sweepFlag = arc.Sweep > 0 ? 1 : 0;
        if (Math.Abs(arc.Sweep) > Math.PI)
        {
            var halved = new ArcSeg(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep / 2);
            AppendArc(path, halved, culture);
            AppendArc(path, new ArcSeg(
                arc.Center, arc.Radius, arc.StartAngle + arc.Sweep / 2, arc.Sweep / 2), culture);
            return;
        }
        var end = arc.End;
        path.Append(string.Create(culture,
            $" A{arc.Radius:R} {arc.Radius:R} 0 0 {sweepFlag} {end.X:R} {end.Y:R}"));
    }

    private static string SanitizeId(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return builder.Length > 0 ? builder.ToString() : "layer";
    }
}
