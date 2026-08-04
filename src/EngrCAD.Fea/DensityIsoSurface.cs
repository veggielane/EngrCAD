using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// Turns a nodal density field on a tetrahedral mesh into a closed surface at a stated
/// threshold — marching TETRAHEDRA over the interior plus the boundary facets clipped by the
/// same level set.
///
/// <para><b>Why not <c>Sdf</c> + Surface Nets, which is this repository's usual answer to
/// "polygonise a field".</b> Three reasons, and the first is the one with teeth.
/// <list type="number">
/// <item><b><c>SurfaceNets.Polygonize</c> culls blocks using the 1-LIPSCHITZ property</b> —
/// a block whose centre reads <c>|d| &gt; R</c> provably contains no surface — and a density
/// field is not 1-Lipschitz and has no useful bound (it goes from 0 to 1 across one element,
/// whatever an element's size happens to be). The cull would drop surface silently, which is
/// the one failure mode a completeness argument exists to prevent.</item>
/// <item><b>The field is DEFINED on this mesh</b>, so marching its own tetrahedra is exact
/// for the piecewise-linear nodal field and introduces no second discretisation. Sampling
/// onto a regular grid would add a resolution nobody asked about, and the answer would depend
/// on it.</item>
/// <item><b>Layering</b>: <c>EngrCAD.Fea</c> references <c>EngrCAD.Core</c> and
/// <c>EngrCAD.Mesh</c> and nothing depends on it. An <c>Sdf</c> lives in
/// <c>EngrCAD.Implicit</c> and Surface Nets in <c>EngrCAD.Interop</c>; reaching either would
/// invert the dependency that keeps this project a leaf. A <c>HalfEdgeMesh</c> is the type
/// both layers already share, and <c>Shape.From(mesh)</c> takes it straight into the
/// modelling API.</item>
/// </list></para>
///
/// <para><b>A crossing is computed from the LOWER-INDEXED end of its edge</b>, which is the
/// weld rule the exact mesh boolean and <c>MeshIsoCurves</c> already follow: every tetrahedron
/// containing an edge then produces bit-identical coordinates for the crossing on it, so the
/// surface assembles by INDEX and no tolerance enters. A crossing landing exactly on a node
/// (a nodal density exactly at the threshold) is interned as that NODE rather than as the
/// edge, or the interior surface and the boundary cap would place two vertices at one
/// position — which is what a crack is.</para>
///
/// <para><b>Orientation is read off the field, not from a case table.</b> The level set of a
/// linear function inside a tetrahedron is exactly planar with normal <c>grad g</c>, and the
/// solid is <c>g &gt; 0</c>, so the outward normal is <c>-grad g</c> and each emitted triangle
/// is wound to agree with it. Sixteen sign cases would each need their winding derived and
/// each be a chance to get one wrong; one dot product settles all of them.</para>
/// </summary>
internal static class DensityIsoSurface
{
    /// <summary>
    /// The nodal density field: each node takes the VOLUME-WEIGHTED mean of the elements
    /// meeting at it — the same rule <see cref="NodalAveraging.VolumeWeighted"/> states for
    /// stress, and for the same reason (a large element carries more of the domain, so it
    /// should carry more of the average).
    /// </summary>
    public static double[] NodalDensity(
        AnalysisMesh mesh, IReadOnlyList<double> elementDensity, IReadOnlyList<double> volumes)
    {
        var accumulated = new double[mesh.NodeCount];
        var weights = new double[mesh.NodeCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double w = volumes[e];
            if (!(w > 0))
                continue;
            foreach (int node in mesh.Element(e))
            {
                accumulated[node] += w * elementDensity[e];
                weights[node] += w;
            }
        }
        for (int v = 0; v < accumulated.Length; v++)
        {
            if (weights[v] > 0)
                accumulated[v] /= weights[v];
        }
        return accumulated;
    }

    /// <summary>
    /// The closed surface bounding <c>{ nodal density &gt;= threshold }</c>.
    /// </summary>
    /// <exception cref="FeaException">The level set is empty, is the whole body with no
    /// boundary, or assembles into a non-manifold surface (which a threshold landing exactly
    /// on a nodal value can produce).</exception>
    public static HalfEdgeMesh Extract(
        AnalysisMesh mesh, IReadOnlyList<double> nodalDensity, double threshold)
    {
        int nodes = mesh.NodeCount;
        var g = new double[nodes];
        for (int v = 0; v < nodes; v++)
            g[v] = nodalDensity[v] - threshold;

        var positions = new List<Vector3d>();
        // One key space for both vertex kinds: an ORIGINAL node is (i, i), a crossing on an
        // edge is (min, max) with min < max. They cannot collide, and both are exact.
        var index = new Dictionary<(int, int), int>();
        var corners = new List<int>();

        int VertexOfNode(int node)
        {
            var key = (node, node);
            if (index.TryGetValue(key, out int found))
                return found;
            int made = positions.Count;
            positions.Add(mesh.Position(node));
            index[key] = made;
            return made;
        }

        int VertexOnEdge(int a, int b)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            // Derived from the LOWER index, so every element containing this edge computes
            // the same bits. Exact-zero and exact-one land on a node, which is interned as
            // the node rather than as a second vertex at that position.
            double gl = g[lo], gh = g[hi];
            double t = gl / (gl - gh);
            if (t <= 0)
                return VertexOfNode(lo);
            if (t >= 1)
                return VertexOfNode(hi);
            var key = (lo, hi);
            if (index.TryGetValue(key, out int found))
                return found;
            var pl = mesh.Position(lo);
            int made = positions.Count;
            positions.Add(pl + t * (mesh.Position(hi) - pl));
            index[key] = made;
            return made;
        }

