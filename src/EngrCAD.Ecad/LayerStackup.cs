namespace EngrCAD.Ecad;

/// <summary>What one physical layer of a <see cref="LayerStackup"/> is made of.</summary>
public enum StackLayerKind
{
    /// <summary>A copper plane — a routing/DRC layer. Its own thickness rides in the stackup
    /// total, but the 2D copper model lives at its derived <see cref="CopperLayerSpec.Z"/>.</summary>
    Copper,

    /// <summary>A dielectric — the core/prepreg between coppers. It is the solid body the board
    /// plate is extruded from; it carries no copper.</summary>
    Dielectric,
}

/// <summary>One physical layer of a <see cref="LayerStackup"/>: what it is made of, a name, and a
/// positive thickness (mm). The ordered list of these, top-most first, IS the stackup.</summary>
/// <param name="Kind">Copper or dielectric.</param>
/// <param name="Name">The layer name (<c>"Top"</c>, <c>"Core"</c>, <c>"In1"</c>).</param>
/// <param name="Thickness">The layer thickness (mm), positive.</param>
public readonly record struct StackLayer(StackLayerKind Kind, string Name, double Thickness)
{
    /// <summary>A copper layer of the given name and thickness.</summary>
    public static StackLayer Copper(string name, double thickness) =>
        new(StackLayerKind.Copper, name, thickness);

    /// <summary>A dielectric layer of the given name and thickness.</summary>
    public static StackLayer Dielectric(string name, double thickness) =>
        new(StackLayerKind.Dielectric, name, thickness);
}

/// <summary>
/// The full physical build-up of a board: an ORDERED list of copper and dielectric
/// <see cref="StackLayer"/>s, top-most first, each carrying a thickness. It GENERALISES the
/// copper-only <see cref="PcbStackup"/> (which carries only the copper planes' z-heights): a
/// <see cref="LayerStackup"/> knows the dielectric between the coppers too, so
/// <see cref="TotalThickness"/> — the sum of every layer's thickness — is the board thickness the
/// plate is extruded through, and the copper planes' z-heights are DERIVED from the accumulated
/// build-up rather than stated.
///
/// <para><b>Where a copper plane sits.</b> A copper layer's reference plane is the surface at which
/// it is contacted (the same physical statement in every case, applied uniformly): the two OUTER
/// copper layers are contacted at the board's faces — the top copper's plane is the top surface
/// (z = <see cref="TotalThickness"/>), the bottom copper's is the bottom (z = 0) — while an INNER
/// copper is reached through the dielectric from either side and so sits at its own MIDPLANE. For
/// the standard copper–dielectric–…–copper builds this puts the outer coppers exactly on the board
/// faces (so an SMD part still seats on the surface) and the inner coppers between, which is the
/// oracle. The derived copper planes are exposed as a <see cref="PcbStackup"/>
/// (<see cref="CopperStackup"/>) so every stage-2..4 consumer reads a <see cref="LayerStackup"/>
/// board through exactly the seam it already reads.</para>
/// </summary>
public sealed class LayerStackup
{
    /// <summary>The physical layers, top-most (highest z) first.</summary>
    public IReadOnlyList<StackLayer> Layers { get; }

    /// <summary>The total board thickness (mm) — the sum of every layer's thickness, EXACTLY.</summary>
    public double TotalThickness { get; }

    /// <summary>The derived copper planes, top-most first, each with the z its build-up puts it at
    /// (see the type remarks). This is the copper model every stage-2..4 consumer reads.</summary>
    public IReadOnlyList<CopperLayerSpec> Coppers { get; }

    /// <summary>The z-range <c>[Low, High]</c> each physical <see cref="StackLayer"/> occupies,
    /// indexed to match <see cref="Layers"/> (top-most first). These are the SAME numbers the copper
    /// planes' z-heights derive from — accumulated bottom-up in the constructor, not recomputed — so
    /// <c>Layers[i]</c> fills exactly <c>[Extents[i].Low, Extents[i].High]</c>, consecutive layers
    /// tile <c>[0, TotalThickness]</c> with no gap (<c>Extents[i].Low == Extents[i+1].High</c>), and
    /// the two outer extremes land on the faces EXACTLY (<c>Extents[0].High == TotalThickness</c>,
    /// <c>Extents[^1].Low == 0</c>). This is the seam an exploded-view decomposition slices the plate
    /// into per-layer slabs on (<c>PcbLayout.ToExplodedAssembly</c>).</summary>
    public IReadOnlyList<(double Low, double High)> Extents { get; }

    /// <summary>The derived copper planes as a <see cref="PcbStackup"/> — the seam a
    /// <see cref="PcbBoard"/> built from this stackup carries as its <see cref="PcbBoard.Stackup"/>.</summary>
    public PcbStackup CopperStackup => new(Coppers);

