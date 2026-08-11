using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// A MULTI-SHELL MID/LDS board — a moulded housing carrying copper on its OUTER wall AND on one or more
/// INNER moulded shells, connected by through-shell vias. It is the multi-layer analogue of a
/// <see cref="MidBoard"/>, and a real LDS part carries exactly this: an outer skin routed with one
/// circuit and an inner shell routed with another, stitched by plated holes through the dielectric.
///
/// <para><b>The inner shell is the outer mesh, offset.</b> Each inner shell is the outer mesh with EVERY
/// VERTEX offset inward by a dielectric thickness along its ANGLE-WEIGHTED vertex normal (the
/// boundary-layer / <c>MeshSdf</c> pseudonormal convention — NEVER a raw face normal, which tears a
/// shared vertex). This keeps the SAME MESH TOPOLOGY, which is the load-bearing decision: a surface point
/// on the outer shell (a face + barycentric weights) has a CORRESPONDING point on every inner shell (the
/// SAME face + weights), so a through-shell <see cref="SurfaceVia"/> is exactly "tie the outer point to
/// its corresponding inner point".</para>
///
/// <para><b>Each shell is its OWN <see cref="MidBoard"/>, with its own exp-map machinery</b>, because an
/// inner shell's intrinsic geometry differs from the outer's (offsetting a curved surface changes its
/// geodesic distances). Pads, traces and seated components are placed on a shell by placing on its
/// <see cref="MidBoard"/> — <c>stack.Shell(k)</c> / <c>stack.Outer</c> / <c>stack.Inner</c> — so the
/// existing single-surface placement / routing / DRC runs PER SHELL unchanged, and the per-shell DRC
/// measures geodesic clearances through THAT shell's charts, never the outer's.</para>
///
/// <para><b>Connectivity and the DRC span shells.</b> <see cref="Connectivity"/> reconstructs each
/// shell's copper with the existing per-surface touch rule, then a net whose copper lies on two shells is
/// ONE connected net IFF a via of that net ties them (the <c>PcbConnectivity</c> cross-layer rule, lifted
/// to surfaces); no via ⇒ the two shells' same-named copper are a cross-shell ratsnest. <see cref="Check"/>
/// runs each shell's same-shell clearance / width DRC (a via's clearance to other-net copper on both
/// shells it touches falls out of the per-shell DRC, since a via pad IS copper on that shell) PLUS the
/// inter-shell via-to-via spacing rule.</para>
///
/// <para><b>The developable oracle is preserved and made exact.</b> On a DEVELOPABLE surface (a cylinder,
/// a cone, a flat patch) the inward normal offset is an isometry: a cylinder's inner shell is a
/// concentric cylinder of radius <c>r − t</c> to round-off. A single-shell stack (no vias) reduces to a
/// plain <see cref="MidBoard"/> and its DRC is BIT-IDENTICAL to <see cref="Mid3dDrc.Check(MidBoard,DrcRuleSet?)"/>.</para>
///
/// <para><b>Scope, v1.</b> A via spans every shell (a plated barrel through the whole stack) and does not
/// cut a physical hole through the plate geometry (vias are the copper / connectivity / DRC model, as on
/// the flat board). Filed follow-ons: cross-shell AUTO-ROUTING (choosing which shell a net rides and
/// placing the vias — v1 routes per shell and places explicit vias), a conformal solder mask / pour on a
/// shell (refused for the distortion reason copper pours already refuse curved walls), and per-via
/// partial spans of a &gt; 2 shell stack.</para>
/// </summary>
public sealed class MidStack
{
    private readonly MidBoard[] _shells;
    private readonly string[] _shellNames;
    private readonly Vector3d[] _basePositions;   // shell-0 surface positions, in the base vertex order
    private readonly Vector3d[] _normals;         // angle-weighted UNIT vertex normals, same order
    private readonly double[] _offsets;           // cumulative dielectric offset per shell (offset[0] = 0)
    private readonly List<SurfaceVia> _vias = [];

