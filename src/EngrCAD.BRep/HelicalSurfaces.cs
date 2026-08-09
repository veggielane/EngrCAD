using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Conical spiral arc about the Z axis of <see cref="Frame"/>: in the frame's cylindrical
/// coordinates the angle IS the parameter, and both the radius and the axial coordinate
/// are LINEAR in it —
/// P(t) = O + X·r(t)·cos t + Y·r(t)·sin t + Z·z(t), with
/// r(t) = <see cref="RadiusAtZero"/> + <see cref="Slope"/>·t and
/// z(t) = <see cref="AxialAtZero"/> + <see cref="AxialRate"/>·t.
///
/// <para><b>This one shape is every curve a coaxial straight-generator surface of
/// revolution cuts from a <see cref="HelicalSurface"/>.</b> Write the band as
/// r = r₀ + dr·v, z = z₀ + dz·v + rate·u and the carrier as r = a + b·z: substituting
/// gives v·(dr − b·dz) = (a + b·z₀ − r₀) + b·rate·u, so v is linear in u, and therefore
/// so are r and z. The special cases fall out of the one formula — a plane perpendicular
/// to the axis (b = 0 in z, i.e. the cap cuts of <c>MakeThreadedRod</c>) leaves
/// <see cref="AxialRate"/> zero and the arc planar; a coaxial CONE (a thread's 45° end
/// chamfer) gives the general form; a zero <see cref="Slope"/> degenerates to a circular
/// or helical arc. Keeping them one type is what lets every cap and chamfer edge of a
/// thread share a single exact sampling rule.</para>
///
/// <para>Built on the surface's own axis frame so the spiral's parameter IS the surface's
/// u (the phase-alignment rule: tessellation samples of the edge and of the surface grid
/// coincide exactly). All derivatives are analytic (never finite differences).</para>
/// </summary>
public sealed class SpiralArc3d : Curve3d
{
    /// <summary>A PLANAR spiral arc in the frame's X/Y plane (no axial advance).</summary>
    public SpiralArc3d(in Frame3d frame, double radiusAtZero, double slope, Interval domain)
        : this(frame, radiusAtZero, slope, 0, 0, domain)
    {
    }

    /// <summary>
    /// The general conical spiral: <paramref name="axialAtZero"/> and
    /// <paramref name="axialRate"/> give the axial coordinate's own linear law in the
    /// angle. Both zero reproduces the planar overload exactly.
    /// </summary>
    public SpiralArc3d(
        in Frame3d frame, double radiusAtZero, double slope,
        double axialAtZero, double axialRate, Interval domain)
    {
        if (!double.IsFinite(radiusAtZero) || !double.IsFinite(slope))
            throw new ArgumentOutOfRangeException(nameof(radiusAtZero), "Spiral coefficients must be finite.");
        if (!double.IsFinite(axialAtZero) || !double.IsFinite(axialRate))
            throw new ArgumentOutOfRangeException(nameof(axialAtZero), "Spiral coefficients must be finite.");
        if (!double.IsFinite(domain.Length) || domain.Length <= 0)
            throw new ArgumentOutOfRangeException(nameof(domain), "Spiral arcs need a finite, non-empty domain.");
        // The radius is linear in t, so positivity at both ends covers the whole arc.
        if (!(radiusAtZero + slope * domain.Start > 0) || !(radiusAtZero + slope * domain.End > 0))
            throw new ArgumentOutOfRangeException(nameof(radiusAtZero),
                "The spiral radius must stay positive over the domain.");
        Frame = frame;
        RadiusAtZero = radiusAtZero;
        Slope = slope;
        AxialAtZero = axialAtZero;
        AxialRate = axialRate;
        _domain = domain;
    }

    private readonly Interval _domain;

    /// <summary>The axis frame: the angle is measured from X, the advance is along Z.</summary>
    public Frame3d Frame { get; }

    /// <summary>Radius extrapolated to t = 0 (which may lie outside <see cref="Domain"/>).</summary>
    public double RadiusAtZero { get; }

    /// <summary>Radial growth per radian; 0 makes the arc circular or helical.</summary>
    public double Slope { get; }

    /// <summary>Axial coordinate extrapolated to t = 0.</summary>
    public double AxialAtZero { get; }

    /// <summary>Axial advance per radian; 0 keeps the arc in the frame's X/Y plane.</summary>
    public double AxialRate { get; }

    /// <summary>
    /// Whether the arc lies in a plane PERPENDICULAR TO THE AXIS — the cap-cut case, and
    /// the property a consumer actually needs (<c>BRepTessellator</c>'s full-helical-band
    /// gate turns on it). Note this is a weaker condition than sitting in the frame's own
    /// X/Y plane: a cut at a constant nonzero height is still planar.
    /// </summary>
    public bool IsPlanar => AxialRate == 0;

    /// <summary>
    /// The stricter condition, and it exists only to keep bits: the frame's X/Y plane
    /// itself, where the axial term can be omitted from the arithmetic entirely. Adding a
    /// zero VECTOR is not a no-op on a −0.0 coordinate, and every cap loop of every
    /// threaded rod is built on that path.
    /// </summary>
    private bool InFramePlane => AxialAtZero == 0 && AxialRate == 0;

