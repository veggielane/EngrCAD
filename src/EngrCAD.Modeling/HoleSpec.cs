using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A hole recipe — simple, counterbored, or countersunk — applied with
/// <see cref="Shape.Drill"/> at a list of 2D points on a plane. Every hole's cutting
/// tool is a solid of revolution of a small (radius, depth) sketch, so drilling is
/// exact in all three representations (B-Rep booleans, exact SDF subtraction, mesh).
/// </summary>
public sealed class HoleSpec
{
    private enum Kind { Simple, Counterbore, Countersink }

    private readonly Kind _kind;
    private readonly double _diameter;
    private readonly double _featureDiameter;   // cbore/csk outer diameter
    private readonly double _counterboreDepth;
    private readonly double _countersinkAngle;  // full included angle, radians
    private readonly double _tipAngle;          // drill-point included angle, radians; 0 = flat

    private HoleSpec(
        Kind kind, double diameter, double featureDiameter, double counterboreDepth,
        double countersinkAngle, double tipAngle = 0)
    {
        _kind = kind;
        _diameter = diameter;
        _featureDiameter = featureDiameter;
        _counterboreDepth = counterboreDepth;
        _countersinkAngle = countersinkAngle;
        _tipAngle = tipAngle;
    }

    /// <summary>A straight drilled hole.</summary>
    public static HoleSpec Simple(double diameter)
    {
        if (diameter <= 0)
            throw new ArgumentOutOfRangeException(nameof(diameter));
        return new HoleSpec(Kind.Simple, diameter, 0, 0, 0);
    }

    /// <summary>A hole with a flat-bottomed cylindrical recess (cap-head screws).</summary>
    public static HoleSpec Counterbore(double diameter, double counterboreDiameter, double counterboreDepth)
    {
        if (diameter <= 0)
            throw new ArgumentOutOfRangeException(nameof(diameter));
        if (counterboreDiameter <= diameter)
            throw new ArgumentOutOfRangeException(nameof(counterboreDiameter),
                "The counterbore must be wider than the hole.");
        if (counterboreDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(counterboreDepth));
        return new HoleSpec(Kind.Counterbore, diameter, counterboreDiameter, counterboreDepth, 0);
    }

    /// <summary>A hole with a conical entry (flat-head screws); the angle is the full
    /// included cone angle, 90° by default.</summary>
    public static HoleSpec Countersink(double diameter, double countersinkDiameter, double angleDegrees = 90)
    {
        if (diameter <= 0)
            throw new ArgumentOutOfRangeException(nameof(diameter));
        if (countersinkDiameter <= diameter)
            throw new ArgumentOutOfRangeException(nameof(countersinkDiameter),
                "The countersink must be wider than the hole.");
        if (angleDegrees <= 0 || angleDegrees >= 180)
            throw new ArgumentOutOfRangeException(nameof(angleDegrees));
        return new HoleSpec(Kind.Countersink, diameter, countersinkDiameter, 0, angleDegrees * Math.PI / 180);
    }

    /// <summary>
    /// The same hole cut by a real twist drill, whose point leaves a conical bottom of
    /// <paramref name="includedAngleDegrees"/> (118° general purpose, 135° split point —
    /// see <see cref="StandardHoles.TwistDrillPoint"/> and
    /// <see cref="StandardHoles.SplitDrillPoint"/>). The tip is exact everywhere: the tool
    /// stays one axis-touching revolved sketch, with the cone as the profile run from the
    /// bore radius down to the apex on the axis.
    /// </summary>
    /// <remarks>
    /// <para><b>Depth is measured to the SHOULDER</b> — the deepest full-diameter point —
    /// and the tip reaches
    /// <c>(diameter / 2) / tan(angle / 2)</c> further, which is the shop-floor and
    /// drawing convention (ASME Y14.5 / ISO 129: a blind hole's depth dimension excludes
    /// the point). So <c>Drill(spec, points, depth)</c> removes the SAME cylinder of
    /// material with a tip as without, plus the cone below it, and adding a tip never
    /// shortens a hole. The consequence worth knowing: a "through" depth chosen to just
    /// clear the far face still clears it, and a blind depth close to the far face may
    /// have its tip break through — check the tip length, not the depth.</para>
    /// <para>The default stays flat, which is not a drilled bottom but is what a CAD
    /// model usually wants for a through hole, a reamed or bored feature, and every
    /// existing design.</para>
    /// </remarks>
    public HoleSpec WithTipAngle(double includedAngleDegrees)
    {
        if (includedAngleDegrees <= 0 || includedAngleDegrees >= 180)
            throw new ArgumentOutOfRangeException(nameof(includedAngleDegrees),
                "A drill point's included angle must be between 0° and 180° (exclusive).");
        return new HoleSpec(_kind, _diameter, _featureDiameter, _counterboreDepth, _countersinkAngle,
            includedAngleDegrees * Math.PI / 180);
    }

