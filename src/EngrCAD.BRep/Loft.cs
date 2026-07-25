using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// The lateral surface of a loft (OCCT's <c>BRepOffsetAPI_ThruSections</c> "skinning"):
/// one section curve per station, blended across the stations by a B-spline that
/// <b>interpolates</b> every section.
///
/// <para>P(u, v) = Σ<sub>k</sub> α<sub>k</sub>(v) · C<sub>k</sub>(u<sub>k</sub>), with
/// u<sub>k</sub> = C<sub>k</sub>.Domain.ParameterAt(u) — sections correspond by
/// <i>normalized</i> parameter, and both surface parameters run over [0, 1].</para>
///
/// <para>The blending weights α are the <b>cardinal basis</b> of B-spline interpolation:
/// with A the collocation matrix A[k][j] = N<sub>j,p</sub>(v<sub>k</sub>) at the section
/// parameters, α<sub>k</sub>(v) = Σ<sub>j</sub> N<sub>j,p</sub>(v) · (A⁻¹)[j][k]. Solving
/// the interpolation once at construction (rather than per evaluation) is what makes this
/// a surface at all: a chord-length re-parameterization computed per u would give every
/// strip of the loft its own v mapping, and the shared rails between strips would then
/// disagree. Degree p = min(3, sections − 1), so two sections give the exact ruled
/// (degree 1) surface and three the exact quadratic.</para>
///
/// <para><b>Exact at the sections.</b> α<sub>k</sub>(v<sub>j</sub>) = δ<sub>jk</sub> is
/// applied as an exact-equality special case, not left to the solve's round-off: the
/// tessellation grid's v = 0 and v = 1 rows must reproduce the first and last section
/// curves <i>bit-for-bit</i>, because those curves are also the shared edges the caps and
/// neighbouring strips are built from. Together with <see cref="NaturalUSegments"/>
/// mirroring the tessellator's own edge-sampling rule, every boundary of a loft face welds
/// by construction rather than by tolerance.</para>
///
/// <para>∂u × ∂v points outward when the sections are wound counter-clockwise about the
/// loft direction — the same convention as <see cref="ExtrudedSurface"/>, which a loft
/// between two translated copies of one section reproduces exactly.</para>
/// </summary>
public sealed class LoftedSurface : Surface
{
    private readonly Curve3d[] _sections;
    private readonly double[] _parameters;
    private readonly double[] _knots;

    /// <summary>Row-major m × m: <c>_cardinal[j * m + k]</c> = (A⁻¹)[j][k].</summary>
    private readonly double[] _cardinal;

    /// <summary>The section curves, in loft order (v = 0 at the first).</summary>
    public IReadOnlyList<Curve3d> Sections => _sections;

    /// <summary>The v parameter of each section: ascending, 0 at the first, 1 at the last.</summary>
    public IReadOnlyList<double> SectionParameters => _parameters;

    /// <summary>Blending degree in v: min(3, sections − 1). 1 is the ruled surface.</summary>
    public int Degree { get; }

    /// <summary>True when the sections are closed curves, making u periodic.</summary>
    public bool IsClosedU => _sections[0].IsClosed;

    /// <summary>
    /// Builds the blend through <paramref name="sections"/>. <paramref name="sectionParameters"/>
    /// must be ascending with 0 first and 1 last; when omitted, a mean-chord-length
    /// parameterization is derived from the curves themselves. All strips of one loft must
    /// share ONE parameterization, so the factory computes it globally and passes it in.
    /// </summary>
    public LoftedSurface(IReadOnlyList<Curve3d> sections, IReadOnlyList<double>? sectionParameters = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count < 2)
            throw new ArgumentException("A lofted surface needs at least 2 section curves.", nameof(sections));
        _sections = [.. sections];
        int m = _sections.Length;
        for (int k = 0; k < m; k++)
        {
            if (_sections[k] is null)
                throw new ArgumentException("Loft section curves must not be null.", nameof(sections));
            if (_sections[k].IsClosed != _sections[0].IsClosed)
                throw new ArgumentException(
                    "Loft sections must be either all closed or all open curves.", nameof(sections));
        }

