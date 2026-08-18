using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// The lateral surface of a TWISTED (and optionally tapered) extrusion — OpenSCAD's
/// <c>linear_extrude(twist, scale)</c> as an exact parametric surface rather than a swept
/// mesh.
///
/// <para>P(u, v) = R(θ·v) · S(v) · C(u) + h·v·ẑ, all in the axis frame: the generator
/// C(u) is scaled per axis by <c>S(v) = diag(lerp(1, sx, v), lerp(1, sy, v))</c> about the
/// frame origin, then rotated by <c>θ·v</c> about the frame's Z, then lifted by
/// <c>h·v</c>. Scale first, then rotate — the OpenSCAD composition, and the one the mesh
/// route already used, so the two representations describe ONE geometry.</para>
///
/// <para><b>Every derivative is closed form</b>, which is the whole reason this is a
/// surface type and not a tessellation trick:</para>
/// <list type="bullet">
/// <item>∂P/∂u = R(θv)·S(v)·C′(u) — the generator's own exact
/// <see cref="Curve3d.DerivativeAt"/>, carried through a linear map.</item>
/// <item>∂P/∂v = R(θv)·[S′·C(u) + θ·J·S(v)·C(u)] + h·ẑ, with J the quarter turn. (J
/// commutes with R, so it may be written on either side.)</item>
/// </list>
///
/// <para><b>Orientation.</b> S(v) is a positive diagonal and R(θv) a rotation, so the
/// section map is orientation-PRESERVING at every v: with the generator wound
/// counter-clockwise about the frame's Z, ∂u × ∂v points outward, exactly as it does for
/// <see cref="ExtrudedSurface"/> (which this reduces to at θ = 0, s = 1).</para>
///
/// <para><b>The axis frame is a parameter, not derived.</b> The twist axis and the
/// scaling centre are the same line — the frame's Z through its origin — and a profile's
/// own <see cref="Profile.Origin"/> is an arbitrary point on its boundary, so deriving the
/// axis from the generator would put the twist somewhere the caller did not ask for. The
/// caller states the frame (a sketch plane, in the modelling layer).</para>
///
/// <para><b>What has no exact answer here is stated rather than approximated.</b> A twist
/// makes the surface non-developable and non-ruled, so there is no closed-form
/// intersection with a plane or a quadric: those pairs fall to
/// <c>SurfaceIntersection</c>'s marching tracer, and the surface has no AP214 entity, so
/// <c>StepWriter</c> refuses it by name (the swept/helical bucket). <see cref="BrepArchive"/>
/// carries it losslessly.</para>
/// </summary>
public sealed class TwistedSurface : Surface
{
    private readonly Curve3d _generator;
    private readonly Frame3d _axis;
    private readonly double _height;
    private readonly double _twist;
    private readonly Vector2d _scaleTop;

    /// <summary>The section curve at v = 0 (a segment of the base profile).</summary>
    public Curve3d Generator => _generator;

    /// <summary>Twist axis and scaling centre: the frame's Z through its origin, with X
    /// and Y the axes the per-axis <see cref="ScaleTop"/> is measured on.</summary>
    public Frame3d Axis => _axis;

    /// <summary>Axial rise from v = 0 to v = 1, along the frame's Z.</summary>
    public double Height => _height;

    /// <summary>Total twist over the height, radians, right-handed about the frame's Z.</summary>
    public double Twist => _twist;

    /// <summary>Per-axis scale of the v = 1 section relative to the v = 0 one.</summary>
    public Vector2d ScaleTop => _scaleTop;

    /// <summary>
    /// Exact-zero semantic test (not a tolerance): a literally untwisted surface is a
    /// ruled taper, affine in v, and every consumer that asks this takes a cheaper exact
    /// path — a v-chord then lies ON the surface, so the natural grid collapses to two
    /// rows (the <see cref="LoftedSurface.IsAffineInV"/> rule) and no wall panel needs
    /// subdividing.
    /// </summary>
    public bool IsTwisted => _twist != 0;

