using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// Animates ONE instance along a 3D polyline at constant speed — the generic
/// follow-a-path pose track (a CNC tool along its toolpath, a probe along a scan line),
/// matrices only, honouring the pose-track contract by construction: the instance count
/// and order never change, and evaluation touches no geometry.
///
/// <para><b>t maps to ARC LENGTH, not to waypoint index</b> (the explode-path rule — a
/// part crosses each corner at constant speed, so unevenly spaced waypoints do not make
/// it lurch), and the followed instance's pose is its template pose TRANSLATED so its
/// origin lands on the path point: model the followed part at the origin (a mill tool
/// with its tip at the origin puts the tip on the path), and any template offset rides
/// as a fixed offset from the path. Waypoints are hit exactly at their own arc-length
/// parameters; every other instance keeps its template matrix bit-for-bit.</para>
/// </summary>
public sealed class FollowPathTrack : PoseTrack
{
    private readonly IReadOnlyList<PartInstance> _template;
    private readonly int _target;
    private readonly IReadOnlyList<Vector3d> _waypoints;
    private readonly double[] _cumulative;
    private readonly double _total;

    internal FollowPathTrack(Scene scene, string instancePath, IReadOnlyList<Vector3d> waypoints)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(instancePath);
        ArgumentNullException.ThrowIfNull(waypoints);
        if (waypoints.Count == 0)
            throw new ArgumentException("A follow track needs at least one waypoint.", nameof(waypoints));

        _template = scene.Instances().ToList();
        _target = -1;
        for (int i = 0; i < _template.Count; i++)
        {
            if (_template[i].Path == instancePath)
            {
                _target = i;
                break;
            }
        }
        // A track pointed at the wrong scene should fail HERE, not render a motionless
        // frame (the MechanismTrack graft rule).
        if (_target < 0)
            throw new ArgumentException(
                $"No instance has the path '{instancePath}'. Available: "
                + string.Join(", ", _template.Select(i => $"'{i.Path}'")), nameof(instancePath));

        _waypoints = waypoints;
        _cumulative = new double[waypoints.Count];
        for (int i = 1; i < waypoints.Count; i++)
            _cumulative[i] = _cumulative[i - 1] + (waypoints[i] - waypoints[i - 1]).Length;
        _total = _cumulative[^1];
    }

    public override IReadOnlyList<PartInstance> PosesAt(double t)
    {
        var point = PointAt(Math.Clamp(t, 0, 1));
        var posed = new List<PartInstance>(_template);
        var target = posed[_target];
        posed[_target] = target with
        {
            World = Matrix4d.CreateTranslation(point) * target.World,
        };
        return posed;
    }

    /// <summary>The path point at t ∈ [0, 1] by arc length — the ends exact (t = 0 is
    /// the first waypoint verbatim, t = 1 the last), interior points on the chords.</summary>
    public Vector3d PointAt(double t)
    {
        if (_total <= 0 || t <= 0)
            return _waypoints[0];
        if (t >= 1)
            return _waypoints[^1];
        double s = t * _total;
        int hi = Array.BinarySearch(_cumulative, s);
        if (hi >= 0)
            return _waypoints[hi];
        hi = ~hi;
        var a = _waypoints[hi - 1];
        var b = _waypoints[hi];
        double span = _cumulative[hi] - _cumulative[hi - 1];
        double f = span > 0 ? (s - _cumulative[hi - 1]) / span : 0;
        return a + (b - a) * f;
    }
}

/// <summary>Factories for path-following pose tracks.</summary>
public static class PathTracks
{
    /// <summary>A track moving the instance at <paramref name="instancePath"/> (a loose
    /// part's path is its name) along <paramref name="waypoints"/> at constant speed;
    /// every other instance keeps its scene pose bit-for-bit. Throws at construction
    /// when no instance matches, naming what does exist.</summary>
    public static FollowPathTrack Follow(
        Scene scene, string instancePath, IReadOnlyList<Vector3d> waypoints) =>
        new(scene, instancePath, waypoints);
}
