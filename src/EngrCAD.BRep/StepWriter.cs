using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// STEP (ISO 10303-21, AP214) export of B-Rep solids as MANIFOLD_SOLID_BREP. The
/// topology maps directly: faces → ADVANCED_FACE (same-sense from <see cref="BrepFace.IsReversed"/>),
/// loops → FACE_(OUTER_)BOUND + EDGE_LOOP, coedges → ORIENTED_EDGE, edges → EDGE_CURVE,
/// vertices → VERTEX_POINT. Surfaces: plane, cylinder, sphere, surface of linear
/// extrusion, surface of revolution. Curves: line, circle, ellipse, NURBS; wrapper
/// curves are simplified to analytic forms where possible, otherwise sampled into a
/// degree-1 B-spline. Swept (RMF) surfaces are not exportable yet. Units: millimetres.
/// <para><see cref="WriteAssembly"/> writes the product-structure form: one PRODUCT per
/// distinct solid, NEXT_ASSEMBLY_USAGE_OCCURRENCE per placement, poses as
/// CONTEXT_DEPENDENT_SHAPE_REPRESENTATIONs.</para>
/// </summary>
public static class StepWriter
{
    /// <summary>
    /// Opt-in export settings. Everything here is OFF by default, so the bytes an existing
    /// caller gets are unchanged.
    /// </summary>
    /// <param name="ArcFitTolerance">
    /// When set, curves with no analytic STEP form — traced <see cref="PolylineCurve3d"/>
    /// edges, RMF rails, <c>TransformedCurve(NurbsCurve)</c> — are fitted with a biarc chain
    /// (<see cref="BiArcFit"/>) instead of being SAMPLED into a degree-1 B-spline, provided
    /// the fit's deviation clears this tolerance. A bore rim traced at display resolution
    /// then exports as two exact rational arcs rather than a 200-control-point polyline.
    /// A curve whose fit misses the tolerance, or which is a genuine space curve, falls back
    /// to sampling exactly as before — the export never fails over a fit.
    /// </param>
    public sealed record Options(double? ArcFitTolerance = null);

    /// <summary>What an export actually did, for callers who need to record it.</summary>
    /// <param name="Text">The STEP file contents.</param>
    /// <param name="ArcFitDeviation">The worst deviation of any ADOPTED arc fit (0 when none
    /// were adopted, including when arc fitting was off).</param>
    /// <param name="ArcFitCount">How many curves were exported as fitted arc chains.</param>
    /// <param name="SampledCurveCount">How many curves still fell back to degree-1 sampling —
    /// the honest count of what the file approximates.</param>
    public sealed record Result(string Text, double ArcFitDeviation, int ArcFitCount, int SampledCurveCount);

    public static void WriteFile(BrepSolid solid, string path, string name = "EngrCAD part")
    {
        File.WriteAllText(path, Write(solid, name));
    }

    /// <summary>
    /// Exports with <see cref="Options"/> and reports what the export approximated. The
    /// no-options path (<see cref="Write(BrepSolid, string)"/>) is byte-for-byte what it
    /// always was.
    /// </summary>
    public static Result Write(BrepSolid solid, Options options, string name = "EngrCAD part")
    {
        ArgumentNullException.ThrowIfNull(options);
        var w = new Writer(options);
        w.EmitHeaderAndContext(name);
        var shells = solid.Shells.Select(s => w.Shell(s)).ToList();
        int brep = w.Emit($"MANIFOLD_SOLID_BREP('{Writer.Text(name)}',#{shells[0]})");
        int representation = w.Emit(
            $"ADVANCED_BREP_SHAPE_REPRESENTATION('',(#{brep}),#{w.GeometricContext})");
        w.Emit($"SHAPE_DEFINITION_REPRESENTATION(#{w.ProductDefinitionShape},#{representation})");
        return new Result(w.Finish(), w.ArcFitDeviation, w.ArcFitCount, w.SampledCurveCount);
    }