    private MidStack(MidBoard[] shells, string[] shellNames, Vector3d[] basePositions, Vector3d[] normals, double[] offsets)
    {
        _shells = shells;
        _shellNames = shellNames;
        _basePositions = basePositions;
        _normals = normals;
        _offsets = offsets;
    }

    // ---- construction --------------------------------------------------------

    /// <summary>
    /// Builds a two-shell MID board: the given moulded OUTER mesh plus an INNER shell offset inward by
    /// <paramref name="dielectricThickness"/> (mm). The two shells share topology, so a
    /// <see cref="AddVia"/> ties an outer point to its corresponding inner point.
    /// </summary>
    /// <param name="outer">The moulded outer routing surface (a triangle mesh, outward-oriented).</param>
    /// <param name="dielectricThickness">The dielectric wall thickness between the shells (mm, &gt; 0).</param>
    /// <param name="schematic">The schematic that names the nets (shared by every shell), or null when
    /// pads carry explicit net names.</param>
    /// <exception cref="MidShellException">The inward offset self-intersects (a concave region where the
    /// thickness exceeds the local curvature radius) — refused, naming the region and thickness.</exception>
    public static MidStack TwoShell(HalfEdgeMesh outer, double dielectricThickness, Schematic? schematic = null) =>
        Build(outer, [dielectricThickness], schematic);

    /// <summary>
    /// Builds an N-shell MID board: the OUTER mesh plus one inner shell per entry of
    /// <paramref name="dielectricThicknesses"/>, each offset inward by the CUMULATIVE thickness so shell
    /// <c>k</c> sits <c>Σ thicknesses[0..k-1]</c> below the outer wall. All shells share topology.
    /// </summary>
    /// <param name="outer">The moulded outer routing surface.</param>
    /// <param name="dielectricThicknesses">The dielectric thickness between each pair of consecutive
    /// shells (mm, each &gt; 0). An empty list is a single-shell stack (a plain <see cref="MidBoard"/>).</param>
    /// <param name="schematic">The shared schematic, or null.</param>
    /// <exception cref="MidShellException">The inward offset self-intersects at some shell.</exception>
    public static MidStack Build(
        HalfEdgeMesh outer, IReadOnlyList<double> dielectricThicknesses, Schematic? schematic = null)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(dielectricThicknesses);
        foreach (double t in dielectricThicknesses)
            if (!(t > 0) || !double.IsFinite(t))
                throw new ArgumentOutOfRangeException(nameof(dielectricThicknesses),
                    $"Every dielectric thickness must be positive and finite; got {t}.");

        int shellCount = dielectricThicknesses.Count + 1;
        var offsets = new double[shellCount];
        for (int k = 1; k < shellCount; k++)
            offsets[k] = offsets[k - 1] + dielectricThicknesses[k - 1];

        // Shell 0 IS the outer mesh; its surface gives the base positions and topology every inner shell
        // is offset from.
        var outerBoard = MidBoard.OnMesh(outer, schematic);
        var s0 = outerBoard.Surface;
        var basePositions = s0.Positions;
        var faces = s0.Faces;
        var normals = AngleWeightedNormals(basePositions, faces);

        var shells = new MidBoard[shellCount];
        var names = new string[shellCount];
        shells[0] = outerBoard;
        names[0] = "outer";
        for (int k = 1; k < shellCount; k++)
        {
            double off = offsets[k];
            var offsetPositions = new Vector3d[basePositions.Length];
            for (int i = 0; i < basePositions.Length; i++)
                offsetPositions[i] = basePositions[i] - normals[i] * off;   // inward = −normal
            var innerMesh = HalfEdgeMesh.Build(offsetPositions, faces);
            CheckOffsetSound(basePositions, offsetPositions, faces, outerBoard.Mesh, innerMesh, off, k);
            shells[k] = MidBoard.OnMesh(innerMesh, schematic);
            names[k] = k == shellCount - 1 ? "inner" : $"shell {k}";
        }