    public override Interval Domain => _domain;
    public override bool IsClosed => false;

    /// <summary>Radius at angle <paramref name="t"/>.</summary>
    public double RadiusAt(double t) => RadiusAtZero + Slope * t;

    /// <summary>Axial coordinate at angle <paramref name="t"/>.</summary>
    public double AxialAt(double t) => AxialAtZero + AxialRate * t;

    public override Vector3d PointAt(double t)
    {
        double r = RadiusAt(t);
        var p = Frame.Origin + Frame.X * (r * Math.Cos(t)) + Frame.Y * (r * Math.Sin(t));
        // Exact-zero SEMANTIC test, not a tolerance — see InFramePlane.
        return InFramePlane ? p : p + Frame.Z * AxialAt(t);
    }

    public override Vector3d DerivativeAt(double t)
    {
        double r = RadiusAt(t), c = Math.Cos(t), s = Math.Sin(t);
        var d = Frame.X * (Slope * c - r * s) + Frame.Y * (Slope * s + r * c);
        return InFramePlane ? d : d + Frame.Z * AxialRate;
    }

    public override Vector3d SecondDerivativeAt(double t)
    {
        // The axial law is linear, so it contributes nothing to the second derivative —
        // no branch needed and none possible to get wrong.
        double r = RadiusAt(t), c = Math.Cos(t), s = Math.Sin(t);
        return Frame.X * (-2 * Slope * s - r * c) + Frame.Y * (2 * Slope * c - r * s);
    }

    public override Vector3d TangentAt(double t) => DerivativeAt(t).Normalized();
}

/// <summary>
/// The co-rotating sweep of a profile GENERATOR along a helix — one facet band of a screw
/// thread. In the frame's cylindrical coordinates,
/// P(u, v) = O + X·r(v)·cos u + Y·r(v)·sin u + Z·(z(v) + p·u/2π), where (r(v), z(v)) walks
/// the generator in the axial half-plane through +X at u = 0 and p is the
/// <see cref="Pitch"/>. u is the turning angle over the finite <see cref="DomainU"/>
/// given at construction (NOT periodic — the axial advance makes every u distinct, so
/// inverse evaluation never wraps a seam); v ∈ [0, 1] along the generator.
/// Like <see cref="RevolvedSurface"/>, a generator traversed with increasing axial
/// coordinate (dz &gt; 0) makes ∂u × ∂v point away from the axis:
/// ∂u × ∂v ∝ r·dz·r̂-terms − r·dr·ẑ (see <see cref="NormalAt"/>) — radially outward on
/// flats, tilted axially on flanks. Points, normals, and inverse evaluation are all
/// exact closed forms (the weld rule: no finite differences, no projected parameters).
///
/// <para><b>The generator is a straight segment or a circular ARC</b>, and the arc is not a
/// generalization for its own sake: a printing CLEARANCE is a distance-field offset of the
/// (radius, axial) profile, and eroding a thread form miters its crest corners (still
/// straight) while ROUNDING its root corners into arcs of the clearance radius. So a
/// clearance thread's lateral boundary is still ONE boolean-free helical sweep, of a
/// generator that mixes both pieces — which is what makes it exact in B-Rep at all.
/// <see cref="IsStraightGenerator"/> is the exact-zero test (<see cref="ArcRadius"/> is
/// bit-zero for a segment), and every consumer whose correctness rests on straightness asks
/// it rather than assuming: the coaxial intersection family's whole derivation is "v is
/// linear in u", and <c>BRepTessellator</c>'s v step is infinite for a straight generator
/// because a v-chord then lies exactly on the surface.</para>
///
/// <para><b>An arc generator's axial coordinate must be strictly monotone</b> — equivalently
/// cos φ keeps one sign over the sweep — and the constructor refuses anything else BY NAME.
/// That is the correctness condition rather than caution: solving the carrier equation for
/// the generator angle gives cos(φ − ψ) = affine(u), whose two branches ±acos separate
/// exactly when the arc stays inside one half-turn about ψ, and the cap plane's ψ is π/2, so
/// "single-branch cap cut" and "z monotone" are the same statement. It is also the same
/// contract <c>MakeThreadedRod</c> already states for its corners, read along the piece
/// rather than only at its ends.</para>
/// </summary>
public sealed class HelicalSurface : Surface
{
    /// <summary>A band whose generator is the straight segment from
    /// <paramref name="profileStart"/> to <paramref name="profileEnd"/>.</summary>
    public HelicalSurface(in Frame3d frame, in Vector2d profileStart, in Vector2d profileEnd, double pitch, Interval domainU)
    {
        if (!(profileStart.X > 0) || !(profileEnd.X > 0))
            throw new ArgumentOutOfRangeException(nameof(profileStart),
                "The generator must stay off the axis (both profile radii positive).");
        ValidateShared(pitch, domainU);
        if ((profileStart - profileEnd).Length <= 0)
            throw new ArgumentException("The profile segment must be non-degenerate.", nameof(profileEnd));
        Frame = frame;
        ProfileStart = profileStart;
        ProfileEnd = profileEnd;
        Pitch = pitch;
        _domainU = domainU;
    }

