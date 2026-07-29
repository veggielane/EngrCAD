using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// IGES (5.3) <b>import</b>. Deliberately import-only: IGES is a legacy format whose one
/// remaining use is receiving files from old CAM and surfacing systems, and writing it
/// would mean maintaining a second lossy encoding of geometry STEP already carries better.
/// <para>Supported entities, chosen because each maps ONTO EXISTING kernel geometry rather
/// than needing new surface types:</para>
/// <list type="bullet">
/// <item><b>Curves</b> — 110 line, 100 circular arc, 104 conic arc (ellipse/parabola/
/// hyperbola, classified from the general conic coefficients), 126 rational B-spline,
/// 102 composite curve, 116 point.</item>
/// <item><b>Surfaces</b> — 128 rational B-spline, 108 plane, 118 ruled (a two-section
/// loft), 120 surface of revolution, 122 tabulated cylinder (an extrusion).</item>
/// <item><b>Trimming</b> — 144 trimmed parametric surface over 142 curve-on-surface,
/// which is the shape of every real surfacing file.</item>
/// <item><b>124 transformation matrices</b>, applied to the DEFINING DATA where the type
/// allows exact reconstruction (a line's endpoints, a NURBS curve's control points — a
/// rational curve is an affine combination of its control points at every parameter) and
/// through a <see cref="TransformedCurve"/> otherwise.</item>
/// </list>
/// <para><b>The result is a FACE SOUP and says so.</b> IGES has no shared topology: every
/// trimmed surface carries its own boundary curves, so two neighbouring faces reference
/// two coincident-but-distinct curves and the assembled solid has edges used once rather
/// than twice. <see cref="IgesReadResult.Solid"/> therefore does NOT satisfy
/// <c>Validate()</c> as it stands — run <see cref="ShapeHealing.Heal"/>, which exists for
/// exactly this case. That is reported as <see cref="IgesReadResult.IsFaceSoup"/> rather
/// than left to be discovered.</para>
/// <para>Units are read from the Global section and scaled to millimetres, the lesson the
/// STEP importer paid for; unknown entity types are skipped with a diagnostic naming the
/// type and the first offender, following <c>StepReader</c>'s conventions exactly.
/// Malformed RECORD structure (a bad section letter, a broken directory pair, a
/// non-numeric sequence number) throws <see cref="FormatException"/>.</para>
/// </summary>
public static class IgesReader
{
    public static IgesReadResult ReadFile(string path, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        Read(File.ReadAllText(path), logger);

    public static IgesReadResult Read(string text, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stopwatch = logger is null ? null : System.Diagnostics.Stopwatch.StartNew();
        var result = new Builder(IgesParser.Parse(text)).Build();
        if (logger is not null)
        {
            KernelLog.IgesImported(
                logger, result.Faces.Count, result.Curves.Count, result.Surfaces.Count,
                result.Diagnostics.Count, stopwatch!.Elapsed.TotalMilliseconds);
        }
        return result;
    }

    private sealed class Builder(IgesFile file)
    {
        private const double Weld = 1e-9;

        private readonly List<string> _diagnostics = [];
        private readonly HashSet<string> _reportedSkips = [];
        private readonly HashSet<int> _consumed = [];
        private readonly Dictionary<int, Matrix4d> _transforms = [];
        private double _scale = 1.0;

        public IgesReadResult Build()
        {
            ResolveUnits();

            var faces = new List<BrepFace>();
            // Trimmed surfaces first, so the curves and surfaces they consume are marked
            // before the leftovers are collected. Ordered by DE pointer, which is the
            // file's own order.
            foreach (var entity in Ordered().Where(e => e.Type == 144))
            {
                if (TrimmedSurface(entity) is { } face)
                    faces.Add(face);
            }

            var curves = new List<Curve3d>();
            var surfaces = new List<Surface>();
            var points = new List<Vector3d>();
            foreach (var entity in Ordered())
            {
                if (_consumed.Contains(entity.Pointer))
                    continue;
                switch (entity.Type)
                {
                    case 116:
                        if (Point(entity) is { } p)
                            points.Add(p);
                        break;
                    case 100 or 102 or 104 or 110 or 126:
                        if (Try(() => Curve(entity.Pointer, Matrix4d.Identity), entity, "curve") is { } c)
                            curves.Add(c);
                        break;
                    case 108 or 118 or 120 or 122 or 128:
                        if (Try(() => Surface(entity.Pointer, Matrix4d.Identity), entity, "surface") is { } s)
                            surfaces.Add(s);
                        break;
                    case 124 or 142 or 314 or 406:
                        break; // consumed by reference, or non-geometric
                    default:
                        NoteSkip(entity, "top-level");
                        break;
                }
            }

            BrepSolid? solid = faces.Count == 0 ? null : new BrepSolid([new BrepShell(faces)]);
            if (solid is not null)
            {
                Note(
                    $"Assembled {faces.Count} trimmed surface(s) into one unsewn shell. IGES carries "
                    + "no shared topology, so neighbouring faces reference distinct coincident "
                    + "curves: run ShapeHealing.Heal before treating the result as a manifold solid.");
            }
            if (!file.SawTerminate)
                Note("The file has no Terminate section; it may be truncated.");
            if (faces.Count == 0 && curves.Count == 0 && surfaces.Count == 0 && points.Count == 0)
                Note("No supported geometry entities were found.");

            return new IgesReadResult(
                solid, faces, curves, surfaces, points, solid is not null, _diagnostics);
        }

        private IEnumerable<IgesEntity> Ordered() => file.Entities.Values.OrderBy(e => e.Pointer);

        // ---- units ----

        private void ResolveUnits()
        {
            // The STEP importer's lesson, translated: a file's declared unit is not
            // optional information, and everything is scaled to millimetres.
            double factor = file.Global.UnitFlag switch
            {
                1 => 25.4,        // inch
                2 => 1.0,         // millimetre
                3 => NamedUnit(), // "see units name"
                4 => 304.8,       // foot
                5 => 1609344.0,   // mile
                6 => 1000.0,      // metre
                7 => 1e6,         // kilometre
                8 => 0.0254,      // mil
                9 => 0.001,       // micron
                10 => 10.0,       // centimetre
                11 => 2.54e-5,    // microinch
                _ => double.NaN,
            };
            if (double.IsNaN(factor))
            {
                Note(
                    $"Unrecognized unit flag {file.Global.UnitFlag}"
                    + (file.Global.UnitName.Length == 0 ? "" : $" ('{file.Global.UnitName}')")
                    + "; coordinates were read unscaled (millimetres assumed).");
                factor = 1.0;
            }
            // Exact-== semantic guard: scale 1 must leave every coordinate bit-identical.
            _scale = factor;
            if (factor != 1.0)
            {
                Note($"Length unit is {UnitDescription()}; all lengths were scaled by "
                    + $"{factor.ToString("R", CultureInfo.InvariantCulture)} to millimetres.");
            }
        }

        private double NamedUnit() => file.Global.UnitName.Trim().ToUpperInvariant() switch
        {
            "MM" or "MILLIMETER" or "MILLIMETRE" => 1.0,
            "IN" or "INCH" or "INCHES" => 25.4,
            "M" or "METER" or "METRE" => 1000.0,
            "CM" or "CENTIMETER" or "CENTIMETRE" => 10.0,
            "FT" or "FOOT" or "FEET" => 304.8,
            _ => double.NaN,
        };

        private string UnitDescription() => file.Global.UnitName.Length > 0
            ? $"'{file.Global.UnitName}' (flag {file.Global.UnitFlag})"
            : $"flag {file.Global.UnitFlag}";

        private Vector3d Scaled(in Vector3d p) => _scale == 1.0 ? p : p * _scale;

        // ---- entity access ----

        private IgesEntity Entity(int pointer) => file.Entities.TryGetValue(pointer, out var entity)
            ? entity
            : throw new FormatException($"IGES directory entry {pointer} does not exist.");

        private double P(IgesEntity entity, int index, double fallback = 0) =>
            IgesParser.Number(index < entity.Parameters.Count ? entity.Parameters[index] : "", fallback);

        private int Pi(IgesEntity entity, int index, int fallback = 0) =>
            (int)Math.Round(P(entity, index, fallback));

        /// <summary>Runs a construction, converting a recoverable failure into a
        /// diagnostic naming the entity — <c>StepReader</c>'s "blast radius of a bad
        /// entity is one face" rule.</summary>
        private T? Try<T>(Func<T?> build, IgesEntity entity, string role) where T : class
        {
            try
            {
                return build();
            }
            catch (Exception ex) when (
                ex is FormatException or NotSupportedException or ArgumentException
                    or InvalidOperationException)
            {
                Note($"Skipped {role} at DE {entity.Pointer} (type {entity.Type}): {ex.Message}");
                return null;
            }
        }

        private void Note(string message)
        {
            if (!_diagnostics.Contains(message))
                _diagnostics.Add(message);
        }

        private void NoteSkip(IgesEntity entity, string role)
        {
            if (_reportedSkips.Add($"{role}:{entity.Type}"))
            {
                Note($"Skipped unsupported {role} entity type {entity.Type} "
                    + $"(first at DE {entity.Pointer}).");
            }
        }

        // ---- transformation matrices (entity 124) ----

        private Matrix4d Transform(IgesEntity entity)
        {
            if (entity.TransformPointer == 0)
                return Matrix4d.Identity;
            if (_transforms.TryGetValue(entity.TransformPointer, out var cached))
                return cached;

            var matrix = Matrix4d.Identity;
            var source = Entity(entity.TransformPointer);
            if (source.Type != 124)
            {
                Note($"DE {entity.Pointer} points at DE {entity.TransformPointer} for its "
                    + $"transformation matrix, but that is a type {source.Type} entity; assuming identity.");
            }
            else
            {
                // A 124 may itself sit under another 124 (nested definition spaces).
                var outer = Transform(source);
                matrix = outer * new Matrix4d(
                    P(source, 1), P(source, 2), P(source, 3), P(source, 4) * _scale,
                    P(source, 5), P(source, 6), P(source, 7), P(source, 8) * _scale,
                    P(source, 9), P(source, 10), P(source, 11), P(source, 12) * _scale,
                    0, 0, 0, 1);
            }
            _transforms[entity.TransformPointer] = matrix;
            return matrix;
        }

        private static bool IsRigid(in Matrix4d m)
        {
            // Read the SIGN and magnitude of the basis, not a normalized comparison: an
            // IGES 124 form-0 matrix is orthonormal by definition, and this only has to
            // decide whether exact type-preserving reconstruction is legal.
            var x = new Vector3d(m.M11, m.M21, m.M31);
            var y = new Vector3d(m.M12, m.M22, m.M32);
            var z = new Vector3d(m.M13, m.M23, m.M33);
            return Math.Abs(x.LengthSquared - 1) < 1e-9
                && Math.Abs(y.LengthSquared - 1) < 1e-9
                && Math.Abs(z.LengthSquared - 1) < 1e-9
                && Math.Abs(x.Dot(y)) < 1e-9 && Math.Abs(x.Dot(z)) < 1e-9 && Math.Abs(y.Dot(z)) < 1e-9;
        }

        private static Vector3d Direction(in Matrix4d m, in Vector3d v) => new(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);

        // ---- points and curves ----

        private Vector3d? Point(IgesEntity entity)
        {
            var m = Transform(entity);
            return m.TransformPoint(Scaled(new Vector3d(P(entity, 1), P(entity, 2), P(entity, 3))));
        }

        /// <summary>
        /// Builds a curve in MODEL space: its own definition-space geometry with its own
        /// 124 matrix and any enclosing one already applied. Composing the matrices on the
        /// way down (rather than transforming a finished curve) is what keeps a revolve's
        /// axis and generator in one coordinate system with no wrapper.
        /// </summary>
        private Curve3d Curve(int pointer, in Matrix4d outer)
        {
            var entity = Entity(pointer);
            _consumed.Add(pointer);
            var m = outer * Transform(entity);
            return entity.Type switch
            {
                110 => Line(entity, m),
                100 => CircularArc(entity, m),
                104 => ConicArc(entity, m),
                126 => BSplineCurve(entity, m),
                102 => CompositeCurve(entity, m),
                _ => throw new NotSupportedException(
                    $"entity type {entity.Type} is not a curve this reader builds"),
            };
        }

        private Curve3d Line(IgesEntity entity, in Matrix4d m)
        {
            var start = m.TransformPoint(Scaled(new Vector3d(P(entity, 1), P(entity, 2), P(entity, 3))));
            var end = m.TransformPoint(Scaled(new Vector3d(P(entity, 4), P(entity, 5), P(entity, 6))));
            // Forms 1 (semi-bounded ray) and 2 (unbounded) have no bounded counterpart
            // here; the segment between the two given points is the honest reading and is
            // what every consumer of a legacy file wants.
            if (entity.Form is 1 or 2)
                Note($"Line DE {entity.Pointer} is form {entity.Form} (unbounded); imported as the bounded segment.");
            return new Line3d(start, end);
        }

        private Curve3d CircularArc(IgesEntity entity, in Matrix4d m)
        {
            // Definition space: everything in the plane z = ZT, with the arc running
            // counter-clockwise from start to end about +z.
            double zt = P(entity, 1) * _scale;
            var centre = new Vector3d(P(entity, 2) * _scale, P(entity, 3) * _scale, zt);
            var start = new Vector3d(P(entity, 4) * _scale, P(entity, 5) * _scale, zt);
            var end = new Vector3d(P(entity, 6) * _scale, P(entity, 7) * _scale, zt);

            double radius = (start - centre).Length;
            if (!(radius > 0))
                throw new FormatException($"circular arc DE {entity.Pointer} has zero radius");

            // The circle's own frame is built from the START point, so the arc begins
            // exactly at u = 0 — the phase-alignment rule the whole kernel obeys, and the
            // reason an imported arc's endpoints are exact rather than nearly right.
            var x = (start - centre) / radius;
            var y = new Vector3d(-x.Y, x.X, 0);
            double sweep = Math.Atan2((end - centre).Dot(y), (end - centre).Dot(x));
            if (sweep <= Weld)
                sweep += 2 * Math.PI; // start == end means a full circle

            var circle = IsRigid(m)
                ? new Circle3d(m.TransformPoint(centre), Direction(m, x), Direction(m, y), radius)
                : null;
            if (circle is null)
            {
                Note($"Circular arc DE {entity.Pointer} carries a non-rigid transformation; "
                    + "imported as a transformed circle rather than an exact one.");
                var plain = new Circle3d(centre, x, y, radius);
                var wrapped = new TransformedCurve(plain, m);
                return Math.Abs(sweep - 2 * Math.PI) < Weld
                    ? wrapped
                    : new CurveSegment(wrapped, 0, sweep);
            }
            return Math.Abs(sweep - 2 * Math.PI) < Weld ? circle : new CurveSegment(circle, 0, sweep);
        }

        private Curve3d ConicArc(IgesEntity entity, in Matrix4d m)
        {
            // A x^2 + B xy + C y^2 + D x + E y + F = 0 in the plane z = ZT. The form
            // number states the intended type (1 ellipse, 2 hyperbola, 3 parabola) but is
            // routinely wrong, so it is used only to CHECK the classification.
            double a = P(entity, 1), b = P(entity, 2), c = P(entity, 3);
            double d = P(entity, 4), e = P(entity, 5), f = P(entity, 6);
            double zt = P(entity, 7) * _scale;
            var start = new Vector3d(P(entity, 8) * _scale, P(entity, 9) * _scale, zt);
            var end = new Vector3d(P(entity, 10) * _scale, P(entity, 11) * _scale, zt);

            // Rotate away the cross term: the axes are the eigenvectors of [[a, b/2],
            // [b/2, c]], available in closed form. Scale-free guard: the cross term is
            // negligible relative to the quadratic part, not against an absolute epsilon.
            double quadratic = Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c)));
            if (quadratic == 0)
                throw new FormatException($"conic arc DE {entity.Pointer} has no quadratic terms");
            double theta = Math.Abs(b) < 1e-13 * quadratic ? 0 : 0.5 * Math.Atan2(b, a - c);
            double cos = Math.Cos(theta), sin = Math.Sin(theta);

