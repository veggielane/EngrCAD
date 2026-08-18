using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// How a silhouette curve is REPRESENTED — a claim about the curve object, not about
/// how accurate it is (<see cref="SilhouetteCurve.Deviation"/> is the measurement).
/// </summary>
public enum SilhouetteFidelity
{
    /// <summary>
    /// An analytic curve that IS the silhouette: a ruling (<see cref="Line3d"/>), a
    /// latitude or great circle (<see cref="Circle3d"/>), or an iso-parameter curve
    /// carried as the generator under a rigid transform. Exact at every parameter.
    /// </summary>
    Exact,

    /// <summary>
    /// A polyline whose VERTICES are solved in CLOSED FORM on the exact silhouette, and
    /// which is chordal between them — the same contract
    /// <see cref="SurfaceIntersection"/>'s tracer output carries, reached by a
    /// derivation rather than by marching. Every surface of revolution whose silhouette
    /// is not a conic lands here (a torus, a vase profile).
    /// </summary>
    Sampled,

    /// <summary>
    /// A polyline whose vertices are Newton-corrected onto <c>N·d = 0</c> numerically,
    /// chordal between them: the surfaces with no closed form at all (NURBS, swept,
    /// lofted, helical).
    /// </summary>
    Traced,
}

/// <summary>
/// The view a silhouette is taken from: an orthographic DIRECTION, or a perspective EYE.
/// </summary>
/// <remarks>
/// <para><b>An orthographic silhouette is invariant under negating the direction</b> — the
/// condition is <c>N·d = 0</c>, whose zero set does not see the sign — so nothing can
/// distinguish <c>Along(d)</c> from <c>Along(-d)</c>, and no test could catch a flipped
/// one. The convention is stated for the caller's benefit (<see cref="Direction"/> points
/// TOWARD the viewer, matching a view <see cref="Frame3d"/>'s Z) and is load-bearing only
/// in the PERSPECTIVE form, where the eye's position genuinely decides the answer.</para>
/// </remarks>
public readonly record struct SilhouetteView
{
    private SilhouetteView(in Vector3d direction, in Vector3d eye, bool perspective)
    {
        Direction = direction;
        Eye = eye;
        IsPerspective = perspective;
    }

    /// <summary>Parallel projection along <paramref name="direction"/> (toward the viewer).</summary>
    /// <exception cref="ArgumentException">The direction has no length.</exception>
    public static SilhouetteView Along(in Vector3d direction)
    {
        if (!direction.TryNormalize(Tolerance.Default, out var unit))
            throw new ArgumentException("A silhouette view direction must be non-zero.", nameof(direction));
        return new SilhouetteView(unit, Vector3d.Zero, perspective: false);
    }

    /// <summary>Perspective projection from <paramref name="eye"/>.</summary>
    public static SilhouetteView From(in Vector3d eye) =>
        new(Vector3d.Zero, eye, perspective: true);

    /// <summary>True when the view is a point rather than a direction.</summary>
    public bool IsPerspective { get; }

    /// <summary>Unit direction toward the viewer; meaningful only when <see cref="IsPerspective"/> is false.</summary>
    public Vector3d Direction { get; }

    /// <summary>The eye position; meaningful only when <see cref="IsPerspective"/> is true.</summary>
    public Vector3d Eye { get; }

    /// <summary>The unit direction from <paramref name="point"/> toward the viewer.</summary>
    public Vector3d DirectionAt(in Vector3d point)
    {
        if (!IsPerspective)
            return Direction;
        var away = Eye - point;
        return away.TryNormalize(Tolerance.Default, out var unit) ? unit : Vector3d.UnitZ;
    }
}

/// <summary>One face's silhouette curve, clipped to that face's trim.</summary>
/// <param name="Face">The face the curve lies on.</param>
/// <param name="Curve">The curve, in the solid's own coordinates.</param>
/// <param name="Fidelity">How the curve is represented.</param>
/// <param name="Deviation">
/// The largest <c>|N̂·v̂|</c> measured over the curve's own exact samples
/// (<see cref="FaceGeometry.ExactSampleParameters"/>, so a polyline is read at its
/// vertices and an analytic curve uniformly) — the SINE of the angle by which the
/// reported curve misses being a silhouette, dimensionless and comparable across every
/// curve kind. An exact analytic answer reads round-off.
/// </param>
public sealed record SilhouetteCurve(
    BrepFace Face, Curve3d Curve, SilhouetteFidelity Fidelity, double Deviation);

/// <summary>
/// A solid's (or a face's) silhouette, plus everything the solve declined to answer.
/// </summary>
/// <param name="Curves">The silhouette curves, in face order.</param>
/// <param name="Notes">
/// Named statements about faces that produced no curve for a GEOMETRIC reason — a plane
/// seen edge-on, a cylinder seen down its axis, a surface family with no closed form when
/// tracing is switched off. A note is not a failure: it is the answer.
/// </param>
public sealed record SilhouetteResult(
    IReadOnlyList<SilhouetteCurve> Curves, IReadOnlyList<string> Notes)
{
    /// <summary>Nothing found and nothing declined.</summary>
    public static SilhouetteResult Empty { get; } = new([], []);

    /// <summary>True when every curve is an exact analytic type.</summary>
    public bool AllExact => Curves.All(c => c.Fidelity == SilhouetteFidelity.Exact);

    /// <summary>The worst <see cref="SilhouetteCurve.Deviation"/>; 0 when there are no curves.</summary>
    public double MaxDeviation => Curves.Count == 0 ? 0 : Curves.Max(c => c.Deviation);
}

/// <summary>Knobs for <see cref="BrepSilhouette"/>.</summary>
public sealed record SilhouetteOptions
{
    /// <summary>
    /// Samples per closed-form or traced curve, and the density every containment probe
    /// and root bracket is taken at. Analytic answers ignore it.
    /// </summary>
    public int Samples { get; init; } = 96;

    /// <summary>
    /// Whether surfaces with no closed form may be answered by the level-set tracer.
    /// False refuses them BY NAME instead — the <c>CornerPolicy.ExactOnly</c> shape, for
    /// a caller that wants exact geometry or nothing.
    /// </summary>
    public bool AllowTraced { get; init; } = true;

    /// <summary>
    /// Clip each curve to its face's own trim. On by default: a carrier's silhouette runs
    /// past the face, exactly as an intersection curve does.
    /// </summary>
    public bool ClipToTrim { get; init; } = true;
}