    /// <summary>
    /// A band whose generator is the circular ARC of <paramref name="arcRadius"/> about
    /// <paramref name="arcCenter"/> in the (radius, axial) half-plane, running from
    /// <paramref name="startAngle"/> through the signed <paramref name="sweep"/>. The
    /// sweep must be under a half turn and must keep the axial coordinate strictly
    /// monotone (see the type remarks).
    /// </summary>
    public HelicalSurface(
        in Frame3d frame, in Vector2d arcCenter, double arcRadius,
        double startAngle, double sweep, double pitch, Interval domainU)
    {
        if (!(arcRadius > 0) || !double.IsFinite(arcRadius))
            throw new ArgumentOutOfRangeException(nameof(arcRadius), "An arc generator needs a positive finite radius.");
        if (!double.IsFinite(startAngle) || !double.IsFinite(sweep) || sweep == 0)
            throw new ArgumentOutOfRangeException(nameof(sweep), "An arc generator needs a finite nonzero sweep.");
        if (!(Math.Abs(sweep) < Math.PI))
            throw new ArgumentOutOfRangeException(nameof(sweep),
                "An arc generator must sweep less than a half turn (past that its axial coordinate cannot stay monotone).");
        ValidateShared(pitch, domainU);

        double endAngle = startAngle + sweep;
        // The monotone-axial condition, exactly: an interval shorter than pi holds at most
        // one zero of cos, and holding one flips the sign at its ends — so equal strict
        // signs at the two ends IS "cos never vanishes inside", with no epsilon anywhere.
        if (!(Math.Cos(startAngle) * Math.Cos(endAngle) > 0))
            throw new ArgumentOutOfRangeException(nameof(sweep),
                "An arc generator's axial coordinate must be strictly monotone: the sweep " +
                $"[{startAngle:g6}, {endAngle:g6}] crosses a radial tangent (cos phi = 0), where the " +
                "carrier equation's two branches meet and a cap cut stops being single-valued.");

        double minCos = Math.Min(Math.Cos(startAngle), Math.Cos(endAngle));
        if (ContainsAngle(startAngle, endAngle, Math.PI))
            minCos = -1;
        if (!(arcCenter.X + arcRadius * minCos > 0))
            throw new ArgumentOutOfRangeException(nameof(arcCenter),
                "The arc generator must stay off the axis (its smallest radius must be positive).");

        Frame = frame;
        ArcCenter = arcCenter;
        ArcRadius = arcRadius;
        ArcStartAngle = startAngle;
        ArcSweep = sweep;
        ProfileStart = new Vector2d(
            arcCenter.X + arcRadius * Math.Cos(startAngle), arcCenter.Y + arcRadius * Math.Sin(startAngle));
        ProfileEnd = new Vector2d(
            arcCenter.X + arcRadius * Math.Cos(endAngle), arcCenter.Y + arcRadius * Math.Sin(endAngle));
        Pitch = pitch;
        _domainU = domainU;
    }