            double a2 = a * cos * cos + b * cos * sin + c * sin * sin;
            double c2 = a * sin * sin - b * cos * sin + c * cos * cos;
            double d2 = d * cos + e * sin;
            double e2 = -d * sin + e * cos;

            var axisX = new Vector3d(cos, sin, 0);
            var axisY = new Vector3d(-sin, cos, 0);
            double discriminant = b * b - 4 * a * c;

            Curve3d curve;
            if (Math.Abs(a2) < 1e-13 * quadratic || Math.Abs(c2) < 1e-13 * quadratic)
            {
                // Parabola: one squared term vanishes. Put the non-degenerate axis on x.
                bool alongY = Math.Abs(a2) < Math.Abs(c2);
                double q = alongY ? c2 : a2;              // the surviving quadratic coefficient
                double linear = alongY ? d2 : e2;         // the surviving linear coefficient
                double other = alongY ? e2 : d2;
                if (Math.Abs(linear) < 1e-13 * quadratic)
                    throw new FormatException($"conic arc DE {entity.Pointer} is a degenerate parabola");
                // q*v^2 + other*v + linear*w + f = 0  =>  w = -(q v^2 + other v + f)/linear
                double vertexV = -other / (2 * q);
                double vertexW = -(q * vertexV * vertexV + other * vertexV + f) / linear;
                double focal = Math.Abs(linear / (4 * q));
                // Parabola3d's x axis points from the apex toward the focus.
                var openTowards = -Math.Sign(linear / q);
                var localApex = alongY
                    ? new Vector3d(vertexW, vertexV, 0)
                    : new Vector3d(vertexV, vertexW, 0);
                var apexAxis = alongY ? axisX * openTowards : axisY * openTowards;
                var otherAxis = alongY ? axisY : axisX;
                var apex = new Vector3d(
                    localApex.X * cos - localApex.Y * sin, localApex.X * sin + localApex.Y * cos, zt);
                double half = Math.Max(
                    Math.Abs((start - apex).Dot(otherAxis)), Math.Abs((end - apex).Dot(otherAxis)));
                curve = new Parabola3d(apex, apexAxis, otherAxis, focal,
                    new Interval(-Math.Max(half, Weld), Math.Max(half, Weld)));
            }
            else
            {
                double centreU = -d2 / (2 * a2);
                double centreV = -e2 / (2 * c2);
                double constant = f - a2 * centreU * centreU - c2 * centreV * centreV;
                var centre = new Vector3d(
                    centreU * cos - centreV * sin, centreU * sin + centreV * cos, zt);
                if (discriminant < 0)
                {
                    // Ellipse: a2 u^2 + c2 v^2 = -constant, same sign both terms.
                    double semiA = Math.Sqrt(Math.Abs(constant / a2));
                    double semiB = Math.Sqrt(Math.Abs(constant / c2));
                    if (!(semiA > 0) || !(semiB > 0))
                        throw new FormatException($"conic arc DE {entity.Pointer} is a degenerate ellipse");
                    var ellipse = new Ellipse3d(centre, axisX * semiA, axisY * semiB);
                    double t0 = Phase(ellipse, start), t1 = Phase(ellipse, end);
                    double sweep = t1 - t0;
                    if (sweep <= Weld)
                        sweep += 2 * Math.PI;
                    curve = Math.Abs(sweep - 2 * Math.PI) < Weld
                        ? ellipse
                        : new CurveSegment(ellipse, t0, t0 + sweep);
                }
                else
                {
                    // Hyperbola: opposite signs. The transverse axis is the one whose
                    // coefficient opposes the constant.
                    bool transverseIsU = Math.Sign(a2) != Math.Sign(constant);
                    double semiT = Math.Sqrt(Math.Abs(constant / (transverseIsU ? a2 : c2)));
                    double semiC = Math.Sqrt(Math.Abs(constant / (transverseIsU ? c2 : a2)));
                    var t = transverseIsU ? axisX : axisY;
                    var n = transverseIsU ? axisY : axisX;
                    // Hyperbola3d parameterizes as centre + t*cosh(s) + n*sinh(s), so the
                    // endpoint parameters come from asinh of the conjugate coordinate in
                    // closed form — the same recovery StepReader does for HYPERBOLA.
                    double p0 = Math.Asinh((start - centre).Dot(n) / semiC);
                    double p1 = Math.Asinh((end - centre).Dot(n) / semiC);
                    curve = new Hyperbola3d(
                        centre, t * semiT, n * semiC, new Interval(Math.Min(p0, p1), Math.Max(p0, p1)));
                }
            }

