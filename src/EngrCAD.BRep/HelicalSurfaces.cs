using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Planar Archimedean spiral arc about the Z axis of <see cref="Frame"/>:
/// P(t) = O + X·r(t)·cos t + Y·r(t)·sin t with r(t) = <see cref="RadiusAtZero"/> +
/// <see cref="Slope"/>·t, where the parameter t is the angle from the frame's X axis.
/// A zero slope degenerates to a circular arc (kept in this type so all cap-cut edges
/// of helical bands share one sampling rule). This is exactly the curve a plane
/// perpendicular to a <see cref="HelicalSurface"/>'s axis cuts from a helical band:
/// with a linear generator, solving axial(v) + rate·u = z<sub>cap</sub> makes v — and
/// hence the radius — linear in the angle u. Built on the surface's own axis frame so
/// the spiral's parameter IS the surface's u (the phase-alignment rule: tessellation
/// samples of the edge and of the surface grid coincide exactly).
/// All derivatives are analytic (never finite differences).
/// </summary>
public sealed class SpiralArc3d : Curve3d
{
    public SpiralArc3d(in Frame3d frame, double radiusAtZero, double slope, Interval domain)
    {
        if (!double.IsFinite(radiusAtZero) || !double.IsFinite(slope))
            throw new ArgumentOutOfRangeException(nameof(radiusAtZero), "Spiral coefficients must be finite.");
        if (!double.IsFinite(domain.Length) || domain.Length <= 0)
            throw new ArgumentOutOfRangeException(nameof(domain), "Spiral arcs need a finite, non-empty domain.");
        // The radius is linear in t, so positivity at both ends covers the whole arc.
        if (!(radiusAtZero + slope * domain.Start > 0) || !(radiusAtZero + slope * domain.End > 0))
            throw new ArgumentOutOfRangeException(nameof(radiusAtZero),
                "The spiral radius must stay positive over the domain.");
        Frame = frame;
        RadiusAtZero = radiusAtZero;
        Slope = slope;
        _domain = domain;
    }

    private readonly Interval _domain;

    /// <summary>The plane frame: the spiral lives in the X/Y plane, angle measured from X.</summary>
    public Frame3d Frame { get; }

    /// <summary>Radius extrapolated to t = 0 (which may lie outside <see cref="Domain"/>).</summary>
    public double RadiusAtZero { get; }

    /// <summary>Radial growth per radian; 0 makes the arc circular.</summary>
    public double Slope { get; }

    public override Interval Domain => _domain;
    public override bool IsClosed => false;

    /// <summary>Radius at angle <paramref name="t"/>.</summary>
    public double RadiusAt(double t) => RadiusAtZero + Slope * t;

    public override Vector3d PointAt(double t)
    {
        double r = RadiusAt(t);
        return Frame.Origin + Frame.X * (r * Math.Cos(t)) + Frame.Y * (r * Math.Sin(t));
    }

    public override Vector3d DerivativeAt(double t)
    {
        double r = RadiusAt(t), c = Math.Cos(t), s = Math.Sin(t);
        return Frame.X * (Slope * c - r * s) + Frame.Y * (Slope * s + r * c);
    }

    public override Vector3d SecondDerivativeAt(double t)
    {
        double r = RadiusAt(t), c = Math.Cos(t), s = Math.Sin(t);
        return Frame.X * (-2 * Slope * s - r * c) + Frame.Y * (2 * Slope * c - r * s);
    }

    public override Vector3d TangentAt(double t) => DerivativeAt(t).Normalized();
}

/// <summary>
/// The co-rotating sweep of a straight profile segment along a helix — one facet band of
/// a screw thread. In the frame's cylindrical coordinates,
/// P(u, v) = O + X·r(v)·cos u + Y·r(v)·sin u + Z·(z(v) + p·u/2π), where (r(v), z(v))
/// interpolates linearly from <see cref="ProfileStart"/> to <see cref="ProfileEnd"/>
/// (the generator segment in the axial half-plane through +X at u = 0) and p is the
/// <see cref="Pitch"/>. u is the turning angle over the finite <see cref="DomainU"/>
/// given at construction (NOT periodic — the axial advance makes every u distinct, so
/// inverse evaluation never wraps a seam); v ∈ [0, 1] along the generator.
/// Like <see cref="RevolvedSurface"/>, a generator traversed with increasing axial
/// coordinate (dz &gt; 0) makes ∂u × ∂v point away from the axis:
/// ∂u × ∂v ∝ r·dz·r̂-terms − r·dr·ẑ (see <see cref="NormalAt"/>) — radially outward on
/// flats, tilted axially on flanks. Points, normals, and inverse evaluation are all
/// exact closed forms (the weld rule: no finite differences, no projected parameters).
/// </summary>
public sealed class HelicalSurface : Surface
{
    public HelicalSurface(in Frame3d frame, in Vector2d profileStart, in Vector2d profileEnd, double pitch, Interval domainU)
    {
        if (!(profileStart.X > 0) || !(profileEnd.X > 0))
            throw new ArgumentOutOfRangeException(nameof(profileStart),
                "The generator must stay off the axis (both profile radii positive).");
        if (pitch == 0 || !double.IsFinite(pitch))
            throw new ArgumentOutOfRangeException(nameof(pitch),
                "Pitch must be finite and nonzero (a zero pitch is a surface of revolution — use RevolvedSurface).");
        if (!double.IsFinite(domainU.Length) || domainU.Length <= 0)
            throw new ArgumentOutOfRangeException(nameof(domainU), "The u domain must be finite and non-empty.");
        if ((profileStart - profileEnd).Length <= 0)
            throw new ArgumentException("The profile segment must be non-degenerate.", nameof(profileEnd));
        Frame = frame;
        ProfileStart = profileStart;
        ProfileEnd = profileEnd;
        Pitch = pitch;
        _domainU = domainU;
    }

    private readonly Interval _domainU;

    /// <summary>The axis frame: the band winds about Z, phase measured from X.</summary>
    public Frame3d Frame { get; }

    /// <summary>(radius, axial) of the generator segment's v = 0 end, at u = 0.</summary>
    public Vector2d ProfileStart { get; }

    /// <summary>(radius, axial) of the generator segment's v = 1 end, at u = 0.</summary>
    public Vector2d ProfileEnd { get; }

    /// <summary>Axial advance per full turn; the sign selects the advance direction.</summary>
    public double Pitch { get; }

    /// <summary>Axial advance per radian, p/2π.</summary>
    public double AxialRate => Pitch / (2 * Math.PI);

    public override Interval DomainU => _domainU;
    public override Interval DomainV => Interval.Unit;

    /// <summary>Generator radius at v.</summary>
    public double RadiusAt(double v) => ProfileStart.X + (ProfileEnd.X - ProfileStart.X) * v;

    /// <summary>Generator axial coordinate at v (before the helical advance).</summary>
    public double AxialAt(double v) => ProfileStart.Y + (ProfileEnd.Y - ProfileStart.Y) * v;

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
    /// </summary>
    public override Vector3d NormalAt(double u, double v)
    {
        double r = RadiusAt(v);
        double dr = ProfileEnd.X - ProfileStart.X;
        double dz = ProfileEnd.Y - ProfileStart.Y;
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

        // Deliberate exact-zero test: dz divides v below; only bit-zero dz (helicoid
        // ramp) is invalid there, and that case takes the radius-based else branch.
        if (dz != 0)
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