/// <summary>
/// TRUE silhouette curves on a B-Rep's own surfaces — the outline a smooth face shows a
/// viewer, computed from the parametric surface rather than read off a tessellation.
///
/// <para><b>The condition.</b> For an orthographic view along <c>d</c> the silhouette on
/// <c>S(u,v)</c> is the zero set of <c>g = N·d</c> with <c>N = S_u × S_v</c>; for a
/// perspective eye <c>e</c> it is <c>N·(S − e) = 0</c>. The normal is never normalised
/// inside the solve — the SIGN is the whole content and a division could only lose
/// precision.</para>
///
/// <para><b>Every analytic family here has a closed form, and one derivation covers most
/// of them.</b> A surface of revolution has <c>N(u,v) = R_u M(v)</c> for a vector
/// <c>M</c> depending on the generator alone (<c>R_u</c> = rotation by u about the axis),
/// so <c>g</c> separates into <c>A(v)·cos u + B(v)·sin u + C(v) = 0</c>: for each
/// generator parameter the azimuths are <c>u = φ(v) ± acos(−C/√(A²+B²))</c>, a CLOSED
/// FORM rather than a root find, and the same shape serves the perspective case with the
/// eye offset folded into the coefficients. Cones and cylindrical bands fall out of it as
/// the case where u does not depend on v (their A, B and C all carry the same factor of
/// the radius, which cancels), so they come back as exact rulings with nothing
/// special-cased; a view ALONG the axis collapses A and B and leaves <c>C(v) = 0</c>, a
/// condition on the generator alone, whose roots are exact latitude CIRCLES. An extrusion
/// has <c>N</c> independent of v, so its silhouette is a set of RULINGS at the generator
/// parameters where <c>C'(u)·(dir × d)</c> vanishes — a one-dimensional root find whose
/// answer is an exact <see cref="Line3d"/> whatever the generator is. A sphere gives its
/// great circle (its polar circle in perspective); a plane is constant, so it is either
/// wholly edge-on or contributes nothing at all.</para>
///
/// <para><b>What is left is genuinely transcendental</b> — NURBS, swept, lofted and
/// helical surfaces — and is traced as a level set on the parameter rectangle, following
/// <see cref="SurfaceIntersection"/>'s own discipline: an anisotropy-aware seed grid
/// (a grid in PARAMETER space says nothing about coverage in MODEL space), a
/// predictor/corrector step measured in model units, and an exact landing on the domain
/// boundary rather than a stop one step short.</para>
///
/// <para><b>Every curve is clipped to its face's trim</b>, for the reason
/// <c>BrepBoolean.ClipToFace</c> states: a carrier is unbounded or bounded only by its
/// own parameter rectangle, so an unclipped silhouette draws a line across surface the
/// face does not carry.</para>
/// </summary>
public static class BrepSilhouette
{
    /// <summary>
    /// Direction-cosine band inside which a dot product of two UNIT vectors counts as
    /// zero. Deliberately ABSOLUTE and deliberately not on the epsilon ladder: a cosine
    /// is dimensionless, so there is no model scale to be relative to (the same argument
    /// the trimmed tessellator's turn guard makes for radians).
    /// </summary>
    private const double Degenerate = 1e-12;

    /// <summary>
    /// Angular spread below which a branch's azimuth counts as CONSTANT in v — the test
    /// that turns a cone's or a cylindrical band's silhouette into an exact ruling. Three
    /// decades looser than <see cref="Degenerate"/> because the azimuth comes through an
    /// <c>acos</c>, which amplifies round-off near its ends; three decades tighter than
    /// anything a genuinely varying branch produces (a torus's sweeps radians).
    /// </summary>
    private const double ConstantAzimuth = 1e-9;

    /// <summary>Every silhouette curve of <paramref name="solid"/>, face by face.</summary>
    public static SilhouetteResult OfSolid(
        BrepSolid solid, in SilhouetteView view, SilhouetteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(solid);
        var opts = options ?? new SilhouetteOptions();
        var curves = new List<SilhouetteCurve>();
        var notes = new List<string>();
        foreach (var face in solid.Faces)
            Collect(face, view, opts, curves, notes);
        return new SilhouetteResult(curves, notes);
    }

    /// <summary>The silhouette curves of ONE face.</summary>
    public static SilhouetteResult OfFace(
        BrepFace face, in SilhouetteView view, SilhouetteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(face);
        var opts = options ?? new SilhouetteOptions();
        var curves = new List<SilhouetteCurve>();
        var notes = new List<string>();
        Collect(face, view, opts, curves, notes);
        return new SilhouetteResult(curves, notes);
    }

    // ------------------------------------------------------------------ dispatch

    private static void Collect(
        BrepFace face, in SilhouetteView view, SilhouetteOptions options,
        List<SilhouetteCurve> curves, List<string> notes)
    {
        var raw = new List<(Curve3d Curve, SilhouetteFidelity Fidelity)>();
        switch (face.Surface)
        {
            case PlaneSurface plane:
                Planar(plane, face, view, notes);
                break;
            case SphereSurface sphere:
                Spherical(sphere.Center, sphere.Radius, face, view, notes, raw);
                break;
            case CylinderSurface cylinder:
                Cylindrical(cylinder, face, view, notes, raw);
                break;
            case RevolvedSurface revolved:
                Revolved(revolved, face, view, options, notes, raw);
                break;
            case ExtrudedSurface extruded:
                Extruded(extruded, face, view, options, notes, raw);
                break;
            default:
                Traced(face, view, options, notes, raw);
                break;
        }

        foreach (var (curve, fidelity) in raw)
        {
            foreach (var piece in options.ClipToTrim ? ClipToTrim(face, curve, options.Samples) : [curve])
                curves.Add(new SilhouetteCurve(
                    face, piece, fidelity, MeasureDeviation(face, piece, view, options.Samples)));
        }
    }

    // ------------------------------------------------------------------ plane

    /// <summary>
    /// A plane's normal is constant, so <c>g</c> is constant: the face is either wholly
    /// edge-on or contributes nothing. Neither case is a CURVE, which is why an edge-on
    /// plane is reported as a note rather than emitted — its projection is a segment, and
    /// the segment's ends are its own boundary edges, which a drawing already carries
    /// exactly.
    /// </summary>
    private static void Planar(
        PlaneSurface plane, BrepFace face, in SilhouetteView view, List<string> notes)
    {
        if (!plane.Normal.TryNormalize(Tolerance.Default, out var n))
            return;
        double g = view.IsPerspective ? n.Dot(view.DirectionAt(plane.Origin)) : n.Dot(view.Direction);
        if (Math.Abs(g) <= Degenerate)
            notes.Add(Note(face,
                "planar face is edge-on: its projection is a segment, so it has no silhouette "
                + "curve of its own and its boundary edges already carry the outline"));
    }

    // ------------------------------------------------------------------ sphere

