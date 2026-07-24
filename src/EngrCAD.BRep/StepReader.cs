using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>Result of reading a STEP file: the solids plus reader diagnostics.</summary>
public sealed class StepReadResult
{
    /// <summary>The imported solids, in entity-id order of their MANIFOLD_SOLID_BREPs.</summary>
    public IReadOnlyList<BrepSolid> Solids { get; }

    /// <summary>
    /// Skipped/unsupported entities, unit warnings, and reconstruction notes. Empty for
    /// files produced by <see cref="StepWriter"/> from round-trippable solids.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }

    internal StepReadResult(IReadOnlyList<BrepSolid> solids, IReadOnlyList<string> diagnostics)
    {
        Solids = solids;
        Diagnostics = diagnostics;
    }
}

/// <summary>
/// STEP (ISO 10303-21, AP214) import of MANIFOLD_SOLID_BREP solids — the inverse of
/// <see cref="StepWriter"/>, whose output round-trips exactly. Topology is rebuilt with
/// shared identity: one <see cref="BrepEdge"/> per EDGE_CURVE and one
/// <see cref="BrepVertex"/> per VERTEX_POINT, so manifold edge sharing survives the trip.
/// Edge parameter domains (not stored in STEP) are reconstructed exactly from the vertex
/// positions: closed-form for lines/circles/ellipses, Newton with exact derivatives for
/// B-splines. SURFACE_OF_REVOLUTION stores neither our swept angle nor generator trims,
/// so both are recovered from the face's boundary: rail arcs give the angle, and rim
/// circles re-trim the generator by bisecting its exact axial/radial profile (projection
/// or distance-minimization would carry ~1e-7 error past the 1e-9 weld tolerance).
/// Units: coordinates are read as-is with millimetres assumed; a diagnostic is emitted
/// when the file declares a different length unit. Unknown entities are skipped with a
/// diagnostic, not a crash; malformed Part 21 syntax throws <see cref="FormatException"/>.
/// </summary>
public static class StepReader
{
    public static StepReadResult ReadFile(string path) => Read(File.ReadAllText(path));

    public static StepReadResult Read(string text) => new Builder(StepParser.Parse(text)).Build();

    private sealed class Builder(StepFile file)
    {
        private readonly List<string> _diagnostics = [];
        private readonly Dictionary<int, BrepVertex> _vertices = [];
        private readonly Dictionary<int, BrepEdge> _edges = [];
        private readonly Dictionary<int, Curve3d> _curves = [];
        private readonly HashSet<string> _reportedSkips = [];

        public StepReadResult Build()
        {
            CheckUnits();

            var breps = file.Entities.Values
                .Where(e => e.Find("MANIFOLD_SOLID_BREP") is not null)
                .OrderBy(e => e.Id)
                .ToList();
            var referencedShells = breps
                .Select(e => e.Find("MANIFOLD_SOLID_BREP")!.Args[1].AsReference())
                .ToHashSet();

            // StepWriter emits every shell of a multi-shell solid but references only the
            // first from the MANIFOLD_SOLID_BREP; adopt the unreferenced ones.
            var orphanShellIds = file.Entities.Values
                .Where(e => e.Find("CLOSED_SHELL") is not null && !referencedShells.Contains(e.Id))
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .ToList();

            var solids = new List<BrepSolid>();
            for (int i = 0; i < breps.Count; i++)
            {
                var shellIds = new List<int> { breps[i].Find("MANIFOLD_SOLID_BREP")!.Args[1].AsReference() };
                if (i == 0 && orphanShellIds.Count > 0)
                {
                    shellIds.AddRange(orphanShellIds);
                    Note($"Adopted {orphanShellIds.Count} CLOSED_SHELL(s) not referenced by any " +
                         "MANIFOLD_SOLID_BREP into the first solid (multi-shell writer convention).");
                }
                BuildSolid(shellIds, solids);
            }

            if (breps.Count == 0 && orphanShellIds.Count > 0)
            {
                Note("No MANIFOLD_SOLID_BREP found; building a solid from the free CLOSED_SHELL(s).");
                BuildSolid(orphanShellIds, solids);
            }
            else if (breps.Count == 0 && orphanShellIds.Count == 0)
            {
                Note("No MANIFOLD_SOLID_BREP or CLOSED_SHELL entities found.");
            }

            return new StepReadResult(solids, _diagnostics);
        }

