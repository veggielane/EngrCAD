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

    /// <summary>
    /// An upper bound on this field's Lipschitz constant over <paramref name="region"/>:
    /// the largest amount the reported value can change per unit of movement, anywhere in
    /// that region. <b>1 by default</b>, which is the whole engine's standing contract —
    /// every primitive here is an exact distance and every operator built before the domain
    /// operators either combines values (min/max/smooth-min, a convex combination of the
    /// operands' gradients) or moves points by an isometry.
    /// <para>
    /// It exists because the domain operators break that contract on purpose. A twist, a
    /// bend and a taper compress or stretch space, so the composed field's value changes
    /// faster than the point moves, and every consumer that reasons "a value of |d| proves
    /// there is no surface within |d|" — <c>SurfaceNets</c>' block cull, the narrow-band
    /// octree, the projection target's step — would then be unsound in the one direction
    /// that matters: it would silently drop geometry. Reporting the bound lets each of them
    /// widen by exactly the right factor.
    /// </para>
    /// <para>
    /// <b>Why the bound takes a region</b> rather than being a plain number: for a twist the
    /// local factor grows with the query point's distance from the axis, so no finite
    /// constant is valid over all of space. A consumer knows the region it is about to
    /// sample, asks once, and gets a number valid everywhere inside it. An unbounded region
    /// legitimately yields <see cref="double.PositiveInfinity"/>, and a consumer must treat
    /// that as "cull nothing" rather than as an error.
    /// </para>
    /// <para>
    /// <b>Overriding is mandatory for any node that wraps a child</b>, even when the node
    /// itself is 1-Lipschitz: a wrapper that inherits the default silently claims 1 for a
    /// twisted subtree. The bound is verified numerically over the whole node catalogue by
    /// <c>LipschitzBoundTests</c>, which measures secants and asserts the reported bound
    /// covers them.
    /// </para>
    /// </summary>
    public virtual double LipschitzBound(in Aabb region) => 1;

    /// <summary>
    /// The compilation seam: emit this node's value as a LINQ expression over the builder's
    /// current coordinates, so <see cref="Compile"/> can flatten the whole AST into one
    /// delegate. The default returns a call back into this node's own
    /// <see cref="Evaluate(in Vector3d)"/>, which is exact but does not flatten — that is the
    /// right answer for a node whose evaluation is a loop, a table lookup or a search
    /// (a sampled grid, a thread, a planar region, a mesh field).
    /// <para>
    /// An override must mirror this node's scalar <see cref="Evaluate(in Vector3d)"/> term
    /// for term in the same association order, exactly as an
    /// <see cref="EvaluateBatch"/> override must — and for the same reason. That the override
    /// lives beside the scalar form rather than in a switch inside the compiler is the point:
    /// two copies of a formula in different files drift, and this one is required to be
    /// bit-identical.
    /// </para>
    /// <para>
    /// <b>Internal on purpose.</b> A node defined outside this assembly — <c>MeshSdf</c> is
    /// the live example — cannot override it and takes the fallback, which is right rather
    /// than merely convenient: a BVH nearest-triangle search is not an arithmetic expression,
    /// so there is nothing to flatten, and a public seam would advertise otherwise while
    /// widening the API surface to the whole builder vocabulary.
    /// </para>
    /// </summary>
    internal virtual System.Linq.Expressions.Expression BuildExpression(SdfExpression builder) =>
        builder.Fallback(this);

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

    /// <summary>
    /// Box centred at the origin with the given full side lengths, its edges and corners
    /// rounded by <paramref name="radius"/>. <b>Exact distance</b> — it is the box of
    /// half-extents (size/2 − radius) offset outward by the radius, which is the definition
    /// of the rounding, so no approximation enters.
    /// </summary>
    public static Sdf RoundedBox(double sizeX, double sizeY, double sizeZ, double radius)
    {
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Corner radius must be non-negative.");
        var half = new Vector3d(sizeX / 2 - radius, sizeY / 2 - radius, sizeZ / 2 - radius);
        if (half.X < 0 || half.Y < 0 || half.Z < 0)
            throw new ArgumentOutOfRangeException(nameof(radius),
                $"Corner radius {radius} exceeds half the smallest side of the " +
                $"{sizeX} x {sizeY} x {sizeZ} box.");
        return new RoundedBoxSdf(half, radius);
    }

    /// <summary>
    /// Axis-aligned ellipsoid with the given semi-axes. <b>A bound, not an exact distance</b>
    /// — a point's distance to an ellipsoid is a quartic root with no closed form, so this is
    /// Quilez's scaled-implicit approximation, whose relative error grows with eccentricity
    /// (measured: see <see cref="EllipsoidSdf"/> and the implicit engine's README for the
    /// numbers, and note the value is <em>not</em> a one-sided bound). The sign is exact
    /// everywhere, and equal semi-axes reduce the expression to the sphere's exact distance
    /// bit for bit.
    /// </summary>
    public static Sdf Ellipsoid(double semiAxisX, double semiAxisY, double semiAxisZ)
    {
        if (semiAxisX <= 0 || semiAxisY <= 0 || semiAxisZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(semiAxisX), "Semi-axes must be positive.");
        return new EllipsoidSdf(new Vector3d(semiAxisX, semiAxisY, semiAxisZ));
    }

    /// <summary>
    /// Square-based pyramid along +Z: the base is <paramref name="baseSize"/> square in the
    /// z = 0 plane, the apex sits at (0, 0, <paramref name="height"/>). Exact distance
    /// (Quilez's <c>sdPyramid</c>, converted to Z-up and scaled off the unit form).
    /// </summary>
    public static Sdf Pyramid(double baseSize, double height)
    {
        if (baseSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(baseSize));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        return new PyramidSdf(baseSize, height);
    }

    /// <summary>
    /// The convex hull of two spheres — a capsule whose two ends may differ in radius, so
    /// the side is a cone tangent to both caps. Along Z: radius
    /// <paramref name="bottomRadius"/> at z = 0 and <paramref name="topRadius"/> at
    /// z = <paramref name="height"/>. <b>Exact distance</b> (Quilez's <c>sdRoundCone</c>).
    /// </summary>
    public static Sdf RoundCone(double bottomRadius, double topRadius, double height)
    {
        if (bottomRadius <= 0 || topRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(bottomRadius), "Radii must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (Math.Abs(bottomRadius - topRadius) >= height)
            throw new ArgumentOutOfRangeException(nameof(height),
                $"One sphere contains the other: |{bottomRadius} - {topRadius}| must be less than the " +
                $"centre separation {height}.");
        return new RoundConeSdf(bottomRadius, topRadius, height);
    }

    /// <summary>
    /// A chain link: a torus of major radius <paramref name="majorRadius"/> and minor radius
    /// <paramref name="minorRadius"/> about the Z axis, pulled apart along Y so its two
    /// straight runs are <paramref name="halfLength"/> long either side of the origin.
    /// <b>Exact distance</b> (Quilez's <c>sdLink</c>): the elongation splits the torus's own
    /// exact field along one axis, which is an isometry-per-piece and so loses nothing.
    /// </summary>
    public static Sdf Link(double majorRadius, double minorRadius, double halfLength)
    {
        if (majorRadius <= 0 || minorRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(majorRadius), "Radii must be positive.");
        if (halfLength < 0)
            throw new ArgumentOutOfRangeException(nameof(halfLength), "Half-length must be non-negative.");
        return new LinkSdf(majorRadius, minorRadius, halfLength);
    }

    /// <summary>
    /// Regular <paramref name="sides"/>-gon prism along Z, centred at the origin: the
    /// polygon has circumradius <paramref name="circumradius"/> with a vertex on +X, and the
    /// prism spans z ∈ [−height/2, +height/2]. <b>Exact distance</b> — the 2D section
    /// distance is exact (nearest point over the polygon's own edges, sign by half-plane
    /// membership) and the axial combination is the cylinder's, which is exact for any
    /// convex section. <c>sides: 3</c> and <c>sides: 6</c> are the triangular and hexagonal
    /// prisms.
    /// </summary>
    public static Sdf Prism(int sides, double circumradius, double height)
    {
        if (sides < 3)
            throw new ArgumentOutOfRangeException(nameof(sides), "A prism needs at least three sides.");
        if (circumradius <= 0)
            throw new ArgumentOutOfRangeException(nameof(circumradius));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        var polygon = new Vector2d[sides];
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides;
            polygon[i] = new Vector2d(circumradius * Math.Cos(a), circumradius * Math.Sin(a));
        }
        return new ConvexPrismSdf(polygon, height / 2, PrismAxis.Z);
    }

    /// <summary>
    /// Rectangular wedge along +Z, centred at the origin — the field twin of
    /// <c>Shape.Wedge</c> and of OCCT's <c>BRepPrimAPI_MakeWedge</c>, with the same
    /// parameters: the base at z = −sizeZ/2 is <paramref name="sizeX"/> ×
    /// <paramref name="sizeY"/>, and the top at z = +sizeZ/2 keeps the same y but is
    /// <paramref name="topX"/> wide, centred at x = <paramref name="topOffsetX"/>.
    /// <b>Exact distance</b>: the XZ section is a convex trapezoid whose 2D distance is
    /// exact, swept along Y.
    /// </summary>
    public static Sdf Wedge(double sizeX, double sizeY, double sizeZ, double topX = 0, double topOffsetX = 0)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeX), "Wedge sides must be positive.");
        if (topX < 0)
            throw new ArgumentOutOfRangeException(nameof(topX), "The top width cannot be negative.");
        double hx = sizeX / 2, hz = sizeZ / 2;
        double t0 = topOffsetX - topX / 2, t1 = topOffsetX + topX / 2;
        // Section in the XZ plane, counter-clockwise in (x, z).
        Vector2d[] section = topX > 0
            ? [new(-hx, -hz), new(hx, -hz), new(t1, hz), new(t0, hz)]
            : [new(-hx, -hz), new(hx, -hz), new(t1, hz)];
        return new ConvexPrismSdf(section, sizeY / 2, PrismAxis.Y);
    }

    /// <summary>
    /// The convex solid bounded by the given half-spaces (each solid where
    /// <c>dot(normal, p) ≤ offset</c>) — the general plane-bounded convex primitive, and the
    /// escape hatch for shapes with no factory.
    /// <para>
    /// Fidelity is the max-of-half-spaces field: <b>exact inside</b> (the distance to the
    /// nearest face plane IS the distance to the boundary for a convex body) and a
    /// <b>lower bound outside</b>, which understates near an edge or a corner because the
    /// nearest feature is then an edge rather than a face. That is exactly the engine's
    /// standing contract — correct sign everywhere, magnitude never nearer than the truth —
    /// so it meshes, culls and offsets soundly.
    /// </para>
    /// <para>
    /// Unlike <c>Sdf.Intersection</c> over the same half-spaces, this reports <b>finite
    /// bounds</b>: the vertices are enumerated from every triple of planes and kept when they
    /// satisfy all the others, so a polygonizer can size its own region. A set of planes that
    /// does not enclose a bounded region is refused by name.
    /// </para>
    /// </summary>
    public static Sdf ConvexPolyhedron(IReadOnlyList<(Vector3d Normal, double Offset)> halfSpaces) =>
        ConvexPolyhedronSdf.Create(halfSpaces);

    // ---- domain operations ----

    /// <summary>
    /// Repeats this solid on an infinite lattice with the given per-axis
    /// <paramref name="spacing"/>; an axis whose spacing is 0 is not repeated. The domain map
    /// is a translation per cell, so it is an <b>isometry and distances stay exact</b> —
    /// unlike the twist/bend/taper family below.
    /// <para>
    /// The child's bounds must fit within one cell along every repeated axis, and that is
    /// enforced rather than assumed: outside that condition a query point can be inside an
    /// instance the evaluation never looks at, and the <em>sign</em> would be wrong. Within
    /// it, this node evaluates the child once per neighbouring cell along each repeated axis
    /// (two per axis, so two, four or eight evaluations) and takes the minimum — the single
    /// nearest-cell form every shader-toy implementation uses is <em>discontinuous</em> at a
    /// cell boundary for a child that is not symmetric about its cell centre, which would
    /// make the field non-Lipschitz and the polygonizer's cull unsound.
    /// </para>
    /// <para>Bounds are infinite along every repeated axis.</para>
    /// </summary>
    public Sdf Repeat(in Vector3d spacing) => RepeatSdf.Create(this, spacing, null);

    /// <summary>
    /// Repeats this solid a bounded number of times: <paramref name="counts"/> instances
    /// along each axis, the first at the child's own position and the rest at successive
    /// multiples of <paramref name="spacing"/> along +axis (the linear-pattern convention).
    /// A count of 1 (or less) leaves that axis alone. Bounds are finite, so a limited
    /// repetition polygonizes directly. See <see cref="Repeat(in Vector3d)"/> for the
    /// fidelity contract and the fits-in-a-cell precondition — both are identical here.
    /// </summary>
    public Sdf Repeat(in Vector3d spacing, in Vector3i counts) => RepeatSdf.Create(this, spacing, counts);

    /// <summary>
    /// Twists this solid about the Z axis: the slice at height z is rotated by
    /// <paramref name="radiansPerUnit"/> · z.
    /// <para>
    /// <b>Distance fidelity: a bound, and the field is not 1-Lipschitz.</b> The domain map is
    /// a rotation whose angle depends on z, which is not an isometry — it shears space — so
    /// the value <em>over-estimates</em> the true distance by up to the factor
    /// <see cref="LipschitzBound"/> reports, and that factor grows with the query point's
    /// distance from the axis. The sign stays exact everywhere. Every consumer that reasons
    /// from |d| asks for the bound, so meshing stays sound; do not sphere-trace this field or
    /// offset it by more than the surface neighbourhood without dividing by the bound
    /// yourself.
    /// </para>
    /// <para>Deliberately scalar-only in the batch path: the map needs
    /// <see cref="Math.Sin"/>/<see cref="Math.Cos"/> and no vector transcendental reproduces
    /// them bit for bit — the same verdict that keeps the gyroid scalar. The child below it
    /// still vectorizes, which is where the time goes.</para>
    /// </summary>
    public Sdf Twist(double radiansPerUnit) => new TwistSdf(this, radiansPerUnit);

    /// <summary>
    /// Bends this solid about the Z axis: the slice at x is rotated in the XY plane by
    /// <paramref name="curvature"/> · x, so a bar along +X curves into an arc of radius
    /// 1/<paramref name="curvature"/> while its Z extent is untouched — the sheet-metal
    /// gesture, in field form.
    /// <para>Same fidelity contract as <see cref="Twist"/>: a bound rather than a distance,
    /// a reported Lipschitz factor above 1, an exact sign, and a scalar-only batch path.</para>
    /// </summary>
    public Sdf Bend(double curvature) => new BendSdf(this, curvature);

    /// <summary>
    /// Tapers this solid along Z: its cross-section is scaled by
    /// <paramref name="bottomScale"/> at the bottom of the child's own bounds and by
    /// <paramref name="topScale"/> at the top, interpolated linearly between and <b>held
    /// constant beyond</b> — which is what keeps the scale strictly positive everywhere and
    /// so keeps the domain map finite at every query point rather than only inside the model.
    /// <para>Same fidelity contract as <see cref="Twist"/> (a bound, a reported Lipschitz
    /// factor, an exact sign), except that the map is division-only, so the batch path
    /// vectorizes. Requires a child with a finite Z extent.</para>
    /// </summary>
    public Sdf Taper(double bottomScale, double topScale) => new TaperSdf(this, bottomScale, topScale);

    /// <summary>
    /// Stretches this solid by splitting it at the origin and pulling the halves
    /// <paramref name="amounts"/> apart along each axis — the standard way to turn a sphere
    /// into a capsule or a rounded box without a second primitive.
    /// <para>The domain map <c>p − clamp(p, −h, h)</c> is 1-Lipschitz per component, so the
    /// field <b>keeps the child's own fidelity and its Lipschitz bound</b>; there is no
    /// widening here. It stretches the child <em>about the origin</em>, so a child that does
    /// not straddle the origin leaves the middle stretch hollow — that is the operation, not
    /// a defect.</para>
    /// </summary>
    public Sdf Elongate(in Vector3d amounts) => new ElongateSdf(this, amounts);

    /// <summary>
    /// Adds a sinusoidal ripple of the given <paramref name="amplitude"/> and per-axis
    /// <paramref name="frequency"/> (radians per unit) to this field — the classic
    /// surface-displacement modifier, for knurling and texture.
    /// <para>
    /// <b>This is the one operator here that is not a distance at all.</b> It adds a value
    /// rather than moving a point, so the solid it defines is exactly <c>{d + ripple &lt; 0}</c>
    /// — the sign is exact <em>for that solid</em> by definition — while the magnitude is
    /// only a bound and the Lipschitz constant rises by <c>amplitude · |frequency|</c>.
    /// Bounds grow by the amplitude, which is right wherever the child's magnitude is a true
    /// distance; a child whose field under-reports far from its surface (a CSG difference
    /// near the subtracted tool's fictitious faces) can have the ripple raise material
    /// outside those bounds, so displace close to the geometry you mean.
    /// </para>
    /// <para>Scalar-only in the batch path (<see cref="Math.Sin"/>), like the gyroid; the
    /// child underneath still vectorizes.</para>
    /// </summary>
    public Sdf Displace(double amplitude, in Vector3d frequency) => new DisplaceSdf(this, amplitude, frequency);

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
    /// <param name="leftHand">Wind the thread left-handed — the exact mirror image of
    /// the right-hand form (the helical phase reads z + P·θ/2π instead of z − P·θ/2π),
    /// so every fidelity guarantee above holds unchanged.</param>
    public static Sdf Thread(
        double majorRadius, double minorRadius, double pitch,
        double crestWidth, double rootWidth, double length,
        double profileOffset = 0, double startChamfer = 0, double endChamfer = 0,
        bool leftHand = false) =>
        new ThreadSdf(majorRadius, minorRadius, pitch, crestWidth, rootWidth, length,
            profileOffset, startChamfer, endChamfer, leftHand);

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
    /// ASTs (mesh SDFs, deep CSG trees) queried many times over the same region.
    /// <para>
    /// With <paramref name="lazy"/> the grid becomes <b>sparse</b>: 16³-sample blocks are
    /// materialized on first touch instead of up front (thread-safe; pays only for regions
    /// actually probed), and the block table itself is two-level, so indexing a huge domain
    /// costs kilobytes instead of the 8 bytes per never-touched block a flat table charges.
    /// That is also the only overload that works past ~1290³ samples: a dense bake needs one
    /// contiguous <c>double[]</c> and is capped by <see cref="int"/> addressing, whereas a
    /// lazy grid over a 4096³ domain holds only the blocks a query actually reaches.
    /// </para>
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

    // ---- compilation ----

    /// <summary>
    /// Compiles this AST to a single flat delegate (LINQ expression tree → IL → JIT) and
    /// returns a node that evaluates it, so a deep CSG tree costs one call instead of one
    /// virtual dispatch per node per query. Composes like any other node: the result is an
    /// <see cref="Sdf"/> with the same <see cref="Bounds"/> and the same
    /// <see cref="LipschitzBound"/>.
    /// <para>
    /// <b>Bit-for-bit identical to the scalar path</b>, by construction rather than by
    /// testing: the emitted expression is the node's own scalar expression, term for term,
    /// calling the same <see cref="Math"/> methods in the same association order. A node the
    /// compiler does not know how to inline (a sampled grid, a thread, a planar region, a
    /// mesh field from another assembly) is emitted as a call to its own
    /// <see cref="Evaluate(in Vector3d)"/>, so compilation always succeeds and is always
    /// exact — it simply stops paying off for that subtree.
    /// </para>
    /// <para>
    /// <b>Read <c>SdfCompiler</c>'s remarks before reaching for this.</b> The measured
    /// answer is that it beats the scalar path on deep trees and <em>loses to the SIMD batch
    /// path</em>, which is what every bulk consumer already uses; it is for callers stuck
    /// with per-point queries.
    /// </para>
    /// </summary>
    public Sdf Compile() => SdfCompiler.Compile(this);
}
