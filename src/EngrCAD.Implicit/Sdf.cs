using System.Buffers;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

/// <summary>
/// A signed distance field: negative inside, zero on the surface, positive outside.
/// Models compose as an AST of primitives and operators; every node reports conservative
/// <see cref="Bounds"/> (infinite for unbounded fields like half-spaces and lattices).
/// Set operators are overloaded for fluent composition: <c>a | b</c> union,
/// <c>a &amp; b</c> intersection, <c>a - b</c> difference.
/// Distances from smooth/blend operators are lower-bound approximations — correct sign
/// everywhere, exact magnitude only away from blend regions.
/// </summary>
public abstract class Sdf
{
    public abstract double Evaluate(in Vector3d point);

    /// <summary>Conservative bounds of the solid (the d &lt; 0 region).</summary>
    public abstract Aabb Bounds { get; }

    /// <summary>
    /// Batch evaluation — the throughput entry point, and the one every bulk consumer
    /// (Surface Nets sampling, grid bakes, section contours) goes through.
    /// <para>
    /// The default implementation deinterleaves the points into pooled structure-of-arrays
    /// scratch — <em>once</em>, at the root of the AST — and drives the SIMD
    /// <see cref="EvaluateBatch"/> seam, which operators forward to their children
    /// unchanged. Results are bit-for-bit identical to calling
    /// <see cref="Evaluate(in Vector3d)"/> per point for all finite inputs (see
    /// <see cref="EvaluateBatch"/> for the one signed-zero caveat). Override this only to
    /// intercept whole batches (an instrumenting wrapper, a cache); to make a node fast,
    /// override <see cref="EvaluateBatch"/> instead.
    /// </para>
    /// </summary>
    public virtual void Evaluate(ReadOnlySpan<Vector3d> points, Span<double> distances)
    {
        if (distances.Length < points.Length)
            throw new ArgumentException("Distance span is shorter than the point span.");

        int total = points.Length;
        if (total == 0)
            return;

        int chunk = Math.Min(total, SdfBatch.ChunkLength);
        var rented = ArrayPool<double>.Shared.Rent(chunk * 3);
        try
        {
            for (int start = 0; start < total; start += chunk)
            {
                int length = Math.Min(chunk, total - start);
                var xs = rented.AsSpan(0, length);
                var ys = rented.AsSpan(chunk, length);
                var zs = rented.AsSpan(chunk * 2, length);
                SdfBatch.Deinterleave(points.Slice(start, length), xs, ys, zs);
                EvaluateBatch(xs, ys, zs, distances.Slice(start, length));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Batch evaluation from <em>already deinterleaved</em> coordinates — for bulk callers
    /// that generate sample positions procedurally (grid sampling) rather than holding an
    /// array of <see cref="Vector3d"/>. It skips the transpose that
    /// <see cref="Evaluate(ReadOnlySpan{Vector3d}, Span{double})"/> performs, and it lets a
    /// caller stream an arbitrarily long run through a fixed-size coordinate buffer instead
    /// of materializing one point per sample: <c>Polygonize</c> saves 24 bytes per grid
    /// corner that way.
    /// <para>
    /// Results are bit-for-bit identical to the interleaved overload (both drive the same
    /// <see cref="EvaluateBatch"/> seam, chunked identically). Note that a node overriding
    /// the interleaved overload to intercept whole batches does <em>not</em> intercept this
    /// one — <see cref="EvaluateBatch"/> is the seam that always sees every batch.
    /// </para>
    /// </summary>
    public void Evaluate(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        if (y.Length < x.Length || z.Length < x.Length || distances.Length < x.Length)
            throw new ArgumentException("Coordinate and distance spans must be at least as long as x.");

        // Chunked exactly like the interleaved entry, so operator temporaries stay cache
        // resident no matter how long a run the caller streams through.
        for (int start = 0; start < x.Length; start += SdfBatch.ChunkLength)
        {
            int length = Math.Min(SdfBatch.ChunkLength, x.Length - start);
            EvaluateBatch(
                x.Slice(start, length), y.Slice(start, length), z.Slice(start, length),
                distances.Slice(start, length));
        }
    }

    /// <summary>
    /// The SIMD seam: evaluate a batch given deinterleaved coordinates (all four spans
    /// share a length). Structure-of-arrays is the layout a lane-wise kernel needs — the
    /// interleaved <see cref="Vector3d"/> form the public API takes would cost a strided
    /// gather per register — so the transpose happens once in
    /// <see cref="Evaluate(ReadOnlySpan{Vector3d}, Span{double})"/> and every node below
    /// the root reads contiguous doubles.
    /// <para>
    /// The default implementation loops the scalar <see cref="Evaluate(in Vector3d)"/>, so
    /// a node that does not override it still composes correctly (and still benefits when
    /// its <em>children</em> vectorize). Overrides must be bit-for-bit identical to the
    /// scalar path for finite inputs: mirror the scalar expression term for term in the
    /// same association order, using only correctly rounded IEEE-754 operations, and let
    /// the ragged tail fall back to the scalar path. The one permitted deviation is the
    /// sign of an exact zero (<c>Vector.Min</c>/<c>Vector.Max</c> break a ±0 tie by
    /// operand position, <c>Math.Min</c>/<c>Math.Max</c> by sign); ±0 compare equal and no
    /// consumer of the field can tell them apart. Kernels that would need a vector
    /// transcendental (sin/cos/exp) are deliberately left scalar — no vector transcendental
    /// reproduces <see cref="Math"/>'s results bit for bit.
    /// </para>
    /// </summary>
    protected internal virtual void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        for (int i = 0; i < x.Length; i++)
            distances[i] = Evaluate(new Vector3d(x[i], y[i], z[i]));
    }

    /// <summary>Outward surface normal by central differences.</summary>
    public Vector3d Normal(in Vector3d point, double epsilon = 1e-6)
    {
        var gradient = new Vector3d(
            Evaluate(point + (epsilon, 0, 0)) - Evaluate(point - (epsilon, 0, 0)),
            Evaluate(point + (0, epsilon, 0)) - Evaluate(point - (0, epsilon, 0)),
            Evaluate(point + (0, 0, epsilon)) - Evaluate(point - (0, 0, epsilon)));
        return gradient.TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.UnitZ;
    }

    internal static readonly Aabb InfiniteBounds = new(
        (double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity),
        (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity));

    public static bool IsFinite(in Aabb bounds) =>
        double.IsFinite(bounds.Min.X) && double.IsFinite(bounds.Min.Y) && double.IsFinite(bounds.Min.Z) &&
        double.IsFinite(bounds.Max.X) && double.IsFinite(bounds.Max.Y) && double.IsFinite(bounds.Max.Z);

    // ---- primitive factories ----

    public static Sdf Sphere(double radius) => new SphereSdf(radius);

    /// <summary>Box centered at the origin with the given full side lengths.</summary>
    public static Sdf Box(double sizeX, double sizeY, double sizeZ) =>
        new BoxSdf(new Vector3d(sizeX / 2, sizeY / 2, sizeZ / 2));

    public static Sdf Box(in Vector3d halfExtents) => new BoxSdf(halfExtents);

    /// <summary>Capped cylinder along Z, centered at the origin.</summary>
    public static Sdf Cylinder(double radius, double height) => new CylinderSdf(radius, height / 2);

    /// <summary>
    /// Capped cone (frustum) along Z, centered at the origin: radius
    /// <paramref name="bottomRadius"/> at z = −height/2 growing linearly to
    /// <paramref name="topRadius"/> at z = +height/2. Exact distance (Quilez's capped
    /// cone); a zero radius gives a pointed apex.
    /// </summary>
    public static Sdf Cone(double bottomRadius, double topRadius, double height)
    {
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (bottomRadius < 0 || topRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(bottomRadius), "Radii must be non-negative.");
        return new ConeSdf(bottomRadius, topRadius, height / 2);
    }

    /// <summary>Torus about the Z axis: ring of radius <paramref name="majorRadius"/> in the XY plane.</summary>
    public static Sdf Torus(double majorRadius, double minorRadius) => new TorusSdf(majorRadius, minorRadius);

    public static Sdf Capsule(in Vector3d a, in Vector3d b, double radius) => new CapsuleSdf(a, b, radius);

    /// <summary>Half-space: solid where dot(normal, p) ≤ offset. Unbounded.</summary>
    public static Sdf HalfSpace(in Vector3d normal, double offset) =>
        new HalfSpaceSdf(normal.Normalized(), offset);

    /// <summary>
    /// Gyroid lattice sheet (triply periodic minimal surface) with the given cell size and
    /// sheet thickness. Approximate distance, unbounded — intersect with a finite solid.
    /// </summary>
    public static Sdf Gyroid(double cellSize, double thickness) => new GyroidSdf(cellSize, thickness);

    /// <summary>The 2D region extruded along +Z from z = 0 to z = <paramref name="height"/>;
    /// exact wherever the region's distance is exact.</summary>
    public static Sdf ExtrudedRegion(IPlanarRegion region, double height) => new ExtrudedRegionSdf(region, height);

    /// <summary>The 2D region — read as (radius, height), x ≥ 0 — revolved a full turn
    /// about Z; exact wherever the region's distance is exact.</summary>
    public static Sdf RevolvedRegion(IPlanarRegion region) => new RevolvedRegionSdf(region);

    /// <summary>
    /// Helical thread solid (right-hand) about +Z, z ∈ [0, <paramref name="length"/>]:
    /// a straight-flanked thread form — crest flat of width <paramref name="crestWidth"/>
    /// at <paramref name="majorRadius"/>, root flat of width <paramref name="rootWidth"/>
    /// at <paramref name="minorRadius"/>, linear flanks between — repeated along the
    /// helical coordinate w = z − pitch·θ/2π; everything below the profile is core
    /// material. For the ISO 68-1 basic profile pass crestWidth = P/8, rootWidth = P/4,
    /// majorRadius − minorRadius = (5/8)·(√3/2)·P.
    /// <para>Approximate distance, exact sign (see <see cref="ThreadSdf"/> for the
    /// fidelity contract). <paramref name="profileOffset"/> dilates (+) / erodes (−) the
    /// profile normal to its boundary — the printing-clearance mechanism.
    /// <paramref name="startChamfer"/>/<paramref name="endChamfer"/> cut 45° cones at
    /// z = 0 / z = length ending at radius majorRadius + profileOffset − chamfer.</para>
    /// </summary>
    public static Sdf Thread(
        double majorRadius, double minorRadius, double pitch,
        double crestWidth, double rootWidth, double length,
        double profileOffset = 0, double startChamfer = 0, double endChamfer = 0) =>
        new ThreadSdf(majorRadius, minorRadius, pitch, crestWidth, rootWidth, length,
            profileOffset, startChamfer, endChamfer);

    // ---- combinators ----

    public Sdf Union(Sdf other) => new UnionSdf(this, other);
    public Sdf Intersect(Sdf other) => new IntersectionSdf(this, other);
    public Sdf Subtract(Sdf other) => new DifferenceSdf(this, other);

    /// <summary>Union with a fillet-like blend of radius ~<paramref name="blend"/>.
    /// A blend ≤ 0 degrades to the exact hard union — field and bounds alike (same
    /// policy as <see cref="Blend"/>); it never shrinks the result.</summary>
    public Sdf SmoothUnion(Sdf other, double blend) => new SmoothUnionSdf(this, other, blend);

    /// <summary>Intersection with a fillet-like blend of radius ~<paramref name="blend"/>.
    /// A blend ≤ 0 degrades to the exact hard intersection (see
    /// <see cref="SmoothUnion(Sdf, double)"/> for the policy).</summary>
    public Sdf SmoothIntersect(Sdf other, double blend) => new SmoothIntersectionSdf(this, other, blend);

    /// <summary>Difference with a fillet-like blend of radius ~<paramref name="blend"/>.
    /// A blend ≤ 0 degrades to the exact hard difference (see
    /// <see cref="SmoothUnion(Sdf, double)"/> for the policy).</summary>
    public Sdf SmoothSubtract(Sdf other, double blend) => new SmoothDifferenceSdf(this, other, blend);

    /// <summary>Positive distance grows (and rounds) the solid; negative shrinks it.</summary>
    public Sdf Offset(double distance) => new OffsetSdf(this, distance);

    /// <summary>Hollow skin of the surface with the given total wall thickness.</summary>
    public Sdf Shell(double thickness) => new ShellSdf(this, thickness);

    public Sdf Translate(in Vector3d translation) => new TranslateSdf(this, translation);
    public Sdf Rotate(in Quaterniond rotation) => new RotateSdf(this, rotation);

    /// <summary>Mirror across the plane through <paramref name="point"/> with
    /// <paramref name="normal"/>: the query point is reflected, so distances stay
    /// exact (reflection is an isometry).</summary>
    public Sdf Mirror(in Vector3d point, in Vector3d normal) =>
        new MirrorSdf(this, point, normal.Normalized());

    /// <summary>Uniform scale about the origin (distances stay exact).</summary>
    public Sdf Scale(double factor) => new ScaleSdf(this, factor);

    // ---- sampled-grid acceleration ----

    /// <summary>
    /// Bakes this field onto a dense uniform grid over its own <see cref="Bounds"/>
    /// (expanded by one cell so the surface stays interior — which guarantees the
    /// correct-sign-outside contract) and returns a node that evaluates the grid by
    /// trilinear interpolation. See <see cref="Sampled(in Aabb, double, bool)"/> for
    /// the distance-fidelity contract. Requires finite bounds.
    /// </summary>
    public Sdf Sampled(double cellSize, bool lazy = false)
    {
        var bounds = Bounds;
        if (!IsFinite(bounds))
            throw new InvalidOperationException(
                "Sampled() over the node's own bounds requires finite Bounds; pass an explicit region for unbounded fields.");
        return Sampled(bounds.Expanded(cellSize), cellSize, lazy);
    }

    /// <summary>
    /// Bakes this field onto a uniform grid of cubic cells covering
    /// <paramref name="region"/> (rounded up to whole cells) and returns a node that
    /// evaluates it by trilinear interpolation — the standard acceleration for expensive
    /// ASTs (mesh SDFs, deep CSG trees) queried many times over the same region. With
    /// <paramref name="lazy"/> the grid is materialized in 16³-sample blocks on first
    /// touch instead of up front (thread-safe; pays only for regions actually probed).
    /// <para>
    /// Distance fidelity: values are <em>approximate</em> — exact at grid sample points,
    /// trilinear between (error O(cellSize²) where the field is smooth, O(cellSize)
    /// across edges/medial axis), so the sign is reliable only where the cell size
    /// resolves the geometry: features thinner than a cell can vanish or fuse. Outside
    /// the baked region the value is the boundary-clamped interpolant plus the distance
    /// to the region — continuous, and correct in sign whenever the solid is contained
    /// in the region (the <see cref="Sampled(double, bool)"/> overload guarantees this;
    /// baking a sub-region that clips the solid makes outside values meaningless for the
    /// clipped part). <see cref="Bounds"/> is the baked region.
    /// </para>
    /// </summary>
    public Sdf Sampled(in Aabb region, double cellSize, bool lazy = false) =>
        lazy ? new LazyGridSdf(this, region, cellSize) : GridSdf.Bake(this, region, cellSize);

    /// <summary>
    /// Bakes a <em>narrow-band</em> grid over this node's own <see cref="Bounds"/>
    /// (expanded by the band plus one cell so the band stays interior). See
    /// <see cref="NarrowBand(in Aabb, double, double)"/> for the fidelity contract.
    /// Requires finite bounds.
    /// </summary>
    public Sdf NarrowBand(double cellSize, double bandWidth = 0)
    {
        if (bandWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(bandWidth), "Band width must be non-negative.");
        double band = bandWidth > 0 ? bandWidth : 2 * cellSize;
        var bounds = Bounds;
        if (!IsFinite(bounds))
            throw new InvalidOperationException(
                "NarrowBand() over the node's own bounds requires finite Bounds; pass an explicit region for unbounded fields.");
        return NarrowBand(bounds.Expanded(band + cellSize), cellSize, band);
    }

    /// <summary>
    /// Like <see cref="Sampled(in Aabb, double, bool)"/>, but evaluates this field only
    /// <em>near its surface</em>: samples within <paramref name="bandWidth"/> of the zero
    /// level set get the exact value, and the rest of the grid is filled by a distance
    /// transform seeded from that band. Source evaluations then scale with the surface's
    /// area rather than the region's volume, which is what makes fine grids affordable for
    /// <em>expensive</em> fields — measured 8–11× faster than a dense bake of a
    /// <c>MeshSdf</c>. It is the wrong tool for a cheap field: the outward fill costs about
    /// 60 ns per sample and does not parallelize, so baking an analytic CSG tree this way
    /// is several times <em>slower</em> than <see cref="Sampled(in Aabb, double, bool)"/>.
    /// A <paramref name="bandWidth"/> of 0 means two cells.
    /// <para>
    /// Fidelity: identical to a dense bake inside the band (and the zero level set is
    /// inside the band, so meshing and inside/outside classification are unaffected);
    /// outside it the sign stays exact at every sample but the magnitude becomes a chamfer
    /// approximation that over-estimates the true distance by up to ~13%. Do not use it
    /// as a sphere-tracing bound or offset it by more than the band width. See
    /// <see cref="NarrowBandSdf"/> for the full contract, including the precondition that
    /// this field's magnitude be a lower bound on its true distance (the engine's field
    /// contract).
    /// </para>
    /// </summary>
    public Sdf NarrowBand(in Aabb region, double cellSize, double bandWidth = 0)
    {
        if (bandWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(bandWidth), "Band width must be non-negative.");
        return NarrowBandSdf.Bake(this, region, cellSize, bandWidth > 0 ? bandWidth : 2 * cellSize);
    }

    public static Sdf operator |(Sdf a, Sdf b) => a.Union(b);
    public static Sdf operator &(Sdf a, Sdf b) => a.Intersect(b);
    public static Sdf operator -(Sdf a, Sdf b) => a.Subtract(b);

    // ---- n-ary combinators ----

    /// <summary>
    /// Exact union of any number of solids as a single flat AST node (min over children,
    /// each evaluated once per query) — use instead of deep chains of binary unions.
    /// A single operand is returned unchanged.
    /// </summary>
    public static Sdf Union(IReadOnlyList<Sdf> children)
    {
        var copy = NaryChildren.Copy(children);
        return copy.Length == 1 ? copy[0] : new NaryUnionSdf(copy);
    }

    /// <inheritdoc cref="Union(IReadOnlyList{Sdf})"/>
    public static Sdf Union(params Sdf[] children) => Union((IReadOnlyList<Sdf>)children);

    /// <summary>
    /// Exact intersection of any number of solids as a single flat AST node (max over
    /// children, each evaluated once per query). A single operand is returned unchanged.
    /// </summary>
    public static Sdf Intersection(IReadOnlyList<Sdf> children)
    {
        var copy = NaryChildren.Copy(children);
        return copy.Length == 1 ? copy[0] : new NaryIntersectionSdf(copy);
    }

    /// <inheritdoc cref="Intersection(IReadOnlyList{Sdf})"/>
    public static Sdf Intersection(params Sdf[] children) => Intersection((IReadOnlyList<Sdf>)children);

    /// <summary>
    /// N-ary smooth union with fillet-like blend radius ~<paramref name="blend"/>: the
    /// polynomial smooth minimum folded over the children (each evaluated once per
    /// query). Coincides exactly with chained binary <see cref="SmoothUnion(Sdf, double)"/>;
    /// see <see cref="NarySmoothUnionSdf"/> for the formulation rationale and the
    /// lower-bound distance caveat near blend regions.
    /// </summary>
    public static Sdf SmoothUnion(IReadOnlyList<Sdf> children, double blend)
    {
        var copy = NaryChildren.Copy(children);
        return copy.Length == 1 ? copy[0] : new NarySmoothUnionSdf(copy, blend);
    }

    /// <summary>
    /// Union of <paramref name="a"/> and <paramref name="b"/> with a fillet-style blend:
    /// material is added where both surfaces lie within <paramref name="blendDistance"/>,
    /// weighted by the falloff <paramref name="kernel"/> (see <see cref="Falloff"/>).
    /// Converges to the plain union as <paramref name="blendDistance"/> → 0 (and is the
    /// plain union for <paramref name="blendDistance"/> ≤ 0). Correct sign everywhere;
    /// distance magnitude is a lower bound near the seam.
    /// </summary>
    public static Sdf Blend(Sdf a, Sdf b, double blendDistance, Falloff kernel = Falloff.Wyvill)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return blendDistance <= 0 ? a.Union(b) : new FalloffBlendSdf(a, b, blendDistance, kernel);
    }
}
