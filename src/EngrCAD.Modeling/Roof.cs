using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// How steep a <see cref="Shape.Roof(Sketch, RoofPitch, SketchPlane?)"/> is — stated EITHER
/// as the pitch angle every roof plane makes with the base, OR as the apex height.
///
/// <para><b>One stored number, never two.</b> The two spellings are related by
/// <c>height = tan(pitch) · maxOffset</c>, where <c>maxOffset</c> is how far the polygon's
/// own straight skeleton reaches — so a type carrying both fields could be asked to honour a
/// pair that contradicts itself. This stores the one the caller gave and DERIVES the other
/// once the skeleton is known (the fine-pitch tap-drill rule), which is why
/// <see cref="RoofFacts"/> can report both and they cannot disagree.</para>
/// </summary>
public readonly struct RoofPitch : IEquatable<RoofPitch>
{
    private readonly double _value;
    private readonly bool _isHeight;

    private RoofPitch(double value, bool isHeight)
    {
        _value = value;
        _isHeight = isHeight;
    }

    /// <summary>A roof at a stated pitch angle, in degrees, strictly between 0 and 90.</summary>
    public static RoofPitch FromAngle(double degrees)
    {
        if (!(degrees > 0) || !(degrees < 90) || double.IsNaN(degrees))
            throw new ArgumentOutOfRangeException(
                nameof(degrees),
                $"A roof pitch must be strictly between 0 and 90 degrees; {degrees:R} would give a flat slab or a vertical wall.");
        return new RoofPitch(degrees, isHeight: false);
    }

    /// <summary>A roof of a stated apex height. The pitch follows from the polygon's own
    /// skeleton, so two different footprints at one height have two different pitches.</summary>
    public static RoofPitch FromHeight(double height)
    {
        if (!(height > 0) || double.IsInfinity(height))
            throw new ArgumentOutOfRangeException(
                nameof(height), $"A roof height must be finite and positive; got {height:R}.");
        return new RoofPitch(height, isHeight: true);
    }

    /// <summary>True when the caller stated a height rather than an angle.</summary>
    public bool IsHeight => _isHeight;

    /// <summary>The number the caller stated: degrees when <see cref="IsHeight"/> is false,
    /// a length when it is true.</summary>
    public double Value => _value;

    /// <summary>The rise per unit of inward offset, resolved against a skeleton that reaches
    /// <paramref name="maxOffset"/>.</summary>
    internal double SlopeFor(double maxOffset)
    {
        if (!(maxOffset > 0))
            throw new InvalidOperationException("The straight skeleton has no extent, so no roof can stand on it.");
        return _isHeight ? _value / maxOffset : Math.Tan(_value * Math.PI / 180.0);
    }

    internal string Describe() => _isHeight ? $"h={_value:R}" : $"{_value:R}°";

    public bool Equals(RoofPitch other) => _value.Equals(other._value) && _isHeight == other._isHeight;
    public override bool Equals(object? obj) => obj is RoofPitch other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_value, _isHeight);
    public static bool operator ==(RoofPitch a, RoofPitch b) => a.Equals(b);
    public static bool operator !=(RoofPitch a, RoofPitch b) => !a.Equals(b);
}

/// <summary>What a built roof turned out to be: the two spellings of its steepness (each
/// derived from the one number the caller stated), the skeleton it stands on, and the
/// closed-form volume its own faces enclose.</summary>
public sealed record RoofFacts(
    double PitchDegrees,
    double Height,
    double Slope,
    StraightSkeleton Skeleton,
    double Volume);

/// <summary>
/// The straight-skeleton roof — OpenSCAD's <c>roof()</c>, and an EXACT operation rather than a
/// polygonal approximation: every face is the plane through one base edge inclined at the
/// pitch, every ridge and valley is a straight line, and every apex is a plane intersection.
/// </summary>
public static class Roof
{
    /// <summary>
    /// The roof over a polygonal sketch, in the sketch's own 2D coordinates lifted by
    /// <paramref name="plane"/> and then by <paramref name="placement"/>.
    /// </summary>
    public static (BrepSolid Solid, RoofFacts Facts) Build(
        Sketch profile, RoofPitch pitch, in SketchPlane plane, in Matrix4d placement)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var corners = Corners(profile);
        var skeleton = StraightSkeleton.Of(corners);
        double slope = pitch.SlopeFor(skeleton.MaxTime);

        // Every node lifted once: its 2D position through the sketch plane, its height from
        // the offset it was reached at. Shared by every face that touches it, so the solid
        // welds by INDEX and no two faces can disagree about where a ridge is.
        var local = plane;
        var points = new Vector3d[skeleton.Nodes.Count];
        for (int i = 0; i < points.Length; i++)
        {
            var node = skeleton.Nodes[i];
            points[i] = placement.TransformPoint(local.ToWorld(node.Position) + local.Normal * (node.Time * slope));
        }

        // A reflection reverses orientation, so every loop is walked the other way round and
        // the outward normals come back out. (The `Shape.Mirror` rule, one level down.)
        bool reflected = placement.Determinant < 0;

        var loops = new List<int[]>(skeleton.Faces.Count + 1);
        int n = corners.Count;
        var baseLoop = new int[n];
        for (int i = 0; i < n; i++)
            baseLoop[i] = n - 1 - i;            // CW seen from above: the base's outward normal points down
        loops.Add(baseLoop);
        foreach (var face in skeleton.Faces)
            loops.Add([.. face]);