    private static void ValidateShared(double pitch, Interval domainU)
    {
        if (pitch == 0 || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch),
                "Pitch must be finite and nonzero (a zero pitch is a surface of revolution — use RevolvedSurface).");
        if (!double.IsFinite(domainU.Length) || domainU.Length <= 0)
            throw new ArgumentOutOfRangeException(nameof(domainU), "The u domain must be finite and non-empty.");
    }

    /// <summary>Whether some angle congruent to <paramref name="angle"/> lies in [a, b] (either order).</summary>
    private static bool ContainsAngle(double a, double b, double angle)
    {
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        double shifted = angle + 2 * Math.PI * Math.Ceiling((lo - angle) / (2 * Math.PI));
        return shifted <= hi;
    }

    private readonly Interval _domainU;

    /// <summary>The axis frame: the band winds about Z, phase measured from X.</summary>
    public Frame3d Frame { get; }

    /// <summary>(radius, axial) of the generator's v = 0 end, at u = 0.</summary>
    public Vector2d ProfileStart { get; }

    /// <summary>(radius, axial) of the generator's v = 1 end, at u = 0.</summary>
    public Vector2d ProfileEnd { get; }

    /// <summary>Centre of the arc generator in (radius, axial); meaningless when straight.</summary>
    public Vector2d ArcCenter { get; }

    /// <summary>Radius of the arc generator, or exactly zero for a straight one.</summary>
    public double ArcRadius { get; }

    /// <summary>Polar angle of the arc generator's v = 0 end about <see cref="ArcCenter"/>.</summary>
    public double ArcStartAngle { get; }

    /// <summary>Signed angular sweep of the arc generator, under a half turn in magnitude.</summary>
    public double ArcSweep { get; }

    /// <summary>
    /// Whether the generator is the straight segment <see cref="ProfileStart"/> →
    /// <see cref="ProfileEnd"/>. A deliberate exact-zero SEMANTIC test on
    /// <see cref="ArcRadius"/>, never a tolerance: this selects which closed form the
    /// surface IS, and consumers whose own derivation assumes straightness ask it.
    /// </summary>
    public bool IsStraightGenerator => ArcRadius == 0;

    /// <summary>Axial advance per full turn; the sign selects the advance direction.</summary>
    public double Pitch { get; }

    /// <summary>Axial advance per radian, p/2π.</summary>
    public double AxialRate => Pitch / (2 * Math.PI);

    public override Interval DomainU => _domainU;
    public override Interval DomainV => Interval.Unit;

    /// <summary>Polar angle on the arc generator at v; meaningless when straight.</summary>
    public double ArcAngleAt(double v) => ArcStartAngle + ArcSweep * v;

    /// <summary>Generator radius at v.</summary>
    public double RadiusAt(double v) => IsStraightGenerator
        ? ProfileStart.X + (ProfileEnd.X - ProfileStart.X) * v
        : ArcCenter.X + ArcRadius * Math.Cos(ArcAngleAt(v));

    /// <summary>Generator axial coordinate at v (before the helical advance).</summary>
    public double AxialAt(double v) => IsStraightGenerator
        ? ProfileStart.Y + (ProfileEnd.Y - ProfileStart.Y) * v
        : ArcCenter.Y + ArcRadius * Math.Sin(ArcAngleAt(v));

    /// <summary>dr/dv along the generator — constant for a segment, turning for an arc.</summary>
    public double RadialRateAt(double v) => IsStraightGenerator
        ? ProfileEnd.X - ProfileStart.X
        : -ArcRadius * ArcSweep * Math.Sin(ArcAngleAt(v));

    /// <summary>dz/dv along the generator; strictly one sign by construction.</summary>
    public double AxialRateAt(double v) => IsStraightGenerator
        ? ProfileEnd.Y - ProfileStart.Y
        : ArcRadius * ArcSweep * Math.Cos(ArcAngleAt(v));

    public override Vector3d PointAt(double u, double v)
    {
        double r = RadiusAt(v);
        return Frame.Origin
            + Frame.X * (r * Math.Cos(u))
            + Frame.Y * (r * Math.Sin(u))
            + Frame.Z * (AxialAt(v) + AxialRate * u);
    }

    /// <summary>
    /// Exact unit normal: with du = (−r·sin u, r·cos u, rate) and
    /// dv = (dr·cos u, dr·sin u, dz) in frame coordinates,
    /// du × dv = (r·dz·cos u − rate·dr·sin u, rate·dr·cos u + r·dz·sin u, −r·dr).
    /// dr and dz are the generator's own derivatives at v, so the arc case is the same
    /// expression evaluated at a turning tangent rather than a second formula.
    /// </summary>
    public override Vector3d NormalAt(double u, double v)
    {
        double r = RadiusAt(v);
        double dr = RadialRateAt(v);
        double dz = AxialRateAt(v);
        double rate = AxialRate;
        double c = Math.Cos(u), s = Math.Sin(u);
        var n = Frame.X * (r * dz * c - rate * dr * s)
              + Frame.Y * (rate * dr * c + r * dz * s)
              + Frame.Z * (-r * dr);
        return n.Normalized();
    }

    /// <summary>
    /// Exact inverse evaluation. The point's angle θ fixes u up to whole turns; for each
    /// candidate u = θ + 2πk near the domain, the axial coordinate solves v linearly
    /// (dz ≠ 0), and the residual against <see cref="PointAt"/> decides. Candidates with
    /// v inside [0, 1] are preferred over extrapolated ones — for steep generators
    /// (|dz| close to a pitch) the band's linear extension can tile onto the
    /// neighboring turn, and the interior solution is the meaningful one. A generator
    /// with dz = 0 (a helicoid ramp) instead takes v from the radius and u from the
    /// axial coordinate. Never iterative: exactness feeds curve pullback, which is
    /// weld-critical.
    /// <para>An ARC generator is the easier case rather than the harder one: for each
    /// candidate u BOTH generator coordinates are known — the radius from the point's own
    /// distance to the axis and the axial coordinate from removing rate·u — so the polar
    /// angle is one <c>Atan2</c> about the arc centre and v follows. Nothing is solved; the
    /// residual against <see cref="PointAt"/> is what rejects a point that is merely near
    /// the arc's circle rather than on the band.</para>
    /// </summary>
    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        uv = default;
        var target = point; // copy: `in` parameters cannot be captured by local functions
        var d = point - Frame.Origin;
        double x = d.Dot(Frame.X), y = d.Dot(Frame.Y), axial = d.Dot(Frame.Z);
        double dr = ProfileEnd.X - ProfileStart.X;
        double dz = ProfileEnd.Y - ProfileStart.Y;
        double rate = AxialRate;

        // Track the best candidate overall and the best with v inside [0, 1]: an
        // in-range solution within tolerance wins (steep generators can tile their
        // linear extension onto the neighboring turn), but a solution a hair outside
        // the rails is still accepted when nothing in range fits.
        double bestU = 0, bestV = 0, bestDistance = double.PositiveInfinity;
        double insideU = 0, insideV = 0, insideDistance = double.PositiveInfinity;
        void Consider(double u, double v)
        {
            double distance = PointAt(u, v).DistanceTo(target);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestU = u;
                bestV = v;
            }
            if (v >= 0 && v <= 1 && distance < insideDistance)
            {
                insideDistance = distance;
                insideU = u;
                insideV = v;
            }
        }

        if (!IsStraightGenerator)
        {
            double theta = Math.Atan2(y, x);
            double radial = Math.Sqrt(x * x + y * y);
            const double slackU = 1e-6;
            const double slackV = 0.2;
            int kMin = (int)Math.Floor((_domainU.Start - slackU - theta) / (2 * Math.PI));
            int kMax = (int)Math.Ceiling((_domainU.End + slackU - theta) / (2 * Math.PI));
            for (int k = kMin; k <= kMax; k++)
            {
                double u = theta + 2 * Math.PI * k;
                if (u < _domainU.Start - slackU || u > _domainU.End + slackU)
                    continue;
                double generatorAxial = axial - rate * u;
                double phi = Math.Atan2(generatorAxial - ArcCenter.Y, radial - ArcCenter.X);
                // The sweep is under a half turn, so the arc's own angular offsets lie
                // strictly inside (-pi, pi] and the principal wrap is the right one.
                double delta = phi - ArcStartAngle;
                delta -= 2 * Math.PI * Math.Floor((delta + Math.PI) / (2 * Math.PI));
                double v = delta / ArcSweep;
                if (v < -slackV || v > 1 + slackV)
                    continue;
                Consider(u, v);
            }
        }
        // Deliberate exact-zero test: dz divides v below; only bit-zero dz (helicoid
        // ramp) is invalid there, and that case takes the radius-based else branch.
        else if (dz != 0)
        {
            double theta = Math.Atan2(y, x);
            const double slackU = 1e-6;   // admit cut endpoints landing a hair outside
            const double slackV = 0.2;    // crossing seeds extrapolate slightly past rails
            int kMin = (int)Math.Floor((_domainU.Start - slackU - theta) / (2 * Math.PI));
            int kMax = (int)Math.Ceiling((_domainU.End + slackU - theta) / (2 * Math.PI));
            for (int k = kMin; k <= kMax; k++)
            {
                double u = theta + 2 * Math.PI * k;
                if (u < _domainU.Start - slackU || u > _domainU.End + slackU)
                    continue;
                double v = (axial - rate * u - ProfileStart.Y) / dz;
                if (v < -slackV || v > 1 + slackV)
                    continue;
                Consider(u, v);
            }
        }
        else
        {
            // Helicoid ramp: the radius fixes v, the axial coordinate fixes u, and the
            // residual verifies the angle.
            double v = (Math.Sqrt(x * x + y * y) - ProfileStart.X) / dr;
            double u = (axial - ProfileStart.Y) / rate;
            Consider(u, v);
        }

        if (insideDistance < tolerance)
        {
            uv = new Vector2d(insideU, insideV);
            return true;
        }
        if (bestDistance < tolerance)
        {
            uv = new Vector2d(bestU, bestV);
            return true;
        }
        return false;
    }
}

