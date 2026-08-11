using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>How a placed component sits in the board's z — on a face, or buried in a cavity.</summary>
public enum Embedding
{
    /// <summary>On an outer copper face, proud of the board (the default, and the stage-2
    /// behaviour). The seat is the outer copper of the placement's <see cref="CopperSide"/>.</summary>
    Surface,

    /// <summary>Buried in an INTERNAL cavity — the build-up above and below stays intact, so the
    /// component has no external access and its body is strictly inside the board volume.</summary>
    Enclosed,

    /// <summary>In an OPEN CAVITY — a well milled down to the seat layer from the placement's
    /// <see cref="CopperSide"/> face, so the component is accessible and the well breaks that
    /// surface.</summary>
    OpenCavity,
}

/// <summary>A component placed on a board: a reference designator naming a
/// <see cref="Component"/> of the layout's <see cref="Schematic"/>, a 2D pose on the board
/// (mm and degrees, about the board Z), which face it references, and — for an inner-layer or
/// embedded part — the copper layer it seats on and how it is embedded.</summary>
/// <param name="Reference">The reference designator (<c>R1</c>), a component of the schematic.</param>
/// <param name="X">The placement X (mm, board coordinates).</param>
/// <param name="Y">The placement Y (mm, board coordinates).</param>
/// <param name="RotationDegrees">Rotation about the board Z axis (degrees, CCW).</param>
/// <param name="Side">The board face the component references (its facing/orientation).</param>
/// <param name="Layer">The copper layer the component seats on, or null for the outer copper of
/// <paramref name="Side"/> (a surface part). A non-null name is an inner layer; the part is
/// embedded and seats at that layer's z (the cavity floor).</param>
/// <param name="Embedding">How the component sits in z — on the surface (default), enclosed
/// (buried) or in an open cavity.</param>
/// <param name="CavityClearance">The gap (mm) milled around and above the component in its cavity;
/// meaningful only for an embedded part.</param>
public readonly record struct PcbPlacement(
    string Reference, double X, double Y, double RotationDegrees, CopperSide Side,
    string? Layer = null, Embedding Embedding = Embedding.Surface, double CavityClearance = 0)
{
    /// <summary>Whether this placement is embedded (buried or in an open cavity) rather than on a
    /// surface.</summary>
    public bool IsEmbedded => Embedding != Embedding.Surface;
}

/// <summary>One footprint pad projected onto the board — the geometric lift of a pin. It ties a
/// schematic <see cref="Pin"/> (as a <see cref="PinRef"/>) to a copper location, which is the
/// seam DRC and routing consume.</summary>
/// <param name="Reference">The owning component's reference designator.</param>
/// <param name="PadNumber">The pad number (equals the pin number by the pin↔pad identity).</param>
/// <param name="World">The pad centre in board-assembly world coordinates (mm).</param>
/// <param name="Kind">Surface-mount or through-hole.</param>
/// <param name="DrillDiameter">The drill through the board (mm) for a through-hole pad, else 0.</param>
/// <param name="Width">The pad width (mm).</param>
/// <param name="Height">The pad height (mm).</param>
/// <param name="Shape">The copper shape.</param>
/// <param name="Pin">The schematic pin this pad realises, or null when the definition has no
/// pin of this number (a pad without a pin — a check violation).</param>
public readonly record struct PlacedPad(
    string Reference, string PadNumber, Vector2d World, PadKind Kind, double DrillDiameter,
    double Width, double Height, PadShape Shape, PinRef? Pin)
{
    /// <summary>The canonical <c>"R1.1"</c> spelling.</summary>
    public string Name => $"{Reference}.{PadNumber}";
}

/// <summary>The placed pad regions of one copper layer — the model the routing and DRC stages
/// consume. Copper thickness is not modelled, so a layer is its z-plane and the pads on it.</summary>
public sealed class CopperLayer
{
    internal CopperLayer(string name, double z, IReadOnlyList<PlacedPad> pads)
    {
        Name = name;
        Z = z;
        Pads = pads;
    }

    /// <summary>The layer name (<c>"Top"</c>, <c>"Bottom"</c>).</summary>
    public string Name { get; }