    /// <summary>
    /// A sphere's silhouette is a circle, exactly: the great circle through the centre
    /// perpendicular to the view direction for a parallel view, and the POLAR circle of
    /// the eye for a perspective one — plane at <c>r²/D</c> from the centre toward the
    /// eye, radius <c>r√(D²−r²)/D</c>, which reduces to the great circle as D grows.
    /// An eye inside the sphere sees no silhouette at all and is named.
    /// </summary>
    private static void Spherical(
        in Vector3d centre, double radius, BrepFace face, in SilhouetteView view,
        List<string> notes, List<(Curve3d, SilhouetteFidelity)> raw)
    {
        Vector3d axis;
        Vector3d planeCentre;
        double planeRadius;
        if (view.IsPerspective)
        {
            var away = view.Eye - centre;
            double distance = away.Length;
            if (distance <= radius * (1 + Degenerate))
            {
                notes.Add(Note(face, "the eye is inside the sphere, which therefore has no silhouette"));
                return;
            }
            axis = away / distance;
            planeCentre = centre + axis * (radius * radius / distance);
            planeRadius = radius * Math.Sqrt(distance * distance - radius * radius) / distance;
        }
        else
        {
            axis = view.Direction;
            planeCentre = centre;
            planeRadius = radius;
        }

        var x = axis.ArbitraryPerpendicular(Tolerance.Default);
        raw.Add((new Circle3d(planeCentre, x, axis.Cross(x), planeRadius), SilhouetteFidelity.Exact));
    }

    // ------------------------------------------------------------------ cylinder

    /// <summary>
    /// A cylinder's normal at azimuth u is <c>x̂ cos u + ŷ sin u</c>, so
    /// <c>g = a cos u + b sin u (+ r)</c> with <c>(a, b)</c> the view's own components in
    /// the cylinder's frame — two rulings in closed form, and no rulings at all when the
    /// view runs down the axis (where g vanishes identically and the outline is the rim,
    /// a modelled edge) or when a perspective eye is inside the cylinder.
    /// </summary>
    private static void Cylindrical(
        CylinderSurface cylinder, BrepFace face, in SilhouetteView view,
        List<string> notes, List<(Curve3d, SilhouetteFidelity)> raw)
    {
        var w = view.IsPerspective ? cylinder.Origin - view.Eye : view.Direction;
        double a = w.Dot(cylinder.XDirection), b = w.Dot(cylinder.YDirection);
        double magnitude = Math.Sqrt(a * a + b * b);
        double offset = view.IsPerspective ? cylinder.Radius : 0;

        if (magnitude <= Degenerate * Math.Max(1, w.Length))
        {
            notes.Add(Note(face, view.IsPerspective
                ? "the eye lies on the cylinder's axis, so every ruling is equally silhouette"
                : "the cylinder is viewed along its own axis: every ruling is a silhouette and "
                  + "the outline is its rim, which is a modelled edge"));
            return;
        }
        double cosine = -offset / magnitude;
        if (Math.Abs(cosine) > 1)
        {
            notes.Add(Note(face, "the eye is inside the cylinder, which therefore has no silhouette"));
            return;
        }

        double phi = Math.Atan2(b, a);
        double delta = Math.Acos(cosine);
        var span = FaceParameterBox(face).V;
        foreach (double u in new[] { phi + delta, phi - delta })
        {
            raw.Add((new Line3d(cylinder.PointAt(u, span.Start), cylinder.PointAt(u, span.End)),
                SilhouetteFidelity.Exact));
        }
    }

    // ------------------------------------------------------------------ revolve

    /// <summary>
    /// The separable form. Writing the generator offset as <c>q(v) = G(v) − o</c> and
    /// <c>M(v) = (axis × q_⊥) × q'</c>, a revolve's normal is exactly <c>R_u M(v)</c>, so
    /// <c>g</c> is <c>A(v)cos u + B(v)sin u + C(v)</c> and every azimuth is closed form.
    /// </summary>
    private readonly record struct RevolveCoefficients(double A, double B, double C, bool Valid);

    private static void Revolved(
        RevolvedSurface revolved, BrepFace face, in SilhouetteView view, SilhouetteOptions options,
        List<string> notes, List<(Curve3d, SilhouetteFidelity)> raw)
    {
        // A revolve whose generator is a meridian of a sphere IS a sphere, and a sphere's
        // answer is an exact circle rather than a sampled one. The kernel spells spheres
        // this way (SolidFactory.MakeSphere revolves a CurveSegment over a Circle3d), so
        // recognising it here is what makes `Shape.Sphere` silhouette exactly.
        if (TryRecogniseSphere(revolved, out var centre, out double radius))
        {
            Spherical(centre, radius, face, view, notes, raw);
            return;
        }

        var axis = revolved.AxisDirection;
        var origin = revolved.AxisOrigin;
        var x = axis.ArbitraryPerpendicular(Tolerance.Default);
        var y = axis.Cross(x);   // right-handed: R_u x̂ = cos u x̂ + sin u ŷ, the sense PointAt uses
        bool perspective = view.IsPerspective;   // `in` parameters cannot be captured by local functions
        var w = perspective ? origin - view.Eye : view.Direction;
        double wx = w.Dot(x), wy = w.Dot(y), wz = w.Dot(axis);

        RevolveCoefficients Coefficients(double v)
        {
            var q = revolved.Generator.PointAt(v) - origin;
            var qd = revolved.Generator.DerivativeAt(v);
            double qz = q.Dot(axis);
            var qPerp = q - axis * qz;
            var m = axis.Cross(qPerp).Cross(qd);
            double mx = m.Dot(x), my = m.Dot(y), mz = m.Dot(axis);
            double a = mx * wx + my * wy;
            double b = mx * wy - my * wx;
            double c = mz * wz;
            if (perspective)
                c += mz * qz + mx * qPerp.Dot(x) + my * qPerp.Dot(y);
            bool valid = m.LengthSquared > 0;
            return new RevolveCoefficients(a, b, c, valid);
        }

        var vSpan = FaceParameterBox(face).V;
        int samples = Math.Max(16, options.Samples);

        // A view along the axis collapses A and B: g is then C(v) alone, a condition on
        // the GENERATOR, whose roots are exact latitude circles (a torus seen down its
        // axis silhouettes to its inner and outer equators).
        double perpendicular = Math.Sqrt(wx * wx + wy * wy);
        if (perpendicular <= Degenerate * Math.Max(1, w.Length))
        {
            AxisView(revolved, face, vSpan, samples, Coefficients, notes, raw);
            return;
        }

        // A PARTIAL revolve sweeps [0, Angle], and its inverse evaluation folds an azimuth
        // outside that back inside — so the trim clip cannot see a ruling belonging to the
        // missing three quarters, and both of a band's two rulings survive where only one
        // is on the face. The azimuth is known in closed form here, so it is filtered at
        // the SOURCE rather than left to a containment probe downstream.
        double sweep = revolved.Angle;
        bool fullTurn = revolved.IsFullTurn;
        bool InRange(double azimuth)
        {
            if (fullTurn)
                return true;
            double folded = azimuth % (2 * Math.PI);
            if (folded < 0)
                folded += 2 * Math.PI;
            // Angular slack at the weld tier: a ruling landing exactly on the sweep's own
            // end is on the face's boundary, which belongs to it.
            return folded <= sweep + Tolerance.Default.Linear;
        }

        var runs = SolveBranches(vSpan, samples, Coefficients);
        if (runs.Count == 0)
        {
            notes.Add(Note(face, "no azimuth on this surface of revolution turns edge-on to the view"));
            return;
        }
        foreach (var run in runs)
            EmitRun(revolved, run, InRange, raw);
    }

