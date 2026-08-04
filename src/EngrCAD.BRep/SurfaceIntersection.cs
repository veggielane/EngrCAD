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
    /// <summary>
    /// OPT-IN post-pass: re-expresses traced <see cref="PolylineCurve3d"/> results as exact
    /// <see cref="Line3d"/> and rational-arc chains where a biarc fit meets the caller's
    /// tolerance. Curves that are already analytic pass through untouched.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing in the kernel calls this.</b> <see cref="Intersect"/> still returns
    /// polylines, and the boolean pipeline still consumes them, deliberately: a traced
    /// polyline is exact only at its VERTICES, and the whole splitting machinery is built
    /// around that fact (<c>FaceGeometry.ExactSampleParameters</c>). Replacing an edge's
    /// carrier with a fitted arc moves every point on it by up to the fit tolerance, which
    /// is orders of magnitude past the 1e-9 weld tier — so adoption is the CALLER's
    /// decision, made against the caller's own tolerance, for consumers that want light
    /// analytic geometry (STEP export, drawing views, path output) rather than weldable
    /// topology.</para>
    /// <para>Every outcome is reported rather than assumed: <see cref="AnalyticFit.Status"/>
    /// says why a curve was not fitted (a non-planar space curve is REFUSED, never silently
    /// flattened) and <see cref="AnalyticFit.Deviation"/> says what the accepted fit cost.
    /// The deviation measures the traced SAMPLES, which says nothing about the true
    /// intersection between them — that is a property of the tracer's step, not of the fit.</para>
    /// </remarks>
    public static IReadOnlyList<AnalyticFit> FitAnalytic(IReadOnlyList<Curve3d> curves, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(curves);
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Fit tolerance must be positive.");

        var results = new List<AnalyticFit>(curves.Count);
        foreach (var curve in curves)
        {
            if (curve is not PolylineCurve3d polyline)
            {
                // Already exact; a "fit" would only be able to make it worse.
                results.Add(new AnalyticFit(curve, [curve], 0, BiArcFitStatus.Success, Fitted: false));
                continue;
            }
            var status = BiArcFit.TryFitPolyline(polyline.Points, tolerance, out var chain);
            if (status != BiArcFitStatus.Success || chain.MaxDeviation > tolerance)
            {
                results.Add(new AnalyticFit(
                    curve, [curve],
                    status == BiArcFitStatus.Success ? chain.MaxDeviation : double.NaN, status, Fitted: false));
                continue;
            }
            results.Add(new AnalyticFit(curve, chain.Curves, chain.MaxDeviation, status, Fitted: true));
        }
        return results;
    }

    public static IReadOnlyList<Curve3d> Intersect(Surface a, Surface b, in Aabb region)
    {
        if (region.IsEmpty)
            throw new ArgumentException("Region must be non-empty.", nameof(region));

        // An extrusion of a full circle along its axis IS a cylinder — promote it so
        // drilled bores get exact analytic intersection circles.
        a = Promote(a);
        b = Promote(b);

        // A plane parallel to an extrusion's generator plane sections it in the generator
        // translated along the extrude direction — exact for ANY generator (straight
        // pocket walls, slot arcs, spline glyphs) and bounded EXACTLY to the generator's
        // own extent. Checked before the planar-patch path below so that a straight
        // generator keeps its own parameterization (adjacent profile segments then share
        // corner points bit-for-bit, which is what lets the pocket outline close).
        if (a is PlaneSurface planeA && b is ExtrudedSurface extrudedB &&
            TryPlaneExtrudedSection(planeA, extrudedB, out var sectionB))
            return sectionB;
        if (b is PlaneSurface planeB && a is ExtrudedSurface extrudedA &&
            TryPlaneExtrudedSection(planeB, extrudedA, out var sectionA))
            return sectionA;

        // Planar carriers meeting at an angle. An extrusion of a STRAIGHT generator is
        // geometrically a plane, but a BOUNDED one: the analytic line must be clipped to
        // the parallelogram it actually covers, not just to the query region — an
        // unclipped carrier line slices clean across neighbouring pockets (the trap that
        // breaks glyph-scale engraving). Two unbounded planes clip to the region only,
        // exactly as before.
        bool planarA = TryPlanarPatch(a, out var patchA);
        bool planarB = TryPlanarPatch(b, out var patchB);
        if (planarA && planarB)
            return PlanarPatches(patchA, patchB, region);

        // A bore drilled into an extruded SIDE wall meets its cylinder in exactly the
        // circle the identical bore on a cap does — but only if the wall is recognized
        // as the plane it is. Without this the rim came back as a fixed ~57-sample
        // tracer polyline whose volume error no tessellation density could lower.
        // BOUNDED patches only: an unbounded PlaneSurface keeps the switch path below
        // verbatim, so nothing that worked before takes a new route.
        if (planarA && patchA.Bounded && TryPatchQuadric(patchA, b, region, out var wallCurvesA))
            return wallCurvesA;
        if (planarB && patchB.Bounded && TryPatchQuadric(patchB, a, region, out var wallCurvesB))
            return wallCurvesB;

        return (a, b) switch
        {
            (PlaneSurface p, CylinderSurface c) => PlaneCylinder(p, c, region),
            (CylinderSurface c, PlaneSurface p) => PlaneCylinder(p, c, region),
            (PlaneSurface p, SphereSurface s) => PlaneSphere(p, s),
            (SphereSurface s, PlaneSurface p) => PlaneSphere(p, s),
            (SphereSurface sa, SphereSurface sb) => SphereSphere(sa, sb),
            (CylinderSurface ca, CylinderSurface cb) when ca.Axis.IsParallelTo(cb.Axis, Tolerance.Default)
                => ParallelCylinders(ca, cb, region),
            (PlaneSurface p, RevolvedSurface r) when IsPerpendicularFullTurn(p, r) => PlaneRevolved(p, r),
            (RevolvedSurface r, PlaneSurface p) when IsPerpendicularFullTurn(p, r) => PlaneRevolved(p, r),
            // Sphere-carrier revolved surfaces (hemispheres of MakeSphere): any plane
            // cut is an exact circle. The perpendicular case above takes priority — its
            // circles are phase-aligned with u = 0 for band-grid welding; a tilted
            // circle is never a grid ring, so the sphere frame is free here. The full
            // carrier circle may run past the bounded generator — the face splitter
            // clips curves to a face's surface itself (partial pullback runs).
            (PlaneSurface p, RevolvedSurface r) when TrySphereCarrier(r, out var sphere) => PlaneSphere(p, sphere),
            (RevolvedSurface r, PlaneSurface p) when TrySphereCarrier(r, out var sphere) => PlaneSphere(p, sphere),
            (PlaneSurface p, HelicalSurface h) when IsPerpendicularToHelicalAxis(p, h) => PlaneHelical(p, h),
            (HelicalSurface h, PlaneSurface p) when IsPerpendicularToHelicalAxis(p, h) => PlaneHelical(p, h),
            // A coaxial ANNULUS is the axis-perpendicular plane restricted to its own
            // radial extent — the b = infinity member of the family below, which no
            // radius = a + b·axial form can carry. Checked first so the disk never
            // reaches the straight-profile fit that must refuse it.
            (HelicalSurface h, RevolvedSurface r) when TryCoaxialDisk(r, h, out double zDisk, out var radial)
                => PlaneHelicalAt(zDisk, h, radial),
            (RevolvedSurface r, HelicalSurface h) when TryCoaxialDisk(r, h, out double zDisk, out var radial)
                => PlaneHelicalAt(zDisk, h, radial),
            // A coaxial surface of revolution whose (radius, axial) profile is a straight
            // LINE — a thread's end-chamfer cone, a coaxial cylinder — meets a helical
            // band in an exact conical spiral. See CoaxialHelical.
            (HelicalSurface h, RevolvedSurface r) when TryCoaxialProfileLine(r, h, out var line)
                => CoaxialHelical(line, h),
            (RevolvedSurface r, HelicalSurface h) when TryCoaxialProfileLine(r, h, out var line)
                => CoaxialHelical(line, h),
            (HelicalSurface h, CylinderSurface c) when TryCoaxialProfileLine(c, h, out var line)
                => CoaxialHelical(line, h),
            (CylinderSurface c, HelicalSurface h) when TryCoaxialProfileLine(c, h, out var line)
                => CoaxialHelical(line, h),
            _ => March(a, b, region),
        };
    }

    /// <summary>
    /// Detects a full-turn revolved surface whose generator lies on a sphere centered on
    /// the revolve axis — the carrier is that sphere. The generator is SAMPLED (never
    /// trust <see cref="Curve3d.Underlying"/> for position): the center along the axis
    /// comes from equating two sample distances, then every sample must agree.
    /// </summary>
    private static bool TrySphereCarrier(RevolvedSurface revolved, out SphereSurface sphere)
    {
        sphere = null!;
        if (!revolved.IsFullTurn)
            return false;

        var axis = revolved.AxisDirection.Normalized();
        var generator = revolved.Generator;
        var domain = generator.Domain;
        const int samples = 16;
        Span<Vector3d> offsets = stackalloc Vector3d[samples + 1];
        for (int i = 0; i <= samples; i++)
            offsets[i] = generator.PointAt(domain.ParameterAt((double)i / samples)) - revolved.AxisOrigin;

        // Center C = AxisOrigin + t·axis with |q0 − t·axis|² = |q1 − t·axis|²  ⇒
        // t = (|q0|² − |q1|²) / (2·(q0 − q1)·axis). Use the endpoints (largest spread).
        var q0 = offsets[0];
        var q1 = offsets[^1];
        double denominator = 2 * (q0 - q1).Dot(axis);
        if (Math.Abs(denominator) < 1e-12)
            return false;
        double t = (q0.LengthSquared - q1.LengthSquared) / denominator;

        double radius = (q0 - axis * t).Length;
        if (radius < Tolerance.Default.Linear)
            return false;
        double tolerance = Math.Max(1e-9, radius * 1e-12);
        for (int i = 1; i <= samples; i++)
        {
            if (Math.Abs((offsets[i] - axis * t).Length - radius) > tolerance)
                return false;
        }
        sphere = new SphereSurface(revolved.AxisOrigin + axis * t, radius);
        return true;
    }

    private static bool IsPerpendicularFullTurn(PlaneSurface plane, RevolvedSurface revolved) =>
        revolved.IsFullTurn && plane.Normal.IsParallelTo(revolved.AxisDirection, Tolerance.Default);

    private static bool IsPerpendicularToHelicalAxis(PlaneSurface plane, HelicalSurface helical) =>
        plane.Normal.IsParallelTo(helical.Frame.Z, Tolerance.Default);

    /// <summary>
    /// Plane ⊥ helical axis: the exact spiral arc (<see cref="SpiralArc3d"/> — radius
    /// linear in the angle, a circular arc on constant-radius bands), built on the
    /// band's own axis frame translated to the cap height (phase alignment: the arc
    /// parameter IS the surface u, the SAME arithmetic <c>MakeThreadedRod</c> uses for
    /// its cap cuts, so tessellation samples coincide). With the generator
    /// (r, z) = start + v·(dr, dz) and axial advance rate·u, the cap height fixes
    /// v(u) = (z_cap − z0 − rate·u)/dz — linear — so the cut spans the u-interval where
    /// v runs 0…1, clipped to the band's domain. A dz = 0 helicoid ramp meets the plane
    /// at a single angle: an exact radial line segment.
    /// </summary>
    private static List<Curve3d> PlaneHelical(PlaneSurface plane, HelicalSurface helical)
    {
        var frame = helical.Frame;
        return PlaneHelicalAt((plane.Origin - frame.Origin).Dot(frame.Z), helical, null);
    }

    /// <summary>
    /// The plane cut above, taking the cap height DIRECTLY and admitting a bounded
    /// carrier: a coaxial annulus is that same plane restricted to
    /// <paramref name="radialRange"/>. Passing null is the unbounded plane and is
    /// bit-for-bit the previous arithmetic — the v = 0 and v = 1 expressions are kept
    /// verbatim and only a clip that actually moves them takes the general form.
    /// </summary>
    private static List<Curve3d> PlaneHelicalAt(
        double zCap, HelicalSurface helical, Interval? radialRange)
    {
        var frame = helical.Frame;
        double rate = helical.AxialRate;
        double z0 = helical.ProfileStart.Y, z1 = helical.ProfileEnd.Y;
        double r0 = helical.ProfileStart.X;
        double dr = helical.ProfileEnd.X - r0, dz = z1 - z0;

        // The generator range the carrier admits. An unbounded plane takes the whole
        // generator; an annulus takes the v where the radius r0 + dr·v lies on it.
        double vLo = 0, vHi = 1;
        if (radialRange is { } radii)
        {
            // Deliberate exact-zero test: dr divides below, and a bit-zero dr (a crest or
            // root flat) makes the radius constant — on the annulus or off it, no interval.
            if (dr == 0)
            {
                if (r0 < radii.Start || r0 > radii.End)
                    return [];
            }
            else
            {
                double vA = (radii.Start - r0) / dr, vB = (radii.End - r0) / dr;
                vLo = Math.Max(vLo, Math.Min(vA, vB));
                vHi = Math.Min(vHi, Math.Max(vA, vB));
                if (!(vHi - vLo > 1e-12))
                    return [];
            }
        }

        // Deliberate exact-zero test: dz divides the general branch below, and only a
        // bit-zero dz (horizontal generator) makes that division invalid.
        if (dz == 0)
        {
            double u = (zCap - z0) / rate;
            if (u < helical.DomainU.Start - 1e-9 || u > helical.DomainU.End + 1e-9)
                return [];
            return [new Line3d(helical.PointAt(u, vLo), helical.PointAt(u, vHi))];
        }

        // u where the cut meets the admitted generator ends, ascending regardless of
        // signs. vLo/vHi are compared against the literals they were seeded with, so an
        // unclipped cut keeps the original expressions bit for bit.
        double uAt0 = vLo == 0 ? (zCap - z0) / rate : (zCap - z0 - dz * vLo) / rate;
        double uAt1 = vHi == 1 ? (zCap - z1) / rate : (zCap - z0 - dz * vHi) / rate;
        double uLo = Math.Max(Math.Min(uAt0, uAt1), helical.DomainU.Start);
        double uHi = Math.Min(Math.Max(uAt0, uAt1), helical.DomainU.End);
        if (!(uHi - uLo > 1e-12))
            return [];

        var capFrame = Frame3d.FromOrthonormal(frame.Origin + frame.Z * zCap, frame.X, frame.Y);
        return
        [
            new SpiralArc3d(capFrame, r0 + dr * (zCap - z0) / dz, -dr * rate / dz, new Interval(uLo, uHi)),
        ];
    }

    /// <summary>
    /// A coaxial carrier expressed in the HELICAL band's own cylindrical coordinates:
    /// radius = <paramref name="A"/> + <paramref name="B"/> · axial, valid over
    /// <paramref name="AxialRange"/>. A cone has B ≠ 0, a coaxial cylinder B = 0.
    /// </summary>
    private readonly record struct CoaxialProfileLine(double A, double B, Interval AxialRange);

    /// <summary>Number of generator samples the coaxial recognizers fit through.</summary>
    private const int CoaxialSamples = 16;

    private static bool IsCoaxialWith(
        in Vector3d axisOrigin, in Vector3d axisDirection, in Frame3d frame)
    {
        if (!axisDirection.IsParallelTo(frame.Z, Tolerance.Default))
            return false;
        var d = axisOrigin - frame.Origin;
        return (d - frame.Z * d.Dot(frame.Z)).Length <= Tolerance.Default.Linear;
    }

    /// <summary>
    /// Samples a full-turn revolve's generator into (radius, axial) pairs measured in the
    /// HELICAL band's own frame — so a revolve axis pointing the other way needs no
    /// special case — and reports both spans. The generator is SAMPLED, never read off
    /// <see cref="Curve3d.Underlying"/>: a translated or wrapped line's underlying curve
    /// sits somewhere else entirely. Returns false when the carrier is not a coaxial
    /// full-turn revolve, or when the generator has no extent at all.
    /// </summary>
    private static bool TrySampleCoaxialGenerator(
        Surface carrier, in Frame3d frame,
        Span<double> radii, Span<double> axials,
        out Interval axialSpan, out Interval radialSpan)
    {
        axialSpan = default;
        radialSpan = default;
        if (carrier is not RevolvedSurface revolved ||
            !revolved.IsFullTurn ||
            !IsCoaxialWith(revolved.AxisOrigin, revolved.AxisDirection, frame))
            return false;

        var axis = frame.Z;
        var generator = revolved.Generator;
        var domain = generator.Domain;
        double axialLo = double.PositiveInfinity, axialHi = double.NegativeInfinity;
        double radiusLo = double.PositiveInfinity, radiusHi = double.NegativeInfinity;
        for (int i = 0; i <= CoaxialSamples; i++)
        {
            var p = generator.PointAt(domain.ParameterAt((double)i / CoaxialSamples));
            var d = p - frame.Origin;
            double axial = d.Dot(axis);
            axials[i] = axial;
            radii[i] = (d - axis * axial).Length;
            axialLo = Math.Min(axialLo, axials[i]);
            axialHi = Math.Max(axialHi, axials[i]);
            radiusLo = Math.Min(radiusLo, radii[i]);
            radiusHi = Math.Max(radiusHi, radii[i]);
        }
        if (!(Math.Max(axialHi - axialLo, radiusHi - radiusLo) > 0))
            return false;
        axialSpan = new Interval(axialLo, axialHi);
        radialSpan = new Interval(radiusLo, radiusHi);
        return true;
    }

    /// <summary>
    /// Recognizes a surface of revolution COAXIAL with a helical band whose (radius,
    /// axial) profile is a straight line — the cone of a thread's 45° end chamfer, or a
    /// coaxial cylinder.
    /// <para>A profile with no axial spread is a coaxial DISK — a plane perpendicular to
    /// the axis, which cannot be written radius = a + b·axial at all (its b is infinite).
    /// It is refused here and recognized by <see cref="TryCoaxialDisk"/> instead.</para>
    /// </summary>
    private static bool TryCoaxialProfileLine(
        Surface carrier, HelicalSurface helical, out CoaxialProfileLine line)
    {
        line = default;
        var frame = helical.Frame;

        if (carrier is CylinderSurface cylinder)
        {
            if (!IsCoaxialWith(cylinder.Origin, cylinder.Axis, frame))
                return false;
            // Unbounded in the axial direction, so nothing to clip against.
            line = new CoaxialProfileLine(
                cylinder.Radius, 0,
                new Interval(double.NegativeInfinity, double.PositiveInfinity));
            return true;
        }

        Span<double> radii = stackalloc double[CoaxialSamples + 1];
        Span<double> axials = stackalloc double[CoaxialSamples + 1];
        if (!TrySampleCoaxialGenerator(carrier, frame, radii, axials, out var axialSpan, out var radialSpan))
            return false;

        double extent = Math.Max(axialSpan.Length, radialSpan.Length);
        // Relative degeneracy guard (scale-free tier): a disk's axial spread is
        // zero next to its radial one, and a fit through it would be meaningless.
        if (axialSpan.Length <= extent * 1e-12)
            return false;

        double b = (radii[^1] - radii[0]) / (axials[^1] - axials[0]);
        double a = radii[0] - b * axials[0];
        double tolerance = Math.Max(Tolerance.Default.Linear, extent * 1e-12);
        for (int i = 0; i <= CoaxialSamples; i++)
        {
            if (Math.Abs(a + b * axials[i] - radii[i]) > tolerance)
                return false;
        }
        line = new CoaxialProfileLine(a, b, axialSpan);
        return true;
    }

    /// <summary>
    /// Recognizes a coaxial full-turn revolve whose generator is PERPENDICULAR to the
    /// axis: a disk or annulus — a shoulder face, a washer seat, the flat that bounds a
    /// chamfer tool. It is the b = ∞ member of <see cref="TryCoaxialProfileLine"/>'s
    /// family and has no radius = a + b·axial form, so it is spelled as what it is —
    /// the axis-perpendicular PLANE at <paramref name="axial"/>, restricted to
    /// <paramref name="radialRange"/> — and cut by <see cref="PlaneHelicalAt"/>, the one
    /// implementation of that cut.
    /// <para>Recognizing it is not a nicety: without this arm the pair fell to the
    /// marching tracer, whose polyline is chordal, has a fixed sample count and — where
    /// the annulus's rim sits ON the band (a chamfer tool's flat meeting the crest
    /// cylinder) — hugs the carrier's own v = 0 edge and ends strictly inside the band,
    /// which face splitting refuses by name.</para>
    /// </summary>
    private static bool TryCoaxialDisk(
        Surface carrier, HelicalSurface helical, out double axial, out Interval radialRange)
    {
        axial = 0;
        radialRange = default;
        Span<double> radii = stackalloc double[CoaxialSamples + 1];
        Span<double> axials = stackalloc double[CoaxialSamples + 1];
        if (!TrySampleCoaxialGenerator(
                carrier, helical.Frame, radii, axials, out var axialSpan, out var radialSpan))
            return false;

        // The exact complement of TryCoaxialProfileLine's guard, on the same scale-free
        // tier: no axial spread beside the radial one is what makes this a disk.
        if (axialSpan.Length > Math.Max(axialSpan.Length, radialSpan.Length) * 1e-12)
            return false;
        // The generator's own start, not a mean: every sample agrees to the guard above,
        // and an endpoint is a point the carrier actually passes through.
        axial = axials[0];
        radialRange = radialSpan;
        return true;
    }

    /// <summary>
    /// Coaxial straight-generator carrier ∩ helical band: an exact
    /// <see cref="SpiralArc3d"/>, no marching.
    /// <para>Write the band as r = r₀ + dr·v, z = z₀ + dz·v + rate·u and the carrier as
    /// r = a + b·z. Substituting gives v·(dr − b·dz) = (a + b·z₀ − r₀) + b·rate·u, so
    /// <b>v is LINEAR in u</b>, and therefore so are the radius and the axial coordinate:
    /// the intersection is a conical spiral in the band's own frame, whose parameter IS
    /// the band's u. That is what keeps the curve phase-aligned with the band's grid, and
    /// it is why a thread's 45° end chamfer needs no traced curve and no opt-in policy —
    /// exactly the same reason the cap cuts are <see cref="SpiralArc3d"/>s (they are the
    /// b = 0-in-z member of this family).</para>
    /// <para>Parallel profiles (dr = b·dz) never cross transversally: either the band
    /// misses the carrier entirely or it lies ON it, and a tangential contact is not
    /// reported here by contract. The u span is whatever survives clipping v to [0, 1],
    /// the axial coordinate to the carrier's own generator extent, and u to the band's
    /// domain.</para>
    /// </summary>
    private static List<Curve3d> CoaxialHelical(in CoaxialProfileLine line, HelicalSurface helical)
    {
        double r0 = helical.ProfileStart.X, z0 = helical.ProfileStart.Y;
        double dr = helical.ProfileEnd.X - r0, dz = helical.ProfileEnd.Y - z0;
        double rate = helical.AxialRate;
        double denominator = dr - line.B * dz;

        // Scale-free degeneracy guard: the profile directions are lengths in (r, z), so
        // compare the cross term against their magnitudes rather than an absolute epsilon.
        double scale = Math.Max(Math.Abs(dr), Math.Abs(line.B * dz));
        if (!(Math.Abs(denominator) > scale * 1e-12))
            return [];

        double alpha = (line.A + line.B * z0 - r0) / denominator;
        double beta = line.B * rate / denominator;

        // v(u) = alpha + beta*u must run inside [0, 1].
        double uLo = helical.DomainU.Start, uHi = helical.DomainU.End;
        if (beta == 0)
        {
            // A coaxial CYLINDER: v is constant, so the whole band either misses the
            // carrier or meets it along one complete iso-v helix.
            if (alpha < 0 || alpha > 1)
                return [];
        }
        else
        {
            double uAt0 = -alpha / beta, uAt1 = (1 - alpha) / beta;
            uLo = Math.Max(uLo, Math.Min(uAt0, uAt1));
            uHi = Math.Min(uHi, Math.Max(uAt0, uAt1));
        }

        // A band with dr == 0 is a strip of a coaxial CYLINDER — a thread's crest or root
        // flat — and a coaxial cone meets one in a CIRCLE: the radius stays r₀ and the
        // axial coordinate is the single z where a + b·z = r₀. Deliberate exact-zero test
        // on the band's OWN generator: this is the semantic case, not a near-axial one.
        //
        // The general expressions reach that circle only up to rounding —
        // dz·(b·rate/(−b·dz)) + rate is mathematically −rate + rate, and lands ~1e-17 off
        // for a pitch whose ratios are not binary-exact — while SpiralArc3d.IsPlanar is an
        // exact-zero test that every downstream gate reads. So whether a crest band's
        // chamfer cut was recognized as the cap-SHAPED cut it is came down to which way
        // the last bit fell: the same 0.3 mm chamfer tessellated at one end of a rod and
        // welded non-manifold at the other, with nothing geometric between the two.
        double radiusAtZero = r0 + dr * alpha, radiusSlope = dr * beta;
        double axialAtZero = dr == 0 ? (r0 - line.A) / line.B : z0 + dz * alpha;
        double axialSlope = dr == 0 ? 0 : dz * beta + rate;

        // Clip to the carrier's own axial extent (a cone's generator is a finite segment;
        // a cylinder's range is infinite and clips nothing).
        if (double.IsFinite(line.AxialRange.Start) && double.IsFinite(line.AxialRange.End))
        {
            if (axialSlope == 0)
            {
                if (axialAtZero < line.AxialRange.Start || axialAtZero > line.AxialRange.End)
                    return [];
            }
            else
            {
                double uA = (line.AxialRange.Start - axialAtZero) / axialSlope;
                double uB = (line.AxialRange.End - axialAtZero) / axialSlope;
                uLo = Math.Max(uLo, Math.Min(uA, uB));
                uHi = Math.Min(uHi, Math.Max(uA, uB));
            }
        }

        if (!(uHi - uLo > 1e-12))
            return [];
        // The radius stays between the profile's own positive radii by construction, but
        // clipping arithmetic can overshoot by an ulp; refuse rather than throw.
        if (!(radiusAtZero + radiusSlope * uLo > 0) || !(radiusAtZero + radiusSlope * uHi > 0))
            return [];

        return
        [
            new SpiralArc3d(
                helical.Frame, radiusAtZero, radiusSlope, axialAtZero, axialSlope,
                new Interval(uLo, uHi)),
        ];
    }

    /// <summary>
    /// Plane ⊥ revolution axis, full turn: exact circles, one per generator crossing of
    /// the plane's axial height. Circle frames are phase-aligned with the surface's
    /// u = 0 (the generator position) so band grids and edges built from these curves
    /// tessellate to identical points.
    /// </summary>
    private static List<Curve3d> PlaneRevolved(PlaneSurface plane, RevolvedSurface revolved)
    {
        var axis = revolved.AxisDirection;
        var generator = revolved.Generator;
        var domain = generator.Domain;
        double planeHeight = (plane.Origin - revolved.AxisOrigin).Dot(axis);
        double Axial(double t) => (generator.PointAt(t) - revolved.AxisOrigin).Dot(axis);

        var curves = new List<Curve3d>();
        const int samples = 128;
        double previousT = domain.Start;
        double previousF = Axial(previousT) - planeHeight;
        for (int i = 1; i <= samples; i++)
        {
            double t = domain.ParameterAt((double)i / samples);
            double f = Axial(t) - planeHeight;
            // Exact-zero fast path: a sample landing bitwise on the plane is a root the
            // sign-change product would miss (0 * f is not < 0) — deliberate ==.
            if (previousF == 0 || previousF * f < 0)
            {
                double lo = previousT, hi = t, fLo = previousF;
                for (int step = 0; step < 60; step++)
                {
                    double mid = (lo + hi) / 2;
                    double fMid = Axial(mid) - planeHeight;
                    if (fLo * fMid <= 0)
                        hi = mid;
                    else
                    {
                        lo = mid;
                        fLo = fMid;
                    }
                }
                var point = generator.PointAt((lo + hi) / 2);
                var offset = point - revolved.AxisOrigin;
                var radial = offset - axis * offset.Dot(axis);
                if (radial.TryNormalize(Tolerance.Default, out var x)) // poles yield no curve
                {
                    var center = revolved.AxisOrigin + axis * planeHeight;
                    curves.Add(new Circle3d(center, x, axis.Cross(x), radial.Length));
                }
            }
            previousT = t;
            previousF = f;
        }
        return curves;
    }

    private static Surface Promote(Surface s)
    {
        if (s is ExtrudedSurface e &&
            e.Generator.Underlying is Circle3d c &&
            e.Direction.IsParallelTo(c.Axis, Tolerance.Default))
        {
            var candidate = new CylinderSurface(c.Center, c.XDirection, c.YDirection, c.Radius);
            if (WrapsWholeCylinder(e.Generator, candidate))
                return candidate;
        }
        return s;
    }

    /// <summary>
    /// Whether the ACTUAL generator wraps the candidate cylinder exactly once — the only
    /// condition under which the extrusion IS that cylinder rather than a bounded patch on
    /// one. Both halves are sampled from the real curve, never read off
    /// <see cref="Curve3d.Underlying"/>: a wrapper (a <c>TransformedCurve</c>, or the
    /// <c>CurveSegment</c> a sketch's rounded corner arrives as) reports the untransformed,
    /// untrimmed circle as its underlying geometry and says nothing about where the
    /// generator actually goes.
    ///
    /// <para><b>The angular half is load-bearing, not belt-and-braces.</b> A rounded
    /// rectangle's corner is a QUARTER arc extruded, and every point of a quarter arc lies
    /// on the full cylinder — so a start-point-only guard promotes it, and the promoted
    /// carrier then reports intersections around 270° of surface the face does not carry.
    /// Measured on a Ø8 counterbore near a Ø12 rounded corner: the tool's band crossed the
    /// *fabricated* far side of the corner cylinder, the tracer produced two open curves
    /// whose endpoints sit strictly inside the tool's band, and
    /// <c>FaceSplitter.SplitByCurve</c> refused them with "Open splitting curves must start
    /// and end outside the face" — for a boolean that geometrically is a plain hole 10 mm
    /// from the corner.</para>
    /// </summary>
    private static bool WrapsWholeCylinder(Curve3d generator, CylinderSurface candidate)
    {
        var axis = candidate.Axis;
        var domain = generator.Domain;
        const int samples = 32;
        double swept = 0, previous = 0;
        for (int i = 0; i <= samples; i++)
        {
            var offset = generator.PointAt(domain.ParameterAt((double)i / samples)) - candidate.Origin;
            var radial = offset - axis * offset.Dot(axis);
            // Weld tier: the generator either IS the cylinder's circle (constructed
            // exactly) or is some other curve that merely shares its underlying type.
            if (Math.Abs(radial.Length - candidate.Radius) > Tolerance.Default.Linear)
                return false;
            double angle = Math.Atan2(radial.Dot(candidate.YDirection), radial.Dot(candidate.XDirection));
            if (i > 0)
            {
                // Shortest step between consecutive samples: 32 of them put a full turn's
                // step at 0.196 rad, so the branch can only ever fire at the seam.
                double delta = angle - previous;
                if (delta > Math.PI)
                    delta -= 2 * Math.PI;
                else if (delta < -Math.PI)
                    delta += 2 * Math.PI;
                swept += delta;
            }
            previous = angle;
        }
        // Radians are dimensionless, so this guard is deliberately absolute (the epsilon
        // ladder's stated exception for angular quantities).
        return Math.Abs(Math.Abs(swept) - 2 * Math.PI) <= Tolerance.Default.Angular;
    }

    // ---- bounded planar carriers ----

    /// <summary>
    /// A planar carrier surface. Unbounded carriers (<see cref="PlaneSurface"/>) are only
    /// clipped to the query region; bounded ones — an <see cref="ExtrudedSurface"/> whose
    /// generator is straight, i.e. the parallelogram
    /// {Corner + E1·s + E2·t : s, t ∈ [0, 1]} — additionally clip the intersection line to
    /// their own extent.
    /// </summary>
    private readonly record struct PlanarPatch(
        PlaneSurface Plane, bool Bounded, Vector3d Corner, Vector3d E1, Vector3d E2);

    /// <summary>
    /// Recognizes planar carriers: planes verbatim, and extrusions of a straight generator
    /// as a bounded parallelogram. Straightness is decided by SAMPLING the actual generator
    /// (<see cref="Curve3d.Underlying"/> is only a type hint — a transformed line's
    /// underlying geometry sits somewhere else entirely), at the 1e-9 weld tier: a
    /// generator that deviates further is genuinely curved and belongs to another path.
    /// </summary>
    private static bool TryPlanarPatch(Surface surface, out PlanarPatch patch)
    {
        switch (surface)
        {
            case PlaneSurface plane:
                patch = new PlanarPatch(plane, false, default, default, default);
                return true;

            case ExtrudedSurface extruded when extruded.Generator.Underlying is Line3d:
            {
                var generator = extruded.Generator;
                var domain = generator.Domain;
                var corner = generator.PointAt(domain.Start);
                var e1 = generator.PointAt(domain.End) - corner;
                var e2 = extruded.Direction;
                if (!e1.TryNormalize(Tolerance.Default, out var x) || !IsStraight(generator, corner, x))
                    break;
                var inPlane = e2 - x * e2.Dot(x);
                if (!inPlane.TryNormalize(Tolerance.Default, out var y))
                    break; // degenerate: the extrusion collapses onto its generator
                // x × y is parallel to generator-tangent × direction, so the promoted
                // plane's normal agrees in sign with the extruded surface's own.
                patch = new PlanarPatch(new PlaneSurface(corner, x, y), true, corner, e1, e2);
                return true;
            }
        }
        patch = default;
        return false;
    }

    /// <summary>Every sample of the ACTUAL curve lies on the ray (start, unit direction).</summary>
    private static bool IsStraight(Curve3d curve, in Vector3d start, in Vector3d direction)
    {
        var domain = curve.Domain;
        const int samples = 8;
        for (int i = 1; i < samples; i++)
        {
            var offset = curve.PointAt(domain.ParameterAt((double)i / samples)) - start;
            if ((offset - direction * offset.Dot(direction)).Length > Tolerance.Default.Linear)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Two planar carriers: the exact analytic line, clipped to the query region AND to
    /// each bounded carrier's parallelogram. Clipping to the bounded extent is what makes
    /// the result usable as a split curve — an extrusion's carrier plane extends far past
    /// the wall it actually represents, and an unclipped line would cut faces the wall
    /// never touches.
    /// </summary>
    private static List<Curve3d> PlanarPatches(in PlanarPatch a, in PlanarPatch b, in Aabb region)
    {
        var na = a.Plane.Normal.Normalized();
        var nb = b.Plane.Normal.Normalized();
        var direction = na.Cross(nb);
        if (!direction.TryNormalize(Tolerance.Default, out var dir))
            return []; // parallel (coincident planes intersect everywhere; not a curve)

        // A point on both planes: solve n_a·p = d_a, n_b·p = d_b in the span of {n_a, n_b}.
        double da = na.Dot(a.Plane.Origin);
        double db = nb.Dot(b.Plane.Origin);
        double dot = na.Dot(nb);
        double denominator = 1 - dot * dot;
        double ka = (da - db * dot) / denominator;
        double kb = (db - da * dot) / denominator;
        var point = na * ka + nb * kb;

        if (!TryClipToRegion(point, dir, region, out double tMin, out double tMax))
            return [];
        if (!ClipToPatch(a, point, dir, ref tMin, ref tMax) ||
            !ClipToPatch(b, point, dir, ref tMin, ref tMax))
            return [];
        if (tMax - tMin <= Tolerance.Default.Linear)
            return [];
        return [new Line3d(point + dir * tMin, point + dir * tMax)];
    }

    /// <summary>
    /// Narrows the line's parameter interval to the patch's parallelogram. The line lies
    /// in the patch's plane, so its (s, t) coordinates in the {E1, E2} basis are affine in
    /// the line parameter — two slab clips. Unbounded patches never narrow anything.
    /// </summary>
    private static bool ClipToPatch(
        in PlanarPatch patch, in Vector3d point, in Vector3d dir, ref double tMin, ref double tMax)
    {
        if (!patch.Bounded)
            return true;

        var normal = patch.E1.Cross(patch.E2);
        double scale = normal.LengthSquared;
        // Degenerate parallelograms are rejected at construction; the guard is a
        // division-by-zero backstop, not a model tolerance.
        if (scale <= 0)
            return true;
        var offset = point - patch.Corner;
        double s0 = offset.Cross(patch.E2).Dot(normal) / scale;
        double sd = dir.Cross(patch.E2).Dot(normal) / scale;
        double t0 = patch.E1.Cross(offset).Dot(normal) / scale;
        double td = patch.E1.Cross(dir).Dot(normal) / scale;
        return ClipSlab(s0, sd, ref tMin, ref tMax) && ClipSlab(t0, td, ref tMin, ref tMax);
    }

    /// <summary>Clips [tMin, tMax] to where value0 + slope·t stays in [0, 1].</summary>
    private static bool ClipSlab(double value0, double slope, ref double tMin, ref double tMax)
    {
        // Exact-zero test: a bit-zero slope means the coordinate never changes along the
        // line, so the whole interval either survives or dies — no division is defined.
        if (slope == 0)
            return value0 >= 0 && value0 <= 1;
        double lo = (0 - value0) / slope;
        double hi = (1 - value0) / slope;
        if (lo > hi)
            (lo, hi) = (hi, lo);
        tMin = Math.Max(tMin, lo);
        tMax = Math.Min(tMax, hi);
        return tMin < tMax;
    }

    /// <summary>
    /// A BOUNDED planar carrier meeting a quadric: the SAME exact analytic curves the main
    /// dispatch would produce for a real <see cref="PlaneSurface"/>, accepted only when
    /// they lie WHOLLY inside the patch's parallelogram. That is the drilled-side-wall case
    /// — a bore's rim circle sits well inside the wall it pierces — and it is what makes a
    /// side bore converge like a cap bore instead of flooring at the tracer's fixed sample
    /// count (measured: a blind Ø0.6 bore in a 4×3×2 plate's side went from a −7.4e-4 …
    /// +6.5e-5 wandering error at 32…256 segments to quadratic convergence).
    /// </summary>
    /// <remarks>
    /// <para>The carrier cases mirror <see cref="Intersect"/>'s switch deliberately rather
    /// than sharing code with it: the switch is the boolean pipeline's whole regression
    /// surface, and an unbounded <see cref="PlaneSurface"/> must keep taking it verbatim.</para>
    /// <para>Containment is decided EXACTLY, not by sampling: the patch coordinates (s, t)
    /// are affine in the point, so each is a HARMONIC of the conic's own angle,
    /// c + a·cos θ + b·sin θ, whose crossings of the patch's four edges are a closed-form
    /// <c>acos</c>. A conic wholly inside the patch is returned as ITSELF (bit-for-bit the
    /// incumbent result); one that pokes out is CLIPPED to the runs the patch carries — see
    /// <see cref="ClipConicToPatch"/>. The axis-parallel line pair still returns false and
    /// falls through to the marching tracer.</para>
    /// </remarks>
    private static bool TryPatchQuadric(
        in PlanarPatch patch, Surface other, in Aabb region, out List<Curve3d> curves)
    {
        var plane = patch.Plane;
        switch (other)
        {
            case CylinderSurface cylinder:
                curves = PlaneCylinder(plane, cylinder, region);
                break;
            case SphereSurface sphere:
                curves = PlaneSphere(plane, sphere);
                break;
            // A bore's wall arrives as a full-turn revolve of a straight axis-parallel
            // generator; PlaneRevolved keeps the generator's BOUNDED axial extent (no
            // circle is invented above the bore's end) and phase-aligns to u = 0.
            case RevolvedSurface revolved when IsPerpendicularFullTurn(plane, revolved):
                curves = PlaneRevolved(plane, revolved);
                break;
            case RevolvedSurface revolved when TrySphereCarrier(revolved, out var carrier):
                curves = PlaneSphere(plane, carrier);
                break;
            default:
                curves = [];
                return false;
        }
        if (curves.Count == 0)
            return true; // the infinite plane misses the quadric, so the wall does too

        var normal = patch.E1.Cross(patch.E2);
        double scale = normal.LengthSquared;
        // Degenerate parallelograms are rejected at construction; a division-by-zero
        // backstop, not a model tolerance.
        if (scale <= 0)
            return false;
        var sRow = patch.E2.Cross(normal) / scale;
        var tRow = normal.Cross(patch.E1) / scale;

        var clipped = new List<Curve3d>(curves.Count);
        foreach (var curve in curves)
        {
            Vector3d centre, axisX, axisY;
            switch (curve)
            {
                case Circle3d c:
                    (centre, axisX, axisY) = (c.Center, c.XDirection * c.Radius, c.YDirection * c.Radius);
                    break;
                case Ellipse3d e:
                    (centre, axisX, axisY) = (e.Center, e.SemiAxisX, e.SemiAxisY);
                    break;
                default:
                    return false; // the axis-parallel line pair: defer to the tracer
            }
            var offset = centre - patch.Corner;
            ClipConicToPatch(
                curve,
                new Harmonic(offset.Dot(sRow), axisX.Dot(sRow), axisY.Dot(sRow)),
                new Harmonic(offset.Dot(tRow), axisX.Dot(tRow), axisY.Dot(tRow)),
                clipped);
        }
        curves = clipped;
        return true;
    }

    /// <summary>
    /// One patch coordinate along a conic: s(θ) = <paramref name="Constant"/> +
    /// <paramref name="Cos"/>·cos θ + <paramref name="Sin"/>·sin θ. Both patch coordinates
    /// are affine in the point and a conic is affine in (cos θ, sin θ), so this is exact
    /// rather than a fit.
    /// </summary>
    private readonly record struct Harmonic(double Constant, double Cos, double Sin)
    {
        public double At(double theta) => Constant + Cos * Math.Cos(theta) + Sin * Math.Sin(theta);

        /// <summary>Amplitude R with the phase φ such that the value is Constant + R·cos(θ − φ).</summary>
        public double Amplitude => Math.Sqrt(Cos * Cos + Sin * Sin);

        /// <summary>The two θ at which the value equals <paramref name="level"/>, if any.</summary>
        public void CrossingsOf(double level, List<double> into)
        {
            double amplitude = Amplitude;
            // Exact-zero guard: a coordinate that does not vary along the conic never
            // crosses, and there is no phase to take.
            if (amplitude == 0)
                return;
            double ratio = (level - Constant) / amplitude;
            if (ratio < -1 || ratio > 1)
                return;
            double phase = Math.Atan2(Sin, Cos);
            double half = Math.Acos(ratio);
            into.Add(Wrap2Pi(phase + half));
            into.Add(Wrap2Pi(phase - half));
        }
    }

    /// <summary>
    /// The angular resolution of the crossing solve, DERIVED from <see cref="Math.Acos"/>'s
    /// own conditioning rather than chosen: near a tangency the argument is within round-off
    /// of ±1, where acos has a square-root singularity, so a relative input error ε comes
    /// out as √(2ε) — about 2.1e-8 rad at double precision, and 4.2e-8 for the pair of roots
    /// either side. Two crossings closer than that are not two crossings: they are one
    /// tangency the arithmetic could not resolve, and merging them is what makes a conic
    /// TOUCHING a patch edge come back as the closed conic it is instead of an arc with a
    /// pinhole in it. Radians are dimensionless, which is why this guard is absolute where
    /// the epsilon ladder's default is relative.
    /// </summary>
    private const double AcosResolution = 1e-7;

    private static double Wrap2Pi(double theta)
    {
        double wrapped = theta % (2 * Math.PI);
        return wrapped < 0 ? wrapped + 2 * Math.PI : wrapped;
    }

    /// <summary>
    /// The runs of a closed conic that the bounded patch actually carries, appended to
    /// <paramref name="output"/> as <see cref="CurveSegment"/>s over the conic itself.
    ///
    /// <para><b>A conic wholly inside is appended VERBATIM</b>, by reference — so every
    /// input the containment test used to accept produces bit-for-bit what it always did,
    /// and a closed curve stays closed (the wrap-splitting and hole-splitting paths key on
    /// <see cref="Curve3d.IsClosed"/>).</para>
    ///
    /// <para><b>The cut angles are closed form, which is what makes the endpoints weld.</b>
    /// A clipped end becomes a VERTEX shared with the neighbouring face — a bore breaking
    /// out of the wall's top edge ends exactly where the top face's own intersection curve
    /// starts — so it must not carry a sampling error. Each patch edge is one equation
    /// <c>c + a·cos θ + b·sin θ = level</c>, i.e. <c>R·cos(θ − φ) = level − c</c>, whose two
    /// roots are <c>φ ± acos(…)</c>: the same accuracy as the conic's own construction, where
    /// the marching tracer this path used to defer to stops up to one march step short of
    /// the boundary and never reaches it at all.</para>
    ///
    /// <para>Membership is decided at each interval's MIDPOINT rather than by an inequality
    /// on the crossing list, because the four edge constraints are independent: two
    /// crossings of the s = 0 edge may bracket a stretch that leaves through t = 1. A
    /// midpoint of an interval between consecutive crossings is strictly inside or strictly
    /// outside every constraint, so the exact test is the right one and no epsilon
    /// enters.</para>
    /// </summary>
    private static void ClipConicToPatch(Curve3d conic, Harmonic s, Harmonic t, List<Curve3d> output)
    {
        // Both callers are Circle3d/Ellipse3d, whose parameter IS the angle over [0, 2π],
        // so the crossing angles below are curve parameters with nothing to map.
        double period = conic.Domain.Length;
        var angles = new List<double>();
        s.CrossingsOf(0, angles);
        s.CrossingsOf(1, angles);
        t.CrossingsOf(0, angles);
        t.CrossingsOf(1, angles);
        angles.Sort();
        for (int i = angles.Count - 1; i > 0; i--)
        {
            if (angles[i] - angles[i - 1] <= AcosResolution)
                angles.RemoveAt(i);
        }

        bool Inside(double theta)
        {
            double su = s.At(theta), tu = t.At(theta);
            return su >= 0 && su <= 1 && tu >= 0 && tu <= 1;
        }

        if (angles.Count == 0)
        {
            // No patch edge is ever reached: the conic is wholly inside or wholly outside.
            if (Inside(0))
                output.Add(conic);
            return;
        }

        // Cyclic intervals between consecutive crossings; each is entirely in or entirely
        // out, so one midpoint decides it.
        int n = angles.Count;
        var keep = new bool[n];
        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            double a = angles[i];
            double b = i + 1 < n ? angles[i + 1] : angles[0] + period;
            if (keep[i] = Inside(0.5 * (a + b)))
                kept++;
        }
        if (kept == 0)
            return;
        if (kept == n)
        {
            // Every interval survives: a conic merely TANGENT to a patch edge is still the
            // whole conic, and must come back as the closed curve it is.
            output.Add(conic);
            return;
        }
        // Start where a kept run begins, so a run straddling the seam is emitted as ONE
        // segment running past the domain end — legal on a closed base, and required, or
        // the seam would leave two edges where the geometry has one.
        int start = 0;
        while (!(keep[start] && !keep[(start + n - 1) % n]))
            start++;
        for (int offset = 0; offset < n;)
        {
            if (!keep[(start + offset) % n])
            {
                offset++;
                continue;
            }
            int length = 0;
            while (offset + length < n && keep[(start + offset + length) % n])
                length++;
            double from = angles[(start + offset) % n];
            double to = angles[(start + offset + length) % n];
            if (to <= from)
                to += period;
            output.Add(new CurveSegment(conic, from, to));
            offset += length;
        }
    }

    /// <summary>
    /// Plane ∩ extrusion when the generator lies in a plane PARALLEL to the cutting plane:
    /// every generator point then meets the plane after the same travel along the extrude
    /// direction, so the section is exactly the generator translated by direction·v. Exact
    /// for any generator shape (lines, rational arcs, splines), bounded exactly to the
    /// generator's extent, and — crucially — built from the generator's own points, so
    /// adjacent profile segments hand over corner points bit-for-bit and a sketch pocket's
    /// outline closes into a chain. Returns false when the configuration is something
    /// else (tilted plane, plane parallel to the direction); true with no curves when the
    /// plane misses the extrusion's v-range.
    /// </summary>
    private static bool TryPlaneExtrudedSection(
        PlaneSurface plane, ExtrudedSurface extruded, out List<Curve3d> curves)
    {
        curves = [];
        var n = plane.Normal.Normalized();
        var d = extruded.Direction;
        double advance = n.Dot(d);
        // Well-conditioned crossing: below this the plane is parallel to the extrude
        // direction (the surface is then coplanar with it or misses entirely).
        if (Math.Abs(advance) <= Tolerance.Default.Linear * d.Length)
            return false;

        // The generator must lie in a plane parallel to the cutting plane, sampled on the
        // ACTUAL curve. This is the exact condition for the section to be a pure
        // translate: the residual at generator parameter u is n·(G(u) − G(u0)).
        var generator = extruded.Generator;
        var domain = generator.Domain;
        double height = n.Dot(generator.PointAt(domain.Start) - plane.Origin);
        const int samples = 16;
        for (int i = 1; i <= samples; i++)
        {
            double h = n.Dot(generator.PointAt(domain.ParameterAt((double)i / samples)) - plane.Origin);
            if (Math.Abs(h - height) > Tolerance.Default.Linear)
                return false;
        }

        double v = -height / advance;
        // Strictly interior, measured in LENGTH units: v·advance is the plane's signed
        // distance above the start rim, (1 − v)·advance its distance below the end rim.
        // A plane flush with either rim is the coplanar/tangent case booleans do not
        // support, and splitting there would only fabricate zero-extent slivers.
        if (v < 0 || v > 1 ||
            Math.Abs(v * advance) <= Tolerance.Default.Linear ||
            Math.Abs((1 - v) * advance) <= Tolerance.Default.Linear)
            return true; // recognized configuration, but no transversal section

        curves.Add(generator.Transformed(Matrix4d.CreateTranslation(d * v)));
        return true;
    }

    // ---- analytic cases ----

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
            // Axis perpendicular to the plane: a circle. Use the cylinder's own frame
            // (not an arbitrary perpendicular) so the circle's parameterization is
            // phase-aligned with the cylinder's u — band grids and the edges created
            // from this curve then sample identical points and weld without cracks.
            return [new Circle3d(center, cylinder.XDirection, cylinder.YDirection, cylinder.Radius)];
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
        if (!TryClipToRegion(point, direction, region, out double tMin, out double tMax) ||
            tMax - tMin <= Tolerance.Default.Linear)
            return null;
        return new Line3d(point + direction * tMin, point + direction * tMax);
    }

    /// <summary>The infinite line's parameter interval inside the region box.</summary>
    private static bool TryClipToRegion(
        in Vector3d point, in Vector3d direction, in Aabb region, out double tMin, out double tMax)
    {
        // Two opposing rays give the full line's parameter interval inside the box.
        var forward = new Ray3d(point, direction);
        var backward = new Ray3d(point, -direction);
        bool hitF = forward.Intersects(region, out double f0, out double f1);
        bool hitB = backward.Intersects(region, out double b0, out double b1);
        if (hitF && hitB)
        {
            tMin = -b1;
            tMax = f1;
            return true;
        }
        if (hitF)
        {
            tMin = f0;
            tMax = f1;
            return true;
        }
        if (hitB)
        {
            tMin = -b1;
            tMax = -b0;
            return true;
        }
        tMin = tMax = 0;
        return false;
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

    /// <summary>
    /// Every numeric constant of the marching tracer, in one place because they are a
    /// SET, not independent knobs — the boolean pipeline's geometry depends on all of
    /// them agreeing. The chain of reasoning that ties them together:
    ///
    /// <list type="bullet">
    /// <item>The <b>march step</b> (region extent / <see cref="StepDivisor"/>) is the
    /// unit everything spatial is measured in: seed deduplication, the branch-jump
    /// rejection, the closed-loop test and the corrector's divergence bail are all
    /// multiples of it, so changing the divisor rescales them all coherently.</item>
    /// <item><see cref="SeedResolution"/> and <see cref="SeedSpacingFactor"/> must find
    /// at least one seed on every branch: the sample spacing implied by the resolution
    /// times the factor is the pairing radius, so loosening one means tightening the
    /// other.</item>
    /// <item>The Newton tolerances form a ladder that must stay ORDERED:
    /// <see cref="NewtonResidual"/> (1e-10, the iteration's own convergence test) is
    /// tighter than <see cref="SeedAcceptance"/> (1e-9, "this seed is on the curve"),
    /// which is tighter than <see cref="CorrectorAcceptance"/> (1e-8, "this traced point
    /// is on the curve"). Loosening the residual past an acceptance makes the
    /// corresponding check unreachable; tightening an acceptance past the residual makes
    /// converged iterations get rejected. Traced points are consumed as
    /// <see cref="PolylineCurve3d"/> vertices and pulled back through
    /// <see cref="FaceGeometry.InverseEvaluationTolerance"/> (1e-6), which is the ceiling
    /// the corrector's acceptance has to clear.</item>
    /// <item><see cref="PartialsStep"/> is a central-difference step, so the Jacobian it
    /// builds is accurate to ~h² = 1e-14 — which is why the Newton residual cannot
    /// usefully be tightened below 1e-10 and why <see cref="PivotFloor"/> sits at
    /// 1e-14.</item>
    /// <item><see cref="TangentDegeneracy"/> guards |n_a x n_b|: below it the surfaces
    /// are tangent and the marching direction is undefined, so the trace stops rather
    /// than wandering.</item>
    /// </list>
    ///
    /// <para><b>Boolean-critical.</b> Tracer output feeds face splitting and
    /// <c>BrepBoolean</c>; these values are the tuning that makes cross-drilled bores and
    /// pierced spheres come out manifold. Change them as a set, with the whole suite plus
    /// the DocsGen snippets as the regression net — never one literal at a time.</para>
    /// </summary>
    private readonly record struct TracerSettings
    {
        /// <summary>Samples per parameter direction when grid-seeding each surface.</summary>
        public int SeedResolution { get; init; }

        /// <summary>
        /// Model-space aspect ratio (long parameter direction / short one) at which a
        /// surface earns a SECOND, anisotropic seed pass. Below it the isotropic grid is
        /// the whole story; above it the grid's samples are so much finer across the
        /// surface than along it that whole branches fall between columns.
        /// </summary>
        public double SeedAnisotropy { get; init; }

        /// <summary>Seed pairing radius as a multiple of the sample-cloud spacing.</summary>
        public double SeedSpacingFactor { get; init; }

        /// <summary>March step = region's longest extent / this.</summary>
        public double StepDivisor { get; init; }

        /// <summary>Squared multiple of the step within which a seed counts as already traced.</summary>
        public double SeedDedupeStepsSquared { get; init; }

        /// <summary>Hard cap on points per traced branch.</summary>
        public int MaxSteps { get; init; }

        /// <summary>Multiple of the step beyond which the corrector is deemed to have jumped branches.</summary>
        public double BranchJumpSteps { get; init; }

        /// <summary>Steps that must elapse before a return to the start counts as a closed loop.</summary>
        public int MinStepsBeforeClosure { get; init; }

        /// <summary>Damped Gauss-Newton iterations when refining a seed.</summary>
        public int SeedIterations { get; init; }

        /// <summary>Newton iterations per corrector step.</summary>
        public int CorrectorIterations { get; init; }

        /// <summary>Residual at which a Newton/Gauss-Newton iteration is converged.</summary>
        public double NewtonResidual { get; init; }

        /// <summary>Residual below which a non-converged seed is still accepted.</summary>
        public double SeedAcceptance { get; init; }

        /// <summary>Residual below which a non-converged corrector step is still accepted.</summary>
        public double CorrectorAcceptance { get; init; }

        /// <summary>Levenberg damping added to the normal equations' diagonal.</summary>
        public double LevenbergDamping { get; init; }

        /// <summary>Squared multiple of the step past which a corrector update is diverging.</summary>
        public double DivergenceStepsSquared { get; init; }

        /// <summary>Cross-product magnitude below which the two normals are parallel (tangential contact).</summary>
        public double TangentDegeneracy { get; init; }

        /// <summary>Parameter slack allowed outside a bounded domain before the trace stops.</summary>
        public double DomainSlack { get; init; }

        /// <summary>Pivot magnitude below which the 4x4 solve reports singularity.</summary>
        public double PivotFloor { get; init; }

        /// <summary>Central-difference step for surface partials: this, or this fraction of the domain length.</summary>
        public double PartialsStep { get; init; }

        public static TracerSettings Default => new()
        {
            SeedResolution = 24,
            SeedAnisotropy = 4.0,
            SeedSpacingFactor = 1.5,
            StepDivisor = 150.0,
            SeedDedupeStepsSquared = 4.0,
            MaxSteps = 4000,
            BranchJumpSteps = 3.0,
            MinStepsBeforeClosure = 5,
            SeedIterations = 12,
            CorrectorIterations = 10,
            NewtonResidual = 1e-10,
            SeedAcceptance = 1e-9,
            CorrectorAcceptance = 1e-8,
            LevenbergDamping = 1e-10,
            DivergenceStepsSquared = 100.0,
            TangentDegeneracy = 1e-7,
            DomainSlack = 1e-9,
            PivotFloor = 1e-14,
            PartialsStep = 1e-7,
        };
    }

    private static bool Outside(double t, in Interval interval, bool periodic, double slack) =>
        !periodic && (t < interval.Start - slack || t > interval.End + slack);

    private sealed record MarchState(
        Surface A, ParamDomain Da, Surface B, ParamDomain Db, double Step, TracerSettings Settings);

    private static List<Curve3d> March(Surface a, Surface b, in Aabb region)
    {
        var settings = TracerSettings.Default;
        var da = GetParamDomain(a, region);
        var db = GetParamDomain(b, region);
        double step = region.Size[region.LongestAxis] / settings.StepDivisor;
        var state = new MarchState(a, da, b, db, step, settings);

        var seeds = FindSeeds(state, settings.SeedResolution);
        var curves = new List<Curve3d>();
        var traced = new List<Vector3d>();

        foreach (var seed in seeds)
        {
            var p = Eval(a, da, seed[0], seed[1]);
            if (traced.Any(q => q.DistanceSquaredTo(p) < settings.SeedDedupeStepsSquared * step * step))
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

            // The pair actually traced on (post-promotion) rides on the curve, so the
            // tessellator can later refine chords back onto the exact intersection —
            // consistent by construction, since these are the surfaces the baked
            // vertices themselves were corrected against.
            curves.Add(new PolylineCurve3d(points, closed, (a, b)));
            traced.AddRange(points);
        }
        return curves;
    }

    /// <summary>
    /// Grid-samples both surfaces, pairs nearby samples via a BVH, and Newton-refines each
    /// pair onto the intersection.
    ///
    /// <para><b>Two passes, and the order is the whole safety argument.</b> The first is the
    /// historical isotropic <c>resolution</c>×<c>resolution</c> grid, emitted in exactly the
    /// order it always was. The second runs only for surfaces whose two parameter directions
    /// differ in MODEL length by more than <see cref="TracerSettings.SeedAnisotropy"/>, and
    /// re-grids them with the same sample budget redistributed to match the shape — so the
    /// spacing is roughly equal in millimetres rather than in parameter units. Because
    /// <see cref="March"/> traces seeds in order and skips any seed already covered by a
    /// traced branch, every branch the old grid found is still traced FIRST and from the
    /// SAME seed: its polyline is bit-identical, and the second pass can only add branches
    /// that used to be missed entirely.</para>
    ///
    /// <para>The case it exists for is a thread band. An M8 crest flat wound over thirteen
    /// turns is ~330 mm long and 0.16 mm tall — an aspect ratio near 2000 — so the isotropic
    /// grid puts its columns 13 mm apart along the band while sampling 24 rows across a strip
    /// a sixth of a millimetre wide. Measured against a Ø6 cross-drill, the isotropic grid
    /// finds ZERO of the branches the drill cuts; the anisotropic pass finds them.</para>
    /// </summary>
    private static List<double[]> FindSeeds(MarchState state, int resolution)
    {
        var seeds = new List<double[]>();
        CollectSeeds(state, resolution, resolution, resolution, resolution, isotropic: true, seeds);

        var (nuA, nvA) = SeedCounts(state.A, state.Da, resolution, state.Settings.SeedAnisotropy);
        var (nuB, nvB) = SeedCounts(state.B, state.Db, resolution, state.Settings.SeedAnisotropy);
        if (nuA != resolution || nvA != resolution || nuB != resolution || nvB != resolution)
            CollectSeeds(state, nuA, nvA, nuB, nvB, isotropic: false, seeds);
        return seeds;
    }

    /// <summary>
    /// One seeding pass at the given per-surface grid counts, appending whatever it refines.
    /// <paramref name="isotropic"/> keeps the original pairing radius — the sample cloud's
    /// diagonal over the resolution — bit-for-bit; the anisotropic pass cannot use that
    /// (a cloud diagonal says nothing about spacing on a 2000:1 strip) and measures the
    /// largest gap between adjacent B samples in each direction instead.
    /// </summary>
    private static void CollectSeeds(
        MarchState state, int nuA, int nvA, int nuB, int nvB, bool isotropic, List<double[]> seeds)
    {
        var samplesB = new List<(double U, double V, Vector3d P)>((nuB + 1) * (nvB + 1));
        var boxes = new List<Aabb>((nuB + 1) * (nvB + 1));
        for (int i = 0; i <= nuB; i++)
        {
            for (int j = 0; j <= nvB; j++)
            {
                double u = state.Db.U.ParameterAt((double)i / nuB);
                double v = state.Db.V.ParameterAt((double)j / nvB);
                var p = state.B.PointAt(u, v);
                samplesB.Add((u, v, p));
                boxes.Add(new Aabb(p, p));
            }
        }
        var bvh = Bvh.Build(boxes.ToArray().AsSpan());

        double spacing;
        if (isotropic)
        {
            var cloud = Aabb.Empty;
            foreach (var s in samplesB)
                cloud = cloud.Union(s.P);
            spacing = cloud.IsEmpty ? 0 : cloud.Size.Length / nuB;
        }
        else
        {
            var (lu, lv) = ParameterExtents(state.B, state.Db);
            spacing = Math.Max(lu / nuB, lv / nvB);
        }

        for (int i = 0; i <= nuA; i++)
        {
            for (int j = 0; j <= nvA; j++)
            {
                double ua = state.Da.U.ParameterAt((double)i / nuA);
                double va = state.Da.V.ParameterAt((double)j / nvA);
                var pa = state.A.PointAt(ua, va);
                if (!bvh.Nearest(pa, k => samplesB[k].P.DistanceTo(pa), out int nearest, out double distance))
                    continue;
                if (distance > spacing * state.Settings.SeedSpacingFactor)
                    continue;

                double[] parameters = [ua, va, samplesB[nearest].U, samplesB[nearest].V];
                if (RefineSeed(state, parameters))
                    seeds.Add(parameters);
            }
        }
    }

    /// <summary>
    /// Grid counts whose sample SPACING is roughly equal in model units, at the same total
    /// budget as the isotropic grid (<c>nu·nv ≈ resolution²</c> with <c>nu/nv = aspect</c>).
    /// Returns the isotropic counts unchanged whenever the surface is not extreme enough to
    /// qualify, which is what keeps the second pass off for ordinary geometry.
    /// </summary>
    private static (int Nu, int Nv) SeedCounts(
        Surface s, in ParamDomain d, int resolution, double anisotropyThreshold)
    {
        var (lu, lv) = ParameterExtents(s, d);
        // Exact-zero guard on a division: a degenerate direction has no aspect to speak of.
        if (!(lu > 0) || !(lv > 0))
            return (resolution, resolution);
        double aspect = lu / lv;
        if (aspect < anisotropyThreshold && aspect > 1 / anisotropyThreshold)
            return (resolution, resolution);
        double root = Math.Sqrt(aspect);
        // The budget is per-pass work, not accuracy: a 64x cap keeps the worst case
        // (a many-turn thread band) at ~1500 samples along the long direction.
        int nu = Math.Clamp((int)Math.Round(resolution * root), 1, resolution * 64);
        int nv = Math.Clamp((int)Math.Round(resolution / root), 1, resolution * 64);
        return (nu, nv);
    }

    /// <summary>
    /// How far the surface's parameter box reaches in MODEL units along each direction:
    /// the mean speed |∂P/∂u| over a coarse cross, times the domain length.
    ///
    /// <para><b>Not a chordal polyline</b>, which is the trap here. The surface this
    /// measurement exists for is a thread band coiled through thirteen turns, and a chord
    /// between two samples a few turns apart measures the distance across the coil rather
    /// than along it — an 8-sample polyline reported 61 mm where the band is 327 mm long,
    /// a number with no relation to either. The speed form is exact for helices, circles
    /// and lines alike because their speed is constant, and it costs a handful of
    /// evaluations rather than a fine sampling.</para>
    /// </summary>
    private static (double U, double V) ParameterExtents(Surface s, in ParamDomain d)
    {
        // Cell CENTRES, never the domain's own nodes: Eval clamps to the domain, so a
        // central difference taken at a boundary node is silently one-sided and halves
        // the speed it reports there.
        const int samples = 4;
        double speedU = 0, speedV = 0;
        double hu = d.U.Length / 1024, hv = d.V.Length / 1024;
        for (int i = 0; i < samples; i++)
        {
            double u = d.U.ParameterAt((i + 0.5) / samples);
            for (int j = 0; j < samples; j++)
            {
                double v = d.V.ParameterAt((j + 0.5) / samples);
                speedU += Eval(s, d, u + hu, v).DistanceTo(Eval(s, d, u - hu, v)) / (2 * hu);
                speedV += Eval(s, d, u, v + hv).DistanceTo(Eval(s, d, u, v - hv)) / (2 * hv);
            }
        }
        return (speedU / (samples * samples) * d.U.Length, speedV / (samples * samples) * d.V.Length);
    }

    /// <summary>Damped Gauss–Newton pulling a parameter 4-tuple onto S_a = S_b.</summary>
    private static bool RefineSeed(MarchState state, double[] parameters)
    {
        var settings = state.Settings;
        for (int iteration = 0; iteration < settings.SeedIterations; iteration++)
        {
            var pa = Eval(state.A, state.Da, parameters[0], parameters[1]);
            var pb = Eval(state.B, state.Db, parameters[2], parameters[3]);
            var f = pa - pb;
            if (f.Length < settings.NewtonResidual)
                return true;

            var (jau, jav) = Partials(state.A, state.Da, parameters[0], parameters[1], settings);
            var (jbu, jbv) = Partials(state.B, state.Db, parameters[2], parameters[3], settings);

            // Normal equations (JᵀJ + λI)Δ = −JᵀF with J = [Ja | −Jb].
            Span<Vector3d> columns = [jau, jav, -jbu, -jbv];
            var m = new double[4, 4];
            var rhs = new double[4];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                    m[r, c] = columns[r].Dot(columns[c]);
                m[r, r] += settings.LevenbergDamping;
                rhs[r] = -columns[r].Dot(f);
            }
            if (!Solve4(m, rhs, settings.PivotFloor, out var delta))
                return false;
            for (int k = 0; k < 4; k++)
                parameters[k] += delta[k];
        }
        return (Eval(state.A, state.Da, parameters[0], parameters[1]) -
                Eval(state.B, state.Db, parameters[2], parameters[3])).Length < settings.SeedAcceptance;
    }

    private static List<Vector3d> Trace(MarchState state, double[] seed, int direction, out bool closed)
    {
        var settings = state.Settings;
        closed = false;
        var parameters = (double[])seed.Clone();
        var points = new List<Vector3d>();
        var start = Eval(state.A, state.Da, parameters[0], parameters[1]);
        points.Add(start);
        Vector3d? previousTangent = null;

        for (int step = 0; step < settings.MaxSteps; step++)
        {
            var p = Eval(state.A, state.Da, parameters[0], parameters[1]);
            var na = NormalOf(state.A, state.Da, parameters[0], parameters[1]);
            var nb = NormalOf(state.B, state.Db, parameters[2], parameters[3]);
            var cross = na.Cross(nb);
            if (!cross.TryNormalize(new Tolerance(settings.TangentDegeneracy, settings.TangentDegeneracy), out var tangent))
                break; // tangential contact: direction undefined
            if (previousTangent is { } prev && tangent.Dot(prev) < 0)
                tangent = -tangent;
            if (previousTangent is null)
                tangent *= direction;
            previousTangent = tangent;

            var target = p + tangent * state.Step;
            if (!Correct(state, parameters, target, tangent))
                break;
            if (Outside(parameters[0], state.Da.U, state.Da.PeriodicU, settings.DomainSlack) ||
                Outside(parameters[1], state.Da.V, state.Da.PeriodicV, settings.DomainSlack) ||
                Outside(parameters[2], state.Db.U, state.Db.PeriodicU, settings.DomainSlack) ||
                Outside(parameters[3], state.Db.V, state.Db.PeriodicV, settings.DomainSlack))
                break;

            var next = Eval(state.A, state.Da, parameters[0], parameters[1]);
            if (next.DistanceTo(p) > settings.BranchJumpSteps * state.Step)
                break; // corrector jumped to a different branch

            points.Add(next);
            if (step > settings.MinStepsBeforeClosure && next.DistanceTo(start) < state.Step)
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
        var settings = state.Settings;
        var t = tangent;
        var goal = target;
        for (int iteration = 0; iteration < settings.CorrectorIterations; iteration++)
        {
            var pa = Eval(state.A, state.Da, parameters[0], parameters[1]);
            var pb = Eval(state.B, state.Db, parameters[2], parameters[3]);
            var f = pa - pb;
            double g = t.Dot(pa - goal);
            if (f.Length < settings.NewtonResidual && Math.Abs(g) < settings.NewtonResidual)
                return true;

            var (jau, jav) = Partials(state.A, state.Da, parameters[0], parameters[1], settings);
            var (jbu, jbv) = Partials(state.B, state.Db, parameters[2], parameters[3], settings);

            var m = new double[4, 4]
            {
                { jau.X, jav.X, -jbu.X, -jbv.X },
                { jau.Y, jav.Y, -jbu.Y, -jbv.Y },
                { jau.Z, jav.Z, -jbu.Z, -jbv.Z },
                { t.Dot(jau), t.Dot(jav), 0, 0 },
            };
            var rhs = new double[] { -f.X, -f.Y, -f.Z, -g };
            if (!Solve4(m, rhs, settings.PivotFloor, out var delta))
                return false;

            double magnitude = 0;
            for (int k = 0; k < 4; k++)
            {
                parameters[k] += delta[k];
                magnitude += delta[k] * delta[k];
            }
            if (magnitude > settings.DivergenceStepsSquared * state.Step * state.Step)
                return false; // diverging
        }
        return (Eval(state.A, state.Da, parameters[0], parameters[1]) -
                Eval(state.B, state.Db, parameters[2], parameters[3])).Length < settings.CorrectorAcceptance;
    }

    private static (Vector3d Du, Vector3d Dv) Partials(
        Surface s, in ParamDomain d, double u, double v, in TracerSettings settings)
    {
        double hu = Math.Max(settings.PartialsStep, d.U.Length * settings.PartialsStep);
        double hv = Math.Max(settings.PartialsStep, d.V.Length * settings.PartialsStep);
        var du = (Eval(s, d, u + hu, v) - Eval(s, d, u - hu, v)) / (2 * hu);
        var dv = (Eval(s, d, u, v + hv) - Eval(s, d, u, v - hv)) / (2 * hv);
        return (du, dv);
    }

    /// <summary>Gaussian elimination with partial pivoting for the 4×4 marching systems.</summary>
    private static bool Solve4(double[,] m, double[] rhs, double pivotFloor, out double[] solution)
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
            if (Math.Abs(a[pivot, col]) < pivotFloor)
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

/// <summary>
/// What <see cref="SurfaceIntersection.FitAnalytic"/> did to one intersection curve.
/// </summary>
/// <param name="Source">The curve as the tracer produced it.</param>
/// <param name="Curves">
/// The pieces to use: the fitted <see cref="Line3d"/>/arc chain when <paramref name="Fitted"/>
/// is true, otherwise the single unchanged <paramref name="Source"/> curve — so a caller that
/// simply concatenates every entry's curves gets a correct result whatever happened.
/// </param>
/// <param name="Deviation">
/// The largest distance from an input SAMPLE to the fit (NaN when no fit was produced).
/// It measures the samples only and says nothing about the true curve between them.
/// </param>
/// <param name="Status">Why a fit was refused; <see cref="BiArcFitStatus.NotPlanar"/> for a
/// genuine space curve, which is never silently flattened.</param>
/// <param name="Fitted">False when the source curve was kept — already analytic, refused, or
/// outside the tolerance.</param>
public sealed record AnalyticFit(
    Curve3d Source, IReadOnlyList<Curve3d> Curves, double Deviation, BiArcFitStatus Status, bool Fitted);
