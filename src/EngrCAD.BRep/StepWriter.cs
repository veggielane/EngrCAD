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
/// </summary>
public static class StepWriter
{
    public static void WriteFile(BrepSolid solid, string path, string name = "EngrCAD part")
    {
        File.WriteAllText(path, Write(solid, name));
    }

    public static string Write(BrepSolid solid, string name = "EngrCAD part")
    {
        var w = new Writer();
        w.EmitHeaderAndContext(name);
        var shells = solid.Shells.Select(s => w.Shell(s)).ToList();
        int brep = w.Emit($"MANIFOLD_SOLID_BREP('{name}',#{shells[0]})");
        int representation = w.Emit(
            $"ADVANCED_BREP_SHAPE_REPRESENTATION('',(#{brep}),#{w.GeometricContext})");
        w.Emit($"SHAPE_DEFINITION_REPRESENTATION(#{w.ProductDefinitionShape},#{representation})");
        return w.Finish();
    }

    private sealed class Writer
    {
        private readonly StringBuilder _data = new();
        private int _next = 1;
        private readonly Dictionary<BrepVertex, int> _vertices = [];
        private readonly Dictionary<BrepEdge, int> _edges = [];

        public int GeometricContext { get; private set; }
        public int ProductDefinitionShape { get; private set; }

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

        public void EmitHeaderAndContext(string name)
        {
            int app = Emit("APPLICATION_CONTEXT('automotive design')");
            Emit($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2010,#{app})");
            int productContext = Emit($"PRODUCT_CONTEXT('',#{app},'mechanical')");
            int definitionContext = Emit($"PRODUCT_DEFINITION_CONTEXT('part definition',#{app},'design')");
            int product = Emit($"PRODUCT('{name}','{name}','',(#{productContext}))");
            Emit($"PRODUCT_RELATED_PRODUCT_CATEGORY('part','',(#{product}))");
            int formation = Emit($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
            int definition = Emit($"PRODUCT_DEFINITION('design','',#{formation},#{definitionContext})");
            ProductDefinitionShape = Emit($"PRODUCT_DEFINITION_SHAPE('','',#{definition})");

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
                    return PolylineAsBspline(polyline.Points);
                default:
                {
                    // Anything else (RMF rails, exotic wrappers): sample to a polyline.
                    var samples = new Vector3d[33];
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = curve.PointAt(curve.Domain.ParameterAt(i / (double)(samples.Length - 1)));
                    return PolylineAsBspline(samples);
                }
            }
        }

        private int PolylineAsBspline(IReadOnlyList<Vector3d> points)
        {
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