    /// <summary>One connected stretch of generator parameters carrying a solution.</summary>
    private sealed record BranchRun(
        List<double> V, List<double> Plus, List<double> Minus, bool ClosedInV, bool JoinsAtEnds);

    /// <summary>
    /// Walks the generator, keeping the stretches where <c>|C| ≤ √(A²+B²)</c> and
    /// recording both azimuth branches. The stretch ENDS are refined by bisection onto
    /// the exact turning point (where the two branches meet), so a torus's silhouette
    /// closes instead of stopping at whichever sample happened to be last inside — the
    /// same reason <see cref="SurfaceIntersection"/> lands a branch on its rail rather
    /// than one march step short.
    /// </summary>
    private static List<BranchRun> SolveBranches(
        Interval span, int samples, Func<double, RevolveCoefficients> coefficients)
    {
        static double Slack(in RevolveCoefficients k) => Math.Sqrt(k.A * k.A + k.B * k.B) - Math.Abs(k.C);

        var runs = new List<BranchRun>();
        List<double>? v = null;
        List<double>? plus = null;
        List<double>? minus = null;
        bool startedAtDomainStart = false;
        double previousParameter = span.Start;
        bool previousInside = false;

        void Open(double parameter)
        {
            v = [];
            plus = [];
            minus = [];
            startedAtDomainStart = parameter <= span.Start;
        }

        void Add(double parameter)
        {
            var k = coefficients(parameter);
            double magnitude = Math.Sqrt(k.A * k.A + k.B * k.B);
            // Exact-zero guard on a division; a stretch with no azimuthal content at all
            // is handled by the axis-view path, never here.
            if (!(magnitude > 0))
                return;
            double phi = Math.Atan2(k.B, k.A);
            double delta = Math.Acos(Math.Clamp(-k.C / magnitude, -1, 1));
            v!.Add(parameter);
            plus!.Add(phi + delta);
            minus!.Add(phi - delta);
        }

        void Close(bool joins, bool reachedDomainEnd)
        {
            if (v is { Count: >= 2 })
                runs.Add(new BranchRun(v, plus!, minus!,
                    ClosedInV: startedAtDomainStart && reachedDomainEnd, JoinsAtEnds: joins));
            v = null;
            plus = null;
            minus = null;
        }

        for (int i = 0; i <= samples; i++)
        {
            double parameter = span.ParameterAt((double)i / samples);
            var k = coefficients(parameter);
            bool inside = k.Valid && Slack(k) >= 0;
            if (inside && v is null)
            {
                Open(parameter);
                if (i > 0)
                {
                    // Refine the turning point: the two branches meet exactly where the
                    // slack changes sign, so landing on it is what closes the loop.
                    double edge = Bisect(previousParameter, parameter, t => Slack(coefficients(t)));
                    Add(edge);
                }
            }
            if (inside)
            {
                Add(parameter);
            }
            else if (v is not null)
            {
                double edge = Bisect(parameter, previousParameter, t => Slack(coefficients(t)));
                Add(edge);
                Close(joins: true, reachedDomainEnd: false);
            }
            previousParameter = parameter;
            previousInside = inside;
        }
        if (v is not null)
            Close(joins: !previousInside, reachedDomainEnd: true);
        return runs;
    }

    /// <summary>
    /// Emits a run's two azimuth branches. A branch whose azimuth does not move in v is an
    /// ISO-PARAMETER curve — the generator rotated rigidly — so it comes back as exactly
    /// that, which makes a cone's or a cylindrical band's silhouette an exact ruling
    /// carrying the generator's own type. The construction is VERIFIED against the
    /// surface before it is trusted (the rule a rebuilt rim already follows), and falls
    /// back to the sampled polyline if it disagrees.
    /// </summary>
    private static void EmitRun(
        RevolvedSurface revolved, BranchRun run, Func<double, bool> inRange,
        List<(Curve3d, SilhouetteFidelity)> raw)
    {
        bool plusExact = TryConstantAzimuth(run.Plus, out double a0) &&
                         inRange(a0) && TryRotatedGenerator(revolved, a0, out _);
        bool minusExact = TryConstantAzimuth(run.Minus, out double a1) &&
                          inRange(a1) && TryRotatedGenerator(revolved, a1, out _);
        if (plusExact)
            raw.Add((new TransformedCurve(revolved.Generator, RotationAbout(revolved, a0)),
                SilhouetteFidelity.Exact));
        if (minusExact)
            raw.Add((new TransformedCurve(revolved.Generator, RotationAbout(revolved, a1)),
                SilhouetteFidelity.Exact));
        if (plusExact && minusExact)
            return;

        // A branch whose azimuth is CONSTANT but outside a partial revolve's own sweep is
        // not on this face at all, and neither is its sampled spelling.
        bool plusDropped = !plusExact && TryConstantAzimuth(run.Plus, out double b0) && !inRange(b0);
        bool minusDropped = !minusExact && TryConstantAzimuth(run.Minus, out double b1) && !inRange(b1);

        // Both branches meet at the run's ends, so where the ends ARE turning points the
        // run is one curve rather than two — which is what makes a torus's silhouette a
        // closed loop instead of a pair of arcs with a visible gap.
        if (run.JoinsAtEnds && !plusExact && !minusExact && !plusDropped && !minusDropped &&
            run.Plus.All(inRange) && run.Minus.All(inRange))
        {
            var joined = new List<Vector3d>(run.V.Count * 2);
            for (int i = 0; i < run.V.Count; i++)
                joined.Add(revolved.PointAt(run.Plus[i], run.V[i]));
            for (int i = run.V.Count - 1; i >= 0; i--)
                joined.Add(revolved.PointAt(run.Minus[i], run.V[i]));
            AddPolyline(joined, closed: true, raw);
            return;
        }

        foreach (var branch in new[]
        {
            (Azimuths: run.Plus, Done: plusExact || plusDropped),
            (Azimuths: run.Minus, Done: minusExact || minusDropped),
        })
        {
            if (branch.Done)
                continue;
            // Maximal in-range stretches, so a branch leaving a partial revolve's own sweep
            // comes back as the pieces that are on the face rather than as one curve running
            // across surface it does not carry.
            var points = new List<Vector3d>();
            for (int i = 0; i <= run.V.Count; i++)
            {
                if (i < run.V.Count && inRange(branch.Azimuths[i]))
                {
                    points.Add(revolved.PointAt(branch.Azimuths[i], run.V[i]));
                    continue;
                }
                if (points.Count > 0)
                {
                    bool whole = points.Count == run.V.Count;
                    AddPolyline(points, closed: whole && run.ClosedInV && revolved.Generator.IsClosed, raw);
                    points = [];
                }
            }
        }
    }