        private void BuildSolid(List<int> shellIds, List<BrepSolid> solids)
        {
            var shells = new List<BrepShell>();
            foreach (int shellId in shellIds)
            {
                try
                {
                    var shell = BuildShell(shellId);
                    if (shell is not null)
                        shells.Add(shell);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    Note($"Skipped shell #{shellId}: {ex.Message}");
                }
            }
            if (shells.Count > 0)
                solids.Add(new BrepSolid(shells));
        }

        private BrepShell? BuildShell(int id)
        {
            var entity = file.Entity(id);
            var record = entity.Find("CLOSED_SHELL") ?? entity.Find("OPEN_SHELL");
            if (record is null)
            {
                NoteSkip(entity, "shell");
                return null;
            }
            if (record.Keyword.Equals("OPEN_SHELL", StringComparison.OrdinalIgnoreCase))
                Note($"Shell #{id} is an OPEN_SHELL; reading it as a shell of the solid anyway.");

            var faces = new List<BrepFace>();
            foreach (var faceRef in record.Args[1].AsList())
            {
                int faceId = faceRef.AsReference();
                try
                {
                    var face = BuildFace(faceId);
                    if (face is not null)
                        faces.Add(face);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    Note($"Skipped face #{faceId}: {ex.Message}");
                }
            }
            if (faces.Count == 0)
            {
                Note($"Shell #{id} has no supported faces; dropped.");
                return null;
            }
            return new BrepShell(faces);
        }

        private BrepFace? BuildFace(int id)
        {
            var entity = file.Entity(id);
            var record = entity.Find("ADVANCED_FACE") ?? entity.Find("FACE_SURFACE");
            if (record is null)
            {
                NoteSkip(entity, "face");
                return null;
            }
            bool sameSense = record.Args[3].AsBool();
            bool isReversed = !sameSense;

            // Collect (edge, sense) descriptors first; BrepCoedges are created only after
            // the surface succeeds, so a skipped face leaves no stale uses on shared edges.
            var bounds = new List<(bool IsOuter, List<(BrepEdge Edge, bool Sense)> Pairs)>();
            foreach (var boundRef in record.Args[1].AsList())
            {
                var boundEntity = file.Entity(boundRef.AsReference());
                var boundRecord = boundEntity.Find("FACE_OUTER_BOUND") ?? boundEntity.Find("FACE_BOUND");
                if (boundRecord is null)
                {
                    NoteSkip(boundEntity, "face bound");
                    continue;
                }
                bool isOuter = boundRecord.Keyword.Equals("FACE_OUTER_BOUND", StringComparison.OrdinalIgnoreCase);
                bool orientation = boundRecord.Args[2].AsBool();

                var loopEntity = file.Entity(boundRecord.Args[1].AsReference());
                var loopRecord = loopEntity.Find("EDGE_LOOP");
                if (loopRecord is null)
                {
                    NoteSkip(loopEntity, "loop");
                    continue;
                }
                var pairs = new List<(BrepEdge, bool)>();
                foreach (var orientedRef in loopRecord.Args[1].AsList())
                {
                    var oriented = file.Entity(orientedRef.AsReference());
                    var orientedRecord = oriented.Find("ORIENTED_EDGE")
                        ?? throw new NotSupportedException($"#{oriented.Id} {oriented.Keyword} is not an ORIENTED_EDGE.");
                    var edge = Edge(orientedRecord.Args[3].AsReference());
                    pairs.Add((edge, orientedRecord.Args[4].AsBool()));
                }
                if (pairs.Count == 0)
                    continue;
                if (!orientation)
                {
                    pairs.Reverse();
                    pairs = pairs.Select(p => (p.Item1, !p.Item2)).ToList();
                }
                bounds.Add((isOuter, pairs));
            }
            if (bounds.Count == 0)
            {
                Note($"Face #{id} has no supported bounds; dropped.");
                return null;
            }
            // Outer bound first (stable within each group), matching our loop convention.
            bounds = [.. bounds.Where(b => b.IsOuter), .. bounds.Where(b => !b.IsOuter)];

            var surface = BuildSurface(record.Args[2].AsReference(), bounds, isReversed);
            if (surface is null)
                return null;

            var loops = bounds
                .Select(b => new BrepLoop(b.Pairs.Select(p => new BrepCoedge(p.Edge, p.Sense)).ToList()))
                .ToList();
            return new BrepFace(surface, loops, isReversed);
        }

        // ---- edges and vertices ----

