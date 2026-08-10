using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// The resolved geometry of one embedded component's cavity — a pocket milled into the plate. It is
/// DERIVED from the placement, the footprint and the body (the one-declaration rule), so nothing is
/// stated twice: the lateral shape is the footprint (and body) bounding box grown by the cavity
/// clearance, and the depth follows from the body height (enclosed) or the distance to the surface
/// (open cavity).
/// </summary>
/// <param name="Source">The embedded component's reference designator.</param>
/// <param name="Layer">The copper layer the component seats on (the cavity floor).</param>
/// <param name="Footprint">The cavity's lateral outline in board-local 2D coordinates (a rotated
/// rectangle) — the milled wall the DRC clears copper against.</param>
/// <param name="ZLow">The lowest board z the cavity reaches (clamped to the board).</param>
/// <param name="ZHigh">The highest board z the cavity reaches (clamped to the board).</param>
/// <param name="RemovedVolume">The material the cavity removes from the plate (mm³): the lateral
/// area × the depth inside the board.</param>
/// <param name="Tool">The subtraction tool — a box the plate is milled with. For an enclosed
/// cavity it is fully interior (an internal void); for an open cavity it overshoots the surface.</param>
public readonly record struct EmbeddedCavity(
    string Source, string Layer, CurvedRegion2d Footprint,
    double ZLow, double ZHigh, double RemovedVolume, Shape Tool);

public sealed partial class PcbLayout
{
    /// <summary>The default cavity clearance (mm) an <see cref="Embed"/> leaves around and above a
    /// component when the caller states none.</summary>
    public const double DefaultCavityClearance = 0.1;

    /// <summary>
    /// Places a component EMBEDDED on an inner copper layer — buried in an internal cavity
    /// (<see cref="Embedding.Enclosed"/>) or in an open well down to that layer
    /// (<see cref="Embedding.OpenCavity"/>). The component seats at the layer's z (the cavity
    /// floor); the cavity is milled into the plate, sized to the footprint (and body) plus
    /// <paramref name="cavityClearance"/>, and its depth follows from the body.
    /// </summary>
    /// <param name="reference">The reference designator, a component of the schematic.</param>
    /// <param name="layer">The copper layer to seat on (an inner layer name of the board's stackup).</param>
    /// <param name="x">The placement X (mm, board coordinates).</param>
    /// <param name="y">The placement Y (mm, board coordinates).</param>
    /// <param name="rotationDegrees">Rotation about the board Z axis (degrees, CCW).</param>
    /// <param name="embedding">Enclosed (buried) or an open cavity.</param>
    /// <param name="cavityClearance">The gap milled around and above the component (mm).</param>
    /// <param name="side">The face the component references (its facing); default top.</param>
    /// <exception cref="ArgumentException">The reference is unknown or already placed, the layer is
    /// not an inner copper layer, the component has no footprint/body, the clearance is negative, or
    /// the cavity would breach the board outline/surface or overlap another cavity — all refused by
    /// name.</exception>
    public PcbLayout Embed(
        string reference, string layer, double x, double y,
        double rotationDegrees = 0, Embedding embedding = Embedding.Enclosed,
        double cavityClearance = DefaultCavityClearance, CopperSide side = CopperSide.Top)
    {
        if (string.IsNullOrEmpty(reference))
            throw new ArgumentException("A placement needs a reference designator.", nameof(reference));
        if (embedding == Embedding.Surface)
            throw new ArgumentException(
                $"Embed('{reference}') is for embedded parts; use Place for a surface part.",
                nameof(embedding));
        if (string.IsNullOrEmpty(layer))
            throw new ArgumentException(
                $"Embedded placement '{reference}' needs a seat layer.", nameof(layer));
        if (cavityClearance < 0)
            throw new ArgumentException(
                $"Cavity clearance for '{reference}' must be non-negative (got {cavityClearance:g6}).",
                nameof(cavityClearance));
        if (Schematic.Find(reference) is null)
            throw new ArgumentException(
                $"'{reference}' is not a component in the schematic — a placement must name a "
                + "component the schematic declares (the schematic is the source).", nameof(reference));
        if (_placed.Contains(reference))
            throw new ArgumentException(
                $"'{reference}' is already placed; a component is placed at most once.",
                nameof(reference));

        var placement = new PcbPlacement(reference, x, y, rotationDegrees, side, layer, embedding, cavityClearance);

        // Resolve and validate this cavity, and refuse an overlap with an already-embedded one —
        // BEFORE the placement is recorded (a refused embed leaves the layout untouched).
        var cavity = ResolveCavity(placement);   // throws by name for a bad layer / no footprint-body
        ValidateCavityFits(placement, cavity);
        foreach (var existing in Cavities())
            if (CavitiesOverlap(cavity, existing))
                throw new ArgumentException(
                    $"Embedded cavity for '{reference}' overlaps the cavity for '{existing.Source}'.",
                    nameof(reference));

        _placed.Add(reference);
        _placements.Add(placement);
        return this;
    }

