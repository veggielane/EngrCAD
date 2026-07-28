using EngrCAD.Core;

namespace EngrCAD.Implicit;

/// <summary>
/// Helical thread solid about +Z, z ∈ [0, length]: a straight-flanked (trapezoidal)
/// thread form — the 60° ISO V-profile is the intended special case — wound as a
/// right-hand helix of the given pitch. The solid is
/// <c>{ (r, θ, z) : (r, wrap(z − pitch·θ/2π)) ∈ Ω }</c> where Ω is the 2D profile
/// region in the (helical phase u, radius r) plane over one pitch, crest centered at
/// u = 0:
/// <list type="bullet">
/// <item><description>crest flat at r = majorRadius for |u| ≤ crestWidth/2,</description></item>
/// <item><description>straight flanks down to r = minorRadius at |u| = pitch/2 − rootWidth/2,</description></item>
/// <item><description>root flat at r = minorRadius out to |u| = pitch/2,</description></item>
/// <item><description>core: everything below r = minorRadius is material.</description></item>
/// </list>
/// <para>
/// <b>Distance fidelity contract</b> (follows the smooth-operator precedent: honest
/// sign, approximate magnitude): the <em>sign</em> is exact everywhere — membership is
/// decided by the exact wrapped 2D profile test, so polygonization reconstructs the
/// true surface. The <em>magnitude</em> is the exact 2D signed profile distance in the
/// (u, r) plane (nearest-period wrap, both mirror images checked) scaled by
/// cos λ of the lead angle λ = atan(pitch / 2πr) evaluated at the root radius. The
/// helical shear stretches in-plane distances by at most sec λ(r) at radius r, and λ
/// shrinks with r, so the scaling keeps the value a lower-bound-style distance near the
/// threaded surface (r ≥ root radius); deep inside and near the axis the magnitude is
/// merely approximate (still correctly signed). Do not assert exact distances near
/// flanks or the axis in tests.
/// </para>
/// <para>
/// <c>profileOffset</c> dilates (+) or erodes (−) the 2D profile normal to its
/// boundary — flanks shift perpendicular to themselves, crest/root flats radially —
/// implemented exactly as a distance shift of the 2D field. This is the printing-
/// clearance mechanism: erode an external thread, dilate an internal thread's void.
/// <c>startChamfer</c>/<c>endChamfer</c> cut 45° lead-in cones at z = 0 / z = length:
/// a chamfer of length c brings the end face down to majorRadius + profileOffset − c
/// (pass the thread depth to land on the minor diameter). The cone distance is exact in
/// the axial half-plane and correctly signed in 3D.
/// </para>
/// </summary>
internal sealed class ThreadSdf : Sdf
{
    private readonly double _majorRadius;
    private readonly double _minorRadius;
    private readonly double _pitch;
    // +1 right-hand, −1 left-hand. The ONLY difference between the two: the helical
    // phase reads z − h·P·θ/2π, so a left-hand thread is the exact mirror image of its
    // right-hand twin (θ → −θ) and the profile arithmetic below is untouched.
    private readonly double _handedness;
    private readonly double _length;
    private readonly double _crestHalfWidth; // u where the crest flat ends
    private readonly double _rootStart;      // u where the root flat begins
    private readonly double _profileOffset;
    private readonly double _startChamfer;
    private readonly double _endChamfer;
    private readonly double _cosLead;
    // End-face radius anchors for the 45° cones — per end, so unequal chamfers each
    // cut exactly their own depth (a shared max-based anchor over-cut the smaller end).
    private readonly double _startChamferBase;
    private readonly double _endChamferBase;

    private static readonly double InvSqrt2 = 1 / Math.Sqrt(2);

