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

    /// <summary>
    /// The tool's diameter at the drilled surface — the recess diameter for
    /// counterbores/countersinks, the bore diameter for simple holes. Two holes whose
    /// surface circles overlap or touch produce degenerate boolean input, so
    /// <see cref="Shape.Drill"/> validates spacing against this diameter.
    /// </summary>
    internal double SurfaceDiameter => _kind == Kind.Simple ? _diameter : _featureDiameter;

    /// <summary>
    /// The cutting tool's revolve profile in (radius, height) coordinates: the drilled
    /// surface is at y = 0, material below. The tool overshoots the surface slightly so
    /// booleans never see coplanar faces (the countersink cone continues its slope, so
    /// the surface diameter stays exact).
    /// </summary>
    internal Sketch ToolProfile(double depth)
    {
        double r = _diameter / 2;
        double overshoot = 0.05 * Math.Max(depth, _diameter);
        switch (_kind)
        {
            case Kind.Simple:
                return Sketch.Polygon([new(0, -depth), new(r, -depth), new(r, overshoot), new(0, overshoot)]);

            case Kind.Counterbore:
            {
                double bigR = _featureDiameter / 2;
                if (_counterboreDepth >= depth)
                    throw new ArgumentException("The counterbore must be shallower than the hole.");
                return Sketch.Polygon(
                [
                    new(0, -depth), new(r, -depth),
                    new(r, -_counterboreDepth), new(bigR, -_counterboreDepth),
                    new(bigR, overshoot), new(0, overshoot),
                ]);
            }

            default:
            {
                double bigR = _featureDiameter / 2;
                double slope = Math.Tan(_countersinkAngle / 2);
                double sinkDepth = (bigR - r) / slope;
                if (sinkDepth >= depth)
                    throw new ArgumentException("The countersink must be shallower than the hole.");
                double topR = bigR + overshoot * slope;
                return Sketch.Polygon(
                [
                    new(0, -depth), new(r, -depth),
                    new(r, -sinkDepth), new(topR, overshoot), new(0, overshoot),
                ]);
            }
        }
    }
}