    /// <summary>Rotation by <paramref name="azimuth"/> about the revolve's own axis line.</summary>
    private static Matrix4d RotationAbout(RevolvedSurface revolved, double azimuth) =>
        Matrix4d.CreateTranslation(revolved.AxisOrigin) *
        Matrix4d.CreateFromAxisAngle(revolved.AxisDirection, azimuth) *
        Matrix4d.CreateTranslation(-revolved.AxisOrigin);

    private static void AddPolyline(
        List<Vector3d> points, bool closed, List<(Curve3d, SilhouetteFidelity)> raw)
    {
        var cleaned = Dedupe(points, closed);
        if (cleaned.Count < (closed ? 4 : 2))
            return;
        raw.Add((new PolylineCurve3d(cleaned, closed), SilhouetteFidelity.Sampled));
    }

    /// <summary>
    /// A view along the axis leaves <c>g = C(v)</c>: the silhouette is the set of
    /// latitude circles where C vanishes, EXACT circles about the axis. A generator on
    /// which C never leaves zero (a cylinder seen down its own axis) is named instead.
    /// </summary>
    private static void AxisView(
        RevolvedSurface revolved, BrepFace face, Interval span, int samples,
        Func<double, RevolveCoefficients> coefficients,
        List<string> notes, List<(Curve3d, SilhouetteFidelity)> raw)
    {
        var axis = revolved.AxisDirection;
        double scale = 0;
        for (int i = 0; i <= samples; i++)
            scale = Math.Max(scale, Math.Abs(coefficients(span.ParameterAt((double)i / samples)).C));
        if (scale <= 0)
        {
            notes.Add(Note(face,
                "the surface of revolution is everywhere edge-on to a view along its own axis, "
                + "so the outline is its rim, which is a modelled edge"));
            return;
        }

        // A sample landing EXACTLY on zero is a root in its own right and must not also be
        // read as a sign change: `Math.Sign(0)` is 0, which makes the bisection's bracket
        // function identically zero and lands the "root" on whichever endpoint the walk
        // happens to collapse toward — measured, a torus down its axis came back with a
        // third, spurious equator 0.13 off the true one, because the generator's own
        // derivative vanishes exactly at the sample the outer band's root sits on.
        var roots = new List<double>();
        double previousParameter = span.Start;
        double previous = coefficients(previousParameter).C;
        if (previous == 0)
            roots.Add(previousParameter);
        for (int i = 1; i <= samples; i++)
        {
            double parameter = span.ParameterAt((double)i / samples);
            double current = coefficients(parameter).C;
            if (current == 0)
                roots.Add(parameter);
            else if (previous != 0 && (previous < 0) != (current < 0))
                roots.Add(Bisect(previousParameter, parameter, t => coefficients(t).C * Math.Sign(current)));
            previousParameter = parameter;
            previous = current;
        }

        foreach (double root in roots)
        {
            var point = revolved.Generator.PointAt(root) - revolved.AxisOrigin;
            double height = point.Dot(axis);
            var radial = point - axis * height;
            double radius = radial.Length;
            if (!(radius > 0))
                continue;   // the generator touches the axis here: a pole, not a circle
            var x = radial / radius;
            raw.Add((new Circle3d(revolved.AxisOrigin + axis * height, x, axis.Cross(x), radius),
                SilhouetteFidelity.Exact));
        }
    }