/// <summary>
/// The curve a COAXIAL straight-profile carrier cuts from an ARC-generator
/// <see cref="HelicalSurface"/> — the arc twin of <see cref="SpiralArc3d"/>, and the one
/// shape that whole family becomes once the generator stops being a segment.
///
/// <para>The derivation is two lines and covers every member at once. Write the carrier in
/// the band's own cylindrical coordinates as α·r + β·Z = γ (a plane ⊥ the axis has α = 0, a
/// coaxial cylinder β = 0, a cone both), and the band as r = C_r + ρ·cos φ,
/// Z = C_z + ρ·sin φ + rate·u. Substituting and collecting gives
/// <c>ρ·cos(φ − ψ) = D + slope·u</c> with ψ = atan2(β, α), D = γ − α·C_r − β·C_z and
/// slope = −β·rate — so the generator ANGLE is a shifted arc-cosine of an affine function
/// of u, and the radius and axial coordinate follow from it. Where a straight generator
/// makes v linear in u (hence <see cref="SpiralArc3d"/>'s conical spiral), an arc generator
/// makes φ an arc-cosine; the parameter is still the band's own u, which is what keeps
/// tessellation samples of this edge and of the band's grid on the same phases.</para>
///
/// <para><b>Which acos branch is a property of the arc, not a parameter to guess</b>, and
/// <see cref="TryBuild"/> reads it off the generator's own angular range: the two branches
/// separate exactly when the arc stays inside one half-turn about ψ, which is why
/// <see cref="HelicalSurface"/> refuses a generator whose axial coordinate is not strictly
/// monotone. An arc that straddles a branch boundary is TANGENT to the carrier somewhere in
/// range, and <see cref="TryBuild"/> declines it rather than returning half a curve.</para>
///
/// <para>The two degenerate carriers keep their exact coordinate rather than reconstructing
/// it: a plane ⊥ the axis returns γ for the axial coordinate verbatim (α is bit-zero) and a
/// coaxial cylinder returns γ for the radius, because those are the values a cap loop and a
/// runout rim have to WELD against. Everything else is closed form, derivatives included.</para>
/// </summary>
public sealed class HelicalArcCut3d : Curve3d
{
    /// <summary>
    /// The explicit spelling, for reconstruction (the archive) and for transport (a
    /// transform). <see cref="TryBuild"/> is how one is DERIVED from a band and a carrier,
    /// and is the only place the branch and the u domain are worked out.
    /// </summary>
    public HelicalArcCut3d(
        in Frame3d frame, in Vector2d arcCenter, double arcRadius, double axialRate,
        double carrierRadial, double carrierAxial, double carrierOffset,
        int branch, Interval domain)
    {
        Frame = frame;
        ArcCenter = arcCenter;
        ArcRadius = arcRadius;
        AxialRate = axialRate;
        CarrierRadial = carrierRadial;
        CarrierAxial = carrierAxial;
        CarrierOffset = carrierOffset;
        Branch = branch;
        _domain = domain;
        _psi = Math.Atan2(carrierAxial, carrierRadial);
        _offset = carrierOffset - carrierRadial * arcCenter.X - carrierAxial * arcCenter.Y;
        _slope = -carrierAxial * axialRate;
    }

