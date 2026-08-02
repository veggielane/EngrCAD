using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// Shared plumbing for turning a parametric plane curve into <see cref="Sketch"/> segments:
/// a tangent-continuous biarc chain fitted to EXACT points and EXACT tangents (never
/// estimated ones), plus the rigid transforms a tooth or a lobe is replicated by.
/// </summary>
/// <remarks>
/// The fit follows <c>BiArcFit</c>'s convention throughout — the deviation is MEASURED
/// after the fit and returned, never silently accepted — and the recursion splits at the
/// worst interior sample rather than at the midpoint, so a span that is already flat
/// enough is never subdivided for symmetry's sake. Because the fit is handed the closed
/// form's own tangents, the chain reproduces the curve's tangent direction exactly at
/// every data point and all of the approximation error lands strictly inside a span.
/// </remarks>
internal static class Curve2dChains
{
    /// <summary>
    /// Fits a biarc chain to <paramref name="point"/>/<paramref name="tangent"/> over
    /// [<paramref name="from"/>, <paramref name="to"/>] so that no sample is further than
    /// <paramref name="tolerance"/> from the result, and reports the deviation an
    /// independent denser pass measures.
    /// </summary>
    public static List<Curve2d> Fit(Func<double, Vector2d> point, Func<double, Vector2d> tangent,
        double from, double to, double tolerance, out double deviation)
    {
        var curves = new List<Curve2d>();
        Span(from, to, 0);

        // Independent verification pass - denser than the per-span acceptance samples, so
        // the reported figure is a measurement rather than a restatement of the rule that
        // accepted each span.
        double worst = 0;
        for (int i = 0; i <= 512; i++)
        {
            var p = point(from + (to - from) * i / 512);
            double best = double.PositiveInfinity;
            foreach (var curve in curves)
                best = Math.Min(best, curve.DistanceTo(p));
            worst = Math.Max(worst, best);
        }
        deviation = worst;
        return curves;

        void Span(double a, double b, int depth)
        {
            if (depth > 48)
                throw new InvalidOperationException(
                    "A biarc chain fit did not converge - this is a bug, not a modelling error.");
            if (BiArcFit.TryFit(point(a), tangent(a), point(b), tangent(b), out var biarc)
                == BiArcFitStatus.Success)
            {
                double worstInterior = -1;
                double worstT = (a + b) / 2;
                for (int i = 1; i < 32; i++)
                {
                    double t = a + (b - a) * i / 32;
                    double d = biarc!.DistanceTo(point(t));
                    if (d > worstInterior)
                    {
                        worstInterior = d;
                        worstT = t;
                    }
                }
                if (worstInterior <= tolerance)
                {
                    Add(biarc!.First);
                    Add(biarc.Second);
                    return;
                }
                Span(a, worstT, depth + 1);
                Span(worstT, b, depth + 1);
                return;
            }

            double mid = (a + b) / 2;
            Span(a, mid, depth + 1);
            Span(mid, b, depth + 1);
        }

        void Add(Curve2d piece)
        {
            // A biarc half can degenerate to (near) zero length at the joint; below the
            // weld tier it is no segment at all.
            if (Length(piece) > 1e-9)
                curves.Add(piece);
        }
    }

    /// <summary>Exact length of a chain piece (lines and arcs have closed forms).</summary>
    public static double Length(Curve2d curve) => curve switch
    {
        Line2d line => (line.End - line.Start).Length,
        Arc2d arc => arc.Length,
        _ => curve.ArcLength(),
    };

    /// <summary>Arc from <paramref name="from"/> to <paramref name="to"/> about
    /// <paramref name="center"/>, taking the shorter way round.</summary>
    public static Arc2d ArcBetween(in Vector2d center, double radius, in Vector2d from, in Vector2d to)
    {
        double a0 = Math.Atan2(from.Y - center.Y, from.X - center.X);
        double a1 = Math.Atan2(to.Y - center.Y, to.X - center.X);
        double sweep = a1 - a0;
        if (sweep > Math.PI)
            sweep -= 2 * Math.PI;
        else if (sweep < -Math.PI)
            sweep += 2 * Math.PI;
        return new Arc2d(center, radius, a0, sweep);
    }

    /// <summary>Reflection across the x axis (exact parameter transform, never a re-fit).</summary>
    public static Curve2d MirrorX(Curve2d curve) => curve switch
    {
        Line2d line => new Line2d(new(line.Start.X, -line.Start.Y), new(line.End.X, -line.End.Y)),
        Arc2d arc => new Arc2d(new(arc.Center.X, -arc.Center.Y), arc.Radius, -arc.StartAngle, -arc.SweepAngle),
        _ => throw new InvalidOperationException($"Unexpected chain curve {curve.GetType().Name}."),
    };

    /// <summary>The same piece traversed the other way.</summary>
    public static Curve2d Reverse(Curve2d curve) => curve switch
    {
        Line2d line => new Line2d(line.End, line.Start),
        Arc2d arc => arc.Reversed(),
        _ => throw new InvalidOperationException($"Unexpected chain curve {curve.GetType().Name}."),
    };

    /// <summary>Rotation about the origin by <paramref name="angle"/> (cos/sin passed in so a
    /// replication loop evaluates them once per copy rather than once per piece).</summary>
    public static Curve2d Rotate(Curve2d curve, double cos, double sin, double angle)
    {
        Vector2d Rot(in Vector2d p) => new(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
        return curve switch
        {
            Line2d line => new Line2d(Rot(line.Start), Rot(line.End)),
            Arc2d arc => new Arc2d(Rot(arc.Center), arc.Radius, arc.StartAngle + angle, arc.SweepAngle),
            _ => throw new InvalidOperationException($"Unexpected chain curve {curve.GetType().Name}."),
        };
    }

    /// <summary>Rotation of a point about the origin.</summary>
    public static Vector2d RotatePoint(double x, double y, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return new Vector2d(x * cos - y * sin, x * sin + y * cos);
    }
}