    /// <summary>The resolved cavities of every embedded placement, in placement order.</summary>
    public IReadOnlyList<EmbeddedCavity> Cavities()
    {
        var list = new List<EmbeddedCavity>();
        foreach (var placement in _placements)
            if (placement.IsEmbedded)
                list.Add(ResolveCavity(placement));
        return list;
    }

    /// <summary>The world AABB of an embedded component's posed body — the oracle for containment
    /// (an enclosed body is strictly inside the board's extruded volume, a surface body is not).</summary>
    public Aabb EmbeddedBodyBounds(PcbPlacement placement)
    {
        var component = Schematic.Find(placement.Reference)
            ?? throw new ArgumentException($"'{placement.Reference}' is not in the schematic.");
        var body = component.Definition.Body
            ?? throw new ArgumentException($"'{placement.Reference}' has no 3D body.");
        return body().Transform(WorldOf(placement)).Bounds();
    }

    // ---- cavity resolution ---------------------------------------------------

    private EmbeddedCavity ResolveCavity(PcbPlacement placement)
    {
        var component = Schematic.Find(placement.Reference)!;
        var footprint = component.Definition.Footprint
            ?? throw new ArgumentException(
                $"Embedded component '{placement.Reference}' has no footprint to place.",
                nameof(placement));
        var bodyBuilder = component.Definition.Body
            ?? throw new ArgumentException(
                $"Embedded component '{placement.Reference}' needs a 3D body to size its cavity depth.",
                nameof(placement));

        var seat = ResolveSeatLayer(placement);        // throws by name for an unknown layer
        double seatZ = seat.Z;
        double total = Board.Thickness;
        double c = placement.CavityClearance;

        // Lateral: the footprint pad bounding box UNION the body's xy extent, grown by the clearance
        // on every side. Using both keeps the cavity fitting the body while the report's closed form
        // stays "lateral area × depth".
        var local = bodyBuilder().Bounds();
        double minX = local.Min.X, maxX = local.Max.X, minY = local.Min.Y, maxY = local.Max.Y;
        foreach (var pad in footprint.Pads)
        {
            minX = Math.Min(minX, pad.Center.X - pad.Width / 2);
            maxX = Math.Max(maxX, pad.Center.X + pad.Width / 2);
            minY = Math.Min(minY, pad.Center.Y - pad.Height / 2);
            maxY = Math.Max(maxY, pad.Center.Y + pad.Height / 2);
        }
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        double hw = (maxX - minX) / 2 + c, hh = (maxY - minY) / 2 + c;
        double lateralArea = (2 * hw) * (2 * hh);

        double bodyHeight = local.Max.Z;               // body height above its local seat (z = 0)
        int facing = placement.Side == CopperSide.Top ? 1 : -1;

        // The cavity's far end, in LOCAL z (0 at the floor). Enclosed stops just past the body;
        // an open cavity runs to the near surface with an overshoot so no cut is coplanar.
        double removedDepth, endLocal;
        if (placement.Embedding == Embedding.Enclosed)
        {
            removedDepth = bodyHeight + c;
            endLocal = facing * removedDepth;
        }
        else // OpenCavity
        {
            double distToSurface = placement.Side == CopperSide.Top ? total - seatZ : seatZ;
            removedDepth = distToSurface;
            endLocal = facing * (distToSurface + total);   // overshoot the surface by a thickness
        }

        double zLoLocal = Math.Min(0, endLocal), zHiLocal = Math.Max(0, endLocal);
        double depthLocal = zHiLocal - zLoLocal, centerLocalZ = (zLoLocal + zHiLocal) / 2;

        var poseMatrix = PlacementPose(placement).ToMatrix();
        var tool = Shape.Box(2 * hw, 2 * hh, depthLocal)
            .Translate(cx, cy, centerLocalZ)
            .Transform(poseMatrix);

        // The board z-range the cavity actually occupies (clamped to the plate for reporting).
        double zA = seatZ, zB = seatZ + endLocal;
        double zLow = Math.Max(0, Math.Min(zA, zB));
        double zHigh = Math.Min(total, Math.Max(zA, zB));

        var footprintRegion = RectRegion(placement, cx, cy, hw, hh);
        return new EmbeddedCavity(
            placement.Reference, seat.Name, footprintRegion, zLow, zHigh,
            lateralArea * removedDepth, tool);
    }

