using EngrCAD.Core;
using EngrCAD.Core.Spatial;

namespace EngrCAD.BRep;

/// <summary>
/// Surface–surface intersection: exact analytic curves for the common quadric pairs
/// (plane/plane, plane/cylinder, plane/sphere, sphere/sphere, parallel cylinders) and a
/// general numerical marching tracer for everything else. Unbounded curves (lines) and
/// unbounded surfaces (planes, cylinders) are clipped to / seeded from
/// <paramref name="region"/>. Traced curves come back as <see cref="PolylineCurve3d"/>;
/// analytic ones as <see cref="Line3d"/>, <see cref="Circle3d"/> or <see cref="Ellipse3d"/>.
/// Tangential contacts (surfaces touching without crossing) are not reported.
/// </summary>
public static class SurfaceIntersection
{
    public static IReadOnlyList<Curve3d> Intersect(Surface a, Surface b, in Aabb region)
    {
        if (region.IsEmpty)
            throw new ArgumentException("Region must be non-empty.", nameof(region));

        // An extrusion of a full circle along its axis IS a cylinder — promote it so
        // drilled bores get exact analytic intersection circles.
        a = Promote(a);
        b = Promote(b);

        return (a, b) switch
        {
            (PlaneSurface pa, PlaneSurface pb) => PlanePlane(pa, pb, region),
            (PlaneSurface p, CylinderSurface c) => PlaneCylinder(p, c, region),
            (CylinderSurface c, PlaneSurface p) => PlaneCylinder(p, c, region),
            (PlaneSurface p, SphereSurface s) => PlaneSphere(p, s),
            (SphereSurface s, PlaneSurface p) => PlaneSphere(p, s),
            (SphereSurface sa, SphereSurface sb) => SphereSphere(sa, sb),
            (CylinderSurface ca, CylinderSurface cb) when ca.Axis.IsParallelTo(cb.Axis, Tolerance.Default)
                => ParallelCylinders(ca, cb, region),
            _ => March(a, b, region),
        };
    }

    private static Surface Promote(Surface s) =>
        s is ExtrudedSurface e &&
        e.Generator.Underlying is Circle3d c &&
        e.Direction.IsParallelTo(c.Axis, Tolerance.Default)
            ? new CylinderSurface(c.Center, c.XDirection, c.YDirection, c.Radius)
            : s;

    // ---- analytic cases ----

    private static List<Curve3d> PlanePlane(PlaneSurface a, PlaneSurface b, in Aabb region)
    {
        var na = a.Normal.Normalized();
        var nb = b.Normal.Normalized();
        var direction = na.Cross(nb);
        if (!direction.TryNormalize(Tolerance.Default, out var dir))
            return []; // parallel (coincident planes intersect everywhere; not a curve)

        // A point on both planes: solve n_a·p = d_a, n_b·p = d_b in the span of {n_a, n_b}.
        double da = na.Dot(a.Origin);
        double db = nb.Dot(b.Origin);
        double dot = na.Dot(nb);
        double denominator = 1 - dot * dot;
        double ka = (da - db * dot) / denominator;
        double kb = (db - da * dot) / denominator;
        var point = na * ka + nb * kb;

        return ClipLine(point, dir, region) is { } line ? [line] : [];
    }

    private static List<Curve3d> PlaneSphere(PlaneSurface plane, SphereSurface sphere)
    {
        var n = plane.Normal.Normalized();
        double signedDistance = n.Dot(sphere.Center - plane.Origin);
        double r2 = sphere.Radius * sphere.Radius - signedDistance * signedDistance;
        if (r2 <= Tolerance.Default.Linear * Tolerance.Default.Linear)
            return []; // missing entirely, or tangential point contact
        var center = sphere.Center - n * signedDistance;
        double radius = Math.Sqrt(r2);
        var x = n.ArbitraryPerpendicular(Tolerance.Default);
        return [new Circle3d(center, x, n.Cross(x), radius)];
    }