    public TwistedSurface(
        Curve3d generator, in Frame3d axis, double height, double twist, Vector2d scaleTop)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (!double.IsFinite(height) || height == 0)
            throw new ArgumentOutOfRangeException(nameof(height), height,
                "A twisted extrusion needs a finite non-zero height.");
        if (!double.IsFinite(twist))
            throw new ArgumentOutOfRangeException(nameof(twist), twist, "The twist must be finite.");
        if (!(scaleTop.X > 0) || !(scaleTop.Y > 0))
            throw new ArgumentOutOfRangeException(nameof(scaleTop), scaleTop,
                "Top-section scale components must be positive; a zero component degenerates the "
                + "top section to a line or a point, which is a loft to a degenerate section rather "
                + "than a twisted extrusion.");
        _generator = generator;
        _axis = axis;
        _height = height;
        _twist = twist;
        _scaleTop = scaleTop;
    }

    public override Interval DomainU => _generator.Domain;
    public override Interval DomainV => Interval.Unit;

    public override Vector3d PointAt(double u, double v) =>
        MapLocal(_axis.ToLocal(_generator.PointAt(u)), v);

    /// <summary>
    /// The section transform at <paramref name="v"/> applied to a point stated in the axis
    /// frame's own coordinates: scale in x/y, rotate about z, lift by <c>h·v</c>.
    /// <para>ONE rule, three consumers — <see cref="PointAt"/>, <see cref="TwistedRailCurve"/>
    /// and <see cref="TransformTo"/> — so a rail edge and the grid column it must weld to
    /// are bit-for-bit the same points rather than two evaluations that agree.</para>
    /// </summary>
    public Vector3d MapLocal(in Vector3d local, double v)
    {
        double phi = _twist * v;
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double x = local.X * (1 + (_scaleTop.X - 1) * v);
        double y = local.Y * (1 + (_scaleTop.Y - 1) * v);
        return _axis.ToWorld(new Vector3d(
            x * cos - y * sin,
            x * sin + y * cos,
            local.Z + _height * v));
    }

    /// <summary>
    /// d/dv of <see cref="MapLocal"/> — exact: the scale term R(θv)·S′·p plus the rotation
    /// term θ·R(θv)·J·S(v)·p plus the axial rise h·ẑ.
    /// </summary>
    public Vector3d MapLocalDerivativeV(in Vector3d local, double v)
    {
        double phi = _twist * v;
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double x = local.X * (1 + (_scaleTop.X - 1) * v);
        double y = local.Y * (1 + (_scaleTop.Y - 1) * v);
        double dx = local.X * (_scaleTop.X - 1);
        double dy = local.Y * (_scaleTop.Y - 1);
        // The rotated point (x̂, ŷ); the twist term is θ·(−ŷ, x̂), i.e. θ·J applied to it.
        double rx = x * cos - y * sin;
        double ry = x * sin + y * cos;
        return _axis.ToWorldVector(new Vector3d(
            (dx * cos - dy * sin) - _twist * ry,
            (dx * sin + dy * cos) + _twist * rx,
            _height));
    }

    /// <summary>∂P/∂u — exact, from the generator's own <see cref="Curve3d.DerivativeAt"/>
    /// carried through the section's linear map (no finite differences anywhere).</summary>
    public Vector3d DerivativeU(double u, double v)
    {
        var slope = _axis.ToLocalVector(_generator.DerivativeAt(u));
        double phi = _twist * v;
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double x = slope.X * (1 + (_scaleTop.X - 1) * v);
        double y = slope.Y * (1 + (_scaleTop.Y - 1) * v);
        return _axis.ToWorldVector(new Vector3d(x * cos - y * sin, x * sin + y * cos, slope.Z));
    }

    /// <summary>∂P/∂v — exact; see <see cref="MapLocalDerivativeV"/>.</summary>
    public Vector3d DerivativeV(double u, double v) =>
        MapLocalDerivativeV(_axis.ToLocal(_generator.PointAt(u)), v);

    public override Vector3d NormalAt(double u, double v) =>
        DerivativeU(u, v).Cross(DerivativeV(u, v)).Normalized();

    /// <summary>
    /// The affine map taking v = 0 section geometry to the section at
    /// <paramref name="v"/> — what a solid factory hands to the shared swept-solid builder
    /// as its "top transform", so the top edges are the base curves MAPPED rather than
    /// rebuilt.
    /// </summary>
    public Matrix4d TransformTo(double v)
    {
        double phi = _twist * v;
        var local = Matrix4d.CreateTranslation((0, 0, _height * v))
                  * Matrix4d.CreateRotationZ(phi)
                  * Matrix4d.CreateScale((1 + (_scaleTop.X - 1) * v, 1 + (_scaleTop.Y - 1) * v, 1));
        // The frame's own inverse (a transpose, exact) rather than a general matrix
        // inversion: F·L·F⁻¹ is the section transform stated in world coordinates.
        return _axis.ToMatrix() * local * _axis.Inverse().ToMatrix();
    }

    /// <summary>
    /// The number of v rows a tessellator's natural grid needs at this density: enough
    /// that each row-to-row twist step matches the circular facet angle
    /// <c>2π/segmentsPerCircle</c>. A pure taper is affine in v, so ONE span is exact.
    /// <para>The rule lives on the surface because the grid and the RAIL edges that bound
    /// the face must agree exactly — <c>SampleEdge</c> asks the same method through
    /// <see cref="TwistedRailCurve.Surface"/>, the <see cref="LoftedSurface.NaturalUSegments"/>
    /// precedent.</para>
    /// </summary>
    public int NaturalVSegments(int segmentsPerCircle)
    {
        if (!IsTwisted)
            return 1;
        double step = 2 * Math.PI / Math.Max(3, segmentsPerCircle);
        // The epsilon guards Ceiling at exact integer boundaries: equal spans computed
        // through different arithmetic must not round apart (BRepTessellator's own rule).
        return Math.Max(2, (int)Math.Ceiling(Math.Abs(_twist) / step - 1e-9));
    }

    /// <summary>
    /// How many u segments a WALL PANEL of this surface needs at this density — the
    /// twist-matched profile subdivision, and the one rule the natural grid and every
    /// generator edge bounding the face both ask.
    ///
    /// <para><b>Why a straight generator needs interior samples at all.</b> A quad between
    /// two v rows is triangulated along a diagonal, and on a twisting wall the diagonal
    /// misses the true surface by ≈ ½·Δφ·L — FIRST order in the twist step, where the
    /// sagitta a curved surface's chord carries is second. (Take a side A→B rotating by
    /// Δφ between rows: the two triangles' shared diagonal joins A and R·B, so the panel
    /// centre sits at ½(A + R·B) against the true ½(R^½A + R^½B); the difference is
    /// ½(I − R)(A − B).) Measured on the mesh route before it was fixed, the volume
    /// deficit fell 286 → 33 → 8.2 for 8 → 64 → 256 slices: first order. Subdividing the
    /// panel to the arc a circle of that radius gets at this density makes the error
    /// second order like every other chord in the mesh.</para>
    ///
    /// <para><b>It is a property of the SURFACE, not of a curve handed in</b>, and that is
    /// what makes the weld hold: the face's u boundaries are the generator AND its v = 1
    /// image, and an ANISOTROPIC top scale changes that image's length-to-radius ratio, so
    /// asking each curve for its own count rounds the grid and the edge polyline apart
    /// (measured: a 0.5 × 1.5 taper with a quarter turn stopped matching its natural grid,
    /// fell to the trimmed path, and its volume oscillated 14090/14685/14399 instead of
    /// converging). Both sections are scanned and the finer count wins.</para>
    ///
    /// <para>Returns 1 when there is nothing to resolve (no twist), so an untwisted
    /// surface's sampling is exactly the incumbent extrusion's.</para>
    /// </summary>
    public int PanelSegments(int segmentsPerCircle)
    {
        var domain = _generator.Domain;
        if (!IsTwisted || !double.IsFinite(domain.Length))
            return 1;
        return Math.Max(SectionSegments(0), SectionSegments(1));

        int SectionSegments(double v)
        {
            // Polyline length and the largest radius from the twist axis, over one scan.
            const int samples = 32;
            double length = 0;
            var previous = PointAt(domain.Start, v);
            double radius = RadiusOf(previous);
            for (int i = 1; i <= samples; i++)
            {
                var point = PointAt(domain.ParameterAt((double)i / samples), v);
                length += previous.DistanceTo(point);
                radius = Math.Max(radius, RadiusOf(point));
                previous = point;
            }
            // A generator ON the axis sweeps nothing laterally, so there is no panel to
            // subdivide (exact-zero guard, not a tolerance).
            if (radius <= 0 || length <= 0)
                return 1;
            double maxPanel = radius * (2 * Math.PI / Math.Max(3, segmentsPerCircle));
            // The epsilon guards Ceiling at exact integer boundaries, as everywhere else.
            return Math.Max(1, (int)Math.Ceiling(length / maxPanel - 1e-9));
        }

        double RadiusOf(in Vector3d p)
        {
            var local = _axis.ToLocal(p);
            return Math.Sqrt(local.X * local.X + local.Y * local.Y);
        }
    }

    /// <summary>
    /// Inverse evaluation as TWO DECOUPLED ONE-DIMENSIONAL SOLVES, the family
    /// <see cref="ExtrudedSurface.TryProjectPoint"/>, <see cref="RevolvedSurface.TryProjectPoint"/>
    /// and <see cref="SweptSurface.TryProjectPoint"/> belong to.
    ///
    /// <para>The reduction: the section map moves a point only within its own plane, so
    /// the AXIAL coordinate of P(u, v) is <c>z(C(u)) + h·v</c> — and the generator is
    /// planar in the axis frame (every construction places it on a sketch plane), so
    /// <c>z(C(u))</c> is a constant and <b>v is fixed by the query point's axial
    /// coordinate alone</b>. With v known the section transform is known, so un-rotating
    /// and un-scaling the point gives a 2D target the generator must match: a second 1D
    /// solve in u, and neither involves the other's unknown.</para>
    ///
    /// <para>The generator's planarity is not ASSUMED, it is CORRECTED: after solving u
    /// the axial coordinate is re-read at that u and v re-derived, then u re-solved. For
    /// a planar generator the correction is a no-op (the recomputed v is the same
    /// number); for one that is not, it is an ordinary fixed-point step. A query that
    /// still misses defers to the base class's grid, so "the override is never worse than
    /// the base" holds by construction — the rule the whole family follows.</para>
    /// </summary>
    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        var target = point; // local copy: `in` parameters cannot be captured by local functions
        var local = _axis.ToLocal(target);
        var domain = _generator.Domain;
        bool periodic = _generator.IsClosed;
        double period = domain.Length;
        if (!double.IsFinite(period))
            return base.TryProjectPoint(point, out uv, tolerance);

        // Seed the axial datum from the generator's own middle; for a planar generator
        // (every construction) this IS its constant axial coordinate.
        double axialDatum = _axis.ToLocal(_generator.PointAt(domain.Mid)).Z;
        double v = (local.Z - axialDatum) / _height;
        double u = domain.Start;

        for (int round = 0; round < 2; round++)
        {
            double clamped = Interval.Unit.Clamp(v);
            u = SolveU(clamped);
            double axial = _axis.ToLocal(_generator.PointAt(u)).Z;
            v = (local.Z - axial) / _height;
        }
        uv = new Vector2d(u, Interval.Unit.Clamp(v));
        if ((PointAt(uv.X, uv.Y) - target).Length < tolerance)
            return true;
        return base.TryProjectPoint(point, out uv, tolerance);

        // The generator parameter whose section-mapped point matches the query, at a
        // known v: un-rotate and un-scale the target into generator coordinates, then
        // match the generator's own (x, y) there.
        double SolveU(double atV)
        {
            double phi = _twist * atV;
            double cos = Math.Cos(phi), sin = Math.Sin(phi);
            double sx = 1 + (_scaleTop.X - 1) * atV;
            double sy = 1 + (_scaleTop.Y - 1) * atV;
            // Positive by construction (both endpoints positive and the lerp is monotone).
            var wanted = new Vector2d(
                (local.X * cos + local.Y * sin) / sx,
                (-local.X * sin + local.Y * cos) / sy);

            const int seeds = 16; // the base class's u resolution
            Span<double> sampled = stackalloc double[seeds + 1];
            for (int i = 0; i <= seeds; i++)
                sampled[i] = (Planar(domain.ParameterAt((double)i / seeds)) - wanted).LengthSquared;

            // Refine from every local minimum AND its neighbours (SeedSelection): a
            // generator that doubles back hides two branches inside one seed interval,
            // and a single seed silently returns the mirrored parameter.
            Span<bool> refine = stackalloc bool[seeds + 1];
            int globalBest = SeedSelection.MarkCandidates(sampled, refine, periodic);
            double answer = domain.ParameterAt((double)globalBest / seeds);
            double best = double.PositiveInfinity;
            for (int i = 0; i <= seeds; i++)
            {
                if (!refine[i])
                    continue;
                double candidate = Refine(domain.ParameterAt((double)i / seeds), wanted);
                double residual = (Planar(candidate) - wanted).LengthSquared;
                if (residual < best)
                {
                    best = residual;
                    answer = candidate;
                }
            }
            return answer;
        }

        Vector2d Planar(double t)
        {
            var q = _axis.ToLocal(_generator.PointAt(t));
            return new Vector2d(q.X, q.Y);
        }

        Vector2d PlanarSlope(double t)
        {
            var d = _axis.ToLocalVector(_generator.DerivativeAt(t));
            return new Vector2d(d.X, d.Y);
        }

        double Refine(double seed, in Vector2d wanted)
        {
            for (int iteration = 0; iteration < 12; iteration++)
            {
                var residual = Planar(seed) - wanted;
                var slope = PlanarSlope(seed);
                double denominator = slope.LengthSquared;
                // Degenerate-Jacobian guard: a scale-free near-underflow test, not a
                // model tolerance (the generator's tangent is parallel to the axis here).
                if (denominator < 1e-30)
                    break;
                double next = FoldIntoDomain(seed - slope.Dot(residual) / denominator, domain, periodic);
                // Stall guard at relative machine precision.
                if (Math.Abs(next - seed) <= period * 1e-15)
                {
                    seed = next;
                    break;
                }
                seed = next;
            }
            return seed;
        }
    }
}