    private readonly Interval _domain;
    private readonly double _psi;
    private readonly double _offset;
    private readonly double _slope;

    /// <summary>The band's axis frame: the angle IS the parameter, the advance is along Z.</summary>
    public Frame3d Frame { get; }

    /// <summary>The generator arc's centre in (radius, axial).</summary>
    public Vector2d ArcCenter { get; }

    /// <summary>The generator arc's radius.</summary>
    public double ArcRadius { get; }

    /// <summary>The band's axial advance per radian.</summary>
    public double AxialRate { get; }

    /// <summary>Carrier coefficient on the radius; bit-zero for a plane ⊥ the axis.</summary>
    public double CarrierRadial { get; }

    /// <summary>Carrier coefficient on the axial coordinate; bit-zero for a coaxial cylinder.</summary>
    public double CarrierAxial { get; }

    /// <summary>Carrier constant: <c>CarrierRadial·r + CarrierAxial·Z = CarrierOffset</c>.</summary>
    public double CarrierOffset { get; }

    /// <summary>Which arc-cosine branch the generator angle rides, +1 or −1.</summary>
    public int Branch { get; }

    public override Interval Domain => _domain;
    public override bool IsClosed => false;

    /// <summary>
    /// Whether the cut lies in a plane PERPENDICULAR TO THE AXIS — the cap-cut case, and
    /// the property downstream tiers read (<c>BRepTessellator</c>'s full-helical-band gate).
    /// A deliberate exact-zero test, exactly as <see cref="SpiralArc3d.IsPlanar"/> is.
    /// </summary>
    public bool IsPlanar => CarrierRadial == 0;

    /// <summary>The generator's polar angle at <paramref name="t"/> (the band's u).</summary>
    public double AngleAt(double t)
    {
        double g = Math.Clamp((_offset + _slope * t) / ArcRadius, -1, 1);
        return _psi + Branch * Math.Acos(g);
    }

    /// <summary>
    /// The parameter at which the generator angle is <paramref name="angle"/> — the
    /// carrier relation read the other way round, <c>u = (ρ·cos(φ − ψ) − D)/slope</c>.
    /// <para>It exists because <b>the band's v is NOT linear in u here</b>, which is the
    /// whole difference from <see cref="SpiralArc3d"/>: a consumer that samples this curve
    /// at uniform u and pairs the samples with rows at uniform v — which is exactly what a
    /// helical band's grid does — shears every quad against the cap it neighbours. Measured
    /// on a 0.05 clearance rod at 16 segments per circle: 308 folded facets, worst normal
    /// agreement −0.366, and the residual GREW with density rather than converging.</para>
    /// </summary>
    public double ParameterAtAngle(double angle) =>
        // Exact-zero test: a bit-zero slope is the coaxial cylinder carrier, whose angle
        // never moves, so no parameter is named by one.
        _slope == 0 ? _domain.Start : (ArcRadius * Math.Cos(angle - _psi) - _offset) / _slope;

    /// <summary>Radius at <paramref name="t"/>; exact for a coaxial cylinder carrier.</summary>
    public double RadiusAt(double t) => CarrierAxial == 0
        ? CarrierOffset / CarrierRadial
        : ArcCenter.X + ArcRadius * Math.Cos(AngleAt(t));

    /// <summary>Axial coordinate at <paramref name="t"/>; exact for a plane ⊥ the axis.</summary>
    public double AxialAt(double t) => CarrierRadial == 0
        ? CarrierOffset / CarrierAxial
        : ArcCenter.Y + ArcRadius * Math.Sin(AngleAt(t)) + AxialRate * t;

    public override Vector3d PointAt(double t) =>
        Frame.Origin
        + Frame.X * (RadiusAt(t) * Math.Cos(t))
        + Frame.Y * (RadiusAt(t) * Math.Sin(t))
        + Frame.Z * AxialAt(t);