    /// <summary>The cavity's lateral rectangle as a board-local <see cref="CurvedRegion2d"/>,
    /// rotated with the placement.</summary>
    private CurvedRegion2d RectRegion(PcbPlacement placement, double cx, double cy, double hw, double hh)
    {
        var pose = PlacementPose(placement);
        Vector2d P(double dx, double dy) => PadGeometry.Map(pose, new Vector2d(cx + dx, cy + dy));
        var bl = P(-hw, -hh);
        var br = P(hw, -hh);
        var tr = P(hw, hh);
        var tl = P(-hw, hh);
        return new CurvedRegion2d([
            CurvedEdge2d.Line(bl, br), CurvedEdge2d.Line(br, tr),
            CurvedEdge2d.Line(tr, tl), CurvedEdge2d.Line(tl, bl),
        ]);
    }

    // ---- cavity validation ---------------------------------------------------

    private void ValidateCavityFits(PcbPlacement placement, in EmbeddedCavity cavity)
    {
        double total = Board.Thickness;
        double seatZ = SeatZ(placement);

        // The seat must be an INNER plane — a floor with board on both sides — not a face.
        if (!(seatZ > 0 && seatZ < total))
            throw new ArgumentException(
                $"Embedded placement '{placement.Reference}' seats on layer '{cavity.Layer}' at "
                + $"z={seatZ:g6}, which is a board face (0 or {total:g6}); embed on an inner layer.",
                nameof(placement));

        // Enclosed cavities must not reach either surface — that is what "buried" means.
        if (placement.Embedding == Embedding.Enclosed && !(cavity.ZLow > 0 && cavity.ZHigh < total))
            throw new ArgumentException(
                $"Enclosed cavity for '{placement.Reference}' would breach the board surface (it "
                + $"reaches z∈[{cavity.ZLow:g6}, {cavity.ZHigh:g6}], board is [0, {total:g6}]); "
                + "reduce the clearance/height or seat it deeper.", nameof(placement));

        // The lateral rectangle must lie within the board outline (its four corners inside; a board
        // outline is convex in the common case, for which corners-inside is the whole rectangle).
        foreach (var loop in cavity.Footprint.AllLoops())
            foreach (var edge in loop)
                if (!Board.ContainsInOutline(edge.Start))
                    throw new ArgumentException(
                        $"Embedded cavity for '{placement.Reference}' would breach the board outline "
                        + $"(a corner at ({edge.Start.X:g6}, {edge.Start.Y:g6}) is off the board).",
                        nameof(placement));
    }

    /// <summary>Whether two cavities overlap in 3D — their z-ranges overlap AND their (rotated)
    /// lateral rectangles overlap. Uses a separating-axis test on the two oriented rectangles, so a
    /// rotated pair that does not truly overlap is not falsely refused.</summary>
    private static bool CavitiesOverlap(in EmbeddedCavity a, in EmbeddedCavity b)
    {
        // z-intervals must overlap (open overlap; touching faces do not count).
        if (a.ZHigh <= b.ZLow || b.ZHigh <= a.ZLow)
            return false;
        return ObbOverlap(Corners(a.Footprint), Corners(b.Footprint));
    }

    private static Vector2d[] Corners(CurvedRegion2d rect)
    {
        var pts = new List<Vector2d>();
        foreach (var loop in rect.AllLoops())
            foreach (var edge in loop)
                pts.Add(edge.Start);
        return [.. pts];
    }

    /// <summary>Separating-axis overlap of two convex polygons (the cavity rectangles). Disjoint iff
    /// some edge normal separates them.</summary>
    private static bool ObbOverlap(Vector2d[] a, Vector2d[] b)
    {
        return !HasSeparatingAxis(a, b) && !HasSeparatingAxis(b, a);

        static bool HasSeparatingAxis(Vector2d[] p, Vector2d[] q)
        {
            for (int i = 0; i < p.Length; i++)
            {
                var edge = p[(i + 1) % p.Length] - p[i];
                var axis = new Vector2d(-edge.Y, edge.X);   // edge normal
                if (axis.LengthSquared < 1e-24)
                    continue;
                Project(p, axis, out double pMin, out double pMax);
                Project(q, axis, out double qMin, out double qMax);
                if (pMax <= qMin || qMax <= pMin)           // a gap on this axis: disjoint
                    return true;
            }
            return false;
        }

        static void Project(Vector2d[] poly, in Vector2d axis, out double min, out double max)
        {
            min = double.PositiveInfinity;
            max = double.NegativeInfinity;
            foreach (var pt in poly)
            {
                double d = pt.Dot(axis);
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }
        }
    }
}