        if (sectionParameters is null)
        {
            _parameters = MeanChordParameters(_sections);
        }
        else
        {
            if (sectionParameters.Count != m)
                throw new ArgumentException(
                    "One section parameter is required per section curve.", nameof(sectionParameters));
            _parameters = [.. sectionParameters];
            for (int k = 1; k < m; k++)
            {
                if (!(_parameters[k] > _parameters[k - 1]))
                    throw new ArgumentException(
                        "Section parameters must be strictly ascending.", nameof(sectionParameters));
            }
            // Exact-value requirement, not a tolerance: PointAt's δ shortcut and the
            // tessellation grid both key on v == 0 and v == 1 exactly.
            if (_parameters[0] != 0 || _parameters[^1] != 1)
                throw new ArgumentException(
                    "Section parameters must start at exactly 0 and end at exactly 1.", nameof(sectionParameters));
        }

        Degree = Math.Min(3, m - 1);
        _knots = AveragedKnots(_parameters, Degree);
        _cardinal = InvertCollocation(_parameters, _knots, Degree);
    }

    public override Interval DomainU => Interval.Unit;
    public override Interval DomainV => Interval.Unit;

    public override Vector3d PointAt(double u, double v)
    {
        int m = _sections.Length;
        Span<double> alpha = m <= 32 ? stackalloc double[32] : new double[m];
        Blend(v, alpha[..m]);

        var point = Vector3d.Zero;
        for (int k = 0; k < m; k++)
            point += _sections[k].PointAt(_sections[k].Domain.ParameterAt(u)) * alpha[k];
        return point;
    }

    /// <summary>∂P/∂u — exact, from each section curve's own <see cref="Curve3d.DerivativeAt"/>
    /// (chain rule through the normalized parameter mapping). Never finite-differenced.</summary>
    public Vector3d DerivativeU(double u, double v)
    {
        int m = _sections.Length;
        Span<double> alpha = m <= 32 ? stackalloc double[32] : new double[m];
        Blend(v, alpha[..m]);

        var slope = Vector3d.Zero;
        for (int k = 0; k < m; k++)
        {
            var domain = _sections[k].Domain;
            slope += _sections[k].DerivativeAt(domain.ParameterAt(u)) * (alpha[k] * domain.Length);
        }
        return slope;
    }

    /// <summary>∂P/∂v — exact: the blend's own derivative applied to the section points.</summary>
    public Vector3d DerivativeV(double u, double v)
    {
        int m = _sections.Length;
        Span<double> alpha = m <= 32 ? stackalloc double[32] : new double[m];
        Span<double> derivative = m <= 32 ? stackalloc double[32] : new double[m];
        BlendWithDerivative(v, alpha[..m], derivative[..m]);

        var slope = Vector3d.Zero;
        for (int k = 0; k < m; k++)
            slope += _sections[k].PointAt(_sections[k].Domain.ParameterAt(u)) * derivative[k];
        return slope;
    }

    public override Vector3d NormalAt(double u, double v) =>
        DerivativeU(u, v).Cross(DerivativeV(u, v)).Normalized();

    /// <summary>
    /// The number of u segments a tessellator should use, mirroring
    /// <c>BRepTessellator.SampleEdge</c>'s rules for the section curves that ARE this
    /// face's u boundaries: straight sections need one segment, circular closed sections
    /// the circle density, everything else the generic curve density. The rule lives here
    /// because the weld depends on the grid and the shared edge polylines agreeing
    /// <i>exactly</i>, and only the surface knows what its sections are.
    /// </summary>
    public int NaturalUSegments(int segmentsPerCircle, int curveSamples)
    {
        bool allStraight = true, allCircular = true;
        foreach (var section in _sections)
        {
            allStraight &= section.Underlying is Line3d;
            allCircular &= section.Underlying is Circle3d;
        }
        if (IsClosedU)
            return allCircular ? segmentsPerCircle : curveSamples;
        return allStraight ? 1 : curveSamples;
    }

    // ---- blending ----

    private void Blend(double v, Span<double> alpha)
    {
        int m = _sections.Length;
        // Exact interpolation at a section: bit-for-bit reproduction of the section curve,
        // which the shared edges and caps are built from. An exact-equality test on
        // purpose — the grid evaluates v = 0 and v = 1 exactly, and a 1e-17 blend leak
        // would put the boundary row a hair off the edge polyline it must weld to.
        for (int k = 0; k < m; k++)
        {
            if (v == _parameters[k])
            {
                alpha.Clear();
                alpha[k] = 1;
                return;
            }
        }

        int span = BSplineBasis.FindSpan(v, Degree, m, _knots);
        Span<double> basis = stackalloc double[Degree + 1];
        BSplineBasis.Evaluate(span, v, Degree, _knots, basis);
        alpha.Clear();
        for (int i = 0; i <= Degree; i++)
        {
            int j = span - Degree + i;
            for (int k = 0; k < m; k++)
                alpha[k] += basis[i] * _cardinal[j * m + k];
        }
    }

    private void BlendWithDerivative(double v, Span<double> alpha, Span<double> derivative)
    {
        int m = _sections.Length;
        int stride = Degree + 1;
        int span = BSplineBasis.FindSpan(v, Degree, m, _knots);
        Span<double> ders = stackalloc double[2 * stride];
        BSplineBasis.EvaluateDerivatives(span, v, Degree, _knots, 1, ders);

        alpha.Clear();
        derivative.Clear();
        for (int i = 0; i <= Degree; i++)
        {
            int j = span - Degree + i;
            for (int k = 0; k < m; k++)
            {
                alpha[k] += ders[i] * _cardinal[j * m + k];
                derivative[k] += ders[stride + i] * _cardinal[j * m + k];
            }
        }
        // The value half honours the exact-interpolation rule; the derivative half cannot
        // (it is genuinely non-zero for every section at a section parameter).
        for (int k = 0; k < m; k++)
        {
            if (v == _parameters[k])
            {
                alpha.Clear();
                alpha[k] = 1;
                break;
            }
        }
    }

    /// <summary>
    /// Section parameters by mean chord length: the average 3D distance between
    /// corresponding points of consecutive sections, accumulated and normalized. Averaging
    /// over the whole section (rather than using one point, e.g. a centroid) keeps the
    /// parameterization stable when sections differ in shape as well as position.
    /// </summary>
    private static double[] MeanChordParameters(Curve3d[] sections)
    {
        const int samples = 16;
        int m = sections.Length;
        var parameters = new double[m];
        double total = 0;
        for (int k = 1; k < m; k++)
        {
            double sum = 0;
            for (int i = 0; i < samples; i++)
            {
                double u = (double)i / samples;
                sum += sections[k].PointAt(sections[k].Domain.ParameterAt(u))
                    .DistanceTo(sections[k - 1].PointAt(sections[k - 1].Domain.ParameterAt(u)));
            }
            double chord = sum / samples;
            if (!(chord > 0))
                throw new ArgumentException(
                    $"Loft sections {k - 1} and {k} coincide; remove the duplicate section.", nameof(sections));
            total += chord;
            parameters[k] = total;
        }
        for (int k = 1; k < m; k++)
            parameters[k] /= total;
        parameters[^1] = 1.0; // exact, despite the division round-off
        return parameters;
    }

    /// <summary>
    /// Clamped knot vector by the averaging rule (The NURBS Book eq. 9.8), which makes the
    /// collocation matrix banded and non-singular for any ascending parameters.
    /// </summary>
    private static double[] AveragedKnots(double[] parameters, int degree)
    {
        int m = parameters.Length;
        var knots = new double[m + degree + 1];
        for (int i = 0; i <= degree; i++)
        {
            knots[i] = 0;
            knots[m + i] = 1;
        }
        for (int j = 1; j <= m - degree - 1; j++)
        {
            double sum = 0;
            for (int i = j; i <= j + degree - 1; i++)
                sum += parameters[i];
            knots[j + degree] = sum / degree;
        }
        return knots;
    }

    /// <summary>
    /// Inverts the collocation matrix A[k][j] = N<sub>j,p</sub>(v<sub>k</sub>) by
    /// Gauss–Jordan with partial pivoting (section counts are small, so O(m³) once at
    /// construction is free). The result is the cardinal basis of the interpolation.
    /// </summary>
    private static double[] InvertCollocation(double[] parameters, double[] knots, int degree)
    {
        int m = parameters.Length;
        var a = new double[m * m];
        var inverse = new double[m * m];
        Span<double> basis = stackalloc double[degree + 1];
        for (int k = 0; k < m; k++)
        {
            int span = BSplineBasis.FindSpan(parameters[k], degree, m, knots);
            BSplineBasis.Evaluate(span, parameters[k], degree, knots, basis);
            for (int i = 0; i <= degree; i++)
                a[k * m + span - degree + i] += basis[i];
            inverse[k * m + k] = 1;
        }

        for (int column = 0; column < m; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < m; row++)
            {
                if (Math.Abs(a[row * m + column]) > Math.Abs(a[pivot * m + column]))
                    pivot = row;
            }
            // Near-underflow singularity guard (scale-free), not a geometric tolerance.
            if (Math.Abs(a[pivot * m + column]) < 1e-300)
                throw new ArgumentException("Loft section parameters make the interpolation singular.");
            if (pivot != column)
            {
                for (int c = 0; c < m; c++)
                {
                    (a[pivot * m + c], a[column * m + c]) = (a[column * m + c], a[pivot * m + c]);
                    (inverse[pivot * m + c], inverse[column * m + c]) = (inverse[column * m + c], inverse[pivot * m + c]);
                }
            }

            double diagonal = a[column * m + column];
            for (int c = 0; c < m; c++)
            {
                a[column * m + c] /= diagonal;
                inverse[column * m + c] /= diagonal;
            }
            for (int row = 0; row < m; row++)
            {
                if (row == column)
                    continue;
                double factor = a[row * m + column];
                // Exact-zero skip: the matrix is banded, so most entries genuinely are 0.
                if (factor == 0)
                    continue;
                for (int c = 0; c < m; c++)
                {
                    a[row * m + c] -= factor * a[column * m + c];
                    inverse[row * m + c] -= factor * inverse[column * m + c];
                }
            }
        }
        return inverse;
    }
}