            int expected = curve switch
            {
                Parabola3d => 3,
                Hyperbola3d => 2,
                _ => 1,
            };
            if (entity.Form != 0 && entity.Form != expected)
            {
                Note($"Conic arc DE {entity.Pointer} declares form {entity.Form} but its coefficients "
                    + $"describe form {expected}; the coefficients were believed.");
            }
            return PlaceConic(curve, m);
        }

        /// <summary>
        /// Moves a conic into model space. Under a RIGID map the defining data transforms
        /// exactly and the curve keeps its type, which matters because the tessellator and
        /// <c>BrepQueries</c> both branch on it; anything else falls back to a
        /// <see cref="TransformedCurve"/>, which is exact geometry in a wrapper.
        /// </summary>
        private Curve3d PlaceConic(Curve3d curve, in Matrix4d m)
        {
            if (m.Equals(Matrix4d.Identity))
                return curve;
            if (!IsRigid(m))
                return new TransformedCurve(curve, m);
            var t = m;
            return curve switch
            {
                Ellipse3d e => new Ellipse3d(
                    t.TransformPoint(e.Center), Direction(t, e.SemiAxisX), Direction(t, e.SemiAxisY)),
                Parabola3d p => new Parabola3d(
                    t.TransformPoint(p.Apex), Direction(t, p.XDirection), Direction(t, p.YDirection),
                    p.FocalLength, p.Domain),
                Hyperbola3d h => new Hyperbola3d(
                    t.TransformPoint(h.Center), Direction(t, h.SemiAxisX), Direction(t, h.SemiAxisY),
                    h.Domain),
                CurveSegment s => new CurveSegment(PlaceConic(s.Base, t), s.BaseStart, s.BaseEnd),
                _ => new TransformedCurve(curve, t),
            };
        }

        private static double Phase(Ellipse3d ellipse, in Vector3d point)
        {
            var d = point - ellipse.Center;
            double u = d.Dot(ellipse.SemiAxisX) / ellipse.SemiAxisX.LengthSquared;
            double v = d.Dot(ellipse.SemiAxisY) / ellipse.SemiAxisY.LengthSquared;
            double angle = Math.Atan2(v, u);
            return angle < 0 ? angle + 2 * Math.PI : angle;
        }

        private Curve3d BSplineCurve(IgesEntity entity, in Matrix4d m)
        {
            int k = Pi(entity, 1);
            int degree = Pi(entity, 2);
            if (degree < 1)
                throw new FormatException($"B-spline curve DE {entity.Pointer} has degree {degree}");
            int count = k + 1;
            int knotCount = k + degree + 2;

            int at = 7;
            var knots = new List<double>(knotCount);
            for (int i = 0; i < knotCount; i++)
                knots.Add(P(entity, at++));
            var weights = new List<double>(count);
            for (int i = 0; i < count; i++)
                weights.Add(P(entity, at++));
            var points = new List<Vector3d>(count);
            for (int i = 0; i < count; i++)
            {
                // Control points transform exactly under ANY affine map: a rational curve
                // is an affine combination of them at every parameter, so weights and
                // knots are untouched. (The lesson STEP export already records.)
                var p = Scaled(new Vector3d(P(entity, at), P(entity, at + 1), P(entity, at + 2)));
                points.Add(m.TransformPoint(p));
                at += 3;
            }
            double v0 = P(entity, at), v1 = P(entity, at + 1);

            var curve = new NurbsCurve(degree, points, weights, knots);
            // IGES states the used parameter range separately from the knot vector; a
            // range narrower than the basis span is a genuine trim.
            double start = curve.Domain.Start, end = curve.Domain.End;
            if (v1 > v0 && (v0 > start + Weld || v1 < end - Weld))
                return new CurveSegment(curve, v0, v1);
            return curve;
        }

        private Curve3d CompositeCurve(IgesEntity entity, in Matrix4d m)
        {
            int count = Pi(entity, 1);
            var pieces = new List<Curve3d>(count);
            for (int i = 0; i < count; i++)
                pieces.Add(Curve(Pi(entity, 2 + i), m));
            if (pieces.Count == 0)
                throw new FormatException($"composite curve DE {entity.Pointer} has no constituents");
            // A composite is a CHAIN, and the kernel expresses a chain as several edges
            // rather than one curve — so the pieces are handed back to the caller, which
            // is a boundary loop and wants them separately anyway. A lone piece returns
            // itself so a composite of one is transparent.
            return pieces.Count == 1 ? pieces[0] : new CompositeMarker(pieces);
        }

        /// <summary>Internal marker: a 102 composite curve's constituents, kept as a list
        /// so a boundary loop can turn them into separate edges (which is what topology
        /// wants) instead of flattening them into one sampled curve.</summary>
        private sealed class CompositeMarker(IReadOnlyList<Curve3d> pieces) : Curve3d
        {
            public IReadOnlyList<Curve3d> Pieces => pieces;
            public override Interval Domain => Interval.Unit;
            public override bool IsClosed => false;
            public override Vector3d PointAt(double t)
            {
                double scaled = Math.Clamp(t, 0, 1) * pieces.Count;
                int index = Math.Min((int)scaled, pieces.Count - 1);
                var piece = pieces[index];
                return piece.PointAt(piece.Domain.ParameterAt(scaled - index));
            }
        }

        private static void Flatten(Curve3d curve, List<Curve3d> into)
        {
            if (curve is CompositeMarker composite)
            {
                foreach (var piece in composite.Pieces)
                    Flatten(piece, into);
            }
            else
            {
                into.Add(curve);
            }
        }

        // ---- surfaces ----

        private Surface Surface(int pointer, in Matrix4d outer)
        {
            var entity = Entity(pointer);
            _consumed.Add(pointer);
            var m = outer * Transform(entity);
            return entity.Type switch
            {
                128 => BSplineSurface(entity, m),
                108 => Plane(entity, m),
                118 => Ruled(entity, m),
                120 => Revolution(entity, m),
                122 => TabulatedCylinder(entity, m),
                _ => throw new NotSupportedException(
                    $"entity type {entity.Type} is not a surface this reader builds"),
            };
        }

        private Surface BSplineSurface(IgesEntity entity, in Matrix4d m)
        {
            int k1 = Pi(entity, 1), k2 = Pi(entity, 2);
            int degreeU = Pi(entity, 3), degreeV = Pi(entity, 4);
            if (degreeU < 1 || degreeV < 1)
                throw new FormatException($"B-spline surface DE {entity.Pointer} has degree 0");
            int countU = k1 + 1, countV = k2 + 1;

            int at = 9;
            var knotsU = new List<double>(k1 + degreeU + 2);
            for (int i = 0; i < k1 + degreeU + 2; i++)
                knotsU.Add(P(entity, at++));
            var knotsV = new List<double>(k2 + degreeV + 2);
            for (int i = 0; i < k2 + degreeV + 2; i++)
                knotsV.Add(P(entity, at++));

            // IGES orders the weight and control grids with the FIRST index varying
            // fastest: W(0,0), W(1,0), ..., W(K1,0), W(0,1), ... A transposed read still
            // parses and still builds a surface, just the wrong one, so this ordering is
            // the load-bearing line in the whole entity.
            var weights = new double[countU, countV];
            for (int j = 0; j < countV; j++)
            {
                for (int i = 0; i < countU; i++)
                    weights[i, j] = P(entity, at++);
            }
            var points = new Vector3d[countU, countV];
            for (int j = 0; j < countV; j++)
            {
                for (int i = 0; i < countU; i++)
                {
                    points[i, j] = m.TransformPoint(
                        Scaled(new Vector3d(P(entity, at), P(entity, at + 1), P(entity, at + 2))));
                    at += 3;
                }
            }
            return new NurbsSurface(degreeU, degreeV, points, weights, knotsU, knotsV);
        }

        private Surface Plane(IgesEntity entity, in Matrix4d m)
        {
            var normal = new Vector3d(P(entity, 1), P(entity, 2), P(entity, 3));
            double d = P(entity, 4) * _scale;
            if (!normal.TryNormalize(Tolerance.Default, out var n))
                throw new FormatException($"plane DE {entity.Pointer} has a zero normal");
            var origin = m.TransformPoint(n * d);
            var axis = Direction(m, n);
            var frame = Frame3d.FromNormal(origin, axis);
            return new PlaneSurface(frame.Origin, frame.X, frame.Y);
        }

        private Surface Ruled(IgesEntity entity, in Matrix4d m)
        {
            var first = Single(Curve(Pi(entity, 1), m), entity);
            var second = Single(Curve(Pi(entity, 2), m), entity);
            // DIRFLG = 1 means the second curve runs the other way (join first-start to
            // second-END), which is exactly a reversal of the second rail. Flattened
            // BEFORE reversing: a ReversedCurve wrapping a composite would hide the
            // composite from the single-curve check.
            if (Pi(entity, 3) == 1)
                second = second.Reversed();
            return new LoftedSurface([first, second], [0, 1]);
        }

        private Surface Revolution(IgesEntity entity, in Matrix4d m)
        {
            var axisEntity = Entity(Pi(entity, 1));
            if (axisEntity.Type != 110)
                throw new NotSupportedException(
                    $"surface of revolution DE {entity.Pointer} names a type {axisEntity.Type} axis; "
                    + "IGES requires a line (type 110)");
            var axis = (Line3d)Curve(axisEntity.Pointer, m);
            var generator = Single(Curve(Pi(entity, 2), m), entity);

            double startAngle = P(entity, 3);
            double endAngle = P(entity, 4, 2 * Math.PI);
            double sweep = endAngle - startAngle;
            if (sweep <= Weld)
                sweep += 2 * Math.PI;
            sweep = Math.Min(sweep, 2 * Math.PI);

            var direction = axis.End - axis.Start;
            if (!direction.TryNormalize(Tolerance.Default, out var unit))
                throw new FormatException($"surface of revolution DE {entity.Pointer} has a zero-length axis");
            // A non-zero start angle is a rotation of the generator, which the kernel's
            // revolve expresses by rotating the generator rather than by an angular offset.
            if (Math.Abs(startAngle) > Weld)
            {
                generator = new TransformedCurve(
                    generator, Rotation(axis.Start, unit, startAngle));
            }
            return new RevolvedSurface(generator, axis.Start, unit, sweep);
        }

        private static Matrix4d Rotation(in Vector3d origin, in Vector3d axis, double angle) =>
            Matrix4d.CreateTranslation(origin)
            * Matrix4d.CreateFromAxisAngle(axis, angle)
            * Matrix4d.CreateTranslation(-origin);

        private Surface TabulatedCylinder(IgesEntity entity, in Matrix4d m)
        {
            var directrix = Single(Curve(Pi(entity, 1), m), entity);
            var terminate = m.TransformPoint(
                Scaled(new Vector3d(P(entity, 2), P(entity, 3), P(entity, 4))));
            var direction = terminate - directrix.PointAt(directrix.Domain.Start);
            if (direction.LengthSquared == 0)
                throw new FormatException($"tabulated cylinder DE {entity.Pointer} has a zero generatrix");
            return new ExtrudedSurface(directrix, direction);
        }

        /// <summary>A composite curve where a single curve is required — flattened into
        /// its pieces and refused if there is more than one, rather than silently sampled
        /// into a polyline.</summary>
        private Curve3d Single(Curve3d curve, IgesEntity entity)
        {
            var pieces = new List<Curve3d>();
            Flatten(curve, pieces);
            if (pieces.Count == 1)
                return pieces[0];
            throw new NotSupportedException(
                $"DE {entity.Pointer} needs a single curve but was given a composite of "
                + $"{pieces.Count} pieces; joining them exactly is not something this reader invents");
        }

        // ---- trimmed surfaces (142 / 144) ----

        private BrepFace? TrimmedSurface(IgesEntity entity)
        {
            return Try(() => BuildTrimmed(entity), entity, "trimmed surface");
        }

        private BrepFace BuildTrimmed(IgesEntity entity)
        {
            var m = Transform(entity);
            var surface = Surface(Pi(entity, 1), m);
            int outerFlag = Pi(entity, 2);
            int innerCount = Pi(entity, 3);
            int outerPointer = Pi(entity, 4);

            var loops = new List<BrepLoop>();
            if (outerFlag != 0 && outerPointer != 0)
            {
                loops.Add(BoundaryLoop(outerPointer, m, entity));
            }
            else
            {
                // Flag 0 means "the surface's own natural boundary is the outer boundary",
                // which is a full-domain face: the loop is the parameter rectangle's rim.
                loops.Add(NaturalBoundary(surface));
            }
            for (int i = 0; i < innerCount; i++)
            {
                int pointer = Pi(entity, 5 + i);
                if (pointer != 0)
                    loops.Add(BoundaryLoop(pointer, m, entity));
            }
            // The outer loop is Loops[0], the kernel's convention, and it is decided by
            // the entity's own N1/PTO fields rather than by area — the same "read the
            // declaration, not the geometry" rule StepReader's FACE_OUTER_BOUND follows.
            return new BrepFace(surface, loops);
        }

        private BrepLoop BoundaryLoop(int pointer, in Matrix4d m, IgesEntity owner)
        {
            var entity = Entity(pointer);
            if (entity.Type != 142)
            {
                throw new NotSupportedException(
                    $"trimmed surface DE {owner.Pointer} names DE {pointer} as a boundary, but that "
                    + $"is a type {entity.Type} entity; IGES requires a curve on a parametric "
                    + "surface (type 142)");
            }
            _consumed.Add(pointer);

            // Take the MODEL-space curve (parameter C), not the parameter-space one. The
            // topology here has no pcurve slot — trimming is expressed by 3D loops pulled
            // back on demand — so the parameter-space representation has nowhere to live,
            // and IGES's own PREF flag sanctions preferring either. What matters is that
            // the choice is stated rather than silently made.
            int modelCurve = Pi(entity, 4);
            if (modelCurve == 0)
            {
                throw new NotSupportedException(
                    $"boundary DE {pointer} carries only a parameter-space curve; this reader needs "
                    + "the model-space representation (parameter C), since B-Rep edges here hold 3D "
                    + "curves");
            }

            var pieces = new List<Curve3d>();
            Flatten(Curve(modelCurve, m), pieces);
            if (pieces.Count == 0)
                throw new FormatException($"boundary DE {pointer} has no curve pieces");
            return Loop(pieces);
        }

        /// <summary>Turns a chain of curves into a loop, interning vertices by position at
        /// the weld tier WITHIN this loop so consecutive pieces share their joint and the
        /// loop closes on its own first vertex. Vertices are never shared BETWEEN faces —
        /// IGES gives no way to know they should be, which is precisely why the result is
        /// a face soup.</summary>
        private static BrepLoop Loop(IReadOnlyList<Curve3d> pieces)
        {
            var vertices = new List<BrepVertex>();
            BrepVertex Intern(in Vector3d position)
            {
                foreach (var existing in vertices)
                {
                    if (existing.Position.DistanceTo(position) <= Weld)
                        return existing;
                }
                var created = new BrepVertex(position);
                vertices.Add(created);
                return created;
            }

            var coedges = new List<BrepCoedge>(pieces.Count);
            foreach (var piece in pieces)
            {
                var domain = piece.Domain;
                var start = Intern(piece.PointAt(domain.Start));
                var end = Intern(piece.PointAt(domain.End));
                coedges.Add(new BrepCoedge(new BrepEdge(piece, domain, start, end), sameSense: true));
            }
            return new BrepLoop(coedges);
        }

        /// <summary>The loop bounding a surface's whole parameter rectangle — the
        /// "boundary is the surface's own" case (N1 = 0), built from the four
        /// iso-parameter edges so the face is genuinely closed rather than loop-less.</summary>
        private static BrepLoop NaturalBoundary(Surface surface)
        {
            var u = surface.DomainU;
            var v = surface.DomainV;
            if (!double.IsFinite(u.Start) || !double.IsFinite(u.End)
                || !double.IsFinite(v.Start) || !double.IsFinite(v.End))
            {
                throw new NotSupportedException(
                    "a trimmed surface declaring its own natural boundary needs a bounded parameter "
                    + "domain, and this surface's is infinite");
            }
            var corners = new[]
            {
                surface.PointAt(u.Start, v.Start),
                surface.PointAt(u.End, v.Start),
                surface.PointAt(u.End, v.End),
                surface.PointAt(u.Start, v.End),
            };
            var pieces = new List<Curve3d>(4);
            for (int i = 0; i < 4; i++)
                pieces.Add(new Line3d(corners[i], corners[(i + 1) % 4]));
            return Loop(pieces);
        }
    }
}

