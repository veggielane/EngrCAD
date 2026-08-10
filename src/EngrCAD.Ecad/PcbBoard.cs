using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>Which face of a board a component is placed on, or a copper layer sits on.</summary>
public enum CopperSide
{
    /// <summary>The top face (highest z; the board occupies z ∈ [0, thickness]).</summary>
    Top,

    /// <summary>The bottom face (z = 0).</summary>
    Bottom,
}

/// <summary>One copper layer of a <see cref="PcbStackup"/>: a name and the z-height it sits
/// at. Copper is a 2D model (the placed pad regions the routing/DRC stages consume); its own
/// thickness is not modelled, so the height is where its plane lies.</summary>
/// <param name="Name">The layer name (<c>"Top"</c>, <c>"Bottom"</c>, <c>"In1"</c>).</param>
/// <param name="Z">The z-height of the layer's plane (mm).</param>
public readonly record struct CopperLayerSpec(string Name, double Z);

/// <summary>
/// A board's copper stackup — an ordered list of <see cref="CopperLayerSpec"/>s (top-most
/// first). The default is a two-layer board (top copper at <c>thickness</c>, bottom at 0);
/// N-layer boards add inner copper at intermediate z. The dielectric between layers is the
/// board plate itself, so the stackup carries only the copper planes.
/// </summary>
public sealed class PcbStackup
{
    /// <summary>The copper layers, top-most (highest z) first.</summary>
    public IReadOnlyList<CopperLayerSpec> Coppers { get; }

    /// <summary>Builds a stackup from explicit copper layers (order preserved; they are also
    /// sorted top-most first for <see cref="Top"/>/<see cref="Bottom"/>).</summary>
    public PcbStackup(IEnumerable<CopperLayerSpec> coppers)
    {
        ArgumentNullException.ThrowIfNull(coppers);
        Coppers = [.. coppers];
        if (Coppers.Count < 2)
            throw new ArgumentException("A stackup needs at least two copper layers.", nameof(coppers));
    }

    /// <summary>The conventional two-layer board: <c>"Top"</c> copper at
    /// <paramref name="thickness"/>, <c>"Bottom"</c> at 0.</summary>
    public static PcbStackup TwoLayer(double thickness) =>
        new([new CopperLayerSpec("Top", thickness), new CopperLayerSpec("Bottom", 0)]);

    /// <summary>An N-layer board: top and bottom copper plus inner layers at the stated
    /// z-heights (each strictly between 0 and <paramref name="thickness"/>).</summary>
    public static PcbStackup Layers(double thickness, params (string Name, double Z)[] inner)
    {
        var coppers = new List<CopperLayerSpec> { new("Top", thickness) };
        foreach (var (name, z) in inner)
        {
            if (z <= 0 || z >= thickness)
                throw new ArgumentException(
                    $"Inner copper '{name}' at z {z:g6} must lie strictly between 0 and the "
                    + $"thickness {thickness:g6}.", nameof(inner));
            coppers.Add(new CopperLayerSpec(name, z));
        }
        coppers.Add(new CopperLayerSpec("Bottom", 0));
        return new PcbStackup(coppers.OrderByDescending(c => c.Z));
    }

    /// <summary>The top-most copper layer (highest z).</summary>
    public CopperLayerSpec Top => Coppers.MaxBy(c => c.Z);

    /// <summary>The bottom copper layer (lowest z).</summary>
    public CopperLayerSpec Bottom => Coppers.MinBy(c => c.Z);

    /// <summary>The outer copper layer of a placement <paramref name="side"/> (where its SMD
    /// pads land).</summary>
    public CopperLayerSpec Outer(CopperSide side) => side == CopperSide.Top ? Top : Bottom;
}

/// <summary>What a <see cref="BoardHole"/> is for.</summary>
public enum BoardHoleKind
{
    /// <summary>A mounting hole (a bolt through the board to a standoff/chassis).</summary>
    Mounting,

    /// <summary>A via — a plated hole connecting copper layers.</summary>
    Via,
}

/// <summary>A drilled hole in the board itself (a mounting hole or a via), distinct from a
/// footprint's through-hole pads which the layout adds per placement.</summary>
/// <param name="Center">The hole centre in board coordinates (mm).</param>
/// <param name="Diameter">The finished hole diameter (mm).</param>
/// <param name="Kind">Mounting hole or via.</param>
public readonly record struct BoardHole(Vector2d Center, double Diameter, BoardHoleKind Kind);