    /// <summary>Builds a stackup from an ordered list of physical layers (top-most first).</summary>
    /// <exception cref="ArgumentException">A layer thickness is non-positive, or fewer than two
    /// copper layers are present (both refused by name).</exception>
    public LayerStackup(IEnumerable<StackLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Layers = [.. layers];
        if (Layers.Count == 0)
            throw new ArgumentException("A stackup needs at least one layer.", nameof(layers));

        foreach (var layer in Layers)
            if (!(layer.Thickness > 0))
                throw new ArgumentException(
                    $"Layer '{layer.Name}' has a non-positive thickness ({layer.Thickness:g6}); "
                    + "every stackup layer needs a positive thickness.", nameof(layers));

        int copperCount = Layers.Count(l => l.Kind == StackLayerKind.Copper);
        if (copperCount < 2)
            throw new ArgumentException(
                $"A stackup needs at least two copper layers (found {copperCount}) — a board is a "
                + "conductor sandwich, and the N-layer builds are copper–dielectric–…–copper.",
                nameof(layers));

        int firstCopper = -1, lastCopper = -1;
        for (int i = 0; i < Layers.Count; i++)
            if (Layers[i].Kind == StackLayerKind.Copper)
            {
                if (firstCopper < 0)
                    firstCopper = i;
                lastCopper = i;
            }

        // Accumulate the build-up from the BOTTOM face (z = 0) upward — walking the layers in
        // reverse — so the bottom copper's plane lands at EXACTLY 0 and, since the total is the same
        // bottom-up sum, the top copper's lands at EXACTLY the total. (Accumulating from the top and
        // subtracting would leave the bottom face at total − Σt, which is 0 only to round-off.) Each
        // layer i occupies [low[i], high[i]].
        var low = new double[Layers.Count];
        var high = new double[Layers.Count];
        double z0 = 0;
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            low[i] = z0;
            high[i] = z0 + Layers[i].Thickness;
            z0 = high[i];
        }
        TotalThickness = z0;   // the bottom-up sum of every layer's thickness

        var coppers = new List<CopperLayerSpec>();
        for (int i = 0; i < Layers.Count; i++)
            if (Layers[i].Kind == StackLayerKind.Copper)
            {
                // The contact surface: the exposed outer face for the two outer coppers, the midplane
                // for the inner ones. For a copper-outer build this is total / 0 / midplanes.
                double z =
                    i == firstCopper ? high[i] :    // top copper: its exposed top face (= total)
                    i == lastCopper ? low[i] :      // bottom copper: its exposed bottom face (= 0)
                    (low[i] + high[i]) / 2;         // inner copper: its own midplane
                coppers.Add(new CopperLayerSpec(Layers[i].Name, z));
            }
        Coppers = coppers;

        // The per-layer z-ranges are exactly the [low, high] pairs the copper z's came off, so they
        // are exposed rather than recomputed — a consumer slicing the plate into per-layer slabs
        // reads the SAME accumulation the copper planes did.
        var extents = new (double Low, double High)[Layers.Count];
        for (int i = 0; i < Layers.Count; i++)
            extents[i] = (low[i], high[i]);
        Extents = extents;
    }

    /// <summary>The conventional two-layer board — copper, core, copper — with the copper planes
    /// at the two faces. <paramref name="copper"/> defaults to 35 µm (1 oz).</summary>
    public static LayerStackup TwoLayer(double core, double copper = 0.035) =>
        new([
            StackLayer.Copper("Top", copper),
            StackLayer.Dielectric("Core", core),
            StackLayer.Copper("Bottom", copper),
        ]);

    /// <summary>A symmetric four-layer board: Top / prepreg / In1 / core / In2 / prepreg / Bottom,
    /// every copper of thickness <paramref name="copper"/>.</summary>
    public static LayerStackup FourLayer(double copper, double prepreg, double core) =>
        new([
            StackLayer.Copper("Top", copper),
            StackLayer.Dielectric("Prepreg1", prepreg),
            StackLayer.Copper("In1", copper),
            StackLayer.Dielectric("Core", core),
            StackLayer.Copper("In2", copper),
            StackLayer.Dielectric("Prepreg2", prepreg),
            StackLayer.Copper("Bottom", copper),
        ]);

    /// <summary>A symmetric six-layer board: six coppers (Top, In1..In4, Bottom) around three cores
    /// and two prepregs, every copper of thickness <paramref name="copper"/>.</summary>
    public static LayerStackup SixLayer(double copper, double prepreg, double core) =>
        new([
            StackLayer.Copper("Top", copper),
            StackLayer.Dielectric("Prepreg1", prepreg),
            StackLayer.Copper("In1", copper),
            StackLayer.Dielectric("Core1", core),
            StackLayer.Copper("In2", copper),
            StackLayer.Dielectric("Core2", core),
            StackLayer.Copper("In3", copper),
            StackLayer.Dielectric("Core3", core),
            StackLayer.Copper("In4", copper),
            StackLayer.Dielectric("Prepreg2", prepreg),
            StackLayer.Copper("Bottom", copper),
        ]);

    /// <summary>The top-most copper layer (highest z).</summary>
    public CopperLayerSpec Top => Coppers[0];

    /// <summary>The bottom copper layer (lowest z).</summary>
    public CopperLayerSpec Bottom => Coppers[^1];

    /// <summary>The copper layer of a given name, or null.</summary>
    public CopperLayerSpec? Copper(string name)
    {
        foreach (var c in Coppers)
            if (c.Name == name)
                return c;
        return null;
    }
}