        private BrepEdge Edge(int id)
        {
            if (_edges.TryGetValue(id, out var existing))
                return existing;
            var entity = file.Entity(id);
            var record = entity.Find("EDGE_CURVE")
                ?? throw new NotSupportedException($"#{id} {entity.Keyword} is not an EDGE_CURVE.");
            var start = Vertex(record.Args[1].AsReference());
            var end = Vertex(record.Args[2].AsReference());
            var curve = Curve(record.Args[3].AsReference());
            bool sameSense = record.Args[4].AsBool();

            BrepEdge edge;
            if (sameSense)
            {
                var (trimmed, domain) = TrimEdgeCurve(curve, start.Position, end.Position, ReferenceEquals(start, end));
                edge = new BrepEdge(trimmed, domain, start, end);
            }
            else
            {
                // Curve runs end→start; trim in curve direction, then walk it backwards.
                var (trimmed, domain) = TrimEdgeCurve(curve, end.Position, start.Position, ReferenceEquals(start, end));
                if (domain != trimmed.Domain)
                    trimmed = new CurveSegment(trimmed, domain.Start, domain.End);
                edge = new BrepEdge(trimmed.Reversed(), trimmed.Domain, start, end);
            }
            _edges[id] = edge;
            return edge;
        }

        private BrepVertex Vertex(int id)
        {
            if (_vertices.TryGetValue(id, out var existing))
                return existing;
            var entity = file.Entity(id);
            var record = entity.Find("VERTEX_POINT")
                ?? throw new NotSupportedException($"#{id} {entity.Keyword} is not a VERTEX_POINT.");
            var vertex = new BrepVertex(Point(record.Args[1].AsReference()));
            _vertices[id] = vertex;
            return vertex;
        }

        /// <summary>
        /// Reconstructs the edge's parameter interval from its vertex positions. STEP does
        /// not store edge domains: lines are rebuilt directly between the vertices,
        /// circle/ellipse arcs get exact phase angles, and B-spline trims are solved by
        /// Newton against the exact curve (a full-domain match is detected positionally
        /// first, which covers every untrimmed edge).
        /// </summary>
        private (Curve3d Curve, Interval Domain) TrimEdgeCurve(
            Curve3d curve, in Vector3d startPosition, in Vector3d endPosition, bool isClosed)
        {
            const double weld = 1e-9;
            switch (curve)
            {
                case Line3d:
                    return (new Line3d(startPosition, endPosition), Interval.Unit);

                case Circle3d circle:
                {
                    if (isClosed)
                    {
                        if (circle.PointAt(0).DistanceTo(startPosition) < weld)
                            return (circle, new Interval(0, 2 * Math.PI));
                        double phase = CirclePhase(circle, startPosition);
                        return (circle, new Interval(phase, phase + 2 * Math.PI));
                    }
                    double t0 = CirclePhase(circle, startPosition);
                    double t1 = CirclePhase(circle, endPosition);
                    double sweep = t1 - t0;
                    if (sweep <= 1e-12)
                        sweep += 2 * Math.PI;
                    return (circle, new Interval(t0, t0 + sweep));
                }

                case Ellipse3d ellipse:
                {
                    if (isClosed)
                    {
                        if (ellipse.PointAt(0).DistanceTo(startPosition) < weld)
                            return (ellipse, new Interval(0, 2 * Math.PI));
                        double phase = EllipsePhase(ellipse, startPosition);
                        return (ellipse, new Interval(phase, phase + 2 * Math.PI));
                    }
                    double t0 = EllipsePhase(ellipse, startPosition);
                    double t1 = EllipsePhase(ellipse, endPosition);
                    double sweep = t1 - t0;
                    if (sweep <= 1e-12)
                        sweep += 2 * Math.PI;
                    return (ellipse, new Interval(t0, t0 + sweep));
                }

                default:
                {
                    var domain = curve.Domain;
                    bool startMatches = curve.PointAt(domain.Start).DistanceTo(startPosition) < weld;
                    if (isClosed)
                    {
                        if (startMatches)
                            return (curve, domain);
                        double seam = SolvePointOnCurve(curve, startPosition);
                        Note("Closed edge seam vertex is off the curve start; wrapping a re-seamed segment.");
                        return (new CurveSegment(curve, seam, seam + domain.Length), Interval.Unit);
                    }
                    bool endMatches = curve.PointAt(domain.End).DistanceTo(endPosition) < weld;
                    if (startMatches && endMatches)
                        return (curve, domain);
                    double t0 = startMatches ? domain.Start : SolvePointOnCurve(curve, startPosition);
                    double t1 = endMatches ? domain.End : SolvePointOnCurve(curve, endPosition);
                    if (t1 > t0)
                        return (curve, new Interval(t0, t1));
                    // Parameters run backwards relative to the curve: map through a segment.
                    return (new CurveSegment(curve, t0, t1), Interval.Unit);
                }
            }
        }

