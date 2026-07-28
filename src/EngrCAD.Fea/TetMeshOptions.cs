using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// Controls for <see cref="TetMesher"/>. Every default is chosen so that a bare
/// <c>TetMesher.Mesh(surface)</c> produces a boundary-conforming mesh with no quality
/// refinement — the cheapest thing that is still correct — and each knob turns on one
/// clearly named extra.
/// </summary>
public sealed record TetMeshOptions
{
    /// <summary>
    /// Quality target for Delaunay refinement: the largest acceptable ratio of a
    /// tetrahedron's circumradius to its shortest edge. Smaller is better quality and more
    /// elements. Shewchuk's analysis guarantees termination for values above roughly 2.0,
    /// which is why that is the default; below it, refinement may fail to converge and the
    /// mesher will say so rather than loop.
    /// </summary>
    public double RadiusEdgeRatio { get; init; } = 2.0;

    /// <summary>
    /// When true (the default is false), refine the mesh for element quality after boundary
    /// recovery. Off by default because the boundary-conforming mesh is already usable and
    /// refinement multiplies the element count.
    /// </summary>
    public bool RefineQuality { get; init; }

    /// <summary>
    /// Desired element size as a function of position — the sizing field. A tetrahedron
    /// whose circumradius exceeds half the field's value at its circumcentre is refined.
    /// An <c>Sdf</c> composes naturally here (<c>p =&gt; baseSize + gradation * |sdf.Evaluate(p)|</c>
    /// grades away from a surface). Null means no size target; quality is then governed by
    /// <see cref="RadiusEdgeRatio"/> alone.
    /// </summary>
    public Func<Vector3d, double>? SizingField { get; init; }

    /// <summary>
    /// Uniform upper bound on element size, as a target edge length. Combined with
    /// <see cref="SizingField"/> by taking the smaller of the two.
    /// </summary>
    public double? MaxElementSize { get; init; }

    /// <summary>
    /// Hard cap on inserted Steiner points, over boundary recovery and quality refinement
    /// together. Exceeding it is an error naming the phase, never a silent truncation:
    /// a half-refined mesh is a mesh whose quality report is a lie.
    /// </summary>
    public int MaxSteinerPoints { get; init; } = 500_000;

    /// <summary>
    /// Maximum boundary-recovery rounds before the mesher gives up and names an
    /// unrecoverable facet.
    /// </summary>
    public int MaxRecoveryRounds { get; init; } = 12;

    /// <summary>
    /// Per-input-triangle tags carried onto the boundary facets that come from them (see
    /// <see cref="TetFacet.SourceTriangle"/>). Supply B-Rep face ids here to drive boundary
    /// conditions; leave null to use raw triangle indices.
    /// </summary>
    public IReadOnlyList<int>? FacetTags { get; init; }
}