        var solid = Assemble(points, loops, reflected);
        double volume = Volume(skeleton, slope);
        double pitchDegrees = Math.Atan(slope) * 180.0 / Math.PI;
        return (solid, new RoofFacts(pitchDegrees, slope * skeleton.MaxTime, slope, skeleton, volume));
    }

    /// <summary>
    /// The enclosed volume in CLOSED FORM: each roof face is planar, so the material under it
    /// is <c>area × z(centroid)</c> exactly, and the faces partition the footprint.
    /// </summary>
    private static double Volume(StraightSkeleton skeleton, double slope)
    {
        double total = 0;
        foreach (var face in skeleton.Faces)
        {
            double twiceArea = 0;
            double cx = 0, cy = 0;
            for (int i = 0; i < face.Count; i++)
            {
                var a = skeleton.Nodes[face[i]].Position;
                var b = skeleton.Nodes[face[(i + 1) % face.Count]].Position;
                double cross = a.Cross(b);
                twiceArea += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            double area = twiceArea * 0.5;
            if (Math.Abs(twiceArea) <= 0)
                continue;
            var centroid = new Vector2d(cx / (3 * twiceArea), cy / (3 * twiceArea));
            // The face's own plane through its base edge, evaluated at the centroid.
            var edgeStart = skeleton.Nodes[face[0]].Position;
            var edgeEnd = skeleton.Nodes[face[1]].Position;
            var inward = (edgeEnd - edgeStart).Normalized().Perpendicular;
            total += area * slope * (centroid - edgeStart).Dot(inward);
        }
        return total;
    }

    /// <summary>The sketch's outer loop as a corner list, refusing everything a straight
    /// skeleton is not defined for BY NAME.</summary>
    private static IReadOnlyList<Vector2d> Corners(Sketch profile)
    {
        if (profile.Holes.Count > 0)
            throw new NotSupportedException(
                "A roof over a sketch with holes is not supported: a hole's wavefront GROWS while the outer "
                + "one shrinks, so the two meet in a merge event whose first contact is — for every "
                + "rectilinear footprint — an edge against an EDGE rather than a vertex against an edge, "
                + "which the vertex-event simulation has no event for. Roof the outer profile and subtract "
                + "the hole's own solid instead.");
        var corners = new List<Vector2d>(profile.Segments.Count);
        for (int i = 0; i < profile.Segments.Count; i++)
        {
            if (profile.Segments[i] is not LineSeg line)
                throw new NotSupportedException(
                    $"A roof needs a POLYGONAL footprint; segment {i} is a {profile.Segments[i].GetType().Name}. "
                    + "A curved edge sweeps a curved surface rather than a plane, and the straight skeleton is "
                    + "defined for straight edges only — approximate the curve with line segments first.");
            corners.Add(line.Start);
        }
        return corners;
    }

    /// <summary>
    /// A polyhedron from planar loops over a shared point list — <see cref="SolidFactory.MakeBox"/>'s
    /// construction, generalised to arbitrary face polygons. Edges intern on the canonical
    /// <c>(min, max)</c> point-index key, so two faces sharing a ridge share the EDGE OBJECT
    /// and the solid is two-manifold by construction rather than by welding.
    /// </summary>
    private static BrepSolid Assemble(Vector3d[] points, List<int[]> loops, bool reflected)
    {
        var vertices = new BrepVertex?[points.Length];
        var edges = new Dictionary<(int, int), BrepEdge>();
        var faces = new List<BrepFace>(loops.Count);

        BrepVertex Vertex(int i) => vertices[i] ??= new BrepVertex(points[i]);

        BrepEdge GetEdge(int a, int b, out bool sameSense)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!edges.TryGetValue(key, out var edge))
            {
                var v0 = Vertex(key.Item1);
                var v1 = Vertex(key.Item2);
                edge = new BrepEdge(new Line3d(v0.Position, v1.Position), Interval.Unit, v0, v1);
                edges[key] = edge;
            }
            sameSense = key.Item1 == a;
            return edge;
        }

        foreach (var loop in loops)
        {
            var indices = reflected ? loop.Reverse().ToArray() : loop;
            var coedges = new List<BrepCoedge>(indices.Length);
            for (int i = 0; i < indices.Length; i++)
            {
                int a = indices[i];
                int b = indices[(i + 1) % indices.Length];
                coedges.Add(new BrepCoedge(GetEdge(a, b, out bool sameSense), sameSense));
            }

            // Newell over the WHOLE loop rather than a corner triple: a roof face routinely
            // carries three collinear nodes along its base edge, where a triple would give a
            // zero normal.
            var normal = Newell(points, indices);
            var origin = points[indices[0]];
            var xDirection = (points[indices[1]] - origin).Normalized();
            faces.Add(new BrepFace(new PlaneSurface(origin, xDirection, normal.Cross(xDirection)),
                [new BrepLoop(coedges)]));
        }

        return new BrepSolid([new BrepShell(faces)]);
    }

    private static Vector3d Newell(Vector3d[] points, int[] loop)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var a = points[loop[i]];
            var b = points[loop[(i + 1) % loop.Length]];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
        return new Vector3d(nx, ny, nz).Normalized();
    }
}
