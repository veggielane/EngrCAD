using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// One piece of copper the DRC reasons about: a region on a named layer, tagged with the NET it
/// belongs to (the load-bearing tag — same net touching is intended, different nets touching is a
/// short) and a human-readable <see cref="Source"/> for messages.
///
/// <para>The <see cref="Region"/> is in BOARD-LOCAL 2D coordinates (mm) — the DRC is a property of
/// the board, independent of where the board sits in an assembly. A pad becomes a disc / rectangle
/// / stadium; a stage-5 trace becomes a stroked centre-line region through the SAME type, so the
/// DRC needs no change to reach traces.</para>
/// </summary>
/// <param name="Layer">The copper layer this feature is on (<c>"Top"</c>, <c>"Bottom"</c>).</param>
/// <param name="Net">The net name this copper belongs to, or null when it is on no ELECTRICAL net
/// (an unconnected pin, or a NoConnect terminal) — a null-net feature is its own net and must
/// clear every other feature, since two floating pieces of copper are electrically distinct.</param>
/// <param name="Source">A name for reports (<c>"R1.1"</c>, a trace id).</param>
/// <param name="Region">The copper outline in board-local 2D coordinates (mm).</param>
public readonly record struct CopperFeature(
    string Layer, string? Net, string Source, CurvedRegion2d Region);

/// <summary>
/// A drilled hole in the stack — a through-hole pad's finished hole, a via, or a mounting hole.
/// A drill goes through every layer, so drill-to-copper is checked against ALL layers.
/// </summary>
/// <param name="Center">The hole centre in board-local 2D coordinates (mm).</param>
/// <param name="Diameter">The finished hole diameter (mm).</param>
/// <param name="PadDiameter">The smaller dimension of the copper pad around the hole (mm), from
/// which the annular ring derives (<c>(PadDiameter − Diameter) / 2</c>); 0 for a bare hole with no
/// pad (a plain mounting hole), which carries no annular-ring rule.</param>
/// <param name="Net">The net the drill belongs to, or null for a bare board hole (its own net —
/// must clear all other copper).</param>
/// <param name="Source">A name for reports (<c>"R1.1"</c>, <c>"via"</c>, <c>"mounting hole"</c>).</param>
public readonly record struct DrilledHole(
    Vector2d Center, double Diameter, double PadDiameter, string? Net, string Source);

/// <summary>
/// The GEOMETRIC copper model the DRC (<see cref="PcbDrc"/>) reads: the board, the copper features
/// per layer, and the drilled holes — all in board-local 2D coordinates. It is the seam between
/// "what is on the board" and "what the rules say", so it is what a stage-5 router builds a
/// candidate route into and asks <see cref="PcbDrc.Violates"/> about.
///
/// <para><see cref="FromLayout"/> derives it from a stage-2 <see cref="PcbLayout"/> — pads become
/// copper, through-hole pads and board holes become drills, and each pad's net is resolved from
/// the one-declaration schematic. A model can also be built by hand (the public constructor), which
/// is how a trace fixture and a stage-5 router feed copper the layout does not yet carry.</para>
/// </summary>
public sealed class PcbCopperModel
{
    /// <summary>Builds a copper model from a board, a set of copper features and drilled holes.</summary>
    public PcbCopperModel(
        PcbBoard board,
        IEnumerable<CopperFeature> copper,
        IEnumerable<DrilledHole>? drills = null,
        IEnumerable<EmbeddedCavity>? cavities = null,
        IEnumerable<PlacedVia>? vias = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(copper);
        Board = board;
        Copper = [.. copper];
        Drills = drills is null ? [] : [.. drills];
        Cavities = cavities is null ? [] : [.. cavities];
        Vias = vias is null ? [] : [.. vias];
    }

    /// <summary>The board (its outline is the copper-to-edge datum).</summary>
    public PcbBoard Board { get; }

    /// <summary>The copper features, in layer then declaration order.</summary>
    public IReadOnlyList<CopperFeature> Copper { get; }

    /// <summary>The drilled holes.</summary>
    public IReadOnlyList<DrilledHole> Drills { get; }

    /// <summary>The embedded-component cavities milled into the board — their walls are a milled
    /// edge copper must clear, checked by the <see cref="DrcRule.CavityClearance"/> rule.</summary>
    public IReadOnlyList<EmbeddedCavity> Cavities { get; }