    /// <summary>The layer's z-height (mm).</summary>
    public double Z { get; }

    /// <summary>The pads on this layer (SMD pads of components placed on this side, plus every
    /// through-hole pad, which appears on all copper layers).</summary>
    public IReadOnlyList<PlacedPad> Pads { get; }
}

/// <summary>
/// A board layout: a <see cref="Schematic"/> (the single source), a <see cref="PcbBoard"/>, and
/// a list of component <see cref="PcbPlacement"/>s. Everything else DERIVES from these — the
/// board is one declaration and the copper, the footprint placement and the 3D bodies all read
/// this one graph, which is the campaign's load-bearing rule.
///
/// <para><b>What a placement derives</b> (the "one declaration produces both"): its footprint
/// pads projected onto the correct copper layer at the placed pose (<see cref="PlacedPads"/> /
/// <see cref="CopperLayers"/>), its through-hole pads drilled into the plate
/// (<see cref="Plate"/>), and — for a component whose definition carries a 3D body — its body
/// posed into the assembly (<see cref="ToAssembly"/>). A bottom-side placement is reflected
/// across the board plane so its body hangs below and its through-hole pads keep the same world
/// (x, y) (a hole serves both faces); the reflection is a genuine <c>Mirror</c> (its square is
/// the identity), and it lives on the component's <see cref="Part.Transform"/>, so the board's
/// own +Z axis — world up — is never touched (the FlipX-not-FlipZ doctrine: the reflection is
/// spent on the part, the pose stays a proper frame).</para>
///
/// <para><b>The one-declaration identity check</b> (<see cref="Check"/>) is the geometric lift
/// of the schematic's pin-counting identity: every pin of every placed component resolves to
/// exactly one placed pad at a known copper location.</para>
/// </summary>
public sealed partial class PcbLayout
{
    private readonly List<PcbPlacement> _placements = [];
    private readonly HashSet<string> _placed = new(StringComparer.Ordinal);