    private static List<Curve3d> SphereSphere(SphereSurface a, SphereSurface b)
    {
        var offset = b.Center - a.Center;
        double d = offset.Length;
        if (d <= Tolerance.Default.Linear ||
            d >= a.Radius + b.Radius - Tolerance.Default.Linear ||
            d <= Math.Abs(a.Radius - b.Radius) + Tolerance.Default.Linear)
            return []; // concentric, separate, contained, or tangential

        var n = offset / d;
        double along = (d * d + a.Radius * a.Radius - b.Radius * b.Radius) / (2 * d);
        double r2 = a.Radius * a.Radius - along * along;
        if (r2 <= 0)
            return [];
        var center = a.Center + n * along;
        var x = n.ArbitraryPerpendicular(Tolerance.Default);
        return [new Circle3d(center, x, n.Cross(x), Math.Sqrt(r2))];
    }

    private static List<Curve3d> PlaneCylinder(PlaneSurface plane, CylinderSurface cylinder, in Aabb region)
    {
        var n = plane.Normal.Normalized();
        var axis = cylinder.Axis;
        double alignment = n.Dot(axis);

        if (Math.Abs(alignment) <= Tolerance.Default.Angular)
        {
            // Axis parallel to the plane: 0, 1 (tangent, not reported) or 2 lines.
            double signedDistance = n.Dot(cylinder.Origin - plane.Origin);
            double halfChord2 = cylinder.Radius * cylinder.Radius - signedDistance * signedDistance;
            if (halfChord2 <= Tolerance.Default.Linear * Tolerance.Default.Linear)
                return [];
            double halfChord = Math.Sqrt(halfChord2);
            var footpoint = cylinder.Origin - n * signedDistance;
            var side = axis.Cross(n); // unit: axis ⊥ n
            var curves = new List<Curve3d>(2);
            if (ClipLine(footpoint + side * halfChord, axis, region) is { } l1)
                curves.Add(l1);
            if (ClipLine(footpoint - side * halfChord, axis, region) is { } l2)
                curves.Add(l2);
            return curves;
        }

        // Axis crosses the plane at the ellipse (or circle) center.
        double t = n.Dot(plane.Origin - cylinder.Origin) / alignment;
        var center = cylinder.Origin + axis * t;

        var majorDirection = axis - n * alignment;
        if (!majorDirection.TryNormalize(Tolerance.Default, out var major))
        {
            // Axis perpendicular to the plane: a circle.
            var x = n.ArbitraryPerpendicular(Tolerance.Default);
            return [new Circle3d(center, x, n.Cross(x), cylinder.Radius)];
        }

        var minor = n.Cross(major);
        return [new Ellipse3d(center, major * (cylinder.Radius / Math.Abs(alignment)), minor * cylinder.Radius)];
    }

    private static List<Curve3d> ParallelCylinders(CylinderSurface a, CylinderSurface b, in Aabb region)
    {
        var axis = a.Axis;
        // Work in the cross-section plane through a's origin.
        var offset = b.Origin - a.Origin;
        var separation = offset - axis * offset.Dot(axis);
        double d = separation.Length;
        if (d <= Tolerance.Default.Linear ||
            d >= a.Radius + b.Radius - Tolerance.Default.Linear ||
            d <= Math.Abs(a.Radius - b.Radius) + Tolerance.Default.Linear)
            return []; // coaxial, separate, contained, or tangential

        var toB = separation / d;
        double along = (d * d + a.Radius * a.Radius - b.Radius * b.Radius) / (2 * d);
        double h2 = a.Radius * a.Radius - along * along;
        if (h2 <= 0)
            return [];
        double h = Math.Sqrt(h2);
        var side = axis.Cross(toB);

        var curves = new List<Curve3d>(2);
        if (ClipLine(a.Origin + toB * along + side * h, axis, region) is { } l1)
            curves.Add(l1);
        if (ClipLine(a.Origin + toB * along - side * h, axis, region) is { } l2)
            curves.Add(l2);
        return curves;
    }

    /// <summary>Clips an infinite line to the region box; null when it misses.</summary>
    private static Line3d? ClipLine(in Vector3d point, in Vector3d direction, in Aabb region)
    {
        // Two opposing rays give the full line's parameter interval inside the box.
        var forward = new Ray3d(point, direction);
        var backward = new Ray3d(point, -direction);
        bool hitF = forward.Intersects(region, out double f0, out double f1);
        bool hitB = backward.Intersects(region, out double b0, out double b1);
        double tMin, tMax;
        if (hitF && hitB)
        {
            tMin = -b1;
            tMax = f1;
        }
        else if (hitF)
        {
            tMin = f0;
            tMax = f1;
        }
        else if (hitB)
        {
            tMin = -b1;
            tMax = -b0;
        }
        else
        {
            return null;
        }
        if (tMax - tMin <= Tolerance.Default.Linear)
            return null;
        return new Line3d(point + direction * tMin, point + direction * tMax);
    }