    /// <summary>The placed vias — resolved copper (centre, net, touched layers, annular pad). Their
    /// annular pads are ALSO in <see cref="Copper"/> (one per touched layer, tagged with the via's
    /// net) and their drills in <see cref="Drills"/>, so the general clearance / drill-to-copper /
    /// annular-ring rules reach them; this list is what the connectivity engine walks (a via joins
    /// its pads across layers) and what the via-to-via rule spaces.</summary>
    public IReadOnlyList<PlacedVia> Vias { get; }

    /// <summary>The distinct copper-layer names, in stackup order.</summary>
    public IReadOnlyList<string> Layers => Board.Stackup.Coppers.Select(c => c.Name).ToList();

    /// <summary>
    /// Derives a copper model from a placed layout: each footprint pad becomes a copper feature on
    /// the right layer(s) at its placed pose (rotated with the placement), each through-hole pad and
    /// each board hole becomes a drill, and each pad's net is resolved from the schematic (the
    /// one-declaration identity — a pad's net IS its pin's net). A NoConnect pin's pad carries a
    /// null net (its own), as does a pin on no net at all.
    /// </summary>
    public static PcbCopperModel FromLayout(PcbLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        // Resolve every connected pin to its net name. Only Signal/Stub nets JOIN their pins
        // electrically; a NoConnect net's pins are each isolated, so they do NOT share a net key
        // (two no-connects are electrically distinct and must clear each other).
        var netOfPin = new Dictionary<PinRef, string>();
        foreach (var net in layout.Schematic.Nets)
            if (net.Kind is NetKind.Signal or NetKind.Stub)
                foreach (var pin in net.Pins)
                    netOfPin[pin] = net.Name;

        var copper = new List<CopperFeature>();
        var drills = new List<DrilledHole>();

        foreach (var placement in layout.Placements)
        {
            var component = layout.Schematic.Find(placement.Reference)!;
            var footprint = component.Definition.Footprint;
            if (footprint is null)
                continue;
            // Board-local pose: the placement's own frame (translation + rotation about Z). The
            // bottom-side reflection lives on Z only (FlipZ), so it does not touch the 2D copper
            // shape — a bottom pad keeps the same board-local (x, y) and orientation as a top one.
            var pose = layout.PlacementPose(placement);
            string layerOfSide = layout.Board.Stackup.Outer(placement.Side).Name;

            foreach (var pad in footprint.Pads)
            {
                var region = PadGeometry.Region(pad, pose);
                var source = $"{placement.Reference}.{pad.Number}";
                string? net = component.Definition.HasPin(pad.Number)
                    && netOfPin.TryGetValue(new PinRef(component, pad.Number), out var n) ? n : null;

                if (pad.Kind == PadKind.ThroughHole)
                {
                    // A plated through-hole carries copper on EVERY layer and one drill.
                    foreach (var layer in layout.Board.Stackup.Coppers)
                        copper.Add(new CopperFeature(layer.Name, net, source, region));
                    var center = PadGeometry.Map(pose, pad.Center);
                    drills.Add(new DrilledHole(
                        center, pad.DrillDiameter, Math.Min(pad.Width, pad.Height), net, source));
                }
                else
                {
                    copper.Add(new CopperFeature(layerOfSide, net, source, region));
                }
            }
        }

        // Board holes: bare drills of their own net (a mounting hole must clear all copper; a board
        // via has no pad in the stage-2 model, so it carries no annular-ring rule here).
        foreach (var hole in layout.Board.Holes)
            drills.Add(new DrilledHole(
                hole.Center, hole.Diameter, 0,
                Net: null, Source: hole.Kind.ToString().ToLowerInvariant()));

        // Vias: a plated cross-layer connection. Each places an annular pad (a disc of the pad
        // diameter with the drill removed) on EVERY copper layer it touches, tagged with the via's
        // net — the copper the general clearance / edge / connectivity walk over — plus one drill
        // (the annular-ring and drill-to-copper rules ride the drill's PadDiameter and Net). The
        // per-layer pad features share the via's Source, so the connectivity engine reads them as
        // one plated barrel (the same rule a through-hole pad's per-layer copies obey).
        var placedVias = layout.PlacedVias();
        foreach (var via in placedVias)
        {
            var pad = via.PadRegion;
            foreach (var layer in via.Layers)
                copper.Add(new CopperFeature(layer, via.Net, via.Source, pad));
            drills.Add(new DrilledHole(
                via.Center, via.DrillDiameter, via.PadDiameter, via.Net, via.Source));
        }

        return new PcbCopperModel(layout.Board, copper, drills, layout.Cavities(), placedVias);
    }
}