    /// <summary>
    /// dφ/dt from the carrier relation itself: differentiating ρ·cos(φ − ψ) = D + slope·t
    /// gives −ρ·sin(φ − ψ)·φ′ = slope, so φ′ = −slope / (ρ·sin(φ − ψ)) — no square root,
    /// and singular only where the carrier is tangent to the arc, which
    /// <see cref="TryBuild"/> has already declined.
    /// </summary>
    private double AngleRateAt(double t)
    {
        // Exact-zero guard on a division, not a tolerance: a constant angle (a coaxial
        // cylinder carrier, slope bit-zero) is the iso-v helix case and has zero rate.
        if (_slope == 0)
            return 0;
        double sin = Math.Sin(AngleAt(t) - _psi);
        return sin == 0 ? 0 : -_slope / (ArcRadius * sin);
    }

    public override Vector3d DerivativeAt(double t)
    {
        double phi = AngleAt(t), dphi = AngleRateAt(t);
        double r = RadiusAt(t);
        // A coaxial cylinder carrier needs no special case here: its slope is bit-zero, so
        // the angle rate is zero and dr falls out zero too.
        double dr = -ArcRadius * Math.Sin(phi) * dphi;
        double dz = CarrierRadial == 0 ? 0 : ArcRadius * Math.Cos(phi) * dphi + AxialRate;
        double c = Math.Cos(t), s = Math.Sin(t);
        return Frame.X * (dr * c - r * s) + Frame.Y * (dr * s + r * c) + Frame.Z * dz;
    }

    public override Vector3d SecondDerivativeAt(double t)
    {
        double phi = AngleAt(t), dphi = AngleRateAt(t);
        double sinDelta = Math.Sin(phi - _psi);
        // φ″ follows from differentiating φ′ = −slope/(ρ·sin(φ−ψ)) once more.
        double ddphi = _slope == 0 || sinDelta == 0
            ? 0
            : _slope * Math.Cos(phi - _psi) * dphi / (ArcRadius * sinDelta * sinDelta);
        double r = RadiusAt(t);
        double dr = -ArcRadius * Math.Sin(phi) * dphi;
        double ddr = -ArcRadius * (Math.Cos(phi) * dphi * dphi + Math.Sin(phi) * ddphi);
        double ddz = CarrierRadial == 0
            ? 0
            : ArcRadius * (-Math.Sin(phi) * dphi * dphi + Math.Cos(phi) * ddphi);
        double c = Math.Cos(t), s = Math.Sin(t);
        return Frame.X * (ddr * c - 2 * dr * s - r * c)
             + Frame.Y * (ddr * s + 2 * dr * c - r * s)
             + Frame.Z * ddz;
    }

    public override Vector3d TangentAt(double t) => DerivativeAt(t).Normalized();