/// <summary>What a <see cref="KeepOut"/> forbids.</summary>
public enum KeepOutKind
{
    /// <summary>Components may not be placed here.</summary>
    Placement,

    /// <summary>Copper may not be routed here.</summary>
    Route,

    /// <summary>Vias may not be drilled here.</summary>
    Via,
}

/// <summary>A polygonal keep-out region (carried in from IDF, or declared): a place a
/// via/hole or a component must avoid. Stage-2 checks via/hole centres against
/// <see cref="KeepOutKind.Via"/>/<see cref="KeepOutKind.Route"/> keep-outs; copper DRC over
/// them is a later stage.</summary>
public sealed class KeepOut
{
    /// <summary>Builds a keep-out from a closed polygon (no repeated closing point needed).</summary>
    public KeepOut(string name, KeepOutKind kind, IEnumerable<Vector2d> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        Name = name ?? "";
        Kind = kind;
        Polygon = PcbGeometry.CanonicalLoop(polygon);
        if (Polygon.Count < 3)
            throw new ArgumentException("A keep-out needs at least 3 polygon points.", nameof(polygon));
    }

    /// <summary>The keep-out's name (may be empty).</summary>
    public string Name { get; }

    /// <summary>What the region forbids.</summary>
    public KeepOutKind Kind { get; }

    /// <summary>The keep-out polygon (no repeated closing point).</summary>
    public IReadOnlyList<Vector2d> Polygon { get; }

    /// <summary>Whether a point lies inside the keep-out.</summary>
    public bool Contains(Vector2d point) => PcbGeometry.PolygonContains(Polygon, point);
}

/// <summary>
/// A printed-circuit board: a closed polygon <b>outline</b>, a <b>thickness</b>, a copper
/// <b>stackup</b>, and its own <b>holes</b> (mounting holes and vias) and <b>keep-outs</b>.
/// The board occupies z ∈ [0, <see cref="Thickness"/>]; the top copper sits at the thickness
/// and the bottom at 0.
///
/// <para><b>The plate is built with the existing <see cref="Shape"/> API</b>: the outline is
/// extruded and the holes are drilled, so it is an exact B-Rep with a closed-form volume
/// (<see cref="ExpectedPlateVolume"/> = outline area × thickness − Σ πr² × thickness), which is
/// the oracle the tests hold it against. The outline is stored as a polygon (the common board
/// shape, and exactly what IDF carries) so it round-trips exactly in the layout file.</para>
/// </summary>
public sealed class PcbBoard
{
    /// <summary>Builds a board from a polygon outline, thickness and stackup.</summary>
    /// <param name="outline">The board outline, a closed polygon (no repeated closing point).</param>
    /// <param name="thickness">The board thickness (mm), positive.</param>
    /// <param name="stackup">The copper stackup; null defaults to a two-layer board.</param>
    /// <param name="holes">The board's mounting holes and vias.</param>
    /// <param name="keepOuts">The board's keep-out regions.</param>
    public PcbBoard(
        IEnumerable<Vector2d> outline,
        double thickness,
        PcbStackup? stackup = null,
        IEnumerable<BoardHole>? holes = null,
        IEnumerable<KeepOut>? keepOuts = null)
    {
        ArgumentNullException.ThrowIfNull(outline);
        if (thickness <= 0)
            throw new ArgumentException($"A board needs a positive thickness (got {thickness:g6}).",
                nameof(thickness));
        OutlinePoints = PcbGeometry.CanonicalLoop(outline);
        if (OutlinePoints.Count < 3)
            throw new ArgumentException("A board outline needs at least 3 points.", nameof(outline));
        Thickness = thickness;
        Stackup = stackup ?? PcbStackup.TwoLayer(thickness);
        Holes = holes is null ? [] : [.. holes];
        KeepOuts = keepOuts is null ? [] : [.. keepOuts];
    }