    public static string Write(BrepSolid solid, string name = "EngrCAD part")
    {
        var w = new Writer();
        w.EmitHeaderAndContext(name);
        var shells = solid.Shells.Select(s => w.Shell(s)).ToList();
        int brep = w.Emit($"MANIFOLD_SOLID_BREP('{Writer.Text(name)}',#{shells[0]})");
        int representation = w.Emit(
            $"ADVANCED_BREP_SHAPE_REPRESENTATION('',(#{brep}),#{w.GeometricContext})");
        w.Emit($"SHAPE_DEFINITION_REPRESENTATION(#{w.ProductDefinitionShape},#{representation})");
        return w.Finish();
    }

    /// <summary>
    /// Writes a STEP <b>assembly</b>: one PRODUCT per distinct solid, one
    /// NEXT_ASSEMBLY_USAGE_OCCURRENCE per instance, and the instance's pose as a
    /// CONTEXT_DEPENDENT_SHAPE_REPRESENTATION over an ITEM_DEFINED_TRANSFORMATION —
    /// the product structure every CAD system reads back as a real assembly tree.
    /// <para>Instances sharing a solid (reference identity) share ONE product
    /// definition, so a bolt placed forty times is written once and referenced forty
    /// times. That is the same sharing the display path gets from
    /// <c>PartInstance</c>, and it is why an assembly file is not forty copies of a
    /// bolt.</para>
    /// <para>Poses must be <b>rigid</b>: a STEP placement is an origin plus two
    /// directions, so a scaled or sheared instance transform has no representation and
    /// is rejected by name rather than silently written as if it were rigid.</para>
    /// </summary>
    public static string WriteAssembly(
        IReadOnlyList<StepInstance> instances, string name = "EngrCAD assembly")
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (instances.Count == 0)
            throw new ArgumentException("A STEP assembly needs at least one instance.", nameof(instances));

        var w = new Writer();
        w.EmitApplicationContexts();
        var assembly = w.Product(name);
        w.EmitUnitsAndContext();

        int assemblyAxis = w.OriginPlacement();
        int assemblyRepresentation = w.Emit(
            $"SHAPE_REPRESENTATION('{Writer.Text(name)}',(#{assemblyAxis}),#{w.GeometricContext})");
        w.Emit($"SHAPE_DEFINITION_REPRESENTATION(#{assembly.Shape},#{assemblyRepresentation})");

        // One product per DISTINCT solid (reference identity, like the display path).
        var products = new Dictionary<BrepSolid, (int Definition, int Representation, int Axis)>();
        foreach (var instance in instances)
        {
            ArgumentNullException.ThrowIfNull(instance.Solid);
            if (products.ContainsKey(instance.Solid))
                continue;
            var product = w.Product(instance.PartName);
            var shells = instance.Solid.Shells.Select(s => w.Shell(s)).ToList();
            int brep = w.Emit($"MANIFOLD_SOLID_BREP('{Writer.Text(instance.PartName)}',#{shells[0]})");
            int axis = w.OriginPlacement();
            int representation = w.Emit(
                $"ADVANCED_BREP_SHAPE_REPRESENTATION('{Writer.Text(instance.PartName)}'," +
                $"(#{axis},#{brep}),#{w.GeometricContext})");
            w.Emit($"SHAPE_DEFINITION_REPRESENTATION(#{product.Shape},#{representation})");
            products[instance.Solid] = (product.Definition, representation, axis);
        }

