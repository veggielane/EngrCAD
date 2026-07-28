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

    private HoleSpec(Kind kind, double diameter, double featureDiameter, double counterboreDepth, double countersinkAngle)
    {
        _kind = kind;
        _diameter = diameter;
        _featureDiameter = featureDiameter;
        _counterboreDepth = counterboreDepth;
        _countersinkAngle = countersinkAngle;
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
    /// surface and material is at negative axial, so the run starts at −depth and ends at
    /// the overshoot above the surface.
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
        switch (_kind)
        {
            case Kind.Simple:
                return [(-depth, r), (overshoot, r)];

            case Kind.Counterbore:
            {
                double bigR = _featureDiameter / 2;
                if (_counterboreDepth >= depth)
                    throw new ArgumentException("The counterbore must be shallower than the hole.");
                return [(-depth, r), (-_counterboreDepth, r), (-_counterboreDepth, bigR), (overshoot, bigR)];
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
                return [(-depth, r), (-sinkDepth, r), (overshoot, bigR + overshoot * slope)];
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
        var points = new Vector2d[silhouette.Length + 2];
        points[0] = new(0, silhouette[0].Axial);
        for (int i = 0; i < silhouette.Length; i++)
            points[i + 1] = new(silhouette[i].Radius, silhouette[i].Axial);
        points[^1] = new(0, silhouette[^1].Axial);
        return Sketch.Polygon(points);
    }
}