        return new MidStack(shells, names, basePositions, normals, offsets);
    }

    // ---- shells --------------------------------------------------------------

    /// <summary>The shells, outer (index 0) to innermost. Each is a full <see cref="MidBoard"/> — place
    /// pads, traces and components on it to route that shell.</summary>
    public IReadOnlyList<MidBoard> Shells => _shells;

    /// <summary>The number of conductive shells (2 for a two-shell board).</summary>
    public int ShellCount => _shells.Length;

    /// <summary>The <see cref="MidBoard"/> for a shell — 0 is the outer wall, the last is the innermost.
    /// This is how a pad, trace or seated component NAMES its shell: place it on the returned board.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is not a shell of this stack.</exception>
    public MidBoard Shell(int index)
    {
        if ((uint)index >= (uint)_shells.Length)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Shell {index} is out of range: this stack has {_shells.Length} shell(s) (0 = outer .. "
                + $"{_shells.Length - 1} = inner).");
        return _shells[index];
    }

    /// <summary>The outer shell (index 0) — the moulded wall's own surface.</summary>
    public MidBoard Outer => _shells[0];

    /// <summary>The innermost shell — the deepest offset moulded surface.</summary>
    public MidBoard Inner => _shells[^1];

    /// <summary>A shell's name for reports — "outer", "inner", or "shell k".</summary>
    public string ShellName(int index) => _shellNames[index];

    /// <summary>The cumulative dielectric offset (inward, mm) of a shell from the outer wall; 0 for the
    /// outer shell.</summary>
    public double Offset(int shell) => _offsets[shell];

    // ---- vias ----------------------------------------------------------------

    /// <summary>The placed through-shell vias, in placement order.</summary>
    public IReadOnlyList<SurfaceVia> Vias => _vias;

    /// <summary>
    /// Places a through-shell VIA at a world point near the OUTER shell, on <paramref name="net"/>. The
    /// via ties the outer surface point to its CORRESPONDING point on every inner shell (the same face +
    /// barycentric weights, on each offset mesh) — the MID analogue of a PCB via — and places a small
    /// copper pad of <paramref name="padDiameter"/> on each shell it passes through. Those pads join same-
    /// net copper they touch on their shells, and the barrel joins them across shells, so a net split over
    /// two shells becomes one connected net (see <see cref="Connectivity"/>).
    /// </summary>
    /// <param name="net">The net the via carries (or null for a via on no electrical net).</param>
    /// <param name="nearOuter">A world point near the outer shell; the via anchors at the nearest outer
    /// surface point.</param>
    /// <param name="padDiameter">The via pad land diameter on each shell (mm, &gt; 0).</param>
    public SurfaceVia AddVia(string? net, in Vector3d nearOuter, double padDiameter = 0.5)
    {
        RequirePadDiameter(padDiameter);
        var sp = _shells[0].Surface.Locate(nearOuter);
        return PlaceViaAt(net, sp, padDiameter);
    }

    /// <summary>
    /// Places a through-shell VIA anchored on the outer shell near <paramref name="nearOuter"/> AND
    /// checks that <paramref name="nearInner"/> is the CORRESPONDING point on the innermost shell —
    /// refusing by name if the caller's two endpoints do not correspond (a point on a different part of
    /// the shell), the one thing the barycentric-correspondence design otherwise makes impossible.
    /// </summary>
    /// <exception cref="MidShellException">The inner endpoint is not the corresponding surface point of
    /// the outer endpoint.</exception>
    public SurfaceVia AddVia(string? net, in Vector3d nearOuter, in Vector3d nearInner, double padDiameter = 0.5)
    {
        RequirePadDiameter(padDiameter);
        var sp = _shells[0].Surface.Locate(nearOuter);
        var fv = _shells[0].Surface.Faces[sp.Face];
        var corresponding = ShellPoint(_shells.Length - 1, fv, sp.Wa, sp.Wb, sp.Wc);
        var located = _shells[^1].Surface.Locate(nearInner);
        double slack = 2 * _shells[0].LongestEdge();
        double drift = located.Position.DistanceTo(corresponding);
        if (drift > slack)
            throw new MidShellException(
                $"The via's two endpoints are not corresponding surface points: the inner endpoint "
                + $"{nearInner} snaps to {located.Position:g4}, which is {drift:g4} mm from the corresponding "
                + $"point {corresponding:g4} of the outer endpoint (tolerance {slack:g4} mm). A through-shell "
                + "via ties an outer point to the SAME (face, barycentric) location on the offset inner "
                + "shell — pass the inner point near that correspondence, or use AddVia(net, nearOuter, "
                + "padDiameter), which computes it.");
        return PlaceViaAt(net, sp, padDiameter);
    }

    private SurfaceVia PlaceViaAt(string? net, in SurfacePoint sp, double padDiameter)
    {
        var fv = _shells[0].Surface.Faces[sp.Face];
        int index = _vias.Count;
        string source = $"via{index}";
        var spanned = new int[_shells.Length];
        var pads = new MidPad[_shells.Length];
        var points = new Vector3d[_shells.Length];
        for (int k = 0; k < _shells.Length; k++)
        {
            var pk = ShellPoint(k, fv, sp.Wa, sp.Wb, sp.Wc);
            spanned[k] = k;
            points[k] = pk;
            pads[k] = _shells[k].PlacePad(net, pk, padDiameter, source);
        }
        var via = new SurfaceVia(net, 0, _shells.Length - 1, spanned, pads, points, source, padDiameter);
        _vias.Add(via);
        return via;
    }

    /// <summary>
    /// Places a through-shell VIA whose foot is a mesh VERTEX (corresponding across all shells, since the
    /// shells share topology) — the surface auto-router's via, dropped where a cross-shell route transitions
    /// between shells. It ties the outer vertex to its corresponding inner point and places a pad of
    /// <paramref name="padDiameter"/> on each shell, exactly as <see cref="AddVia(string?, in Vector3d,
    /// double)"/> does, so the route's per-shell traces stitch through it.
    /// </summary>
    internal SurfaceVia AddViaAtVertex(string? net, int vertex, double padDiameter)
    {
        RequirePadDiameter(padDiameter);
        var sp = OuterVertexPoint(vertex);
        return PlaceViaAt(net, sp, padDiameter);
    }

    /// <summary>
    /// The via foot points a via at mesh <paramref name="vertex"/> would land on, one per shell — the
    /// corresponding (same face + barycentric) points on each offset mesh. This is the candidate barrel the
    /// router certifies (via-to-via) BEFORE committing, computed by the SAME <see cref="ShellPoint"/>
    /// machinery <see cref="PlaceViaAt"/> uses, so a certified span equals the placed via's
    /// <see cref="SurfaceVia.SpanPolyline"/>.
    /// </summary>
    internal Vector3d[] ViaSpanAtVertex(int vertex)
    {
        var sp = OuterVertexPoint(vertex);
        var fv = _shells[0].Surface.Faces[sp.Face];
        var span = new Vector3d[_shells.Length];
        for (int k = 0; k < _shells.Length; k++)
            span[k] = ShellPoint(k, fv, sp.Wa, sp.Wb, sp.Wc);
        return span;
    }

    /// <summary>The outer shell's surface point AT a mesh vertex — its position located back onto the
    /// surface, so the barycentric weights place it exactly on that vertex's corner. Both via helpers seed
    /// from this so the placed via and its certified span agree.</summary>
    private SurfacePoint OuterVertexPoint(int vertex) =>
        _shells[0].Surface.Locate(_shells[0].Mesh.GetPosition(vertex));

    /// <summary>The point on shell <paramref name="shell"/> corresponding to the outer surface point with
    /// the given face vertices and barycentric weights — the SAME (face, barycentric) location on the
    /// offset mesh. Lands exactly on that shell's face by construction.</summary>
    private Vector3d ShellPoint(int shell, IReadOnlyList<int> faceVertices, double wa, double wb, double wc)
    {
        double off = _offsets[shell];
        Vector3d P(int v) => _basePositions[v] - _normals[v] * off;
        return P(faceVertices[0]) * wa + P(faceVertices[1]) * wb + P(faceVertices[2]) * wc;
    }

    private static void RequirePadDiameter(double padDiameter)
    {
        if (!(padDiameter > 0) || !double.IsFinite(padDiameter))
            throw new ArgumentOutOfRangeException(nameof(padDiameter), "A via pad diameter must be positive.");
    }

    // ---- the DRC (spans shells) ----------------------------------------------

    /// <summary>
    /// The multi-shell 3D DRC. It runs each shell's SAME-SHELL clearance / short / trace-width check
    /// (<see cref="Mid3dDrc"/> per shell — a via's clearance to other-net copper on both shells it touches
    /// falls out of this, since a via pad IS copper on its shell), adds the inter-shell VIA-TO-VIA spacing
    /// rule, and reports the CROSS-SHELL ratsnest (nets the copper does not yet connect across shells).
    /// A single-shell stack with no vias reduces to <see cref="Mid3dDrc.Check(MidBoard,DrcRuleSet?)"/>.
    /// </summary>
    public Mid3dDrcReport Check(DrcRuleSet? rules = null)
    {
        rules ??= DrcRuleSet.Default;
        var violations = new List<MidDrcViolation>();
        var uncertain = new List<MidDrcViolation>();

        // Same-shell clearance / short / trace-width, run per shell with the shell named in the message.
        for (int k = 0; k < _shells.Length; k++)
        {
            var report = Mid3dDrc.Check(_shells[k], rules);
            string name = _shellNames[k];
            foreach (var v in report.Violations)
                violations.Add(v with { Message = $"[{name}] {v.Message}" });
            foreach (var u in report.Uncertain)
                uncertain.Add(u with { Message = $"[{name}] {u.Message}" });
        }

        // Inter-shell via-to-via spacing (the drill web) — all via pairs, regardless of net.
        CheckViaSpacing(rules.MinViaToVia, violations);

        return new Mid3dDrcReport(violations, uncertain, Connectivity().Unrouted);
    }

    private void CheckViaSpacing(double minViaToVia, List<MidDrcViolation> violations)
    {
        if (!(minViaToVia > 0))
            return;
        for (int i = 0; i < _vias.Count; i++)
            for (int j = i + 1; j < _vias.Count; j++)
            {
                var a = _vias[i];
                var b = _vias[j];
                // The two barrels are segments through the dielectric; the copper-to-copper via web is the
                // 3D distance between the barrels less the two pad radii.
                double web = SurfaceGeometry.PolylineDistance(a.SpanPolyline, b.SpanPolyline)
                    - a.PadDiameter / 2 - b.PadDiameter / 2;
                if (web < minViaToVia)
                    violations.Add(new MidDrcViolation(
                        MidDrcRule.ViaSpacing, MidDrcVerdict.Violation,
                        $"via-to-via spacing {web:g3} < {minViaToVia:g3} mm: {a.Source} (net {Display(a.Net)}) "
                        + $"and {b.Source} (net {Display(b.Net)})",
                        Vector2d.Zero, a.OuterPoint, web, minViaToVia, 1, 1));
            }
    }

    // ---- cross-shell connectivity --------------------------------------------

    /// <summary>
    /// The connectivity of every net ACROSS the shells — the crux of a multi-shell board. Each shell's
    /// copper is joined by the existing per-surface touch rule (two same-net features whose 3D footprints
    /// meet), and a via joins its own pads across shells (the plated barrel). A net is CONNECTED when all
    /// its component pads (the user pads — via pads and traces are connectors) fall into ONE connected
    /// component; a net whose copper is split over two shells with no via tying them is a cross-shell
    /// ratsnest (<see cref="ConnectivityReport.Unrouted"/>).
    /// </summary>
    public ConnectivityReport Connectivity()
    {
        var viaPads = new HashSet<MidPad>(ReferenceEqualityComparer.Instance);
        foreach (var via in _vias)
            foreach (var pad in via.Pads)
                viaPads.Add(pad);

        var feats = new List<Feat>();
        var padIndex = new Dictionary<MidPad, int>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < _shells.Length; k++)
        {
            foreach (var pad in _shells[k].Pads)
            {
                bool isVia = viaPads.Contains(pad);
                padIndex[pad] = feats.Count;
                feats.Add(new Feat(k, pad.Net, IsTerminal: !isVia, pad.Source, pad.SurfaceFeature()));
            }
            foreach (var trace in _shells[k].Traces)
                foreach (var f in trace.SurfaceFeatures())
                    feats.Add(new Feat(k, f.Net, IsTerminal: false, f.Source, f));
        }

        var parent = MakeSet(feats.Count);

        // Within-shell touch: two same-net features on one shell whose 3D copper footprints meet (the
        // RatsnestSurface rule — a trace endpoint lands exactly on its pad, so they coincide there).
        for (int i = 0; i < feats.Count; i++)
            for (int j = i + 1; j < feats.Count; j++)
            {
                if (feats[i].Shell != feats[j].Shell || !SameNet(feats[i].Net, feats[j].Net))
                    continue;
                var fa = feats[i].Feature;
                var fb = feats[j].Feature;
                double edge = SurfaceGeometry.PolylineDistance(fa.Polyline, fb.Polyline)
                    - fa.Width / 2 - fb.Width / 2;
                double weld = 1e-6 * Math.Max(1, Math.Max(fa.Bounds.Size.Length, fb.Bounds.Size.Length));
                if (edge <= weld)
                    Union(parent, i, j);
            }

        // Cross-shell plated barrels: a via joins its own pads across the shells it spans.
        foreach (var via in _vias)
        {
            int first = -1;
            foreach (var pad in via.Pads)
            {
                if (!padIndex.TryGetValue(pad, out int idx))
                    continue;
                if (first < 0)
                    first = idx;
                else
                    Union(parent, first, idx);
            }
        }

        // Per-net: how many components the TERMINAL (user pad) features fall into.
        var termByNet = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < feats.Count; i++)
            if (feats[i].IsTerminal && feats[i].Net is { } net)
            {
                if (!termByNet.TryGetValue(net, out var list))
                    termByNet[net] = list = [];
                list.Add(i);
            }

        var nets = new List<NetConnectivity>();
        foreach (var net in termByNet.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var terminals = termByNet[net];
            var islands = new Dictionary<int, List<string>>();
            foreach (int i in terminals)
            {
                int root = Find(parent, i);
                if (!islands.TryGetValue(root, out var members))
                    islands[root] = members = [];
                members.Add(feats[i].Source);
            }
            var padIslands = islands.Values
                .Select(members => { members.Sort(StringComparer.Ordinal); return (IReadOnlyList<string>)members; })
                .OrderBy(members => members[0], StringComparer.Ordinal)
                .ToList();
            nets.Add(new NetConnectivity(net, terminals.Count, islands.Count, padIslands));
        }
        return new ConnectivityReport(nets);
    }

    /// <summary>One piece of copper the cross-shell connectivity reasons about: which shell it is on, its
    /// net, whether it is a TERMINAL (a user pad that must connect) or a connector (a via pad or a trace),
    /// its source, and the surface feature the touch test measures.</summary>
    private readonly record struct Feat(int Shell, string? Net, bool IsTerminal, string Source, MidSurfaceFeature Feature);

    // ---- the inward offset (angle-weighted normals + self-intersection guard) -

    /// <summary>
    /// The ANGLE-WEIGHTED unit vertex normals — the boundary-layer march / <c>MeshSdf</c> pseudonormal
    /// convention (Bærentzen &amp; Aanæs): each face's unit normal is weighted by the corner angle it
    /// subtends at the vertex and summed, then normalized. This is the ONE convention that does not tear a
    /// shared vertex (a raw face normal would move each incident face's copy of the vertex to a different
    /// place), and it is radial by symmetry on a cylinder, which is what makes the developable offset
    /// exact.
    /// </summary>
    private static Vector3d[] AngleWeightedNormals(Vector3d[] positions, IReadOnlyList<int[]> faces)
    {
        var accum = new Vector3d[positions.Length];
        foreach (var t in faces)
            for (int i = 0; i < 3; i++)
            {
                var p = positions[t[i]];
                var u = positions[t[(i + 1) % 3]] - p;
                var v = positions[t[(i + 2) % 3]] - p;
                var raw = u.Cross(v);
                double twiceArea = raw.Length;
                if (twiceArea <= 0)
                    continue;
                // atan2 of the cross and dot magnitudes is the exact corner angle at any vector length,
                // with no normalization and no epsilon (TetGeometry.DihedralAngles' rule).
                double angle = Math.Atan2(twiceArea, u.Dot(v));
                accum[t[i]] += raw / twiceArea * angle;   // unit face normal weighted by corner angle
            }
        var normals = new Vector3d[positions.Length];
        for (int i = 0; i < positions.Length; i++)
            normals[i] = accum[i].TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.Zero;
        return normals;
    }

    /// <summary>
    /// Refuses the inward offset if it self-intersects. An INWARD offset folds where the surface is CONVEX
    /// and the thickness exceeds the local convex curvature radius (the offset crosses the local centre of
    /// curvature); a concave region offset inward only diverges, so it never self-intersects. TWO
    /// complementary exact-sign checks cover it:
    /// <list type="bullet">
    /// <item><b>Local fold</b> — an offset triangle whose RAW cross product flips (or collapses) relative
    /// to the original has folded through itself: the sign of the dot of the two unnormalized cross
    /// products decides it. This catches a DEVELOPABLE inversion (a tube / cone offset past its section
    /// radius, whose offset scales one direction and so keeps the linear sign) and any concave-side fold.</item>
    /// <item><b>Global orientation</b> — a doubly-convex CLOSED surface offset past its curvature radius
    /// turns inside out (a sphere → a smaller sphere on the far side of its centre), which a raw cross
    /// product CANNOT see (a uniform inward offset is a uniform scale whose cross product squares the sign)
    /// but the SIGNED VOLUME can: the offset's orientation flips, so its signed-volume sign no longer
    /// matches the outer's.</item>
    /// </list>
    /// The refusal names the region / cause and the thickness. (An OPEN doubly-convex cap offset past its
    /// radius — no signed volume, no linear-sign fold — is a curvature-reach check filed as a follow-on;
    /// a real dielectric is far thinner than a housing wall's curvature radius.)
    /// </summary>
    private static void CheckOffsetSound(
        Vector3d[] basePositions, Vector3d[] offsetPositions, IReadOnlyList<int[]> faces,
        HalfEdgeMesh outerMesh, HalfEdgeMesh offsetMesh, double thickness, int shell)
    {
        foreach (var t in faces)
        {
            var n0 = (basePositions[t[1]] - basePositions[t[0]]).Cross(basePositions[t[2]] - basePositions[t[0]]);
            var n1 = (offsetPositions[t[1]] - offsetPositions[t[0]]).Cross(offsetPositions[t[2]] - offsetPositions[t[0]]);
            if (n1.Dot(n0) <= 0)
            {
                var centroid = (basePositions[t[0]] + basePositions[t[1]] + basePositions[t[2]]) / 3;
                throw new MidShellException(
                    $"The inward offset of the outer shell by {thickness:g4} mm (to shell {shell}) "
                    + $"self-intersects near {centroid:g4}: an offset triangle inverts there, so the "
                    + "dielectric thickness exceeds the surface's local convex curvature radius and the "
                    + "inner shell would fold through itself. Reduce the thickness, or route only where the "
                    + "surface curvature admits it.");
            }
        }

        if (offsetMesh.BoundaryLoops().Count == 0)
        {
            double outerVolume = outerMesh.SignedVolume();
            double innerVolume = offsetMesh.SignedVolume();
            if (Math.Sign(innerVolume) != Math.Sign(outerVolume) || innerVolume == 0)
                throw new MidShellException(
                    $"The inward offset of the outer shell by {thickness:g4} mm (to shell {shell}) "
                    + "self-intersects: the shell turns inside out (its signed volume flips sign, "
                    + $"{outerVolume:g3} → {innerVolume:g3}), so the dielectric thickness exceeds the "
                    + "surface's convex curvature radius somewhere and the inner shell crosses through "
                    + "itself. Reduce the thickness.");
        }
    }

    // ---- union-find ----------------------------------------------------------

    private static bool SameNet(string? a, string? b) => a is not null && b is not null && a == b;

    private static string Display(string? net) => net is null ? "(unconnected)" : $"'{net}'";

    private static int[] MakeSet(int n)
    {
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        return parent;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
            i = parent[i] = parent[parent[i]];
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }
}