        // ---- curves ----

        private Curve3d Curve(int id)
        {
            if (_curves.TryGetValue(id, out var existing))
                return existing;
            var entity = file.Entity(id);
            Curve3d curve;
            if (entity.Find("B_SPLINE_CURVE") is not null || entity.Find("B_SPLINE_CURVE_WITH_KNOTS") is not null)
            {
                curve = BsplineCurve(entity);
            }
            else
            {
                var record = entity.Records[0];
                curve = record.Keyword.ToUpperInvariant() switch
                {
                    "LINE" => Line(record),
                    "CIRCLE" => Circle(record),
                    "ELLIPSE" => Ellipse(record),
                    _ => throw new NotSupportedException($"curve type {entity.Keyword} (#{id}) is not supported"),
                };
            }
            _curves[id] = curve;
            return curve;
        }

        private Line3d Line(StepRecord record)
        {
            var start = Point(record.Args[1].AsReference());
            var direction = VectorOf(record.Args[2].AsReference());
            return new Line3d(start, start + direction);
        }

        private Circle3d Circle(StepRecord record)
        {
            var (origin, z, x) = Axis2(record.Args[1].AsReference());
            double radius = record.Args[2].AsNumber();
            return new Circle3d(origin, x, z.Cross(x), radius);
        }

        private Ellipse3d Ellipse(StepRecord record)
        {
            var (origin, z, x) = Axis2(record.Args[1].AsReference());
            double a = record.Args[2].AsNumber();
            double b = record.Args[3].AsNumber();
            return new Ellipse3d(origin, x * a, z.Cross(x) * b);
        }

        private NurbsCurve BsplineCurve(StepEntity entity)
        {
            int degree;
            IReadOnlyList<StepValue> pointRefs, multiplicities, knotValues;
            IReadOnlyList<double>? weights = null;
            if (!entity.IsComplex)
            {
                var record = entity.Find("B_SPLINE_CURVE_WITH_KNOTS")
                    ?? throw new NotSupportedException($"B-spline curve #{entity.Id} lacks knots.");
                degree = record.Args[1].AsInt();
                pointRefs = record.Args[2].AsList();
                multiplicities = record.Args[6].AsList();
                knotValues = record.Args[7].AsList();
            }
            else
            {
                // Complex instance: B_SPLINE_CURVE carries degree + control points,
                // B_SPLINE_CURVE_WITH_KNOTS the knots, RATIONAL_B_SPLINE_CURVE the weights.
                var baseRecord = entity.Find("B_SPLINE_CURVE")
                    ?? throw new NotSupportedException($"complex B-spline curve #{entity.Id} lacks B_SPLINE_CURVE.");
                var knotRecord = entity.Find("B_SPLINE_CURVE_WITH_KNOTS")
                    ?? throw new NotSupportedException($"complex B-spline curve #{entity.Id} lacks knots.");
                degree = baseRecord.Args[0].AsInt();
                pointRefs = baseRecord.Args[1].AsList();
                multiplicities = knotRecord.Args[0].AsList();
                knotValues = knotRecord.Args[1].AsList();
                if (entity.Find("RATIONAL_B_SPLINE_CURVE") is { } rational)
                    weights = rational.Args[0].AsList().Select(w => w.AsNumber()).ToList();
            }
            var controlPoints = pointRefs.Select(p => Point(p.AsReference())).ToList();
            var knots = ExpandKnots(multiplicities, knotValues);
            return new NurbsCurve(degree, controlPoints, weights, knots);
        }

        private static List<double> ExpandKnots(
            IReadOnlyList<StepValue> multiplicities, IReadOnlyList<StepValue> knotValues)
        {
            if (multiplicities.Count != knotValues.Count)
                throw new FormatException("Knot multiplicities and values differ in count.");
            var knots = new List<double>();
            for (int i = 0; i < knotValues.Count; i++)
            {
                double knot = knotValues[i].AsNumber();
                int multiplicity = multiplicities[i].AsInt();
                for (int k = 0; k < multiplicity; k++)
                    knots.Add(knot);
            }
            return knots;
        }

        // ---- surfaces ----