/// <summary>
/// The path a fixed section point traces up a twisted extrusion (a twist "rail") — the
/// analogue of <see cref="SweptRailCurve"/> and <see cref="LoftRailCurve"/>.
///
/// <para>It is stated in the surface's AXIS-FRAME coordinates rather than as a generator
/// parameter, because the rails of a multi-segment profile are shared between two side
/// faces whose surfaces carry DIFFERENT generators: one master surface supplies the
/// section transform for every rail (the <see cref="SweptRailCurve"/> arrangement), so
/// both faces' u = 0 / u = 1 grid columns and the rail edge between them are the same
/// points bit-for-bit.</para>
/// </summary>
public sealed class TwistedRailCurve(TwistedSurface surface, Vector3d localBase) : Curve3d
{
    /// <summary>The twisted extrusion this rail rides (its generator is irrelevant here —
    /// only its axis frame, height, twist and scale are).</summary>
    public TwistedSurface Surface => surface;

    /// <summary>The fixed section point, in the surface's axis-frame coordinates.</summary>
    public Vector3d LocalBase => localBase;

    public override Interval Domain => surface.DomainV;
    public override bool IsClosed => false;
    public override Vector3d PointAt(double t) => surface.MapLocal(localBase, t);
    public override Vector3d DerivativeAt(double t) => surface.MapLocalDerivativeV(localBase, t);
}