/// <summary>
/// What reading an IGES file produced. Mirrors <c>StepReadResult</c>'s conventions:
/// findings are DATA the caller acts on, never log lines.
/// </summary>
/// <param name="Solid">The trimmed surfaces assembled into one shell, or null when the
/// file carried none. <b>Un-sewn</b> — see <paramref name="IsFaceSoup"/>.</param>
/// <param name="Faces">The same faces, individually.</param>
/// <param name="Curves">Curves not consumed by a trimmed surface — the wireframe content
/// of a legacy CAM file, which is often the whole point of one.</param>
/// <param name="Surfaces">Untrimmed surfaces not consumed by a 144.</param>
/// <param name="Points">Type 116 points.</param>
/// <param name="IsFaceSoup">True whenever <paramref name="Solid"/> is non-null: IGES
/// carries no shared topology, so the shell's edges are used once rather than twice and
/// <c>Validate()</c> will refuse it until <c>ShapeHealing.Heal</c> has sewn it.</param>
/// <param name="Diagnostics">Everything skipped, assumed or scaled.</param>
public sealed record IgesReadResult(
    BrepSolid? Solid,
    IReadOnlyList<BrepFace> Faces,
    IReadOnlyList<Curve3d> Curves,
    IReadOnlyList<Surface> Surfaces,
    IReadOnlyList<Vector3d> Points,
    bool IsFaceSoup,
    IReadOnlyList<string> Diagnostics);