        private Surface? BuildSurface(
            int id, List<(bool IsOuter, List<(BrepEdge Edge, bool Sense)> Pairs)> bounds, bool isReversed)
        {
            var entity = file.Entity(id);
            if (entity.Find("B_SPLINE_SURFACE") is not null || entity.Find("B_SPLINE_SURFACE_WITH_KNOTS") is not null)
                return BsplineSurface(entity);

            var record = entity.Records[0];
            switch (record.Keyword.ToUpperInvariant())
            {
                case "PLANE":
                {
                    var (origin, z, x) = Axis2(record.Args[1].AsReference());
                    return new PlaneSurface(origin, x, z.Cross(x));
                }
                case "CYLINDRICAL_SURFACE":
                {
                    var (origin, z, x) = Axis2(record.Args[1].AsReference());
                    return new CylinderSurface(origin, x, z.Cross(x), record.Args[2].AsNumber());
                }
                case "SPHERICAL_SURFACE":
                {
                    var (origin, _, _) = Axis2(record.Args[1].AsReference());
                    return new SphereSurface(origin, record.Args[2].AsNumber());
                }
                case "SURFACE_OF_LINEAR_EXTRUSION":
                {
                    var generator = Curve(record.Args[1].AsReference());
                    var direction = VectorOf(record.Args[2].AsReference());
                    return new ExtrudedSurface(generator, direction);
                }
                case "SURFACE_OF_REVOLUTION":
                {
                    var generator = Curve(record.Args[1].AsReference());
                    var (origin, direction) = Axis1(record.Args[2].AsReference());
                    return RecoverRevolvedSurface(generator, origin, direction, bounds, isReversed);
                }
                default:
                    NoteSkip(entity, "surface");
                    return null;
            }
        }

        private NurbsSurface BsplineSurface(StepEntity entity)
        {
            int degreeU, degreeV;
            IReadOnlyList<StepValue> grid, uMults, vMults, uKnots, vKnots;
            IReadOnlyList<StepValue>? weightGrid = null;
            if (!entity.IsComplex)
            {
                var record = entity.Find("B_SPLINE_SURFACE_WITH_KNOTS")
                    ?? throw new NotSupportedException($"B-spline surface #{entity.Id} lacks knots.");
                degreeU = record.Args[1].AsInt();
                degreeV = record.Args[2].AsInt();
                grid = record.Args[3].AsList();
                uMults = record.Args[8].AsList();
                vMults = record.Args[9].AsList();
                uKnots = record.Args[10].AsList();
                vKnots = record.Args[11].AsList();
            }
            else
            {
                var baseRecord = entity.Find("B_SPLINE_SURFACE")
                    ?? throw new NotSupportedException($"complex B-spline surface #{entity.Id} lacks B_SPLINE_SURFACE.");
                var knotRecord = entity.Find("B_SPLINE_SURFACE_WITH_KNOTS")
                    ?? throw new NotSupportedException($"complex B-spline surface #{entity.Id} lacks knots.");
                degreeU = baseRecord.Args[0].AsInt();
                degreeV = baseRecord.Args[1].AsInt();
                grid = baseRecord.Args[2].AsList();
                uMults = knotRecord.Args[0].AsList();
                vMults = knotRecord.Args[1].AsList();
                uKnots = knotRecord.Args[2].AsList();
                vKnots = knotRecord.Args[3].AsList();
                if (entity.Find("RATIONAL_B_SPLINE_SURFACE") is { } rational)
                    weightGrid = rational.Args[0].AsList();
            }

            int countU = grid.Count;
            int countV = countU > 0 ? grid[0].AsList().Count : 0;
            var controlPoints = new Vector3d[countU, countV];
            for (int i = 0; i < countU; i++)
            {
                var row = grid[i].AsList();
                if (row.Count != countV)
                    throw new FormatException("Ragged B-spline surface control grid.");
                for (int j = 0; j < countV; j++)
                    controlPoints[i, j] = Point(row[j].AsReference());
            }
            double[,]? weights = null;
            if (weightGrid is not null)
            {
                weights = new double[countU, countV];
                for (int i = 0; i < countU; i++)
                {
                    var row = weightGrid[i].AsList();
                    for (int j = 0; j < countV; j++)
                        weights[i, j] = row[j].AsNumber();
                }
            }
            return new NurbsSurface(
                degreeU, degreeV, controlPoints, weights,
                ExpandKnots(uMults, uKnots), ExpandKnots(vMults, vKnots));
        }