    public ThreadSdf(
        double majorRadius, double minorRadius, double pitch,
        double crestWidth, double rootWidth, double length,
        double profileOffset, double startChamfer, double endChamfer, bool leftHand = false)
    {
        if (!(minorRadius > 0) || !(majorRadius > minorRadius))
            throw new ArgumentOutOfRangeException(nameof(majorRadius),
                "Thread radii must satisfy 0 < minorRadius < majorRadius.");
        if (!(pitch > 0))
            throw new ArgumentOutOfRangeException(nameof(pitch));
        if (crestWidth < 0 || rootWidth < 0 || crestWidth + rootWidth >= pitch)
            throw new ArgumentOutOfRangeException(nameof(crestWidth),
                "Crest and root flats must be non-negative and leave room for the flanks (crest + root < pitch).");
        if (!(length > 0))
            throw new ArgumentOutOfRangeException(nameof(length));
        if (!double.IsFinite(profileOffset) || majorRadius + profileOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(profileOffset));
        if (startChamfer < 0 || endChamfer < 0)
            throw new ArgumentOutOfRangeException(nameof(startChamfer), "Chamfer lengths must be non-negative.");
        double effectiveMajor = majorRadius + profileOffset;
        double maxChamfer = Math.Max(startChamfer, endChamfer);
        if (maxChamfer >= effectiveMajor)
            throw new ArgumentOutOfRangeException(nameof(endChamfer),
                "A chamfer must be shorter than the (offset) major radius.");

        _majorRadius = majorRadius;
        _minorRadius = minorRadius;
        _pitch = pitch;
        _handedness = leftHand ? -1 : 1;
        _length = length;
        _crestHalfWidth = crestWidth / 2;
        _rootStart = pitch / 2 - rootWidth / 2;
        _profileOffset = profileOffset;
        _startChamfer = startChamfer;
        _endChamfer = endChamfer;
        _startChamferBase = effectiveMajor - startChamfer;
        _endChamferBase = effectiveMajor - endChamfer;

        // Lead angle at the smallest surface radius (most conservative — λ shrinks
        // with r, so cos λ(rRef) · sec λ(r ≥ rRef) ≤ 1 on the threaded surface).
        // The 1e-9 floor only keeps cos λ defined if the offset swallows the minor
        // radius entirely — a degenerate-input guard, not a tolerance comparison.
        double rRef = Math.Max(1e-9, minorRadius - Math.Abs(profileOffset));
        double circumference = 2 * Math.PI * rRef;
        _cosLead = circumference / Math.Sqrt(circumference * circumference + pitch * pitch);
    }

    public override double Evaluate(in Vector3d p)
    {
        double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double theta = Math.Atan2(p.Y, p.X);
        // Helical phase; θ's 2π jump across the −x half-plane shifts w by exactly one
        // pitch, which the nearest-period wrap absorbs — u is continuous off the axis.
        double w = p.Z - _handedness * _pitch * theta / (2 * Math.PI);
        double u = Math.Abs(w - _pitch * Math.Round(w / _pitch)); // [0, pitch/2]

        double side = (SignedProfileDistance(u, r) - _profileOffset) * _cosLead;

        if (_endChamfer > 0)
            side = Math.Max(side, (r - (_endChamferBase + (_length - p.Z))) * InvSqrt2);
        if (_startChamfer > 0)
            side = Math.Max(side, (r - (_startChamferBase + p.Z)) * InvSqrt2);

        double axial = Math.Max(-p.Z, p.Z - _length);
        double outside = Math.Sqrt(
            Math.Max(side, 0) * Math.Max(side, 0) +
            Math.Max(axial, 0) * Math.Max(axial, 0));
        double inside = Math.Min(Math.Max(side, axial), 0);
        return outside + inside;
    }

    /// <summary>
    /// Exact signed distance in the (u, r) plane to the periodic profile boundary,
    /// negative where r ≤ R(u). The boundary is even in u and p-periodic, so the
    /// nearest boundary point of any feature within one pitch appears among the three
    /// canonical half-period segments evaluated at u and at its mirror pitch − u.
    /// </summary>
    private double SignedProfileDistance(double u, double r)
    {
        double unsigned = Math.Min(BoundaryDistance(u, r), BoundaryDistance(_pitch - u, r));
        return r <= RadiusAt(u) ? -unsigned : unsigned;
    }

    /// <summary>Profile outline radius R(u) for u ∈ [0, pitch/2].</summary>
    private double RadiusAt(double u)
    {
        if (u <= _crestHalfWidth)
            return _majorRadius;
        if (u >= _rootStart)
            return _minorRadius;
        double t = (u - _crestHalfWidth) / (_rootStart - _crestHalfWidth);
        return _majorRadius + (_minorRadius - _majorRadius) * t;
    }

    private double BoundaryDistance(double u, double r) =>
        Math.Min(
            SegmentDistance(u, r, 0, _majorRadius, _crestHalfWidth, _majorRadius),
            Math.Min(
                SegmentDistance(u, r, _crestHalfWidth, _majorRadius, _rootStart, _minorRadius),
                SegmentDistance(u, r, _rootStart, _minorRadius, _pitch / 2, _minorRadius)));

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax, aby = by - ay;
        double t = Math.Clamp(((px - ax) * abx + (py - ay) * aby) / (abx * abx + aby * aby), 0, 1);
        double dx = px - (ax + abx * t), dy = py - (ay + aby * t);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override Aabb Bounds
    {
        get
        {
            double r = _majorRadius + Math.Max(0, _profileOffset);
            return new Aabb((-r, -r, 0), (r, r, _length));
        }
    }
}