    /// <summary>
    /// The cut of <paramref name="band"/> by the coaxial carrier
    /// <c>radial·r + axial·Z = offset</c>, clipped to <paramref name="clip"/> in u and
    /// optionally to <paramref name="radialRange"/> — the carrier's own finite extent,
    /// which every bounded coaxial carrier states as a radius band (a cone's generator
    /// segment, an annulus's rim pair). Returns false when the band's generator is straight
    /// (that is <see cref="SpiralArc3d"/>'s family), when the carrier is tangent to the
    /// generator arc inside its range, or when nothing of the cut survives the clip.
    /// <para>The radial clip is exact and needs no root solve: the cut's radius is
    /// <c>C_r + ρ·cos φ</c> and the generator angle stays inside one half-period of cos by
    /// construction, so a radius names ONE angle on the arc, and u follows from the carrier
    /// relation the same way it does everywhere else here.</para>
    /// </summary>
    public static bool TryBuild(
        HelicalSurface band, double radial, double axial, double offset, Interval clip,
        out HelicalArcCut3d cut, Interval? radialRange = null)
    {
        cut = null!;
        if (band.IsStraightGenerator)
            return false;
        double norm = Math.Sqrt(radial * radial + axial * axial);
        if (!(norm > 0) || !double.IsFinite(norm) || !double.IsFinite(offset))
            return false;
        // Normalize to a unit (radial, axial) with a non-negative axial part, so that the
        // two degenerate carriers land on EXACT unit coefficients and their preserved
        // coordinate is a division by 1 rather than a reconstruction.
        (radial, axial, offset) = (radial / norm, axial / norm, offset / norm);
        if (axial < 0 || (axial == 0 && radial < 0))
            (radial, axial, offset) = (-radial, -axial, -offset);

        double psi = Math.Atan2(axial, radial);
        double rho = band.ArcRadius;
        double d = offset - radial * band.ArcCenter.X - axial * band.ArcCenter.Y;
        double slope = -axial * band.AxialRate;

        // The branch is which side of psi the generator arc sits on, and it is decided
        // against the representative of psi NEAREST the arc rather than through a principal
        // wrap: an arc ending exactly at delta = +-pi (its own extreme radius, which is
        // where a coaxial cylinder carrier meets it) would otherwise have that end wrapped
        // to the far side and be refused for a boundary it merely touches. An arc reaching
        // delta = 0 or +-pi in its INTERIOR is a different matter — the carrier is tangent
        // to it there, acos has a square-root singularity, and the cut is not one branch.
        //
        // The slack is an absolute ANGULAR floor, which the epsilon ladder admits because
        // radians are dimensionless; it only decides which of two labels an endpoint
        // tangency takes, and at such a point the two branches agree.
        const double angleFloor = 1e-9;
        double phiLo = Math.Min(band.ArcStartAngle, band.ArcAngleAt(1));
        double phiHi = Math.Max(band.ArcStartAngle, band.ArcAngleAt(1));
        double turns = Math.Round(((phiLo + phiHi) / 2 - psi) / (2 * Math.PI));
        double near = psi + 2 * Math.PI * turns;
        double deltaLo = phiLo - near, deltaHi = phiHi - near;
        int branch;
        if (deltaLo + deltaHi >= 0)
        {
            if (!(deltaLo >= -angleFloor && deltaHi <= Math.PI + angleFloor))
                return false;
            branch = 1;
        }
        else
        {
            if (!(deltaHi <= angleFloor && deltaLo >= -Math.PI - angleFloor))
                return false;
            branch = -1;
        }
        // The generator angles the cut may reach: the arc's own ends, narrowed by the
        // carrier's radial extent when it has one.
        double phiA = band.ArcStartAngle, phiB = band.ArcAngleAt(1);
        if (radialRange is { } radii)
        {
            double rA = band.ProfileStart.X, rB = band.ProfileEnd.X;
            if (Math.Max(rA, rB) < radii.Start || Math.Min(rA, rB) > radii.End)
                return false;
            if (radii.Start > Math.Min(rA, rB) &&
                TryAngleForRadius(band, radii.Start, out double atLow))
                (phiA, phiB) = NarrowTo(phiA, phiB, atLow, rA < rB);
            if (radii.End < Math.Max(rA, rB) &&
                TryAngleForRadius(band, radii.End, out double atHigh))
                (phiA, phiB) = NarrowTo(phiA, phiB, atHigh, rA > rB);
        }
        // Measured against the SAME representative of psi the branch was chosen against,
        // so the cosines below are the ones that branch's own acos inverts.
        double delta0 = phiA - near, delta1 = phiB - near;

        double lo = clip.Start, hi = clip.End;
        // Deliberate exact-zero test: a bit-zero slope is the coaxial CYLINDER carrier,
        // where the generator angle never moves and the cut is one complete iso-v helix.
        if (slope == 0)
        {
            double g = d / rho;
            double cos0 = Math.Cos(delta0), cos1 = Math.Cos(delta1);
            if (g < Math.Min(cos0, cos1) || g > Math.Max(cos0, cos1))
                return false;
        }
        else
        {
            double uA = (rho * Math.Cos(delta0) - d) / slope;
            double uB = (rho * Math.Cos(delta1) - d) / slope;
            lo = Math.Max(lo, Math.Min(uA, uB));
            hi = Math.Min(hi, Math.Max(uA, uB));
        }
        if (!(hi - lo > 0))
            return false;

        cut = new HelicalArcCut3d(
            band.Frame, band.ArcCenter, rho, band.AxialRate,
            radial, axial, offset, branch, new Interval(lo, hi));
        return true;
    }

    /// <summary>
    /// The generator angle at which the arc reaches <paramref name="radius"/>. The arc
    /// stays inside one half-period of cos, so ±acos names one representative in range and
    /// the nearest candidate IS it; the clamp absorbs the round-off of a radius taken from
    /// the arc's own endpoint.
    /// </summary>
    private static bool TryAngleForRadius(HelicalSurface band, double radius, out double angle)
    {
        angle = 0;
        double q = (radius - band.ArcCenter.X) / band.ArcRadius;
        if (!(Math.Abs(q) <= 1))
            return false;
        double lo = Math.Min(band.ArcStartAngle, band.ArcAngleAt(1));
        double hi = Math.Max(band.ArcStartAngle, band.ArcAngleAt(1));
        double baseAngle = Math.Acos(q);
        double best = double.PositiveInfinity;
        for (int k = -2; k <= 2; k++)
        {
            foreach (double candidate in (ReadOnlySpan<double>)
                     [baseAngle + 2 * Math.PI * k, -baseAngle + 2 * Math.PI * k])
            {
                double distance = Math.Max(lo - candidate, candidate - hi);
                if (distance < best)
                    (best, angle) = (distance, candidate);
            }
        }
        angle = Math.Clamp(angle, lo, hi);
        return true;
    }

    /// <summary>
    /// Replaces whichever end of [<paramref name="a"/>, <paramref name="b"/>] the clip
    /// bites into. <paramref name="atStart"/> says the clip belongs to the a end.
    /// </summary>
    private static (double A, double B) NarrowTo(double a, double b, double angle, bool atStart) =>
        atStart ? (angle, b) : (a, angle);
}