        int ordinal = 0;
        foreach (var instance in instances)
        {
            var (definition, representation, axis) = products[instance.Solid];
            string occurrence = Writer.Text(instance.OccurrenceName);
            int nauo = w.Emit(
                $"NEXT_ASSEMBLY_USAGE_OCCURRENCE('{++ordinal}','{occurrence}','',"
                + $"#{assembly.Definition},#{definition},$)");
            int nauoShape = w.Emit($"PRODUCT_DEFINITION_SHAPE('','',#{nauo})");
            int placement = w.Placement(instance.OccurrenceName, instance.World);
            int transformation = w.Emit(
                $"ITEM_DEFINED_TRANSFORMATION('','',#{axis},#{placement})");
            int relationship = w.Emit(
                $"(REPRESENTATION_RELATIONSHIP('','',#{representation},#{assemblyRepresentation})"
                + $"REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#{transformation})"
                + "SHAPE_REPRESENTATION_RELATIONSHIP())");
            w.Emit($"CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#{relationship},#{nauoShape})");
        }
        return w.Finish();
    }

    /// <summary><see cref="WriteAssembly"/> straight to a file.</summary>
    public static void WriteAssemblyFile(
        IReadOnlyList<StepInstance> instances, string path, string name = "EngrCAD assembly")
    {
        File.WriteAllText(path, WriteAssembly(instances, name));
    }

    private sealed class Writer(Options? options = null)
    {
        private readonly StringBuilder _data = new();
        private int _next = 1;
        private readonly Dictionary<BrepVertex, int> _vertices = [];
        private readonly Dictionary<BrepEdge, int> _edges = [];

        public int GeometricContext { get; private set; }
        public int ProductDefinitionShape { get; private set; }

        /// <summary>Worst deviation of any ADOPTED arc fit.</summary>
        public double ArcFitDeviation { get; private set; }

        /// <summary>Curves exported as fitted arc chains.</summary>
        public int ArcFitCount { get; private set; }

        /// <summary>Curves that still fell back to degree-1 sampling.</summary>
        public int SampledCurveCount { get; private set; }

        public int Emit(string entity)
        {
            int id = _next++;
            _data.Append('#').Append(id).Append('=').Append(entity).Append(";\n");
            return id;
        }

        private static string Real(double v)
        {
            string s = v.ToString("G16", CultureInfo.InvariantCulture);
            return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".";
        }

        private int Point(in Vector3d p) =>
            Emit($"CARTESIAN_POINT('',({Real(p.X)},{Real(p.Y)},{Real(p.Z)}))");

        private int Direction(in Vector3d d) =>
            Emit($"DIRECTION('',({Real(d.X)},{Real(d.Y)},{Real(d.Z)}))");

        private int Placement(in Vector3d origin, in Vector3d z, in Vector3d x) =>
            Emit($"AXIS2_PLACEMENT_3D('',#{Point(origin)},#{Direction(z)},#{Direction(x)})");

        /// <summary>The identity placement every shape representation is anchored to —
        /// also the "from" end of an assembly occurrence's transformation.</summary>
        public int OriginPlacement() => Placement(Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX);

        /// <summary>
        /// A rigid world matrix as an AXIS2_PLACEMENT_3D. A STEP placement is an origin
        /// plus two orthonormal directions and has no room for scale or shear, so a
        /// non-rigid instance transform is refused by name instead of being written as
        /// though it were rigid.
        /// </summary>
        public int Placement(string what, in Matrix4d world)
        {
            var x = world.TransformVector(Vector3d.UnitX);
            var y = world.TransformVector(Vector3d.UnitY);
            var z = world.TransformVector(Vector3d.UnitZ);
            // Direction cosines and unit lengths are DIMENSIONLESS, so this guard is a
            // scale-free machine-epsilon test and deliberately not the linear tolerance
            // ladder: a rotation composed down an assembly chain carries ~1e-15 of drift.
            const double rigid = 1e-9;
            if (Math.Abs(x.Length - 1) > rigid || Math.Abs(y.Length - 1) > rigid ||
                Math.Abs(z.Length - 1) > rigid ||
                Math.Abs(x.Dot(y)) > rigid || Math.Abs(y.Dot(z)) > rigid || Math.Abs(z.Dot(x)) > rigid)
            {
                throw new NotSupportedException(
                    $"'{what}' is placed by a non-rigid transform (scale or shear), which a STEP " +
                    "AXIS2_PLACEMENT cannot express. Bake the scale into the part's geometry, or " +
                    "export that part on its own.");
            }
            return Placement(world.TransformPoint(Vector3d.Zero), z, x);
        }

        /// <summary>Escapes a STEP string literal's body (a single quote doubles).
        /// Names come from user models, so this is not optional.</summary>
        public static string Text(string value) => value.Replace("'", "''");

        public void EmitHeaderAndContext(string name)
        {
            EmitApplicationContexts();
            ProductDefinitionShape = Product(name).Shape;
            EmitUnitsAndContext();
        }

        private int _productContext;
        private int _definitionContext;

        /// <summary>The application/product contexts every product shares.</summary>
        public void EmitApplicationContexts()
        {
            int app = Emit("APPLICATION_CONTEXT('automotive design')");
            Emit($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2010,#{app})");
            _productContext = Emit($"PRODUCT_CONTEXT('',#{app},'mechanical')");
            _definitionContext = Emit($"PRODUCT_DEFINITION_CONTEXT('part definition',#{app},'design')");
        }

        /// <summary>One PRODUCT and its definition chain: the entity an assembly
        /// occurrence points at, and the anchor a shape representation hangs from.</summary>
        public (int Definition, int Shape) Product(string name)
        {
            string text = Text(name);
            int product = Emit($"PRODUCT('{text}','{text}','',(#{_productContext}))");
            Emit($"PRODUCT_RELATED_PRODUCT_CATEGORY('part','',(#{product}))");
            int formation = Emit($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
            int definition = Emit($"PRODUCT_DEFINITION('design','',#{formation},#{_definitionContext})");
            return (definition, Emit($"PRODUCT_DEFINITION_SHAPE('','',#{definition})"));
        }

        public void EmitUnitsAndContext()
        {
            int length = Emit("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
            int angle = Emit("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
            int solidAngle = Emit("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
            int uncertainty = Emit(
                $"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-6),#{length},'distance accuracy','')");
            GeometricContext = Emit(
                "(GEOMETRIC_REPRESENTATION_CONTEXT(3)" +
                $"GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{uncertainty}))" +
                $"GLOBAL_UNIT_ASSIGNED_CONTEXT((#{length},#{angle},#{solidAngle}))" +
                "REPRESENTATION_CONTEXT('',''))");
        }

        public int Shell(BrepShell shell)
        {
            var faces = shell.Faces.Select(Face).ToList();
            return Emit($"CLOSED_SHELL('',({string.Join(",", faces.Select(f => "#" + f))}))");
        }

        private int Face(BrepFace face)
        {
            var bounds = new List<int>();
            for (int i = 0; i < face.Loops.Count; i++)
            {
                int loop = Loop(face.Loops[i]);
                bounds.Add(i == 0
                    ? Emit($"FACE_OUTER_BOUND('',#{loop},.T.)")
                    : Emit($"FACE_BOUND('',#{loop},.T.)"));
            }
            int surface = Surface(face.Surface);
            string sameSense = face.IsReversed ? ".F." : ".T.";
            return Emit(
                $"ADVANCED_FACE('',({string.Join(",", bounds.Select(b => "#" + b))}),#{surface},{sameSense})");
        }

        private int Loop(BrepLoop loop)
        {
            var oriented = loop.Coedges
                .Select(c => Emit($"ORIENTED_EDGE('',*,*,#{Edge(c.Edge)},{(c.SameSense ? ".T." : ".F.")})"))
                .ToList();
            return Emit($"EDGE_LOOP('',({string.Join(",", oriented.Select(o => "#" + o))}))");
        }

        private int Edge(BrepEdge edge)
        {
            if (_edges.TryGetValue(edge, out int existing))
                return existing;
            int start = Vertex(edge.StartVertex);
            int end = Vertex(edge.EndVertex);
            int curve = Curve(edge.Curve);
            int id = Emit($"EDGE_CURVE('',#{start},#{end},#{curve},.T.)");
            _edges[edge] = id;
            return id;
        }

        private int Vertex(BrepVertex vertex)
        {
            if (_vertices.TryGetValue(vertex, out int existing))
                return existing;
            int id = Emit($"VERTEX_POINT('',#{Point(vertex.Position)})");
            _vertices[vertex] = id;
            return id;
        }

        // ---- geometry ----

        private int Surface(Surface surface) => surface switch
        {
            PlaneSurface p => Emit($"PLANE('',#{Placement(p.Origin, p.Normal, p.XDirection)})"),
            CylinderSurface c => Emit(
                $"CYLINDRICAL_SURFACE('',#{Placement(c.Origin, c.Axis, c.XDirection)},{Real(c.Radius)})"),
            SphereSurface s => Emit(
                $"SPHERICAL_SURFACE('',#{Placement(s.Center, Vector3d.UnitZ, Vector3d.UnitX)},{Real(s.Radius)})"),
            ExtrudedSurface e => Emit(
                $"SURFACE_OF_LINEAR_EXTRUSION('',#{Curve(e.Generator)}," +
                $"#{Emit($"VECTOR('',#{Direction(e.Direction.Normalized())},{Real(e.Direction.Length)})")})"),
            RevolvedSurface r => Emit(
                $"SURFACE_OF_REVOLUTION('',#{Curve(r.Generator)}," +
                $"#{Emit($"AXIS1_PLACEMENT('',#{Point(r.AxisOrigin)},#{Direction(r.AxisDirection)})")})"),
            _ => throw new NotSupportedException($"{surface.GetType().Name} cannot be exported to STEP yet."),
        };

        private int Curve(Curve3d curve)
        {
            var simplified = Simplify(curve);
            switch (simplified)
            {
                case Line3d line:
                {
                    var direction = line.End - line.Start;
                    int vector = Emit($"VECTOR('',#{Direction(direction.Normalized())},{Real(direction.Length)})");
                    return Emit($"LINE('',#{Point(line.Start)},#{vector})");
                }
                case Circle3d circle:
                    return Emit($"CIRCLE('',#{Placement(circle.Center, circle.Axis, circle.XDirection)},{Real(circle.Radius)})");
                case Ellipse3d ellipse:
                {
                    var x = ellipse.SemiAxisX.Normalized();
                    var z = ellipse.SemiAxisX.Cross(ellipse.SemiAxisY).Normalized();
                    return Emit($"ELLIPSE('',#{Placement(ellipse.Center, z, x)}," +
                                $"{Real(ellipse.SemiAxisX.Length)},{Real(ellipse.SemiAxisY.Length)})");
                }
                case NurbsCurve nurbs:
                    return BsplineCurve(nurbs.Degree, nurbs.ControlPoints, nurbs.Weights, nurbs.Knots);
                case PolylineCurve3d polyline:
                    return TryArcFit(polyline.Points, out int fittedPolyline)
                        ? fittedPolyline
                        : PolylineAsBspline(polyline.Points);
                default:
                {
                    // Anything else (RMF rails, TransformedCurve(NurbsCurve), exotic
                    // wrappers): sample to a polyline — or, with Options.ArcFitTolerance
                    // set, fit exact arcs to those samples first.
                    var samples = new Vector3d[33];
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = curve.PointAt(curve.Domain.ParameterAt(i / (double)(samples.Length - 1)));
                    return TryArcFit(samples, out int fittedSamples) ? fittedSamples : PolylineAsBspline(samples);
                }
            }
        }

        /// <summary>
        /// Opt-in: exports a biarc fit of <paramref name="points"/> instead of the degree-1
        /// sampled polyline, when <see cref="Options.ArcFitTolerance"/> is set and the fit
        /// clears it. Returns false — and costs nothing but the attempt — otherwise.
        /// </summary>
        /// <remarks>
        /// A STEP EDGE_CURVE references ONE curve, so the chain is emitted as a single
        /// degree-2 RATIONAL B-spline rather than a COMPOSITE_CURVE: consecutive rational
        /// quadratic Bézier segments joined at double interior knots ARE a degree-2 B-spline
        /// (it is exactly how <see cref="NurbsCurve.Arc"/> builds a multi-quadrant arc), and
        /// a straight piece is the same form with control points p0, (p0+p1)/2, p1 and unit
        /// weights — which reproduces the line exactly, parameterization included. That keeps
        /// the file inside the entity set <c>StepReader</c> already parses, where a composite
        /// curve would not be.
        /// </remarks>
        private bool TryArcFit(IReadOnlyList<Vector3d> points, out int id)
        {
            id = 0;
            double? tolerance = options?.ArcFitTolerance;
            if (tolerance is not > 0 || points.Count < 2)
                return false;
            if (BiArcFit.TryFitPolyline(points, tolerance.Value, out var chain) != BiArcFitStatus.Success
                || chain.MaxDeviation > tolerance.Value)
                return false;

            // A one-piece chain is an ordinary analytic curve; let the normal path emit it
            // (a full-turn circle becomes CIRCLE, a straight run becomes LINE).
            if (chain.Curves.Count == 1)
            {
                id = Curve(chain.Curves[0]);
                Adopted(chain.MaxDeviation);
                return true;
            }

            var control = new List<Vector3d>();
            var weights = new List<double>();
            foreach (var piece in chain.Curves)
            {
                if (!TryBezierTriples(piece, out var triples))
                    return false;
                foreach (var (p0, c, w, p1) in triples)
                {
                    // The junction point is contributed once, by the EARLIER piece: biarc
                    // chains reproduce shared points to round-off, and one of the two
                    // spellings has to win.
                    if (control.Count == 0)
                    {
                        control.Add(p0);
                        weights.Add(1);
                    }
                    control.Add(c);
                    weights.Add(w);
                    control.Add(p1);
                    weights.Add(1);
                }
            }

            int segments = (control.Count - 1) / 2;
            var knots = new List<double> { 0, 0, 0 };
            for (int i = 1; i < segments; i++)
            {
                knots.Add(i);
                knots.Add(i);
            }
            knots.Add(segments);
            knots.Add(segments);
            knots.Add(segments);

            id = BsplineCurve(2, control, weights, knots);
            Adopted(chain.MaxDeviation);
            return true;
        }

        private void Adopted(double deviation)
        {
            ArcFitCount++;
            ArcFitDeviation = Math.Max(ArcFitDeviation, deviation);
        }

        /// <summary>
        /// A fitted chain piece as a run of rational quadratic Bézier segments
        /// (start, control, weight, end). A <see cref="Line3d"/> becomes the unit-weight
        /// triple whose control point is the chord midpoint — exactly the line, linearly
        /// parameterized. A rational arc is ALREADY such a run: <see cref="NurbsCurve.Arc"/>
        /// emits control points P₀, C₀, P₁, C₁, P₂ … over double interior knots.
        /// </summary>
        private static bool TryBezierTriples(
            Curve3d piece, out List<(Vector3d Start, Vector3d Control, double Weight, Vector3d End)> triples)
        {
            triples = [];
            switch (piece)
            {
                case Line3d line:
                    triples.Add((line.Start, (line.Start + line.End) * 0.5, 1, line.End));
                    return true;
                case NurbsCurve { Degree: 2 } arc when arc.ControlPoints.Count % 2 == 1:
                    for (int i = 0; i + 2 < arc.ControlPoints.Count; i += 2)
                    {
                        triples.Add((
                            arc.ControlPoints[i], arc.ControlPoints[i + 1],
                            arc.Weights[i + 1], arc.ControlPoints[i + 2]));
                    }
                    return triples.Count > 0;
                default:
                    return false; // anything else stays on the sampled path
            }
        }

        private int PolylineAsBspline(IReadOnlyList<Vector3d> points)
        {
            SampledCurveCount++;
            // Degree-1 clamped B-spline: knots 0..n-1 with doubled ends.
            var knots = new List<double> { 0 };
            for (int i = 0; i < points.Count; i++)
                knots.Add(i);
            knots.Add(points.Count - 1);
            return BsplineCurve(1, points, null, knots);
        }

        private int BsplineCurve(
            int degree, IReadOnlyList<Vector3d> controlPoints, IReadOnlyList<double>? weights, IReadOnlyList<double> knots)
        {
            var pointRefs = string.Join(",", controlPoints.Select(p => "#" + Point(p)));
            var distinct = new List<double>();
            var multiplicities = new List<int>();
            foreach (double k in knots)
            {
                // Knot-multiplicity grouping in parameter space: exact-repeat detection
                // with round-off slack; knots are dimensionless, not model units.
                if (distinct.Count > 0 && Math.Abs(distinct[^1] - k) < 1e-12)
                    multiplicities[^1]++;
                else
                {
                    distinct.Add(k);
                    multiplicities.Add(1);
                }
            }
            string knotList = string.Join(",", distinct.Select(Real));
            string multList = string.Join(",", multiplicities);

            bool rational = weights is not null && weights.Any(w => Math.Abs(w - 1) > 1e-12);
            if (!rational)
            {
                return Emit(
                    $"B_SPLINE_CURVE_WITH_KNOTS('',{degree},({pointRefs}),.UNSPECIFIED.,.F.,.F.," +
                    $"({multList}),({knotList}),.UNSPECIFIED.)");
            }
            string weightList = string.Join(",", weights!.Select(Real));
            return Emit(
                "(BOUNDED_CURVE()" +
                $"B_SPLINE_CURVE({degree},({pointRefs}),.UNSPECIFIED.,.F.,.F.)" +
                $"B_SPLINE_CURVE_WITH_KNOTS(({multList}),({knotList}),.UNSPECIFIED.)" +
                "CURVE()GEOMETRIC_REPRESENTATION_ITEM()" +
                $"RATIONAL_B_SPLINE_CURVE(({weightList}))" +
                "REPRESENTATION_ITEM(''))");
        }

        /// <summary>Resolves wrapper curves to analytic forms where a rigid interpretation exists.</summary>
        private static Curve3d? Simplify(Curve3d curve) => curve switch
        {
            Line3d or Circle3d or Ellipse3d or NurbsCurve or PolylineCurve3d => curve,
            ReversedCurve r => Simplify(r.Base) switch
            {
                Line3d l => new Line3d(l.End, l.Start),
                Circle3d c => new Circle3d(c.Center, c.XDirection, -c.YDirection, c.Radius),
                Ellipse3d e => new Ellipse3d(e.Center, e.SemiAxisX, -e.SemiAxisY),
                _ => null,
            },
            TransformedCurve t => Simplify(t.Base) switch
            {
                Line3d l => new Line3d(t.Transform.TransformPoint(l.Start), t.Transform.TransformPoint(l.End)),
                Circle3d c => new Circle3d(
                    t.Transform.TransformPoint(c.Center),
                    t.Transform.TransformVector(c.XDirection),
                    t.Transform.TransformVector(c.YDirection),
                    c.Radius),
                Ellipse3d e => new Ellipse3d(
                    t.Transform.TransformPoint(e.Center),
                    t.Transform.TransformVector(e.SemiAxisX),
                    t.Transform.TransformVector(e.SemiAxisY)),
                _ => null,
            },
            CurveSegment s => Simplify(s.Base), // vertices carry the trim
            _ => null,
        };

        public string Finish()
        {
            var sb = new StringBuilder();
            sb.Append("ISO-10303-21;\nHEADER;\n");
            sb.Append("FILE_DESCRIPTION(('EngrCAD B-Rep export'),'2;1');\n");
            sb.Append($"FILE_NAME('part.step','{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}',('EngrCAD'),('EngrCAD'),'EngrCAD','EngrCAD','');\n");
            sb.Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\n");
            sb.Append("ENDSEC;\nDATA;\n");
            sb.Append(_data);
            sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
            return sb.ToString();
        }
    }
}

/// <summary>
/// One placed solid for <see cref="StepWriter.WriteAssembly"/> — the STEP-side mirror of
/// the document model's flattened part instance. Instances that share a
/// <see cref="Solid"/> (reference identity) become one PRODUCT with several
/// NEXT_ASSEMBLY_USAGE_OCCURRENCEs.
/// </summary>
/// <param name="PartName">The product's name (shared by every instance of the solid).</param>
/// <param name="OccurrenceName">This placement's name — the occurrence path reads well here.</param>
/// <param name="Solid">The geometry, in its own coordinates (never pre-posed).</param>
/// <param name="World">The rigid placement of this instance in assembly coordinates.</param>
public readonly record struct StepInstance(
    string PartName, string OccurrenceName, BrepSolid Solid, Matrix4d World);