/// <summary>
/// The curve a fixed section parameter traces across a loft (a loft "rail"), the analogue
/// of <see cref="SweptRailCurve"/>. It evaluates the surface itself rather than
/// re-interpolating the junction points, so the rail edge and the face's u = 0 grid column
/// are bit-for-bit identical and cannot crack apart.
/// </summary>
public sealed class LoftRailCurve(LoftedSurface surface, double u) : Curve3d
{
    public LoftedSurface Surface => surface;

    /// <summary>The surface u parameter this rail follows.</summary>
    public double U => u;

    public override Interval Domain => surface.DomainV;
    public override bool IsClosed => false;
    public override Vector3d PointAt(double t) => surface.PointAt(u, t);
    public override Vector3d DerivativeAt(double t) => surface.DerivativeV(u, t);
}

/// <summary>
/// A closed curve whose seam has been moved: P(t) = base(wrap(t + shift)). Lofting builds
/// these to align the parameterization of successive closed sections so the skin does not
/// twist (the closed-curve analogue of choosing which segment pairs with which in a
/// multi-segment section).
///
/// <para>The domain and closure are the base curve's, and <see cref="Underlying"/> forwards
/// so consumers keep the base's sampling rules (a shifted circle is still sampled as a
/// circle) — but, as for every wrapper here, <c>Underlying</c> must never be trusted for
/// POSITION.</para>
/// </summary>
public sealed class PhaseShiftedCurve : Curve3d
{
    private readonly Curve3d _base;
    private readonly double _shift;

    public PhaseShiftedCurve(Curve3d baseCurve, double shift)
    {
        ArgumentNullException.ThrowIfNull(baseCurve);
        if (!baseCurve.IsClosed)
            throw new ArgumentException("Only closed curves can be phase shifted.", nameof(baseCurve));
        _base = baseCurve;
        _shift = shift;
    }

    public Curve3d Base => _base;

    /// <summary>Parameter offset applied before evaluation (in the base curve's own units).</summary>
    public double Shift => _shift;

    public override Interval Domain => _base.Domain;
    public override bool IsClosed => true;
    public override Curve3d Underlying => _base.Underlying;

    private double Map(double t)
    {
        var domain = _base.Domain;
        double local = (t + _shift - domain.Start) % domain.Length;
        if (local < 0)
            local += domain.Length;
        return domain.Start + local;
    }

    public override Vector3d PointAt(double t) => _base.PointAt(Map(t));
    public override Vector3d TangentAt(double t) => _base.TangentAt(Map(t));
    public override Vector3d DerivativeAt(double t) => _base.DerivativeAt(Map(t));
    public override Vector3d SecondDerivativeAt(double t) => _base.SecondDerivativeAt(Map(t));
}
