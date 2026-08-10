using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

public sealed partial class PcbLayout
{
    /// <summary>Copper layers render as copper; the dielectric between them as FR4 green — so the
    /// exploded stack reads as a board sandwich rather than a pile of identical slabs.</summary>
    private static readonly PartColor CopperColor = Palette.Copper;
    private static readonly PartColor DielectricColor = Palette.Sage;

    /// <summary>
    /// Decomposes a MULTILAYER board into PER-LAYER slabs and assembles them with the placed
    /// components as an <see cref="Assembly"/> whose <see cref="Occurrence.ExplodeOffset"/>s fan the
    /// stack apart along the <b>stackup normal</b> — the PCB exploded view. It is the sibling of
    /// <see cref="ToAssembly"/> (which is the board as ONE part) and leaves it untouched; the
    /// component occurrences are built EXACTLY as <see cref="ToAssembly"/> builds them, so at explode
    /// factor 0 a component's world transform is bit-identical between the two.
    ///
    /// <para><b>The decomposition.</b> Each physical <see cref="StackLayer"/> (a dielectric core or a
    /// thin copper film) becomes its own slab — the outline extruded over that layer's own z-range
    /// (reused from <see cref="LayerStackup.Extents"/>, never recomputed), drilled by every through
    /// hole and milled by every embedded cavity that reaches into its z-range. So the slabs are
    /// DISJOINT (disjoint z-ranges), TILE the stackup exactly (<c>[0, TotalThickness]</c>), and their
    /// union IS the plate: <c>Σ slab volume == <see cref="ExpectedPlateVolume"/></c> as a closed
    /// form. A component whose definition carries a 3D body is placed once as an occurrence, surface
    /// AND embedded alike.</para>
    ///
    /// <para><b>The explode is pure along the stackup normal</b> <c>n = BoardFrame.Z</c> (world +Z for
    /// an un-posed board), because a PCB's one interesting relationship is its z-stacking:
    /// <list type="bullet">
    /// <item><description><b>Layers</b> fan up from the BOTTOM layer as the datum (it stays put) —
    /// the natural datum, since the stackup itself is accumulated from the bottom face (z = 0). A
    /// layer's offset is <c>n · gap · rank</c> with rank counted from the bottom, so stack order IS
    /// explode order (a layer above another in the stack is above it when exploded).</description></item>
    /// <item><description><b>Surface components</b> lift off their face — a top part up clear of the
    /// fanned stack, a bottom part down below the datum. Pure Z.</description></item>
    /// <item><description><b>Embedded components</b> come OUT OF THEIR CAVITY along Z FIRST, then
    /// spread aside — an <see cref="Occurrence.ExplodePath"/> DOGLEG whose first leg is pure ±n
    /// (straight out of the cavity) and whose final offset carries a lateral spread so the die does
    /// not tunnel straight up through the layers above it (a diagonal reads as "insert at an angle",
    /// the recorded explode-path lesson). Every LAYER and SURFACE offset is pure Z; an embedded part's
    /// offset is the one that carries the lateral leg, because that lateral leg IS the dogleg.</description></item>
    /// </list></para>
    ///
    /// <para><b>Not for the render thread.</b> Building the slabs and (for offsets) reading the parts
    /// is geometry work — call it off the render thread, like <see cref="Assembly.AutoExplode"/>.</para>
    /// </summary>
    /// <param name="spacing">The gap between consecutive fanned layers (mm), along the stackup normal.
    /// 0 (the default) derives one that opens the stack to about twice the board's own thickness — big
    /// enough to read, small enough to still frame in one view.</param>
    /// <param name="name">The assembly name; null uses the schematic's name (or "PCB").</param>
    /// <exception cref="ArgumentException">The board was built the copper-only way (no physical
    /// <see cref="LayerStackup"/>), so there are no layers to slice — refused by name; use
    /// <see cref="ToAssembly"/> for a single-slab board. A negative <paramref name="spacing"/> is
    /// refused too.</exception>
    public Assembly ToExplodedAssembly(double spacing = 0, string? name = null)
    {
        if (spacing < 0)
            throw new ArgumentException(
                $"An explode spacing cannot be negative (got {spacing:g6}).", nameof(spacing));
        if (Board.LayerStackup is not { } stackup)
            throw new ArgumentException(
                "ToExplodedAssembly needs a physical LayerStackup (the copper + dielectric build-up) "
                + "to slice into per-layer slabs — this board was built copper-only (its copper is N "
                + "planes with no modelled dielectric), so there is nothing to slice apart. Use "
                + "ToAssembly for a single-slab board, or build it from a LayerStackup.",
                nameof(Board));

        var layers = stackup.Layers;
        var extents = stackup.Extents;
        int layerCount = layers.Count;                       // >= 3 (a stackup needs >= 2 coppers)
        double total = Board.Thickness;
        double gap = spacing > 0 ? spacing : 2 * total / (layerCount - 1);

        var nHat = BoardFrame.Z;                             // the stackup normal, in world coords
        var assembly = new Assembly(
            string.IsNullOrEmpty(name) ? (Schematic.Name.Length > 0 ? Schematic.Name : "PCB") : name);

        // ---- the per-layer slabs (top-most first, matching Layers) ----
        var holes = ThroughHoles().ToList();
        var cavities = Cavities();
        int bottom = layerCount - 1;                          // the datum: the layer at z = 0
        for (int i = 0; i < layerCount; i++)
        {
            var layer = layers[i];
            var part = new Part(
                layer.Name, LayerSlab(extents[i], layer, holes, cavities),
                layer.Kind == StackLayerKind.Copper ? CopperColor : DielectricColor);
            var occurrence = assembly.Add(part, BoardFrame, layer.Name);

            // Fan up from the bottom datum by stack rank. The datum stays put (null), so an
            // un-exploded flatten leaves it bit-for-bit where it was.
            int rank = bottom - i;                            // 0 at the bottom, layerCount-1 at the top
            occurrence.ExplodeOffset = rank == 0 ? null : nHat * (gap * rank);
        }

        // ---- the placed components (built exactly as ToAssembly, plus explode offsets) ----
        var cache = new Dictionary<(PartDefinition, CopperSide), Part>();
        foreach (var placement in _placements)
        {
            var definition = Schematic.Find(placement.Reference)!.Definition;
            if (definition.Body is null)
                continue;
            var key = (definition, placement.Side);
            if (!cache.TryGetValue(key, out var part))
            {
                part = new Part(definition.Name, definition.Body())
                {
                    Transform = PartTransform(placement.Side),
                };
                cache[key] = part;
            }
            var occurrence = assembly.Add(part, OccurrenceFrame(placement), placement.Reference);
            ApplyComponentExplode(occurrence, placement, nHat, gap, layerCount, total);
        }
        return assembly;
    }

