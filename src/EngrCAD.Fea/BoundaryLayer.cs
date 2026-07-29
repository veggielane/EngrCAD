namespace EngrCAD.Fea;

/// <summary>
/// Controls for the anisotropic boundary layer: a graded stack of elements marched INWARD
/// from a selected set of wall facets, with the isotropic tetrahedralization filling
/// whatever is left.
///
/// <para><b>The three controls are the standard ones</b> — first-layer thickness, growth
/// ratio and layer count — because they are what a boundary-layer mesh is specified by
/// everywhere else: the first thickness is chosen from a target y+, the ratio keeps the
/// jump to the isotropic fill smooth, and the count sets how far the resolved region
/// reaches. The total stack height is
/// <c>t1 * (r^n - 1) / (r - 1)</c> (or <c>t1 * n</c> at <c>r == 1</c>), reported by
/// <see cref="TotalThickness"/> so it can be checked against the part before meshing.</para>
///
/// <para><b>Walls are named the way boundary conditions are named.</b>
/// <see cref="Wall"/> is a <see cref="Facets"/> predicate over the INPUT surface's
/// triangles, so <c>Facets.Tag(id)</c> selects the same face for the layer that it selects
/// for a no-slip condition later; the geometric selectors work here too. The
/// <see cref="FacetRef.Index"/> a predicate sees is the input triangle index and the
/// <see cref="FacetRef.Tag"/> its <c>TetMeshOptions.FacetTags</c> entry.</para>
/// </summary>
public sealed record BoundaryLayerSpec
{
    /// <summary>
    /// Which input triangles the layer grows from. Required; a spec selecting nothing is
    /// refused rather than silently meshed without a layer.
    /// </summary>
    public required Func<FacetRef, bool> Wall { get; init; }

    /// <summary>Thickness of the first (wall-adjacent) layer, in model units.</summary>
    public required double FirstLayerThickness { get; init; }

    /// <summary>Number of layers. Must be at least one.</summary>
    public required int LayerCount { get; init; }

    /// <summary>
    /// Ratio of each layer's thickness to the one before it. 1.0 gives a uniform stack;
    /// the usual engineering range is 1.1 to 1.3. Values below 1 (a shrinking stack) are
    /// legal but unusual, and are not refused.
    /// </summary>
    public double GrowthRatio { get; init; } = 1.2;

    /// <summary>
    /// Extra Laplacian smoothing passes applied to the marching directions over the wall's
    /// own vertex graph, after the angle-weighted per-node average. Zero (the default) is
    /// right for CAD tessellations, where the per-node average is already smooth; raise it
    /// for noisy scanned surfaces. Junction constraints are re-applied after every pass, so
    /// smoothing can never push a node off the surface it has to slide along.
    /// </summary>
    public int DirectionSmoothingPasses { get; init; }

    /// <summary>
    /// How far a marching direction may be turned by its junction constraints before the
    /// layer is refused: the cosine of the largest acceptable turn, measured between the
    /// smoothed normal and its projection into the planes the node must stay in. The
    /// default of 0.5 (60 degrees) refuses a wall meeting its neighbour at a sharp angle,
    /// where the layer would have to fold to stay on the surface.
    /// </summary>
    public double MinimumConstraintCosine { get; init; } = 0.5;

    /// <summary>
    /// Total stack height for this spec: the sum of the geometric series of layer
    /// thicknesses.
    /// </summary>
    public double TotalThickness
    {
        get
        {
            double t = FirstLayerThickness;
            // The closed form loses accuracy as the ratio approaches 1, and the recurrence
            // is what the mesher itself marches with, so summing it is the value that
            // actually describes the mesh rather than a second opinion about it.
            double total = 0;
            for (int i = 0; i < LayerCount; i++)
            {
                total += t;
                t *= GrowthRatio;
            }
            return total;
        }
    }

    /// <summary>The thickness of each layer, wall-adjacent first.</summary>
    public IReadOnlyList<double> Thicknesses
    {
        get
        {
            var list = new double[Math.Max(LayerCount, 0)];
            double t = FirstLayerThickness;
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = t;
                t *= GrowthRatio;
            }
            return list;
        }
    }
}

/// <summary>
/// What the boundary-layer stage did — a value, following the <see cref="TetMeshDiagnostics"/>
/// convention. <see cref="ElementCount"/> elements sit at the FRONT of the finished
/// <see cref="TetMesh"/>, so <c>[0, ElementCount)</c> names them without a second lookup.
/// </summary>
/// <param name="WallTriangles">Input triangles the layer grew from.</param>
/// <param name="Layers">Layers marched.</param>
/// <param name="ElementCount">Tetrahedra in the stack (three per prism per layer).</param>
/// <param name="Nodes">Nodes the march created (levels 1..n; level 0 is the input surface).</param>
/// <param name="JunctionNodes">Wall nodes constrained to slide along a non-wall face.</param>
/// <param name="RetriangulatedPatches">Non-wall planar patches rebuilt around a trimmed rim.</param>
/// <param name="TotalThickness">Height of the stack, measured.</param>
/// <param name="FirstLayerThickness">First layer's thickness, measured.</param>
/// <param name="MeasuredGrowthRatio">Largest deviation of a measured layer ratio from the requested one.</param>
/// <param name="StackVolume">Volume of the stack's elements.</param>
/// <param name="MinMarchClearance">
/// Smallest ratio of a marched node's distance from the WALL to the distance it marched — how
/// much room the stack has left. It is reported and deliberately never refused on: an ordinary
/// convex box corner reads 0.577 (the column marches along the body diagonal, so its
/// perpendicular stand-off from each face is the cosine of half the corner angle), and a
/// threshold tight enough to catch a collision would refuse those. The refusal is the exact
/// self-intersection test on the surface the stack leaves behind.
/// </param>
public readonly record struct BoundaryLayerReport(
    int WallTriangles,
    int Layers,
    int ElementCount,
    int Nodes,
    int JunctionNodes,
    int RetriangulatedPatches,
    double TotalThickness,
    double FirstLayerThickness,
    double MeasuredGrowthRatio,
    double StackVolume,
    double MinMarchClearance)
{
    /// <summary>A one-line human summary.</summary>
    public override string ToString() =>
        $"{ElementCount} layer elements in {Layers} layer(s) over {WallTriangles} wall triangles; " +
        $"first {FirstLayerThickness:G4}, total {TotalThickness:G4}, ratio {MeasuredGrowthRatio:G6}; " +
        $"{Nodes} marched nodes ({JunctionNodes} sliding), clearance {MinMarchClearance:F3}.";
}