    // ---- general numerical marching ----

    private readonly record struct ParamDomain(Interval U, Interval V, bool PeriodicU, bool PeriodicV);

    private static ParamDomain GetParamDomain(Surface surface, in Aabb region)
    {
        switch (surface)
        {
            case PlaneSurface plane:
            {
                double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
                double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3d(
                        (i & 1) == 0 ? region.Min.X : region.Max.X,
                        (i & 2) == 0 ? region.Min.Y : region.Max.Y,
                        (i & 4) == 0 ? region.Min.Z : region.Max.Z);
                    var uv = plane.Project(corner);
                    uMin = Math.Min(uMin, uv.X);
                    uMax = Math.Max(uMax, uv.X);
                    vMin = Math.Min(vMin, uv.Y);
                    vMax = Math.Max(vMax, uv.Y);
                }
                return new ParamDomain(new Interval(uMin, uMax), new Interval(vMin, vMax), false, false);
            }
            case CylinderSurface cylinder:
            {
                double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3d(
                        (i & 1) == 0 ? region.Min.X : region.Max.X,
                        (i & 2) == 0 ? region.Min.Y : region.Max.Y,
                        (i & 4) == 0 ? region.Min.Z : region.Max.Z);
                    double v = (corner - cylinder.Origin).Dot(cylinder.Axis);
                    vMin = Math.Min(vMin, v);
                    vMax = Math.Max(vMax, v);
                }
                return new ParamDomain(new Interval(0, 2 * Math.PI), new Interval(vMin, vMax), true, false);
            }
            case SphereSurface:
                return new ParamDomain(new Interval(0, 2 * Math.PI), new Interval(-Math.PI / 2, Math.PI / 2), true, false);
            case ExtrudedSurface extruded:
                return new ParamDomain(extruded.DomainU, extruded.DomainV, extruded.Generator.IsClosed, false);
            case RevolvedSurface revolved:
                return new ParamDomain(revolved.DomainU, revolved.DomainV, revolved.IsFullTurn, revolved.Generator.IsClosed);
            case SweptSurface swept:
                return new ParamDomain(swept.DomainU, swept.DomainV, swept.Generator.IsClosed, false);
            default:
                var du = surface.DomainU;
                var dv = surface.DomainV;
                if (!double.IsFinite(du.Length) || !double.IsFinite(dv.Length))
                    throw new NotSupportedException(
                        $"{surface.GetType().Name} has an unbounded domain; marching intersection needs finite parameter bounds.");
                return new ParamDomain(du, dv, false, false);
        }
    }

    private static double Wrap(double t, in Interval interval, bool periodic)
    {
        if (!periodic)
            return interval.Clamp(t);
        double len = interval.Length;
        double local = (t - interval.Start) % len;
        if (local < 0)
            local += len;
        return interval.Start + local;
    }

    private static Vector3d Eval(Surface s, in ParamDomain d, double u, double v) =>
        s.PointAt(Wrap(u, d.U, d.PeriodicU), Wrap(v, d.V, d.PeriodicV));

    private static Vector3d NormalOf(Surface s, in ParamDomain d, double u, double v) =>
        s.NormalAt(Wrap(u, d.U, d.PeriodicU), Wrap(v, d.V, d.PeriodicV));

    private static bool Outside(double t, in Interval interval, bool periodic) =>
        !periodic && (t < interval.Start - 1e-9 || t > interval.End + 1e-9);

    private sealed record MarchState(Surface A, ParamDomain Da, Surface B, ParamDomain Db, double Step);

    private static List<Curve3d> March(Surface a, Surface b, in Aabb region)
    {
        const int seedResolution = 24;
        var da = GetParamDomain(a, region);
        var db = GetParamDomain(b, region);
        double step = region.Size[region.LongestAxis] / 150.0;
        var state = new MarchState(a, da, b, db, step);

        var seeds = FindSeeds(state, seedResolution);
        var curves = new List<Curve3d>();
        var traced = new List<Vector3d>();

        foreach (var seed in seeds)
        {
            var p = Eval(a, da, seed[0], seed[1]);
            if (traced.Any(q => q.DistanceSquaredTo(p) < 4 * step * step))
                continue;

            var forward = Trace(state, seed, +1, out bool closed);
            List<Vector3d> points;
            if (closed)
            {
                points = forward;
            }
            else
            {
                var backward = Trace(state, seed, -1, out _);
                backward.Reverse();
                backward.RemoveAt(backward.Count - 1); // shared seed point
                points = [.. backward, .. forward];
            }
            if (points.Count < 3)
                continue;

            curves.Add(new PolylineCurve3d(points, closed));
            traced.AddRange(points);
        }
        return curves;
    }

    /// <summary>Grid-samples both surfaces, pairs nearby samples via a BVH, and Newton-refines each pair onto the intersection.</summary>
    private static List<double[]> FindSeeds(MarchState state, int resolution)
    {
        var samplesB = new List<(double U, double V, Vector3d P)>();
        var boxes = new List<Aabb>();
        for (int i = 0; i <= resolution; i++)
        {
            for (int j = 0; j <= resolution; j++)
            {
                double u = state.Db.U.ParameterAt((double)i / resolution);
                double v = state.Db.V.ParameterAt((double)j / resolution);
                var p = state.B.PointAt(u, v);
                samplesB.Add((u, v, p));
                boxes.Add(new Aabb(p, p));
            }
        }
        var bvh = Bvh.Build(boxes.ToArray().AsSpan());

        var cloud = Aabb.Empty;
        foreach (var s in samplesB)
            cloud = cloud.Union(s.P);
        double spacing = cloud.IsEmpty ? 0 : cloud.Size.Length / resolution;

        var seeds = new List<double[]>();
        for (int i = 0; i <= resolution; i++)
        {
            for (int j = 0; j <= resolution; j++)
            {
                double ua = state.Da.U.ParameterAt((double)i / resolution);
                double va = state.Da.V.ParameterAt((double)j / resolution);
                var pa = state.A.PointAt(ua, va);
                if (!bvh.Nearest(pa, k => samplesB[k].P.DistanceTo(pa), out int nearest, out double distance))
                    continue;
                if (distance > spacing * 1.5)
                    continue;

                double[] parameters = [ua, va, samplesB[nearest].U, samplesB[nearest].V];
                if (RefineSeed(state, parameters))
                    seeds.Add(parameters);
            }
        }
        return seeds;
    }

    /// <summary>Damped Gauss–Newton pulling a parameter 4-tuple onto S_a = S_b.</summary>
    private static bool RefineSeed(MarchState state, double[] parameters)
    {
        for (int iteration = 0; iteration < 12; iteration++)
        {
            var pa = Eval(state.A, state.Da, parameters[0], parameters[1]);
            var pb = Eval(state.B, state.Db, parameters[2], parameters[3]);
            var f = pa - pb;
            if (f.Length < 1e-10)
                return true;

            var (jau, jav) = Partials(state.A, state.Da, parameters[0], parameters[1]);
            var (jbu, jbv) = Partials(state.B, state.Db, parameters[2], parameters[3]);

            // Normal equations (JᵀJ + λI)Δ = −JᵀF with J = [Ja | −Jb].
            Span<Vector3d> columns = [jau, jav, -jbu, -jbv];
            var m = new double[4, 4];
            var rhs = new double[4];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                    m[r, c] = columns[r].Dot(columns[c]);
                m[r, r] += 1e-10;
                rhs[r] = -columns[r].Dot(f);
            }
            if (!Solve4(m, rhs, out var delta))
                return false;
            for (int k = 0; k < 4; k++)
                parameters[k] += delta[k];
        }
        return (Eval(state.A, state.Da, parameters[0], parameters[1]) -
                Eval(state.B, state.Db, parameters[2], parameters[3])).Length < 1e-9;
    }

    private static List<Vector3d> Trace(MarchState state, double[] seed, int direction, out bool closed)
    {
        closed = false;
        var parameters = (double[])seed.Clone();
        var points = new List<Vector3d>();
        var start = Eval(state.A, state.Da, parameters[0], parameters[1]);
        points.Add(start);
        Vector3d? previousTangent = null;

        for (int step = 0; step < 4000; step++)
        {
            var p = Eval(state.A, state.Da, parameters[0], parameters[1]);
            var na = NormalOf(state.A, state.Da, parameters[0], parameters[1]);
            var nb = NormalOf(state.B, state.Db, parameters[2], parameters[3]);
            var cross = na.Cross(nb);
            if (!cross.TryNormalize(new Tolerance(1e-7, 1e-7), out var tangent))
                break; // tangential contact: direction undefined
            if (previousTangent is { } prev && tangent.Dot(prev) < 0)
                tangent = -tangent;
            if (previousTangent is null)
                tangent *= direction;
            previousTangent = tangent;

            var target = p + tangent * state.Step;
            if (!Correct(state, parameters, target, tangent))
                break;
            if (Outside(parameters[0], state.Da.U, state.Da.PeriodicU) ||
                Outside(parameters[1], state.Da.V, state.Da.PeriodicV) ||
                Outside(parameters[2], state.Db.U, state.Db.PeriodicU) ||
                Outside(parameters[3], state.Db.V, state.Db.PeriodicV))
                break;

            var next = Eval(state.A, state.Da, parameters[0], parameters[1]);
            if (next.DistanceTo(p) > 3 * state.Step)
                break; // corrector jumped to a different branch

            points.Add(next);
            if (step > 5 && next.DistanceTo(start) < state.Step)
            {
                closed = true;
                break;
            }
        }
        return points;
    }

    /// <summary>Newton step onto both surfaces, constrained to the plane through the predicted point.</summary>
    private static bool Correct(MarchState state, double[] parameters, in Vector3d target, in Vector3d tangent)
    {
        var t = tangent;
        var goal = target;
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var pa = Eval(state.A, state.Da, parameters[0], parameters[1]);
            var pb = Eval(state.B, state.Db, parameters[2], parameters[3]);
            var f = pa - pb;
            double g = t.Dot(pa - goal);
            if (f.Length < 1e-10 && Math.Abs(g) < 1e-10)
                return true;

            var (jau, jav) = Partials(state.A, state.Da, parameters[0], parameters[1]);
            var (jbu, jbv) = Partials(state.B, state.Db, parameters[2], parameters[3]);

            var m = new double[4, 4]
            {
                { jau.X, jav.X, -jbu.X, -jbv.X },
                { jau.Y, jav.Y, -jbu.Y, -jbv.Y },
                { jau.Z, jav.Z, -jbu.Z, -jbv.Z },
                { t.Dot(jau), t.Dot(jav), 0, 0 },
            };
            var rhs = new double[] { -f.X, -f.Y, -f.Z, -g };
            if (!Solve4(m, rhs, out var delta))
                return false;

            double magnitude = 0;
            for (int k = 0; k < 4; k++)
            {
                parameters[k] += delta[k];
                magnitude += delta[k] * delta[k];
            }
            if (magnitude > 100 * state.Step * state.Step)
                return false; // diverging
        }
        return (Eval(state.A, state.Da, parameters[0], parameters[1]) -
                Eval(state.B, state.Db, parameters[2], parameters[3])).Length < 1e-8;
    }

    private static (Vector3d Du, Vector3d Dv) Partials(Surface s, in ParamDomain d, double u, double v)
    {
        double hu = Math.Max(1e-7, d.U.Length * 1e-7);
        double hv = Math.Max(1e-7, d.V.Length * 1e-7);
        var du = (Eval(s, d, u + hu, v) - Eval(s, d, u - hu, v)) / (2 * hu);
        var dv = (Eval(s, d, u, v + hv) - Eval(s, d, u, v - hv)) / (2 * hv);
        return (du, dv);
    }

    /// <summary>Gaussian elimination with partial pivoting for the 4×4 marching systems.</summary>
    private static bool Solve4(double[,] m, double[] rhs, out double[] solution)
    {
        solution = new double[4];
        var a = (double[,])m.Clone();
        var b = (double[])rhs.Clone();

        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 4; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                    pivot = r;
            }
            if (Math.Abs(a[pivot, col]) < 1e-14)
                return false;
            if (pivot != col)
            {
                for (int c = 0; c < 4; c++)
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (int r = col + 1; r < 4; r++)
            {
                double factor = a[r, col] / a[col, col];
                for (int c = col; c < 4; c++)
                    a[r, c] -= factor * a[col, c];
                b[r] -= factor * b[col];
            }
        }
        for (int r = 3; r >= 0; r--)
        {
            double sum = b[r];
            for (int c = r + 1; c < 4; c++)
                sum -= a[r, c] * solution[c];
            solution[r] = sum / a[r, r];
        }
        return true;
    }
}