    /// <summary>Builds a board from a full physical <see cref="LayerStackup"/> — the copper AND
    /// dielectric build-up. The thickness is the stackup's <see cref="LayerStackup.TotalThickness"/>
    /// (extruded through as the solid body) and the copper planes are the stackup's derived
    /// <see cref="LayerStackup.CopperStackup"/>, so an inner-layer component seats at its layer's z.</summary>
    /// <param name="outline">The board outline, a closed polygon (no repeated closing point).</param>
    /// <param name="layers">The physical stackup (copper + dielectric layers, thicknesses).</param>
    /// <param name="holes">The board's mounting holes and vias.</param>
    /// <param name="keepOuts">The board's keep-out regions.</param>
    public PcbBoard(
        IEnumerable<Vector2d> outline,
        LayerStackup layers,
        IEnumerable<BoardHole>? holes = null,
        IEnumerable<KeepOut>? keepOuts = null)
        : this(outline, (layers ?? throw new ArgumentNullException(nameof(layers))).TotalThickness,
            layers.CopperStackup, holes, keepOuts)
    {
        LayerStackup = layers;
    }

    /// <summary>The board outline, a closed polygon (no repeated closing point).</summary>
    public IReadOnlyList<Vector2d> OutlinePoints { get; }

    /// <summary>The board thickness (mm). The board occupies z ∈ [0, thickness].</summary>
    public double Thickness { get; }

    /// <summary>The copper stackup (the derived copper planes every stage-2..4 consumer reads).</summary>
    public PcbStackup Stackup { get; }

    /// <summary>The full physical stackup (copper + dielectric build-up), or null when the board was
    /// built the copper-only way (a plain thickness + <see cref="PcbStackup"/>). When present its
    /// <see cref="LayerStackup.TotalThickness"/> equals <see cref="Thickness"/> and its
    /// <see cref="LayerStackup.CopperStackup"/> equals <see cref="Stackup"/> by construction.</summary>
    public LayerStackup? LayerStackup { get; private set; }

    /// <summary>The board's own holes (mounting holes and vias).</summary>
    public IReadOnlyList<BoardHole> Holes { get; }

    /// <summary>The board's keep-out regions.</summary>
    public IReadOnlyList<KeepOut> KeepOuts { get; }

    /// <summary>A rectangular board centred at the origin.</summary>
    public static PcbBoard Rectangle(double width, double height, double thickness,
        PcbStackup? stackup = null) =>
        new(
            [
                new Vector2d(-width / 2, -height / 2), new Vector2d(width / 2, -height / 2),
                new Vector2d(width / 2, height / 2), new Vector2d(-width / 2, height / 2),
            ],
            thickness, stackup);

    /// <summary>A rectangular board centred at the origin, built from a full physical stackup.</summary>
    public static PcbBoard Rectangle(double width, double height, LayerStackup layers) =>
        new(
            [
                new Vector2d(-width / 2, -height / 2), new Vector2d(width / 2, -height / 2),
                new Vector2d(width / 2, height / 2), new Vector2d(-width / 2, height / 2),
            ],
            layers);

    /// <summary>The outline as a <see cref="Sketch"/> (the extrusion profile).</summary>
    public Sketch Outline() => Sketch.Polygon([.. OutlinePoints]);

    /// <summary>The outline's area (mm²), always positive.</summary>
    public double OutlineArea() => Math.Abs(Outline().Area());

    /// <summary>Whether a point lies inside the board outline.</summary>
    public bool ContainsInOutline(Vector2d point) =>
        PcbGeometry.PolygonContains(OutlinePoints, point);

    /// <summary>The board plate as an exact B-Rep <see cref="Shape"/>: the outline extruded to
    /// the thickness, then the board's OWN holes drilled through. (The layout adds the
    /// footprints' through-hole pads on top of this — see <c>PcbLayout.Plate</c>.)</summary>
    public Shape Plate() =>
        PcbGeometry.BuildPlate(OutlinePoints, Thickness,
            Holes.Select(h => (h.Center, h.Diameter)));

    /// <summary>The closed-form volume of the plate <see cref="Plate"/> builds: outline area ×
    /// thickness, less each board hole's cylinder (πr² × thickness). The tessellated volume
    /// approaches this from below by the inscribed-polygon chord deficit of each round hole.</summary>
    public double ExpectedPlateVolume() =>
        PcbGeometry.ExpectedPlateVolume(OutlineArea(), Thickness,
            Holes.Select(h => h.Diameter));
}