    private static bool TryConstantAzimuth(List<double> branch, out double azimuth)
    {
        azimuth = branch.Count > 0 ? branch[0] : 0;
        if (branch.Count == 0)
            return false;
        // Compared as unit VECTORS, so a branch straddling the ±pi seam is not mistaken
        // for one that swings right round.
        var reference = new Vector2d(Math.Cos(azimuth), Math.Sin(azimuth));
        foreach (double u in branch)
        {
            if (new Vector2d(Math.Cos(u), Math.Sin(u)).DistanceTo(reference) > ConstantAzimuth)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The generator rotated rigidly onto a constant azimuth — the exact iso-u curve.
    /// Built and then VERIFIED against the surface's own evaluation, so a transform
    /// convention cannot silently put it somewhere else.
    /// </summary>
    private static bool TryRotatedGenerator(RevolvedSurface revolved, double azimuth, out Curve3d curve)
    {
        curve = new TransformedCurve(revolved.Generator, RotationAbout(revolved, azimuth));
        var domain = revolved.Generator.Domain;
        for (int i = 0; i <= 4; i++)
        {
            double v = domain.ParameterAt(i / 4.0);
            if (curve.PointAt(v).DistanceTo(revolved.PointAt(azimuth, v)) > Tolerance.Default.Linear)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the generator is a meridian of a sphere: every sampled point equidistant
    /// from one centre ON the axis. Two samples fix the centre and the radius in closed
    /// form; the rest verify it, which is what stops a torus or a cone being mistaken for
    /// one.
    /// </summary>
    private static bool TryRecogniseSphere(RevolvedSurface revolved, out Vector3d centre, out double radius)
    {
        centre = default;
        radius = 0;
        var axis = revolved.AxisDirection;
        var origin = revolved.AxisOrigin;
        var domain = revolved.Generator.Domain;
        const int samples = 8;
        Span<double> heights = stackalloc double[samples + 1];
        Span<double> lengths = stackalloc double[samples + 1];
        double extent = 0;
        for (int i = 0; i <= samples; i++)
        {
            var q = revolved.Generator.PointAt(domain.ParameterAt((double)i / samples)) - origin;
            heights[i] = q.Dot(axis);
            lengths[i] = q.LengthSquared;
            extent = Math.Max(extent, q.Length);
        }
        if (extent <= 0)
            return false;

        // Pick the widest-separated pair in the axial coordinate: |q|² − 2h·q_z + h² = r²
        // differenced over two samples gives h with no iteration.
        int lo = 0, hi = 0;
        for (int i = 0; i <= samples; i++)
        {
            if (heights[i] < heights[lo]) lo = i;
            if (heights[i] > heights[hi]) hi = i;
        }
        double separation = heights[hi] - heights[lo];
        if (separation <= extent * 1e-6)
            return false;   // a flat generator: an annulus, not a meridian

        double h = (lengths[hi] - lengths[lo]) / (2 * separation);
        double squared = lengths[lo] - 2 * h * heights[lo] + h * h;
        if (!(squared > 0))
            return false;
        radius = Math.Sqrt(squared);
        centre = origin + axis * h;
        for (int i = 0; i <= samples; i++)
        {
            var q = revolved.Generator.PointAt(domain.ParameterAt((double)i / samples));
            if (Math.Abs(q.DistanceTo(centre) - radius) > extent * 1e-9)
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ extrusion

    /// <summary>
    /// An extrusion's normal <c>C'(u) × dir</c> does not depend on v, so its silhouette is
    /// a set of RULINGS — the generator parameters where <c>C'(u)·(dir × d)</c> vanishes
    /// (or, in perspective, <c>(C'(u) × dir)·(C(u) − e)</c>, which loses its v term for
    /// the same reason). The roots are bracketed on a sampling of the generator and
    /// bisected on the exact function, so the ruling is an exact <see cref="Line3d"/> ON
    /// the surface whatever the generator is; the residual rides on
    /// <see cref="SilhouetteCurve.Deviation"/> rather than being asserted.
    /// </summary>
    private static void Extruded(
        ExtrudedSurface extruded, BrepFace face, in SilhouetteView view, SilhouetteOptions options,
        List<string> notes, List<(Curve3d, SilhouetteFidelity)> raw)
    {
        var direction = extruded.Direction;
        var eye = view.Eye;
        var d = view.Direction;
        bool perspective = view.IsPerspective;
        var m = perspective ? Vector3d.Zero : direction.Cross(d);

        double G(double u)
        {
            var tangent = extruded.Generator.DerivativeAt(u);
            return perspective
                ? tangent.Cross(direction).Dot(extruded.Generator.PointAt(u) - eye)
                : tangent.Dot(m);
        }

        if (!perspective && m.Length <= Degenerate)
        {
            notes.Add(Note(face,
                "the extrusion is viewed along its own direction, so every ruling is edge-on and "
                + "the outline is the generator itself, which is a modelled edge"));
            return;
        }

        var span = FaceParameterBox(face).U;
        int samples = Math.Max(32, options.Samples * 2);
        var roots = new List<double>();
        double previousParameter = span.Start;
        double previous = G(previousParameter);
        if (previous == 0)
            roots.Add(previousParameter);
        for (int i = 1; i <= samples; i++)
        {
            double parameter = span.ParameterAt((double)i / samples);
            double current = G(parameter);
            if (current == 0)
                roots.Add(parameter);
            else if (previous != 0 && (previous < 0) != (current < 0))
                roots.Add(Bisect(previousParameter, parameter, t => G(t) * Math.Sign(current)));
            previousParameter = parameter;
            previous = current;
        }
        if (roots.Count == 0)
        {
            notes.Add(Note(face, "no ruling of this extrusion turns edge-on to the view"));
            return;
        }

        var vSpan = FaceParameterBox(face).V;
        foreach (double u in roots)
        {
            raw.Add((new Line3d(extruded.PointAt(u, vSpan.Start), extruded.PointAt(u, vSpan.End)),
                SilhouetteFidelity.Exact));
        }
    }

    // ------------------------------------------------------------------ tracer

    /// <summary>
    /// The level-set trace for the families with no closed form: NURBS, swept, lofted and
    /// helical surfaces. <c>g(u,v) = N̂·v̂</c> — the UNIT normal against the unit view
    /// direction, so g is a sine and the corrector's tolerance is an angle rather than a
    /// quantity whose scale depends on the surface's parameterization.
    ///
    /// <para>Three rules are taken from <see cref="SurfaceIntersection"/> verbatim rather
    /// than re-derived: the seed grid's counts are proportional to the surface's MODEL
    /// extents in each direction (a grid in parameter space says nothing about coverage in
    /// model space), the march step is measured in model units, and a branch leaving the
    /// parameter rectangle is landed exactly on the boundary rather than stopped one step
    /// short.</para>
    /// </summary>
    private static void Traced(
        BrepFace face, in SilhouetteView view, SilhouetteOptions options,
        List<string> notes, List<(Curve3d, SilhouetteFidelity)> raw)
    {
        var surface = face.Surface;
        if (!options.AllowTraced)
        {
            notes.Add(Note(face,
                $"{surface.GetType().Name} has no closed-form silhouette and tracing is switched off"));
            return;
        }

        var box = FaceParameterBox(face);
        if (!double.IsFinite(box.U.Length) || !double.IsFinite(box.V.Length) ||
            box.U.Length <= 0 || box.V.Length <= 0)
        {
            notes.Add(Note(face,
                $"{surface.GetType().Name} has no bounded parameter box to trace its silhouette over"));
            return;
        }

        var viewCopy = view;
        double G(double u, double v)
        {
            var p = surface.PointAt(u, v);
            var n = surface.NormalRawAt(u, v);
            double length = n.Length;
            // Normalised so g is a SINE and the corrector's tolerance is an angle rather
            // than a quantity whose scale depends on the parameterization; a degenerate
            // normal (a pole) reads zero, which the corrector then walks away from.
            return length > 0 ? n.Dot(viewCopy.DirectionAt(p)) / length : 0;
        }

        // Model extents per direction, measured the way the intersection tracer measures
        // them: mean speed times domain length, never a chord (a chord across a coiled
        // band measures the coil rather than the band).
        var (extentU, extentV, modelSize) = ModelExtents(surface, box);
        if (!(modelSize > 0))
            return;

        int budget = Math.Max(24, options.Samples / 2);
        var (nu, nv) = GridCounts(extentU, extentV, budget);
        double step = modelSize / 150.0;

        var traced = new List<Vector3d>();
        for (int i = 0; i <= nu; i++)
        {
            for (int j = 0; j <= nv; j++)
            {
                double u0 = box.U.ParameterAt((double)i / nu);
                double v0 = box.V.ParameterAt((double)j / nv);
                double g0 = G(u0, v0);
                // Seeds come from sign changes along the grid's own edges: a cull over the
                // sampled grid, not a flood from a guess.
                foreach (var (u1, v1) in new[]
                {
                    (i < nu ? box.U.ParameterAt((double)(i + 1) / nu) : u0, v0),
                    (u0, j < nv ? box.V.ParameterAt((double)(j + 1) / nv) : v0),
                })
                {
                    if (u1 == u0 && v1 == v0)
                        continue;
                    double g1 = G(u1, v1);
                    if ((g0 < 0) == (g1 < 0))
                        continue;
                    double t = Bisect(0, 1, s => G(u0 + (u1 - u0) * s, v0 + (v1 - v0) * s) * Math.Sign(g1));
                    double su = u0 + (u1 - u0) * t, sv = v0 + (v1 - v0) * t;
                    var seedPoint = surface.PointAt(su, sv);
                    if (traced.Any(q => q.DistanceSquaredTo(seedPoint) < 4 * step * step))
                        continue;

                    var points = TraceLevelSet(surface, box, G, su, sv, step, out bool closed);
                    if (points.Count < 3)
                        continue;
                    traced.AddRange(points);
                    var cleaned = Dedupe(points, closed);
                    if (cleaned.Count >= (closed ? 4 : 2))
                        raw.Add((new PolylineCurve3d(cleaned, closed), SilhouetteFidelity.Traced));
                }
            }
        }
    }

    private static List<Vector3d> TraceLevelSet(
        Surface surface, (Interval U, Interval V) box, Func<double, double, double> g,
        double seedU, double seedV, double step, out bool closed)
    {
        closed = false;
        var forward = Walk(+1, out bool loop);
        if (loop)
        {
            closed = true;
            return forward;
        }
        var backward = Walk(-1, out _);
        backward.Reverse();
        backward.RemoveAt(backward.Count - 1);
        backward.AddRange(forward);
        return backward;

        List<Vector3d> Walk(int direction, out bool closedLoop)
        {
            closedLoop = false;
            const int maxSteps = 4000;
            double hu = box.U.Length * 1e-6, hv = box.V.Length * 1e-6;
            double u = seedU, v = seedV;
            var start = surface.PointAt(u, v);
            var points = new List<Vector3d> { start };
            Vector2d? previousTangent = null;

            for (int i = 0; i < maxSteps; i++)
            {
                double gu = (g(u + hu, v) - g(u - hu, v)) / (2 * hu);
                double gv = (g(u, v + hv) - g(u, v - hv)) / (2 * hv);
                double gradient = Math.Sqrt(gu * gu + gv * gv);
                if (!(gradient > 0))
                    break;   // the level set is not locally a curve here

                var tangent = new Vector2d(-gv, gu) / gradient;
                if (previousTangent is { } prev && tangent.Dot(prev) < 0)
                    tangent = -tangent;
                else if (previousTangent is null)
                    tangent *= direction;
                previousTangent = tangent;

                // Parameter step whose MODEL displacement is one march step.
                var du = (surface.PointAt(u + hu, v) - surface.PointAt(u - hu, v)) / (2 * hu);
                var dv = (surface.PointAt(u, v + hv) - surface.PointAt(u, v - hv)) / (2 * hv);
                double speed = (du * tangent.X + dv * tangent.Y).Length;
                if (!(speed > 0))
                    break;
                double scale = step / speed;

                double nu = u + tangent.X * scale, nv = v + tangent.Y * scale;
                bool outside = nu < box.U.Start || nu > box.U.End || nv < box.V.Start || nv > box.V.End;
                if (outside)
                {
                    // Land exactly on the rail: bisect the step against the boundary it
                    // crosses first, then correct back onto g = 0 along the boundary.
                    double fraction = 1;
                    fraction = Math.Min(fraction, Fraction(u, nu, box.U));
                    fraction = Math.Min(fraction, Fraction(v, nv, box.V));
                    double lu = u + (nu - u) * fraction, lv = v + (nv - v) * fraction;
                    points.Add(surface.PointAt(
                        Math.Clamp(lu, box.U.Start, box.U.End), Math.Clamp(lv, box.V.Start, box.V.End)));
                    break;
                }

                // Corrector: Newton along the gradient, which is the shortest way back.
                for (int k = 0; k < 6; k++)
                {
                    double value = g(nu, nv);
                    if (Math.Abs(value) < 1e-13)
                        break;
                    double cu = (g(nu + hu, nv) - g(nu - hu, nv)) / (2 * hu);
                    double cv = (g(nu, nv + hv) - g(nu, nv - hv)) / (2 * hv);
                    double squared = cu * cu + cv * cv;
                    if (!(squared > 0))
                        break;
                    nu -= value * cu / squared;
                    nv -= value * cv / squared;
                }
                if (nu < box.U.Start || nu > box.U.End || nv < box.V.Start || nv > box.V.End)
                    break;

                u = nu;
                v = nv;
                var point = surface.PointAt(u, v);
                points.Add(point);
                if (i > 5 && point.DistanceTo(start) < step)
                {
                    closedLoop = true;
                    break;
                }
            }
            return points;
        }

        static double Fraction(double from, double to, Interval interval)
        {
            if (to > interval.End && to != from)
                return (interval.End - from) / (to - from);
            if (to < interval.Start && to != from)
                return (interval.Start - from) / (to - from);
            return 1;
        }
    }

    private static (double U, double V, double Size) ModelExtents(Surface surface, (Interval U, Interval V) box)
    {
        const int samples = 4;
        double speedU = 0, speedV = 0;
        double hu = box.U.Length / 1024, hv = box.V.Length / 1024;
        var bounds = Aabb.Empty;
        for (int i = 0; i < samples; i++)
        {
            double u = box.U.ParameterAt((i + 0.5) / samples);
            for (int j = 0; j < samples; j++)
            {
                double v = box.V.ParameterAt((j + 0.5) / samples);
                speedU += surface.PointAt(u + hu, v).DistanceTo(surface.PointAt(u - hu, v)) / (2 * hu);
                speedV += surface.PointAt(u, v + hv).DistanceTo(surface.PointAt(u, v - hv)) / (2 * hv);
                bounds = bounds.Union(surface.PointAt(u, v));
            }
        }
        double size = bounds.IsEmpty ? 0 : bounds.Size.Length;
        return (speedU / (samples * samples) * box.U.Length,
                speedV / (samples * samples) * box.V.Length,
                size);
    }

    private static (int Nu, int Nv) GridCounts(double extentU, double extentV, int budget)
    {
        if (!(extentU > 0) || !(extentV > 0))
            return (budget, budget);
        double aspect = extentU / extentV;
        if (aspect < 4 && aspect > 0.25)
            return (budget, budget);
        double root = Math.Sqrt(aspect);
        return (Math.Clamp((int)Math.Round(budget * root), 1, budget * 64),
                Math.Clamp((int)Math.Round(budget / root), 1, budget * 64));
    }

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// The piece(s) of a silhouette curve that lie on the face, for the reason
    /// <c>BrepBoolean.ClipToFace</c> gives: a carrier is unbounded (a plane, a cylinder)
    /// or bounded only by its own parameter rectangle, so the curve runs past the face
    /// and an unclipped answer draws a line across surface the face does not carry.
    ///
    /// <para>Transitions are found by bisecting the face's own containment predicate
    /// (<see cref="FaceGeometry.InsideOrOnBoundary"/>), so a piece ends on the trim rather
    /// than at whichever sample happened to be nearest; a curve surviving whole is
    /// returned as ITSELF so a closed circle stays closed, and a closed curve's surviving
    /// stretches join across the seam — both rules the boolean's clip already states.</para>
    /// </summary>
    private static List<Curve3d> ClipToTrim(BrepFace face, Curve3d curve, int samples)
    {
        var domain = curve.Domain;
        double epsilon = Math.Max(1e-12, domain.Length * 1e-9);
        var parameters = FaceGeometry.ExactSampleParameters(
            curve, domain.Start, domain.End, Math.Max(16, samples));

        bool Inside(double t) => FaceGeometry.InsideOrOnBoundary(face, curve.PointAt(t));

        var kept = new List<(double S0, double S1)>();
        bool previous = Inside(parameters[0]);
        double runStart = previous ? parameters[0] : double.NaN;
        for (int i = 1; i < parameters.Count; i++)
        {
            bool current = Inside(parameters[i]);
            if (current == previous)
                continue;
            double edge = BisectPredicate(parameters[i - 1], parameters[i], t => Inside(t) == previous);
            if (previous)
                kept.Add((runStart, edge));
            else
                runStart = edge;
            previous = current;
        }
        if (previous)
            kept.Add((runStart, parameters[^1]));

        if (kept.Count == 0)
            return [];
        if (kept.Count == 1 && kept[0].S0 <= domain.Start + epsilon && kept[0].S1 >= domain.End - epsilon)
            return [curve];
        if (curve.IsClosed && kept.Count > 1 &&
            kept[0].S0 <= domain.Start + epsilon && kept[^1].S1 >= domain.End - epsilon)
        {
            var wrapped = (kept[^1].S0, kept[0].S1 + domain.Length);
            kept.RemoveAt(kept.Count - 1);
            kept[0] = wrapped;
        }
        return [.. kept
            .Where(k => k.S1 - k.S0 > epsilon)
            .Select(k => (Curve3d)new CurveSegment(curve, k.S0, k.S1))];
    }

    /// <summary>
    /// How far the reported curve misses being a silhouette, as the SINE of the angle
    /// between the surface normal and the view plane. Measured at the curve's own exact
    /// samples through the one shared rule, so a polyline is read at its vertices — where
    /// it is exact — and an analytic curve uniformly.
    /// </summary>
    private static double MeasureDeviation(
        BrepFace face, Curve3d curve, in SilhouetteView view, int samples)
    {
        double worst = 0;
        var domain = curve.Domain;
        foreach (double t in FaceGeometry.ExactSampleParameters(
            curve, domain.Start, domain.End, Math.Max(8, samples)))
        {
            var p = curve.PointAt(t);
            // Projected TIGHT first: the measurement's own floor would otherwise be the
            // 1e-6 inverse-evaluation tolerance rather than the answer's accuracy — a
            // sphere's exact great circle measured 1.3e-7 purely because the normal was
            // read a micron along the surface from the point it belongs to.
            if (!face.Surface.TryProjectPoint(p, out var uv, 1e-12) &&
                !face.Surface.TryProjectPoint(p, out uv, FaceGeometry.InverseEvaluationTolerance))
                continue;
            // NormalRawAt, not NormalAt: the exact unnormalised normal, whose LENGTH is
            // the guard a pole needs (a pole has no normal, and a silhouette circle on a
            // sphere legitimately runs through the parameter value where one sits).
            var n = face.Surface.NormalRawAt(uv.X, uv.Y);
            double length = n.Length;
            if (!(length > 0))
                continue;
            worst = Math.Max(worst, Math.Abs(n.Dot(view.DirectionAt(p)) / length));
        }
        return worst;
    }

    /// <summary>The face's own parameter box, from its pulled-back loops.</summary>
    private static (Interval U, Interval V) FaceParameterBox(BrepFace face)
    {
        double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
        double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
        try
        {
            foreach (var loop in FaceGeometry.PullLoops(face))
            {
                foreach (var p in loop)
                {
                    uMin = Math.Min(uMin, p.X);
                    uMax = Math.Max(uMax, p.X);
                    vMin = Math.Min(vMin, p.Y);
                    vMax = Math.Max(vMax, p.Y);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // A loop that will not pull back says nothing about the face's extent; fall
            // through to the surface's own domain, which is what the caller can use.
        }
        var du = face.Surface.DomainU;
        var dv = face.Surface.DomainV;
        if (double.IsInfinity(uMin) || uMax <= uMin)
            (uMin, uMax) = (du.Start, du.End);
        if (double.IsInfinity(vMin) || vMax <= vMin)
            (vMin, vMax) = (dv.Start, dv.End);
        if (double.IsFinite(du.Start) && double.IsFinite(du.End))
            (uMin, uMax) = (Math.Max(uMin, du.Start), Math.Min(uMax, du.End));
        if (double.IsFinite(dv.Start) && double.IsFinite(dv.End))
            (vMin, vMax) = (Math.Max(vMin, dv.Start), Math.Min(vMax, dv.End));
        return (new Interval(uMin, uMax), new Interval(vMin, vMax));
    }

    /// <summary>Bisection for a function negative at <paramref name="a"/> and positive at
    /// <paramref name="b"/>, to relative machine precision.</summary>
    private static double Bisect(double a, double b, Func<double, double> f)
    {
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (a + b);
            if (mid == a || mid == b)
                break;
            if (f(mid) < 0)
                a = mid;
            else
                b = mid;
        }
        return 0.5 * (a + b);
    }

    /// <summary>Bisection for a boolean predicate true at <paramref name="a"/> and false at
    /// <paramref name="b"/>.</summary>
    private static double BisectPredicate(double a, double b, Func<double, bool> holds)
    {
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (a + b);
            if (mid == a || mid == b)
                break;
            if (holds(mid))
                a = mid;
            else
                b = mid;
        }
        return 0.5 * (a + b);
    }

    private static List<Vector3d> Dedupe(List<Vector3d> points, bool closed)
    {
        var result = new List<Vector3d>(points.Count);
        foreach (var p in points)
        {
            if (result.Count == 0 || result[^1].DistanceTo(p) > Tolerance.Default.Linear)
                result.Add(p);
        }
        if (closed && result.Count > 1 && result[0].DistanceTo(result[^1]) > Tolerance.Default.Linear)
            result.Add(result[0]);
        else if (closed && result.Count > 1)
            result[^1] = result[0];   // a closed polyline's end IS its start, exactly
        return result;
    }

    private static string Note(BrepFace face, string message) =>
        $"{face.Surface.GetType().Name}: {message}.";
}