    /// <summary>Builds a layout over a schematic and a board (no placements yet).</summary>
    /// <param name="schematic">The connectivity source.</param>
    /// <param name="board">The board.</param>
    /// <param name="boardFrame">Where the board sits in the assembly; null = world XY.</param>
    public PcbLayout(Schematic schematic, PcbBoard board, Frame3d? boardFrame = null)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        ArgumentNullException.ThrowIfNull(board);
        Schematic = schematic;
        Board = board;
        BoardFrame = boardFrame ?? Frame3d.WorldXY;
    }

    /// <summary>The connectivity source.</summary>
    public Schematic Schematic { get; }

    /// <summary>The board.</summary>
    public PcbBoard Board { get; }

    /// <summary>Where the board sits in the assembly (default world XY).</summary>
    public Frame3d BoardFrame { get; }

    /// <summary>The placements, in declaration order.</summary>
    public IReadOnlyList<PcbPlacement> Placements => _placements;

    // ---- declaring placements ------------------------------------------------

    /// <summary>Places a component at a pose on a board face. The reference designator must name
    /// a component of the schematic and must not already be placed.</summary>
    /// <exception cref="ArgumentException">The reference designator is not a component of the
    /// schematic, or is already placed (both refused by name).</exception>
    public PcbLayout Place(string reference, double x, double y,
        double rotationDegrees = 0, CopperSide side = CopperSide.Top)
    {
        if (string.IsNullOrEmpty(reference))
            throw new ArgumentException("A placement needs a reference designator.", nameof(reference));
        if (Schematic.Find(reference) is null)
            throw new ArgumentException(
                $"'{reference}' is not a component in the schematic — a placement must name a "
                + "component the schematic declares (the schematic is the source).", nameof(reference));
        if (!_placed.Add(reference))
            throw new ArgumentException(
                $"'{reference}' is already placed; a component is placed at most once.",
                nameof(reference));
        _placements.Add(new PcbPlacement(reference, x, y, rotationDegrees, side));
        return this;
    }

    // Used by the loader to reconstruct placements verbatim (same validation). Geometry-dependent
    // cavity validation (breach/overlap) is deferred to Plate/Cavities so a data-only load with no
    // bodies still round-trips; the seat layer must exist, which is refused by name here.
    internal void AddLoadedPlacement(PcbPlacement placement)
    {
        if (Schematic.Find(placement.Reference) is null)
            throw new FormatException(
                $"Placement '{placement.Reference}' names a component the schematic does not contain.");
        if (placement.IsEmbedded)
        {
            if (string.IsNullOrEmpty(placement.Layer))
                throw new FormatException(
                    $"Embedded placement '{placement.Reference}' names no seat layer.");
            if (!Board.Stackup.Coppers.Any(c => c.Name == placement.Layer))
                throw new FormatException(
                    $"Placement '{placement.Reference}' names copper layer '{placement.Layer}', which "
                    + $"the stackup does not contain (layers: "
                    + $"{string.Join(", ", Board.Stackup.Coppers.Select(c => c.Name))}).");
        }
        if (!_placed.Add(placement.Reference))
            throw new FormatException($"Component '{placement.Reference}' is placed more than once.");
        _placements.Add(placement);
    }

    // ---- placement transforms (the seam ToAssembly and the oracles share) ----

    /// <summary>The z the placement seats at (the plane its body's local origin lands on): the
    /// board thickness for a top surface part, 0 for a bottom surface part, and the seat layer's
    /// copper z (the cavity floor) for an embedded part.</summary>
    public double SeatZ(PcbPlacement placement)
    {
        if (placement.IsEmbedded)
            return ResolveSeatLayer(placement).Z;
        return placement.Side == CopperSide.Top ? Board.Thickness : 0;
    }

    /// <summary>The copper layer a placement's pads occupy: the seat layer for an embedded part,
    /// else the outer copper of its side.</summary>
    public string TargetLayerName(PcbPlacement placement) =>
        placement.IsEmbedded ? ResolveSeatLayer(placement).Name : Board.Stackup.Outer(placement.Side).Name;

    /// <summary>Resolves the copper layer an embedded placement names, or throws by name.</summary>
    private CopperLayerSpec ResolveSeatLayer(PcbPlacement placement)
    {
        string name = placement.Layer
            ?? throw new InvalidOperationException(
                $"Embedded placement '{placement.Reference}' names no seat layer.");
        foreach (var c in Board.Stackup.Coppers)
            if (c.Name == name)
                return c;
        throw new ArgumentException(
            $"Placement '{placement.Reference}' names copper layer '{name}', which the stackup does "
            + $"not contain. Its layers are: {string.Join(", ", Board.Stackup.Coppers.Select(c => c.Name))}.",
            nameof(placement));
    }

    /// <summary>The board-local rigid pose of a placement (translation + rotation about Z,
    /// seated at <see cref="SeatZ"/>). The reflection for a bottom placement is NOT here — it is on
    /// the part transform, so this stays a proper frame.</summary>
    public Frame3d PlacementPose(PcbPlacement placement)
    {
        double rot = placement.RotationDegrees * Math.PI / 180.0;
        double c = Math.Cos(rot), s = Math.Sin(rot);
        double seatZ = SeatZ(placement);
        return Frame3d.FromOrthonormal(
            new Vector3d(placement.X, placement.Y, seatZ),
            new Vector3d(c, s, 0),
            new Vector3d(-s, c, 0));
    }

    /// <summary>The frame handed to <see cref="Assembly.Add(Part, Frame3d?, string?)"/> — the
    /// board-local pose composed onto <see cref="BoardFrame"/>.</summary>
    public Frame3d OccurrenceFrame(PcbPlacement placement) => PlacementPose(placement).Then(BoardFrame);

    /// <summary>The part transform for a placement side: identity on top, a reflection across the
    /// board plane on the bottom (so the body hangs below and the footprint's through-holes keep
    /// the same world x, y). Its square is the identity — <c>Mirror(Mirror(x)) == x</c>.</summary>
    public static Matrix4d PartTransform(CopperSide side) =>
        side == CopperSide.Top ? Matrix4d.Identity : Matrix4d.CreateScale(new Vector3d(1, 1, -1));

    /// <summary>The full world transform of a placed component's body — exactly the
    /// <see cref="PartInstance.World"/> the assembly produces for it
    /// (<c>occurrenceFrame.Then(worldXY).ToMatrix() * partTransform</c>), so a body posed here is
    /// bit-identical to the assembly transform math.</summary>
    public Matrix4d WorldOf(PcbPlacement placement) =>
        OccurrenceFrame(placement).Then(Frame3d.WorldXY).ToMatrix() * PartTransform(placement.Side);

    // ---- derived copper -----------------------------------------------------

    /// <summary>Every footprint pad projected onto the board, in placement then pad order. Each
    /// pad carries its world (x, y) and the schematic pin it realises (or null if the definition
    /// has no pin of that number).</summary>
    public IReadOnlyList<PlacedPad> PlacedPads()
    {
        var pads = new List<PlacedPad>();
        foreach (var placement in _placements)
        {
            var component = Schematic.Find(placement.Reference)!;
            var footprint = component.Definition.Footprint;
            if (footprint is null)
                continue;
            var world = WorldOf(placement);
            foreach (var pad in footprint.Pads)
            {
                var w = world.TransformPoint(new Vector3d(pad.Center.X, pad.Center.Y, 0));
                var pin = component.Definition.HasPin(pad.Number)
                    ? new PinRef(component, pad.Number)
                    : (PinRef?)null;
                pads.Add(new PlacedPad(
                    placement.Reference, pad.Number, new Vector2d(w.X, w.Y),
                    pad.Kind, pad.DrillDiameter, pad.Width, pad.Height, pad.Shape, pin));
            }
        }
        return pads;
    }

    /// <summary>The placed pad regions per copper layer: an SMD pad lands on its placement side's
    /// outer copper, a through-hole pad on every layer (a plated hole spans them all).</summary>
    public IReadOnlyList<CopperLayer> CopperLayers()
    {
        var placed = PlacedPads();
        var placementByRef = _placements.ToDictionary(p => p.Reference, StringComparer.Ordinal);
        var layers = new List<CopperLayer>();
        foreach (var spec in Board.Stackup.Coppers)
        {
            var onLayer = new List<PlacedPad>();
            foreach (var pad in placed)
            {
                if (pad.Kind == PadKind.ThroughHole)
                {
                    onLayer.Add(pad);   // through-hole pads are on every copper layer
                }
                else if (placementByRef.TryGetValue(pad.Reference, out var placement)
                    && TargetLayerName(placement) == spec.Name)
                {
                    onLayer.Add(pad);   // SMD pad on its target copper (outer side, or embedded seat)
                }
            }
            layers.Add(new CopperLayer(spec.Name, spec.Z, onLayer));
        }
        return layers;
    }

    /// <summary>The placed pads realising a net's pins — the net's copper. Each pin resolves to
    /// its one placed pad (empty for pins whose components are unplaced or have no footprint).</summary>
    public IReadOnlyList<PlacedPad> PadsOfNet(Net net)
    {
        ArgumentNullException.ThrowIfNull(net);
        var byPin = PlacedPads().Where(p => p.Pin is not null).ToLookup(p => p.Pin!.Value);
        return net.Pins.SelectMany(pin => byPin[pin]).ToList();
    }

    // ---- the plate (board holes + this layout's through-hole pads) ----------

    /// <summary>The board-local (x, y) of every through hole drilled through the plate — the
    /// board's own mounting holes and vias, plus each placement's through-hole pads at their
    /// placed positions (top and bottom placements share a hole, so this is a set of xy).</summary>
    private IEnumerable<(Vector2d Center, double Diameter)> ThroughHoles()
    {
        foreach (var hole in Board.Holes)
            yield return (hole.Center, hole.Diameter);
        foreach (var placement in _placements)
        {
            var footprint = Schematic.Find(placement.Reference)!.Definition.Footprint;
            if (footprint is null)
                continue;
            var pose = PlacementPose(placement).ToMatrix();
            foreach (var pad in footprint.Pads)
                if (pad.IsDrilled)
                {
                    var p = pose.TransformPoint(new Vector3d(pad.Center.X, pad.Center.Y, 0));
                    yield return (new Vector2d(p.X, p.Y), pad.DrillDiameter);
                }
        }
    }

    /// <summary>The board plate as an exact B-Rep <see cref="Shape"/>: the outline extruded to
    /// the thickness, then every through hole drilled (the board's own holes AND this layout's
    /// through-hole pads) and every embedded component's cavity milled (an internal void for an
    /// enclosed part, a well for an open cavity).</summary>
    public Shape Plate()
    {
        var plate = PcbGeometry.BuildPlate(Board.OutlinePoints, Board.Thickness, ThroughHoles());
        foreach (var cavity in Cavities())
            plate = plate.Subtract(cavity.Tool);
        return plate;
    }

    /// <summary>The closed-form volume of <see cref="Plate"/>: outline area × thickness, less each
    /// through hole's cylinder πr² × thickness (board holes and through-hole pads) and each embedded
    /// cavity's removed volume (lateral area × depth). Assumes cavities do not overlap holes or each
    /// other (an overlap is refused at <see cref="Embed"/>).</summary>
    public double ExpectedPlateVolume() =>
        PcbGeometry.ExpectedPlateVolume(Board.OutlineArea(), Board.Thickness,
            ThroughHoles().Select(h => h.Diameter))
        - Cavities().Sum(c => c.RemovedVolume);

    // ---- the assembly -------------------------------------------------------

    /// <summary>Builds the board and its placed components as an
    /// <see cref="EngrCAD.Modeling.Assembly"/>: the drilled plate as the base part, plus one
    /// occurrence per placed component whose definition carries a 3D body (a body-less component
    /// still places its pads; it just has no 3D occurrence). Bottom-side components share a
    /// reflected part; identical components on one side share a part (N occurrences), so a
    /// <c>Bom.For</c> over the flattened instances counts them correctly.</summary>
    public Assembly ToAssembly(string? name = null)
    {
        var assembly = new Assembly(
            string.IsNullOrEmpty(name) ? (Schematic.Name.Length > 0 ? Schematic.Name : "PCB") : name);
        assembly.Add(new Part("board", Plate()), BoardFrame, "board");

        // One part per (definition, side): identical components share it, so occurrences of one
        // part is exactly the "one product, N occurrences" the BOM counts.
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
            assembly.Add(part, OccurrenceFrame(placement), placement.Reference);
        }
        return assembly;
    }

    // ---- verification -------------------------------------------------------

    /// <summary>Runs the one-declaration identity check — the geometric lift of the schematic's
    /// pin-counting identity. See <see cref="PcbLayoutCheckResult"/>.</summary>
    public PcbLayoutCheckResult Check() => PcbLayoutChecker.Check(this);

    // ---- fabrication settings (LAYOUT TRUTH: solder mask / silkscreen) -------

    /// <summary>The solder-mask settings (expansion, via policy), or null for the defaults. It is
    /// LAYOUT TRUTH — it rides in the layout file (write-only-when-stated), so a layout that states none
    /// saves byte-identically to a pre-mask one, exactly as a pour's settings do.</summary>
    public PcbMaskSettings? MaskSettings { get; private set; }

    /// <summary>The silkscreen settings (text height, pen width, what to draw, board marks), or null
    /// for the defaults. LAYOUT TRUTH — see <see cref="MaskSettings"/>.</summary>
    public PcbSilkscreenSettings? SilkscreenSettings { get; private set; }

    /// <summary>Sets the solder-mask settings (see <see cref="MaskSettings"/>); returns this layout.</summary>
    public PcbLayout WithMask(PcbMaskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        MaskSettings = settings;
        return this;
    }

    /// <summary>Sets the silkscreen settings (see <see cref="SilkscreenSettings"/>); returns this
    /// layout.</summary>
    public PcbLayout WithSilkscreen(PcbSilkscreenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        SilkscreenSettings = settings;
        return this;
    }

    // Loader entry points — validated the same way, so a saved-then-loaded setting is refused for the
    // same reasons a freshly-set one is.
    internal void SetLoadedMaskSettings(PcbMaskSettings settings)
    {
        settings.Validate();
        MaskSettings = settings;
    }

    internal void SetLoadedSilkscreenSettings(PcbSilkscreenSettings settings)
    {
        settings.Validate();
        SilkscreenSettings = settings;
    }
}