/// <summary>
/// A through-shell VIA of a <see cref="MidStack"/>: a plated connection tying a surface point on the
/// outer shell to its CORRESPONDING point on the inner shell(s) it spans, on one net. It is the MID
/// analogue of a PCB via — the copper across the dielectric that lets a net split over two moulded
/// shells become one connected net. It places one copper pad per spanned shell (its
/// <see cref="Pads"/>), and its barrel joins them across shells in <see cref="MidStack.Connectivity"/>.
/// </summary>
public sealed class SurfaceVia
{
    internal SurfaceVia(
        string? net, int fromShell, int toShell, IReadOnlyList<int> shells,
        IReadOnlyList<MidPad> pads, IReadOnlyList<Vector3d> points, string source, double padDiameter)
    {
        Net = net;
        FromShell = fromShell;
        ToShell = toShell;
        Shells = shells;
        Pads = pads;
        Points = points;
        Source = source;
        PadDiameter = padDiameter;
    }

    /// <summary>The net this via carries, or null for a via on no electrical net.</summary>
    public string? Net { get; }

    /// <summary>The outermost shell the via touches (0 = the outer wall).</summary>
    public int FromShell { get; }

    /// <summary>The innermost shell the via touches.</summary>
    public int ToShell { get; }

    /// <summary>The shells the via passes through, outer to inner.</summary>
    public IReadOnlyList<int> Shells { get; }

