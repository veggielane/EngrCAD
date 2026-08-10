using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// A net's routed copper on ONE layer: a polyline centre-line of a given <see cref="Width"/> on a
/// named copper <see cref="Layer"/>, belonging to a <see cref="Net"/>. A trace is the AUTOROUTER's
/// OUTPUT and is LAYOUT TRUTH — a routed board's copper is part of the design, so traces round-trip
/// in the layout file and feed both the DRC and the connectivity engine.
///
/// <para>The copper region is the polyline's exact stroke — the Minkowski sum of the centre-line
/// with a disc of radius <c>Width/2</c> (round caps and round joins), which is precisely the model
/// the DRC's clearance rule grows against, so a trace built here and the clearance rule it is
/// checked with cannot disagree. Round joins also mean the copper carries no sharp corner, so a
/// routed trace passes the acid-trap (acute-angle) rule with nothing arranged.</para>
///
/// <para>The centre-line runs 45°/90° on the router's grid, but a trace is just a polyline — the
/// type carries whatever points it is given, so a hand-built or imported trace is equally valid.
/// The <see cref="Points"/> are in BOARD-LOCAL 2D coordinates (mm), the same frame the copper model
/// and the DRC work in.</para>
/// </summary>
/// <param name="Net">The net this trace carries — a routed trace always belongs to a net.</param>
/// <param name="Layer">The copper layer the trace runs on (a copper layer of the board stackup).</param>
/// <param name="Width">The trace width (mm), positive.</param>
/// <param name="Points">The centre-line, in board-local 2D coordinates (mm); at least two points,
/// with at least one segment of non-zero length.</param>
public readonly record struct PcbTrace(
    string Net, string Layer, double Width, IReadOnlyList<Vector2d> Points);

/// <summary>Turns a <see cref="PcbTrace"/>'s centre-line into board-local copper regions — the exact
/// stroke of the polyline. Kept in one place so a trace's copper is built one way (the DRC and the
/// connectivity engine both read this).</summary>
internal static class TraceGeometry
{
    /// <summary>The copper region(s) of a trace: its centre-line stroked to the trace width with
    /// round caps and round joins (the exact Minkowski sum with a disc of radius <c>Width/2</c>).
    /// A simple open polyline strokes to ONE region; a self-crossing one may split into several,
    /// which share the trace's source so the connectivity engine still reads them as one connector.</summary>
    internal static IReadOnlyList<CurvedRegion2d> Regions(in PcbTrace trace)
    {
        var edges = new List<CurvedEdge2d>(trace.Points.Count - 1);
        for (int i = 1; i < trace.Points.Count; i++)
        {
            var a = trace.Points[i - 1];
            var b = trace.Points[i];
            if (a.DistanceTo(b) > CurvedRegion2d.BoundaryTolerance)   // drop a coincident repeat
                edges.Add(CurvedEdge2d.Line(a, b));
        }
        if (edges.Count == 0)
            throw new InvalidOperationException(
                "A trace has no segment of non-zero length; it cannot be stroked into copper.");
        return CurvedRegion2dOffset.Stroke(edges, trace.Width, StrokeCap.Round, OffsetJoin.Round);
    }
}

public sealed partial class PcbLayout
{
    private readonly List<PcbTrace> _traces = [];

    /// <summary>The routed traces on this layout, in declaration order. Traces are LAYOUT TRUTH
    /// (routed copper is part of the design), so they round-trip in the layout file — a layout with
    /// no traces saves byte-identically to before routing existed.</summary>
    public IReadOnlyList<PcbTrace> Traces => _traces;

    /// <summary>
    /// Adds a routed trace. The net must be non-empty, the layer must be a copper layer of the
    /// board stackup, the width must be positive, and the centre-line must carry at least two points
    /// with at least one segment of non-zero length — all refused by name.
    /// </summary>
    public PcbLayout AddTrace(PcbTrace trace)
    {
        ValidateTrace(trace);
        _traces.Add(trace);
        return this;
    }

    /// <summary>Adds a routed trace from its parts (see <see cref="AddTrace(PcbTrace)"/>).</summary>
    public PcbLayout AddTrace(string net, string layer, double width, IReadOnlyList<Vector2d> points) =>
        AddTrace(new PcbTrace(net, layer, width, points));

    // Used by the loader to reconstruct a trace verbatim (the same validation, so a saved-then-loaded
    // trace is refused for the same reasons a freshly-declared one is).
    internal void AddLoadedTrace(PcbTrace trace)
    {
        try
        {
            ValidateTrace(trace);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"A saved trace is invalid: {ex.Message}");
        }
        _traces.Add(trace);
    }

    private void ValidateTrace(PcbTrace trace)
    {
        if (string.IsNullOrEmpty(trace.Net))
            throw new ArgumentException(
                "A trace has no net — a routed trace carries a net (the net it connects).", nameof(trace));
        string layer = trace.Layer;
        if (!Board.Stackup.Coppers.Any(c => c.Name == layer))
            throw new ArgumentException(
                $"Trace on net '{trace.Net}' names copper layer '{trace.Layer}', which the stackup does "
                + $"not contain (layers: {string.Join(", ", Board.Stackup.Coppers.Select(c => c.Name))}).",
                nameof(trace));
        if (!(trace.Width > 0))
            throw new ArgumentException(
                $"Trace on net '{trace.Net}' needs a positive width (got {trace.Width:g6}).", nameof(trace));
        if (trace.Points is null || trace.Points.Count < 2)
            throw new ArgumentException(
                $"Trace on net '{trace.Net}' needs at least two centre-line points.", nameof(trace));
        double length = 0;
        for (int i = 1; i < trace.Points.Count; i++)
            length += trace.Points[i - 1].DistanceTo(trace.Points[i]);
        if (!(length > CurvedRegion2d.BoundaryTolerance))
            throw new ArgumentException(
                $"Trace on net '{trace.Net}' has no segment of non-zero length.", nameof(trace));
    }

    /// <summary>The report id a trace's copper features are tagged with (<c>"trace1"</c>, one-based
    /// in declaration order) — stable so the DRC and connectivity name the same trace, and so the
    /// connectivity engine can read a trace as a CONNECTOR (not a terminal) the way it reads a via.</summary>
    internal static string TraceSource(int index) => $"trace{index + 1}";
}