/// <summary>Turns a footprint <see cref="Pad"/> into a board-local copper <see cref="CurvedRegion2d"/>
/// at a placement pose — a disc for a round pad, a rectangle for a rectangular one, a stadium for an
/// oval, all rotated with the placement. A rounded-rectangle pad is modelled as its BOUNDING
/// rectangle, which is CONSERVATIVE: the modelled copper is a superset of the real land, so a
/// clearance violation is never missed (the corner rounding only makes the real clearance
/// larger).</summary>
internal static class PadGeometry
{
    internal static CurvedRegion2d Region(in Pad pad, Frame3d pose)
    {
        double w = pad.Width, h = pad.Height;
        return pad.Shape switch
        {
            // A round pad is a disc; use the larger dimension so a mis-shaped round pad stays a
            // superset (conservative in the never-miss-a-violation direction).
            PadShape.Round => CurvedRegion2d.Disc(Map(pose, pad.Center), Math.Max(w, h) / 2),
            // An oval is a stadium: a rounded rectangle whose corner radius is half the short side.
            PadShape.Oval => RoundedRect(pose, pad.Center, w, h, Math.Min(w, h) / 2),
            // A rectangle, and a rounded rectangle conservatively as its bounding rectangle.
            _ => RoundedRect(pose, pad.Center, w, h, 0),
        };
    }

    /// <summary>Maps a footprint-local 2D point through the placement pose into board-local 2D.
    /// The pose is orthonormal (a proper rotation about Z), so the 2D action is the placement's
    /// rotation and translation with no scale.</summary>
    internal static Vector2d Map(Frame3d pose, in Vector2d local) => new(
        pose.Origin.X + local.X * pose.X.X + local.Y * pose.Y.X,
        pose.Origin.Y + local.X * pose.X.Y + local.Y * pose.Y.Y);

    /// <summary>Builds a rounded rectangle (r = 0 gives a plain rectangle) centred at a footprint-
    /// local point, mapped through the pose. The chain is counter-clockwise; the rotation is a
    /// proper isometry, so it preserves the corner radius and the CCW turn of the arcs.</summary>
    private static CurvedRegion2d RoundedRect(Frame3d pose, in Vector2d center, double w, double h, double r)
    {
        double hw = w / 2, hh = h / 2;
        r = Math.Min(r, Math.Min(hw, hh));
        var c = center;   // a local `in` parameter cannot be captured by the closure below

        // Footprint-local corner/tangent offsets around the pad centre, then mapped to board-local.
        Vector2d P(double x, double y) => Map(pose, c + new Vector2d(x, y));

        var edges = new List<CurvedEdge2d>();
        void Line(in Vector2d a, in Vector2d b)
        {
            // Drop a zero-length side (a stadium whose short side is a full semicircle).
            if (a.DistanceTo(b) > 1e-15 * Math.Max(1, hw + hh))
                edges.Add(CurvedEdge2d.Line(a, b));
        }
        void Corner(in Vector2d a, in Vector2d b, in Vector2d c) =>
            edges.Add(CurvedEdge2d.ArcBetween(a, b, c, r, turn: 1));

        if (r <= 0)
        {
            var bl = P(-hw, -hh);
            var br = P(hw, -hh);
            var tr = P(hw, hh);
            var tl = P(-hw, hh);
            edges.Add(CurvedEdge2d.Line(bl, br));
            edges.Add(CurvedEdge2d.Line(br, tr));
            edges.Add(CurvedEdge2d.Line(tr, tl));
            edges.Add(CurvedEdge2d.Line(tl, bl));
            return new CurvedRegion2d(edges);
        }

        // CCW: bottom → BR corner → right → TR → top → TL → left → BL corner.
        Line(P(-hw + r, -hh), P(hw - r, -hh));
        Corner(P(hw - r, -hh), P(hw, -hh + r), P(hw - r, -hh + r));
        Line(P(hw, -hh + r), P(hw, hh - r));
        Corner(P(hw, hh - r), P(hw - r, hh), P(hw - r, hh - r));
        Line(P(hw - r, hh), P(-hw + r, hh));
        Corner(P(-hw + r, hh), P(-hw, hh - r), P(-hw + r, hh - r));
        Line(P(-hw, hh - r), P(-hw, -hh + r));
        Corner(P(-hw, -hh + r), P(-hw + r, -hh), P(-hw + r, -hh + r));
        return new CurvedRegion2d(edges);
    }
}