        /// <summary>
        /// STEP's SURFACE_OF_REVOLUTION is a full, untrimmed revolution, but our
        /// <see cref="RevolvedSurface"/> carries the swept angle and a trimmed generator,
        /// and tessellation is domain-driven — so both are recovered from the face
        /// boundary. Open circular arcs centered on the axis (partial-revolve rails) give
        /// the swept angle; closed rim circles re-trim the generator: each rim's
        /// (radius, axial) profile coordinates are solved against the exact generator by
        /// bisection to machine precision. A single-rim (pole-bounded) band takes its
        /// free end from whichever generator end touches the axis, falling back to the
        /// rim coedge's sense under the band conventions when both ends are poles.
        /// </summary>
        private Surface RecoverRevolvedSurface(
            Curve3d generator, in Vector3d axisOrigin, in Vector3d axisDirection,
            List<(bool IsOuter, List<(BrepEdge Edge, bool Sense)> Pairs)> bounds, bool isReversed)
        {
            var origin = axisOrigin;
            var axis = axisDirection.Normalized();

            (double Radius, double Axial) ProfileOf(Vector3d p)
            {
                var d = p - origin;
                double axial = d.Dot(axis);
                return ((d - axis * axial).Length, axial);
            }

            double? angle = null;
            var rims = new List<(double V, bool Sense)>();
            foreach (var (edge, sense) in bounds.SelectMany(b => b.Pairs))
            {
                if (edge.Curve is not Circle3d circle)
                    continue;
                var (centerRadius, centerAxial) = ProfileOf(circle.Center);
                if (centerRadius > Math.Max(1e-6, 1e-9 * circle.Radius) ||
                    Math.Abs(circle.Axis.Normalized().Dot(axis)) < 1 - 1e-6)
                    continue; // not centered on / perpendicular to the revolve axis

                if (edge.IsClosedEdge)
                {
                    rims.Add((SolveGeneratorParameter(generator, circle.Radius, centerAxial, ProfileOf), sense));
                }
                else
                {
                    double sweep = edge.Domain.Length;
                    if (angle is { } known && Math.Abs(known - sweep) > 1e-9)
                        Note($"Inconsistent rail sweeps on a surface of revolution ({known} vs {sweep}).");
                    angle ??= sweep;
                }
            }

            var trimmed = generator;
            var domain = generator.Domain;
            double parameterTolerance = 1e-9 * Math.Max(1, domain.Length);
            bool NearParameter(double a, double b) => Math.Abs(a - b) <= parameterTolerance;

            if (angle is null && rims.Count >= 2)
            {
                double v0 = rims.Min(r => r.V);
                double v1 = rims.Max(r => r.V);
                if (!(NearParameter(v0, domain.Start) && NearParameter(v1, domain.End)))
                    trimmed = new CurveSegment(generator, v0, v1);
            }
            else if (angle is null && rims.Count == 1)
            {
                double v = rims[0].V;
                if (!NearParameter(v, domain.Start) && !NearParameter(v, domain.End))
                {
                    bool startOnAxis = ProfileOf(generator.PointAt(domain.Start)).Radius <= 1e-9;
                    bool endOnAxis = ProfileOf(generator.PointAt(domain.End)).Radius <= 1e-9;
                    if (endOnAxis && !startOnAxis)
                    {
                        trimmed = new CurveSegment(generator, v, domain.End);
                    }
                    else if (startOnAxis && !endOnAxis)
                    {
                        trimmed = new CurveSegment(generator, domain.Start, v);
                    }
                    else
                    {
                        // Pole-to-pole generator: the rim coedge's sense says which side
                        // this band covers (start-junction loops run sameSense under our
                        // outward-band convention; reversed faces flip it).
                        bool rimAtStart = rims[0].Sense != isReversed;
                        trimmed = rimAtStart
                            ? new CurveSegment(generator, v, domain.End)
                            : new CurveSegment(generator, domain.Start, v);
                    }
                }
            }

            return new RevolvedSurface(trimmed, origin, axis, angle ?? 2 * Math.PI);
        }