    /// <summary>The via's copper pad on each shell it spans (one per <see cref="Shells"/> entry).</summary>
    public IReadOnlyList<MidPad> Pads { get; }

    /// <summary>The via's corresponding surface point on each shell it spans — the SAME (face,
    /// barycentric) location on each offset mesh.</summary>
    public IReadOnlyList<Vector3d> Points { get; }

    /// <summary>The via's barrel centre-line through the shells (its <see cref="Points"/>).</summary>
    public IReadOnlyList<Vector3d> SpanPolyline => Points;

    /// <summary>A name for reports.</summary>
    public string Source { get; }

    /// <summary>The via pad land diameter on each shell (mm).</summary>
    public double PadDiameter { get; }

    /// <summary>The outer surface point the via anchors on.</summary>
    public Vector3d OuterPoint => Points[0];

    /// <summary>The corresponding point on the innermost shell.</summary>
    public Vector3d InnerPoint => Points[^1];

    /// <summary>The via's outer-shell pad.</summary>
    public MidPad OuterPad => Pads[0];

    /// <summary>The via's inner-shell pad.</summary>
    public MidPad InnerPad => Pads[^1];

    /// <summary>
    /// The via barrel as a thin solid <see cref="Shape"/> — a cylinder of diameter
    /// <paramref name="diameter"/> from the outer point through the dielectric to the inner point. For
    /// visualisation; the connectivity / DRC model does not need it.
    /// </summary>
    public Shape Barrel(double diameter)
    {
        if (!(diameter > 0) || !double.IsFinite(diameter))
            throw new ArgumentOutOfRangeException(nameof(diameter), "The via barrel diameter must be positive.");
        var a = OuterPoint;
        var b = InnerPoint;
        var axis = b - a;
        double length = axis.Length;
        if (!(length > 0))
            throw new InvalidOperationException(
                $"Via {Source} has no depth (its outer and inner points coincide), so it has no barrel.");
        var frame = Frame3d.FromZX(a, axis / length, axis.ArbitraryPerpendicular(Tolerance.Default));
        return Shape.Cylinder(diameter / 2, length).Transform(frame.ToMatrix());
    }
}

/// <summary>Thrown when a multi-shell MID construction is refused — an inward offset that self-intersects,
/// or a via whose endpoints do not correspond. Named so a caller can distinguish a modelling refusal from
/// an argument error.</summary>
public sealed class MidShellException : Exception
{
    /// <summary>Creates the exception with a message naming the region / thickness / endpoints.</summary>
    public MidShellException(string message) : base(message) { }
}