        void Emit(int a, int b, int c, Vector3d outward)
        {
            if (a == b || b == c || c == a)
                return;
            var normal = (positions[b] - positions[a]).Cross(positions[c] - positions[a]);
            // The SIGN of a dot of two unnormalized vectors — no epsilon, and a facet whose
            // normal is exactly zero (a degenerate crossing triple) is dropped rather than
            // wound by round-off.
            double agree = normal.Dot(outward);
            if (agree == 0)
                return;
            corners.Add(a);
            if (agree > 0)
            {
                corners.Add(b);
                corners.Add(c);
            }
            else
            {
                corners.Add(c);
                corners.Add(b);
            }
        }

        Span<int> inside = stackalloc int[4];
        Span<int> outside = stackalloc int[4];
        Span<Vector3d> tetPositions = stackalloc Vector3d[4];
        Span<Vector3d> gradients = stackalloc Vector3d[10];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var element = mesh.Element(e);
            int inCount = 0, outCount = 0;
            for (int i = 0; i < 4; i++)
            {
                int node = element[i];
                if (g[node] > 0)
                    inside[inCount++] = node;
                else
                    outside[outCount++] = node;
            }
            if (inCount == 0 || inCount == 4)
                continue;

            for (int i = 0; i < 4; i++)
                tetPositions[i] = mesh.Position(element[i]);
            // grad g from the element's own CORNER shape functions, which is the exact
            // gradient of the linear field the crossings were computed from. A quadratic
            // element's mid-edge nodes take no part: the field this extracts is the one the
            // crossings interpolate, and that is linear on the corner tetrahedron.
            if (!TetElement.ShapeGradients(
                    ElementOrder.Linear, tetPositions, 0.25, 0.25, 0.25, gradients, out _))
                continue;
            var grad = Vector3d.Zero;
            for (int i = 0; i < 4; i++)
                grad += g[element[i]] * gradients[i];
            var outward = -grad;
            if (outward == Vector3d.Zero)
                continue;

            if (inCount == 1)
            {
                int a = inside[0];
                Emit(VertexOnEdge(a, outside[0]), VertexOnEdge(a, outside[1]),
                    VertexOnEdge(a, outside[2]), outward);
            }
            else if (inCount == 3)
            {
                int d = outside[0];
                Emit(VertexOnEdge(d, inside[0]), VertexOnEdge(d, inside[1]),
                    VertexOnEdge(d, inside[2]), outward);
            }
            else
            {
                int p = VertexOnEdge(inside[0], outside[0]);
                int q = VertexOnEdge(inside[0], outside[1]);
                int r = VertexOnEdge(inside[1], outside[1]);
                int s = VertexOnEdge(inside[1], outside[0]);
                Emit(p, q, r, outward);
                Emit(p, r, s, outward);
            }
        }

        // The CAPS: wherever material reaches the body's own boundary the level set is open
        // there, and the missing lid is the part of the boundary facet lying inside the level
        // set. Its crossings are on the SAME edges the interior march used, so they intern to
        // the same vertices and the lid welds by index.
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            var facet = mesh.Facet(f);
            int a = facet[0], b = facet[1], c = facet[2];
            bool ia = g[a] > 0, ib = g[b] > 0, ic = g[c] > 0;
            int count = (ia ? 1 : 0) + (ib ? 1 : 0) + (ic ? 1 : 0);
            if (count == 0)
                continue;
            // The facet's own winding is outward, so the cap keeps it verbatim rather than
            // being re-derived: the boundary of the extracted solid coincides with the body's
            // boundary here, and its outward direction is the body's.
            var normal = (mesh.Position(b) - mesh.Position(a))
                .Cross(mesh.Position(c) - mesh.Position(a));
            if (count == 3)
            {
                Emit(VertexOfNode(a), VertexOfNode(b), VertexOfNode(c), normal);
                continue;
            }
            if (count == 1)
            {
                (int keep, int drop0, int drop1) = ia ? (a, b, c) : ib ? (b, c, a) : (c, a, b);
                Emit(VertexOfNode(keep), VertexOnEdge(keep, drop0), VertexOnEdge(keep, drop1),
                    normal);
                continue;
            }
            {
                (int drop, int keep0, int keep1) = !ia ? (a, b, c) : !ib ? (b, c, a) : (c, a, b);
                int p = VertexOfNode(keep0);
                int q = VertexOfNode(keep1);
                int r = VertexOnEdge(keep1, drop);
                int s = VertexOnEdge(keep0, drop);
                Emit(p, q, r, normal);
                Emit(p, r, s, normal);
            }
        }

        if (corners.Count == 0)
            throw new FeaException(
                $"No material survives the threshold {threshold:G6}: the extracted surface is "
                + "empty. The threshold is a stated parameter of the extraction, not a "
                + "property of the answer — try a lower one, or read "
                + $"{nameof(TopologyResult)}.{nameof(TopologyResult.Discreteness)} to see "
                + "whether the design is still too grey to threshold at all.");

        try
        {
            return HalfEdgeMesh.Build(positions, System.Runtime.InteropServices
                .CollectionsMarshal.AsSpan(corners), 3);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new FeaException(
                $"The level set at {threshold:G6} does not bound a two-manifold solid: "
                + ex.Message
                + " That happens when the threshold lands exactly on a nodal density, which "
                + "pinches the surface at that node; move it by any amount.", ex);
        }
    }
}