    /// <summary>Sets a placed component's explode offset (and, for an embedded part, its dogleg
    /// path). Surface parts lift straight off their face; an embedded part rises straight out of its
    /// cavity, then spreads up-and-aside.</summary>
    private void ApplyComponentExplode(
        Occurrence occurrence, in PcbPlacement placement, in Vector3d nHat,
        double gap, int layerCount, double total)
    {
        if (!placement.IsEmbedded)
        {
            // A top part lifts clear ABOVE the fanned stack (whose top layer sits at gap·(L-1)); a
            // bottom part drops one gap below the datum. Pure Z either way.
            double level = placement.Side == CopperSide.Top ? layerCount : -1;
            occurrence.ExplodeOffset = nHat * (gap * level);
            return;
        }

        // Embedded: come straight OUT of the cavity first, then spread. The exit direction is the
        // face the placement is quoted on; the first leg is pure ±n (no lateral), the final offset
        // adds the lateral spread — that lateral leg is the dogleg (a diagonal would read as
        // "insert at an angle"), which is why an embedded offset is the one that is not pure Z.
        var exit = placement.Side == CopperSide.Top ? nHat : -nHat;
        double seatZ = SeatZ(placement);
        double faceDist = placement.Side == CopperSide.Top ? total - seatZ : seatZ;   // seat -> exit face
        double clearMag = faceDist + gap * 0.5;                     // waypoint: just clear the face
        double finalMag = faceDist + gap * (layerCount + 1);       // clear the whole fanned stack
        var lateral = RadialSpread(placement) * gap;

        occurrence.ExplodePath.Add(exit * clearMag);         // leg 1: straight out of the cavity
        occurrence.ExplodeOffset = exit * finalMag + lateral;   // leg 2: up (clear) and aside
    }

    /// <summary>A unit vector in the board plane pointing radially outward from the board's centroid
    /// through the placement — the direction an embedded part spreads aside as it comes out. Falls
    /// back to the board's own +X for a part sitting on the centroid.</summary>
    private Vector3d RadialSpread(in PcbPlacement placement)
    {
        double cx = 0, cy = 0;
        var outline = Board.OutlinePoints;
        foreach (var p in outline)
        {
            cx += p.X;
            cy += p.Y;
        }
        cx /= outline.Count;
        cy /= outline.Count;
        var radialLocal = new Vector3d(placement.X - cx, placement.Y - cy, 0);
        if (!radialLocal.TryNormalize(Tolerance.Default, out var unit))
            unit = Vector3d.UnitX;                            // a part on the centroid: spread along +X
        return BoardFrame.ToWorldVector(unit);
    }

    /// <summary>One physical layer as a slab: the board outline extruded over its own z-range, drilled
    /// by every through hole and milled by every embedded cavity that reaches into that z-range. The
    /// slabs therefore tile the plate exactly — <c>Σ slab volume == ExpectedPlateVolume()</c>.</summary>
    private Shape LayerSlab(
        (double Low, double High) extent, in StackLayer layer,
        IReadOnlyList<(Vector2d Center, double Diameter)> holes, IReadOnlyList<EmbeddedCavity> cavities)
    {
        double height = extent.High - extent.Low;
        // Reuse the plate builder (outline extruded + holes drilled), then place it at its z and mill
        // the cavities that reach this slab — the SAME construction the plate is built from, so the
        // decomposition cannot diverge from the plate it reassembles into.
        var slab = PcbGeometry.BuildPlate(Board.OutlinePoints, height, holes).Translate(0, 0, extent.Low);
        foreach (var cavity in cavities)
            if (cavity.ZHigh > extent.Low && cavity.ZLow < extent.High)   // z-ranges overlap
                slab = slab.Subtract(cavity.Tool);
        return slab;
    }
}