        /// <summary>
        /// Generator parameter whose revolved point traces the rim circle: solves the
        /// profile-space match (radius, axial) by bisection on the exact generator — a
        /// root solve, not a distance minimization (which stalls near √ε and would shift
        /// cut rings past weld tolerance). Candidates come from sign changes of the
        /// axial residual (or the radial residual for locally axial-constant generators)
        /// plus the domain ends; the best profile match wins.
        /// </summary>
        private double SolveGeneratorParameter(
            Curve3d generator, double rimRadius, double rimAxial,
            Func<Vector3d, (double Radius, double Axial)> profileOf)
        {
            var domain = generator.Domain;
            double AxialResidual(double v) => profileOf(generator.PointAt(v)).Axial - rimAxial;
            double RadialResidual(double v) => profileOf(generator.PointAt(v)).Radius - rimRadius;
            double ErrorAt(double v)
            {
                var (radius, axial) = profileOf(generator.PointAt(v));
                double dr = radius - rimRadius, dz = axial - rimAxial;
                return dr * dr + dz * dz;
            }

            const int samples = 512;
            var candidates = new List<double> { domain.Start, domain.End };
            AddRootCandidates(AxialResidual);
            AddRootCandidates(RadialResidual);

            void AddRootCandidates(Func<double, double> residual)
            {
                double previousParameter = domain.Start;
                double previousValue = residual(previousParameter);
                for (int i = 1; i <= samples; i++)
                {
                    double parameter = domain.ParameterAt((double)i / samples);
                    double value = residual(parameter);
                    if (previousValue == 0)
                    {
                        candidates.Add(previousParameter);
                    }
                    else if (value * previousValue < 0)
                    {
                        double lo = previousParameter, hi = parameter, fLo = previousValue;
                        for (int k = 0; k < 100; k++)
                        {
                            double mid = 0.5 * (lo + hi);
                            double fMid = residual(mid);
                            if (fLo * fMid <= 0)
                                hi = mid;
                            else
                            {
                                lo = mid;
                                fLo = fMid;
                            }
                        }
                        candidates.Add(0.5 * (lo + hi));
                    }
                    previousParameter = parameter;
                    previousValue = value;
                }
            }

            double best = candidates[0];
            double bestError = double.PositiveInfinity;
            foreach (double candidate in candidates)
            {
                double error = ErrorAt(candidate);
                if (error < bestError)
                {
                    bestError = error;
                    best = candidate;
                }
            }
            if (bestError > 1e-12) // (1e-6)²
                Note($"Rim circle sits {Math.Sqrt(bestError):G3} from the revolution generator; trim is approximate.");
            return best;
        }

        /// <summary>
        /// Parameter of a point lying on the curve: dense seeding plus Newton with the
        /// exact B-spline derivative (golden-section-free — comparison-based minimization
        /// stalls near √ε, which is not weldable accuracy).
        /// </summary>
        private double SolvePointOnCurve(Curve3d curve, in Vector3d point)
        {
            var domain = curve.Domain;
            int seedCount = curve is NurbsCurve seeded ? Math.Max(64, 8 * seeded.ControlPoints.Count) : 256;
            double best = domain.Start;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i <= seedCount; i++)
            {
                double t = domain.ParameterAt((double)i / seedCount);
                double distance = curve.PointAt(t).DistanceSquaredTo(point);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = t;
                }
            }

            if (curve is NurbsCurve nurbs)
            {
                double t = best;
                for (int i = 0; i < 50; i++)
                {
                    var residual = nurbs.PointAt(t) - point;
                    if (residual.Length < 1e-13)
                        break;
                    var derivative = nurbs.DerivativeAt(t);
                    double denominator = derivative.Dot(derivative);
                    if (denominator < 1e-30)
                        break;
                    double step = -residual.Dot(derivative) / denominator;
                    t = domain.Clamp(t + step);
                    if (Math.Abs(step) < 1e-16 * Math.Max(1, domain.Length))
                        break;
                }
                best = t;
            }
            else
            {
                // Derivative-free: bisect on the sign of d/dt|C−p|² estimated from the
                // exact curve at interval endpoints (localizes the projection root).
                double h = domain.Length / seedCount;
                double lo = Math.Max(domain.Start, best - h);
                double hi = Math.Min(domain.End, best + h);
                for (int i = 0; i < 100; i++)
                {
                    double m1 = lo + (hi - lo) / 3;
                    double m2 = hi - (hi - lo) / 3;
                    if (curve.PointAt(m1).DistanceSquaredTo(point) < curve.PointAt(m2).DistanceSquaredTo(point))
                        hi = m2;
                    else
                        lo = m1;
                }
                best = 0.5 * (lo + hi);
            }