    /// <summary>The drill point's full included angle in degrees, or null for a flat bottom.</summary>
    // Exact-zero test: 0 is the sentinel this type stores for "no tip", not a measurement.
    public double? TipAngleDegrees => _tipAngle == 0 ? null : _tipAngle * 180 / Math.PI;

    /// <summary>
    /// How far the drill point reaches below the shoulder — 0 for a flat bottom. Add it
    /// to a blind depth to find where the tool actually stops.
    /// </summary>
    public double TipLength => _tipAngle == 0 ? 0 : _diameter / 2 / Math.Tan(_tipAngle / 2);

    // Read access for callout generation (HoleCallout in Annotations); the spec's
    // public surface stays the three factories.
    internal bool IsCounterbore => _kind == Kind.Counterbore;
    internal bool IsCountersink => _kind == Kind.Countersink;
    internal double Diameter => _diameter;
    internal double FeatureDiameter => _featureDiameter;
    internal double CounterboreDepth => _counterboreDepth;
    internal double CountersinkAngleDegrees => _countersinkAngle * 180 / Math.PI;

    /// <summary>
    /// The tool's diameter at the drilled surface — the recess diameter for
    /// counterbores/countersinks, the bore diameter for simple holes. Two holes whose
    /// surface circles overlap or touch produce degenerate boolean input, so
    /// <see cref="Shape.Drill"/> validates spacing against this diameter.
    /// </summary>
    internal double SurfaceDiameter => _kind == Kind.Simple ? _diameter : _featureDiameter;

    /// <summary>
    /// The cutting tool's OUTER silhouette as (axial, radius) breakpoints, ascending in
    /// axial: the tool's radius is piecewise linear between them. Axial 0 is the drilled
    /// surface and material is at negative axial, so the run starts at the tool's deepest
    /// point (−depth, or −depth − <see cref="TipLength"/> with a drill point) and ends at
    /// the overshoot above the surface. A leading radius of 0 is the drill point's apex,
    /// on the axis.
    /// </summary>
    /// <remarks>
    /// This is the single source of truth for the tool's shape:
    /// <see cref="ToolProfile"/> closes it against the axis, and <c>DrillShape</c>'s
    /// cross-plane interference test bounds it slab by slab. Deriving one from the other
    /// is what keeps a validated configuration and the geometry actually cut in agreement.
    /// </remarks>
    internal (double Axial, double Radius)[] ToolSilhouette(double depth)
    {
        double r = _diameter / 2;
        double overshoot = 0.05 * Math.Max(depth, _diameter);
        // The point hangs below the shoulder; depth is measured to the shoulder, so the
        // cylindrical run is unchanged by adding a tip.
        (double, double)[] tip = _tipAngle == 0 ? [] : [(-depth - TipLength, 0.0)];
        switch (_kind)
        {
            case Kind.Simple:
                return [.. tip, (-depth, r), (overshoot, r)];

            case Kind.Counterbore:
            {
                double bigR = _featureDiameter / 2;
                if (_counterboreDepth >= depth)
                    throw new ArgumentException("The counterbore must be shallower than the hole.");
                return
                [
                    .. tip,
                    (-depth, r), (-_counterboreDepth, r), (-_counterboreDepth, bigR), (overshoot, bigR),
                ];
            }

            default:
            {
                double bigR = _featureDiameter / 2;
                double slope = Math.Tan(_countersinkAngle / 2);
                double sinkDepth = (bigR - r) / slope;
                if (sinkDepth >= depth)
                    throw new ArgumentException("The countersink must be shallower than the hole.");
                // The cone continues its slope past the surface, so the surface diameter
                // stays exactly the specified one despite the overshoot.
                return [.. tip, (-depth, r), (-sinkDepth, r), (overshoot, bigR + overshoot * slope)];
            }
        }
    }

    /// <summary>
    /// The cutting tool's revolve profile in (radius, height) coordinates: the drilled
    /// surface is at y = 0, material below. The tool overshoots the surface slightly so
    /// booleans never see coplanar faces (the countersink cone continues its slope, so
    /// the surface diameter stays exact).
    /// </summary>
    internal Sketch ToolProfile(double depth)
    {
        var silhouette = ToolSilhouette(depth);
        var points = new List<Vector2d>(silhouette.Length + 2);
        // A silhouette end already ON the axis closes the profile by itself — repeating
        // the axis point there would make a zero-length segment. Exact comparison: a
        // drill point's apex radius is the literal 0 the silhouette stores.
        if (silhouette[0].Radius != 0)
            points.Add(new(0, silhouette[0].Axial));
        foreach (var (axial, radius) in silhouette)
            points.Add(new(radius, axial));
        if (silhouette[^1].Radius != 0)
            points.Add(new(0, silhouette[^1].Axial));
        return Sketch.Polygon(points);
    }
}