            double finalDistance = curve.PointAt(best).DistanceTo(point);
            if (finalDistance > 1e-6)
                Note($"Edge vertex sits {finalDistance:G3} from its curve; trim parameter is approximate.");
            return best;
        }

        // ---- shared geometry helpers ----

        private Vector3d Point(int id)
        {
            var entity = file.Entity(id);
            var record = entity.Find("CARTESIAN_POINT")
                ?? throw new NotSupportedException($"#{id} {entity.Keyword} is not a CARTESIAN_POINT.");
            var coordinates = record.Args[1].AsList();
            return new Vector3d(
                coordinates.Count > 0 ? coordinates[0].AsNumber() : 0,
                coordinates.Count > 1 ? coordinates[1].AsNumber() : 0,
                coordinates.Count > 2 ? coordinates[2].AsNumber() : 0);
        }

        private Vector3d Direction(int id)
        {
            var entity = file.Entity(id);
            var record = entity.Find("DIRECTION")
                ?? throw new NotSupportedException($"#{id} {entity.Keyword} is not a DIRECTION.");
            var components = record.Args[1].AsList();
            return new Vector3d(
                components.Count > 0 ? components[0].AsNumber() : 0,
                components.Count > 1 ? components[1].AsNumber() : 0,
                components.Count > 2 ? components[2].AsNumber() : 0);
        }

        private Vector3d VectorOf(int id)
        {
            var entity = file.Entity(id);
            var record = entity.Find("VECTOR")
                ?? throw new NotSupportedException($"#{id} {entity.Keyword} is not a VECTOR.");
            return Direction(record.Args[1].AsReference()) * record.Args[2].AsNumber();
        }

        private (Vector3d Origin, Vector3d Z, Vector3d X) Axis2(int id)
        {
            var entity = file.Entity(id);
            var record = entity.Find("AXIS2_PLACEMENT_3D")
                ?? throw new NotSupportedException($"#{id} {entity.Keyword} is not an AXIS2_PLACEMENT_3D.");
            var origin = Point(record.Args[1].AsReference());
            var z = (record.Args[2].IsNull ? Vector3d.UnitZ : Direction(record.Args[2].AsReference())).Normalized();
            var xRaw = record.Args.Count > 3 && !record.Args[3].IsNull
                ? Direction(record.Args[3].AsReference())
                : z.ArbitraryPerpendicular(Tolerance.Default);
            var x = (xRaw - z * z.Dot(xRaw)).Normalized();
            return (origin, z, x);
        }

        private (Vector3d Origin, Vector3d Direction) Axis1(int id)
        {
            var entity = file.Entity(id);
            var record = entity.Find("AXIS1_PLACEMENT")
                ?? throw new NotSupportedException($"#{id} {entity.Keyword} is not an AXIS1_PLACEMENT.");
            var origin = Point(record.Args[1].AsReference());
            var direction = record.Args[2].IsNull ? Vector3d.UnitZ : Direction(record.Args[2].AsReference());
            return (origin, direction.Normalized());
        }

        private static double CirclePhase(Circle3d circle, in Vector3d point)
        {
            var d = point - circle.Center;
            double phase = Math.Atan2(d.Dot(circle.YDirection), d.Dot(circle.XDirection));
            return phase < 0 ? phase + 2 * Math.PI : phase;
        }

        private static double EllipsePhase(Ellipse3d ellipse, in Vector3d point)
        {
            var d = point - ellipse.Center;
            double cos = d.Dot(ellipse.SemiAxisX) / ellipse.SemiAxisX.LengthSquared;
            double sin = d.Dot(ellipse.SemiAxisY) / ellipse.SemiAxisY.LengthSquared;
            double phase = Math.Atan2(sin, cos);
            return phase < 0 ? phase + 2 * Math.PI : phase;
        }

        // ---- diagnostics ----

        private void CheckUnits()
        {
            foreach (var entity in file.Entities.Values)
            {
                bool isLengthUnit = entity.Find("LENGTH_UNIT") is not null;
                if (!isLengthUnit)
                    continue;
                if (entity.Find("SI_UNIT") is { } si)
                {
                    string prefix = si.Args[0].Kind == StepValueKind.Enumeration ? si.Args[0].Text : "";
                    string unit = si.Args.Count > 1 && si.Args[1].Kind == StepValueKind.Enumeration ? si.Args[1].Text : "";
                    if (!(prefix.Equals("MILLI", StringComparison.OrdinalIgnoreCase) &&
                          unit.Equals("METRE", StringComparison.OrdinalIgnoreCase)))
                    {
                        Note($"Length unit is {prefix} {unit}; coordinates were read unscaled (millimetres assumed).");
                    }
                }
                else
                {
                    Note($"Non-SI length unit #{entity.Id} {entity.Keyword}; coordinates were read unscaled (millimetres assumed).");
                }
            }
        }

        private static bool IsRecoverable(Exception ex) =>
            ex is FormatException or NotSupportedException or ArgumentException or InvalidOperationException;

        private void Note(string message)
        {
            if (!_diagnostics.Contains(message))
                _diagnostics.Add(message);
        }

        private void NoteSkip(StepEntity entity, string role)
        {
            if (_reportedSkips.Add($"{role}:{entity.Keyword}"))
                Note($"Skipped unsupported {role} entity {entity.Keyword} (first at #{entity.Id}).");
        }
    }
}
