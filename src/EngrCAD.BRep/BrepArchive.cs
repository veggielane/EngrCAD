using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// EngrCAD's <b>native</b> B-Rep archive (<c>.ecb</c>): a versioned, human-diffable text
/// format that round-trips every curve and surface type this kernel has, including the
/// ones STEP cannot carry — <see cref="HelicalSurface"/>, <see cref="LoftedSurface"/>,
/// <see cref="SweptSurface"/>, <see cref="TwistedSurface"/>, <see cref="OffsetCurve3d"/>,
/// <see cref="SpiralArc3d"/>, <see cref="CurveSegment"/> mappings and trimmed edge domains.
/// <para><b>Why text.</b> The alternative — a compact binary — buys size on files nobody
/// has complained about, and gives up the one property this codebase's testing culture is
/// built on: a committed corpus file that <i>diffs</i>. Golden fingerprints, byte-compared
/// docs PNGs and bit-identity assertions are how kernel regressions get caught here, and a
/// format whose output can be read by eye and compared line by line joins that toolkit
/// instead of needing a decoder before anyone can look at it. Exactness is not the
/// trade-off it sounds like: .NET's round-trip <c>"R"</c> formatting is a bijection on
/// finite doubles, so a value written and read back is bit-identical.</para>
/// <para><b>Structure</b> follows the STEP writer's entity model with none of the AP214
/// ceremony: a numbered entity table where every reference is <c>#n</c>, so shared
/// topology stays shared — an edge used by two faces is written once and referenced
/// twice, and a curve shared by two edges likewise. Entities are keyed by REFERENCE
/// identity, never by structural equality, because <see cref="BrepEdge.IsClosedEdge"/> is
/// <c>ReferenceEquals(StartVertex, EndVertex)</c>: two coincident vertices and one shared
/// vertex are different solids, and a format that could not tell them apart would silently
/// change topology.</para>
/// <para>One entity per line, dependencies always defined before use (the object graph is
/// a DAG — nothing reachable from a surface leads back to an edge), so reading is a single
/// pass and a forward reference is a malformed file, reported by name.</para>
/// <para><b>Versioned from day one</b>: the first line is <c>ENGRCAD-BREP &lt;version&gt;</c>
/// and a version this build does not know is refused by name rather than parsed
/// hopefully.</para>
/// </summary>
public static class BrepArchive
{
    /// <summary>The format version this build writes and reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>The conventional file extension.</summary>
    public const string Extension = ".ecb";

    private const string Magic = "ENGRCAD-BREP";

    // ---- writing ----

    /// <summary>Writes one solid.</summary>
    public static string Write(BrepSolid solid, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(solid);
        return Write([solid], name);
    }

    /// <summary>Writes several solids into one archive (each becomes a root).</summary>
    public static string Write(IReadOnlyList<BrepSolid> solids, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(solids);
        if (solids.Count == 0)
            throw new ArgumentException("An EngrCAD BREP archive needs at least one solid.", nameof(solids));

        var writer = new ArchiveWriter();
        foreach (var solid in solids)
        {
            ArgumentNullException.ThrowIfNull(solid);
            writer.Solid(solid);
        }
        return writer.Finish(name);
    }

    public static void WriteFile(BrepSolid solid, string path, string? name = null) =>
        File.WriteAllText(path, Write(solid, name));

    public static void WriteFile(IReadOnlyList<BrepSolid> solids, string path, string? name = null) =>
        File.WriteAllText(path, Write(solids, name));

    // ---- reading ----

    /// <summary>Reads an archive. Throws <see cref="BrepArchiveException"/> naming the
    /// problem — a version this build does not know, an unknown keyword, a forward or
    /// dangling reference — rather than returning something half-built.</summary>
    public static BrepArchiveResult Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ArchiveReader(text).Read();
    }

    public static BrepArchiveResult ReadFile(string path) => Read(File.ReadAllText(path));

    // ---- writer ----

    private sealed class ArchiveWriter
    {
        private readonly StringBuilder _body = new();
        // Reference identity throughout. Two structurally identical Line3d objects are two
        // entities (they are two objects); one object referenced twice is one entity
        // referenced twice. That is exactly the sharing the format exists to preserve.
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly List<int> _roots = [];
        private int _next;

        public string Finish(string? name)
        {
            var text = new StringBuilder();
            text.Append(Magic).Append(' ').Append(FormatVersion).Append('\n');
            text.Append("UNITS MM\n");
            if (name is not null)
                text.Append("NAME ").Append(Quote(name)).Append('\n');
            text.Append("GENERATOR 'EngrCAD'\n");
            text.Append('\n');
            text.Append(_body);
            text.Append('\n');
            foreach (int root in _roots)
                text.Append("ROOT #").Append(root).Append('\n');
            return text.ToString();
        }

        private int Emit(object key, string body)
        {
            int id = ++_next;
            _ids[key] = id;
            _body.Append('#').Append(id).Append(" = ").Append(body).Append('\n');
            return id;
        }

        // ---- topology ----

        public void Solid(BrepSolid solid)
        {
            if (_ids.TryGetValue(solid, out int existing))
            {
                _roots.Add(existing);
                return;
            }
            var shells = solid.Shells.Select(Shell).ToList();
            int id = Emit(solid, $"Solid({Refs(shells)})");
            _roots.Add(id);
        }

        private int Shell(BrepShell shell)
        {
            if (_ids.TryGetValue(shell, out int existing))
                return existing;
            var faces = shell.Faces.Select(Face).ToList();
            return Emit(shell, $"Shell({Refs(faces)})");
        }

        private int Face(BrepFace face)
        {
            if (_ids.TryGetValue(face, out int existing))
                return existing;
            int surface = Surface(face.Surface);
            var loops = face.Loops.Select(Loop).ToList();
            return Emit(face, $"Face(#{surface}, {Refs(loops)}, {Bool(face.IsReversed)})");
        }

        private int Loop(BrepLoop loop)
        {
            if (_ids.TryGetValue(loop, out int existing))
                return existing;
            var coedges = loop.Coedges.Select(Coedge).ToList();
            return Emit(loop, $"Loop({Refs(coedges)})");
        }

        private int Coedge(BrepCoedge coedge)
        {
            if (_ids.TryGetValue(coedge, out int existing))
                return existing;
            int edge = Edge(coedge.Edge);
            return Emit(coedge, $"Coedge(#{edge}, {Bool(coedge.SameSense)})");
        }

        private int Edge(BrepEdge edge)
        {
            if (_ids.TryGetValue(edge, out int existing))
                return existing;
            int curve = Curve(edge.Curve);
            int start = Vertex(edge.StartVertex);
            // A closed edge shares ONE vertex object; the table hands back the same id,
            // and the reader's table then hands back the same object, so IsClosedEdge
            // survives the round trip. Nothing here compares positions.
            int end = Vertex(edge.EndVertex);
            return Emit(edge, $"Edge(#{curve}, {Range(edge.Domain)}, #{start}, #{end})");
        }

        private int Vertex(BrepVertex vertex)
        {
            if (_ids.TryGetValue(vertex, out int existing))
                return existing;
            return Emit(vertex, $"Vertex({Vec(vertex.Position)})");
        }

        // ---- surfaces ----

        private int Surface(Surface surface)
        {
            if (_ids.TryGetValue(surface, out int existing))
                return existing;
            return surface switch
            {
                PlaneSurface s => Emit(s,
                    $"Plane({Vec(s.Origin)}, {Vec(s.XDirection)}, {Vec(s.YDirection)})"),
                CylinderSurface s => Emit(s,
                    $"Cylinder({Vec(s.Origin)}, {Vec(s.XDirection)}, {Vec(s.YDirection)}, {N(s.Radius)})"),
                SphereSurface s => Emit(s, $"Sphere({Vec(s.Center)}, {N(s.Radius)})"),
                NurbsSurface s => NurbsSurfaceEntity(s),
                ExtrudedSurface s => ExtrudedEntity(s),
                RevolvedSurface s => RevolvedEntity(s),
                SweptSurface s => SweptEntity(s),
                TwistedSurface s => TwistedEntity(s),
                HelicalSurface s => HelicalEntity(s),
                LoftedSurface s => LoftedEntity(s),
                _ => throw Unsupported("surface", surface),
            };
        }

        private int NurbsSurfaceEntity(NurbsSurface s)
        {
            int countU = s.ControlPoints.GetLength(0), countV = s.ControlPoints.GetLength(1);
            var points = new List<string>(countU * countV);
            var weights = new List<string>(countU * countV);
            // Row-major (u outer, v inner) — stated here because the reader has to agree
            // and a transposed control grid is a surface that still parses.
            for (int i = 0; i < countU; i++)
            {
                for (int j = 0; j < countV; j++)
                {
                    points.Add(Vec(s.ControlPoints[i, j]));
                    weights.Add(N(s.Weights[i, j]));
                }
            }
            return Emit(s,
                $"NurbsSurface({s.DegreeU}, {s.DegreeV}, {countU}, {countV}, "
                + $"{List(points)}, {List(weights)}, {Numbers(s.KnotsU)}, {Numbers(s.KnotsV)})");
        }

        private int ExtrudedEntity(ExtrudedSurface s)
        {
            int generator = Curve(s.Generator);
            return Emit(s, $"Extruded(#{generator}, {Vec(s.Direction)})");
        }

        private int RevolvedEntity(RevolvedSurface s)
        {
            int generator = Curve(s.Generator);
            return Emit(s,
                $"Revolved(#{generator}, {Vec(s.AxisOrigin)}, {Vec(s.AxisDirection)}, {N(s.Angle)})");
        }

        private int SweptEntity(SweptSurface s)
        {
            int generator = Curve(s.Generator);
            int path = Curve(s.Path);
            // FrameCount is part of the surface's identity, not a hint: every interior
            // frame is interpolated between the computed ones, so a sweep re-read at the
            // default 64 would be a DIFFERENT surface wherever the original used another
            // count.
            return Emit(s, $"Swept(#{generator}, #{path}, {Vec(s.StartX)}, {s.FrameCount})");
        }

        /// <summary>
        /// A twisted extrusion's lateral surface. The axis FRAME rides verbatim (the
        /// <c>Frame</c> form stores its axes and rebuilds through
        /// <see cref="Frame3d.FromOrthonormal"/>, so the round trip is a fixed point
        /// rather than a re-derivation), and height/twist/scale are the three numbers
        /// that make the section transform.
        /// </summary>
        private int TwistedEntity(TwistedSurface s)
        {
            int generator = Curve(s.Generator);
            return Emit(s,
                $"Twisted(#{generator}, {Frame(s.Axis)}, {N(s.Height)}, {N(s.Twist)}, {Vec2(s.ScaleTop)})");
        }

        // Two entities rather than one variadic Helical: an arc generator is a different
        // set of numbers, not extra ones, and a name that says which is easier to diff than
        // an arity that has to be counted.
        private int HelicalEntity(HelicalSurface s) => s.IsStraightGenerator
            ? Emit(s,
                $"Helical({Frame(s.Frame)}, {Vec2(s.ProfileStart)}, {Vec2(s.ProfileEnd)}, "
                + $"{N(s.Pitch)}, {Range(s.DomainU)})")
            : Emit(s,
                $"HelicalArc({Frame(s.Frame)}, {Vec2(s.ArcCenter)}, {N(s.ArcRadius)}, "
                + $"{N(s.ArcStartAngle)}, {N(s.ArcSweep)}, {N(s.Pitch)}, {Range(s.DomainU)})");

        private int LoftedEntity(LoftedSurface s)
        {
            var sections = s.Sections.Select(Curve).ToList();
            // The section parameters are always written explicitly: leaving them out makes
            // the constructor re-derive them by mean-chord sampling, which is not
            // guaranteed bit-identical to what was stored — and the constructor checks
            // parameters[0] == 0 and [^1] == 1 by EXACT equality.
            return Emit(s, $"Lofted({Refs(sections)}, {Numbers(s.SectionParameters)})");
        }

        // ---- curves ----

        private int Curve(Curve3d curve)
        {
            if (_ids.TryGetValue(curve, out int existing))
                return existing;
            return curve switch
            {
                Line3d c => Emit(c, $"Line({Vec(c.Start)}, {Vec(c.End)})"),
                Circle3d c => Emit(c,
                    $"Circle({Vec(c.Center)}, {Vec(c.XDirection)}, {Vec(c.YDirection)}, {N(c.Radius)})"),
                Ellipse3d c => Emit(c,
                    $"Ellipse({Vec(c.Center)}, {Vec(c.SemiAxisX)}, {Vec(c.SemiAxisY)})"),
                Parabola3d c => Emit(c,
                    $"Parabola({Vec(c.Apex)}, {Vec(c.XDirection)}, {Vec(c.YDirection)}, "
                    + $"{N(c.FocalLength)}, {Range(c.Domain)})"),
                Hyperbola3d c => Emit(c,
                    $"Hyperbola({Vec(c.Center)}, {Vec(c.SemiAxisX)}, {Vec(c.SemiAxisY)}, {Range(c.Domain)})"),
                NurbsCurve c => Emit(c,
                    $"Nurbs({c.Degree}, {Points(c.ControlPoints)}, {Numbers(c.Weights)}, {Numbers(c.Knots)})"),
                PolylineCurve3d c => PolylineEntity(c),
                Helix3d c => Emit(c,
                    $"Helix({Frame(c.Frame)}, {N(c.Radius)}, {N(c.Pitch)}, {N(c.Turns)})"),
                // The two axial coefficients are written only when the spiral actually
                // CLIMBS — the Polyline carrier-pair precedent — so every archive of a
                // planar cap cut stays byte-identical while a CONICAL spiral (a thread's
                // 45-degree chamfer or its runout) stops being flattened into its frame's
                // plane on reload, which is what the four-argument form silently did.
                SpiralArc3d { AxialAtZero: 0, AxialRate: 0 } c => Emit(c,
                    $"SpiralArc({Frame(c.Frame)}, {N(c.RadiusAtZero)}, {N(c.Slope)}, {Range(c.Domain)})"),
                SpiralArc3d c => Emit(c,
                    $"SpiralArc({Frame(c.Frame)}, {N(c.RadiusAtZero)}, {N(c.Slope)}, {Range(c.Domain)}, "
                    + $"{N(c.AxialAtZero)}, {N(c.AxialRate)})"),
                HelicalArcCut3d c => Emit(c,
                    $"HelicalArcCut({Frame(c.Frame)}, {Vec2(c.ArcCenter)}, {N(c.ArcRadius)}, "
                    + $"{N(c.AxialRate)}, {N(c.CarrierRadial)}, {N(c.CarrierAxial)}, "
                    + $"{N(c.CarrierOffset)}, {c.Branch}, {Range(c.Domain)})"),
                OffsetCurve3d c => Wrapped(c, c.Base,
                    b => $"Offset(#{b}, {Vec(c.PlaneNormal)}, {N(c.Distance)})"),
                CurveSegment c => Wrapped(c, c.Base,
                    b => $"Segment(#{b}, {N(c.BaseStart)}, {N(c.BaseEnd)})"),
                ReversedCurve c => Wrapped(c, c.Base, b => $"Reversed(#{b})"),
                TransformedCurve c => Wrapped(c, c.Base, b => $"Transformed(#{b}, {Mat(c.Transform)})"),
                PhaseShiftedCurve c => Wrapped(c, c.Base, b => $"PhaseShifted(#{b}, {N(c.Shift)})"),
                LoftRailCurve c => LoftRailEntity(c),
                SweptRailCurve c => SweptRailEntity(c),
                TwistedRailCurve c => TwistedRailEntity(c),
                _ => throw Unsupported("curve", curve),
            };
        }

        private int Wrapped(Curve3d curve, Curve3d baseCurve, Func<int, string> body)
        {
            int id = Curve(baseCurve);
            return Emit(curve, body(id));
        }

        private int LoftRailEntity(LoftRailCurve c)
        {
            int surface = Surface(c.Surface);
            return Emit(c, $"LoftRail(#{surface}, {N(c.U)})");
        }

        /// <summary>
        /// A traced polyline's carrier pair travels as two OPTIONAL trailing surface
        /// references — the tessellator refines chords against them, so dropping the pair
        /// would make a reloaded solid tessellate coarser than the one that was saved
        /// (the boolean corpus holds reload volumes to 1e-12 relative). A carrier-less
        /// polyline writes exactly the two-argument form it always did, so existing files
        /// stay byte-identical and load unchanged.
        /// </summary>
        private int PolylineEntity(PolylineCurve3d c)
        {
            if (c.Carriers is { } pair)
            {
                int a = Surface(pair.A);
                int b = Surface(pair.B);
                return Emit(c, $"Polyline({Bool(c.IsClosed)}, {Points(c.Points)}, #{a}, #{b})");
            }
            return Emit(c, $"Polyline({Bool(c.IsClosed)}, {Points(c.Points)})");
        }

        private int SweptRailEntity(SweptRailCurve c)
        {
            int surface = Surface(c.Surface);
            return Emit(c, $"SweptRail(#{surface}, {Vec2(c.LocalOffset)})");
        }

        /// <summary>
        /// A twist rail. The surface reference is written through the shared entity table,
        /// so the ONE master surface every rail of a solid rides comes back as one object
        /// — which is what keeps the reloaded rails and the faces' grid columns the same
        /// arithmetic rather than several numerically-equal copies.
        /// </summary>
        private int TwistedRailEntity(TwistedRailCurve c)
        {
            int surface = Surface(c.Surface);
            return Emit(c, $"TwistRail(#{surface}, {Vec(c.LocalBase)})");
        }

        private static BrepArchiveException Unsupported(string kind, object value) =>
            new($"EngrCAD BREP archive: no entity form for the {kind} type "
                + $"'{value.GetType().Name}'. Every {kind} type in the kernel is supposed to have "
                + "one — add it to BrepArchive rather than letting the archive silently "
                + "approximate the geometry.");

        // ---- formatting ----

        private static string Refs(IEnumerable<int> ids) => "[" + string.Join(", ", ids.Select(i => "#" + i)) + "]";

        private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items) + "]";

        private static string Numbers(IEnumerable<double> values) => List(values.Select(N));

        private static string Points(IEnumerable<Vector3d> points) => List(points.Select(p => Vec(p)));

        private static string Vec(in Vector3d v) => $"({N(v.X)} {N(v.Y)} {N(v.Z)})";

        private static string Vec2(in Vector2d v) => $"({N(v.X)} {N(v.Y)})";

        private static string Range(in Interval i) => $"({N(i.Start)} {N(i.End)})";

        private static string Frame(in Frame3d f) => $"({N(f.Origin.X)} {N(f.Origin.Y)} {N(f.Origin.Z)} "
            + $"{N(f.X.X)} {N(f.X.Y)} {N(f.X.Z)} {N(f.Y.X)} {N(f.Y.Y)} {N(f.Y.Z)})";

        private static string Mat(in Matrix4d m) =>
            $"({N(m.M11)} {N(m.M12)} {N(m.M13)} {N(m.M14)} "
            + $"{N(m.M21)} {N(m.M22)} {N(m.M23)} {N(m.M24)} "
            + $"{N(m.M31)} {N(m.M32)} {N(m.M33)} {N(m.M34)} "
            + $"{N(m.M41)} {N(m.M42)} {N(m.M43)} {N(m.M44)})";

        private static string Bool(bool value) => value ? "true" : "false";

        /// <summary>Round-trip number formatting. <c>"R"</c> is a bijection on finite
        /// doubles, so this is exact, not close; the non-finite spellings are explicit
        /// tokens rather than the BCL's "Infinity"/"NaN" words so the grammar has one
        /// number rule.</summary>
        private static string N(double value)
        {
            if (double.IsNaN(value)) return "nan";
            if (double.IsPositiveInfinity(value)) return "inf";
            if (double.IsNegativeInfinity(value)) return "-inf";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
    }

    // ---- reader ----

    private sealed class ArchiveReader(string text)
    {
        private readonly Dictionary<int, object> _entities = [];
        private readonly List<string> _diagnostics = [];
        private readonly List<BrepSolid> _roots = [];
        private string? _name;
        private int _version;
        private int _line;

        public BrepArchiveResult Read()
        {
            var lines = text.Split('\n');
            bool sawMagic = false;

            for (_line = 0; _line < lines.Length; _line++)
            {
                string raw = lines[_line].Trim().TrimEnd('\r');
                if (raw.Length == 0 || raw[0] == ';')
                    continue;

                if (!sawMagic)
                {
                    ReadMagic(raw);
                    sawMagic = true;
                    continue;
                }

                if (raw.StartsWith('#'))
                {
                    ReadEntity(raw);
                    continue;
                }
                ReadHeaderLine(raw);
            }

            if (!sawMagic)
            {
                throw new BrepArchiveException(
                    $"Not an EngrCAD BREP archive: the file does not start with '{Magic} <version>'.");
            }
            if (_roots.Count == 0)
            {
                throw new BrepArchiveException(
                    "EngrCAD BREP archive contains no ROOT declaration, so it names no solid. "
                    + $"({_entities.Count} entit{(_entities.Count == 1 ? "y" : "ies")} were read.)");
            }
            return new BrepArchiveResult(_roots, _name, _version, _diagnostics);
        }

        private void ReadMagic(string raw)
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] != Magic)
            {
                throw new BrepArchiveException(
                    $"Not an EngrCAD BREP archive: expected '{Magic} <version>' on the first line, "
                    + $"found '{raw}'.");
            }
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _version))
            {
                throw new BrepArchiveException(
                    $"EngrCAD BREP archive declares a non-numeric format version '{parts[1]}'.");
            }
            // Refuse an unknown version BY NAME rather than parsing hopefully: a newer
            // writer may have added entity forms, and a partial parse of a file that
            // describes a solid we cannot build is worse than a clear refusal.
            if (_version != FormatVersion)
            {
                throw new BrepArchiveException(
                    $"EngrCAD BREP archive declares format version {_version}, but this build "
                    + $"reads version {FormatVersion}. "
                    + (_version > FormatVersion
                        ? "The file was written by a newer EngrCAD; upgrade to read it."
                        : "Re-export it from the version that wrote it, or upgrade that build."));
            }
        }

        private void ReadHeaderLine(string raw)
        {
            int space = raw.IndexOf(' ');
            string keyword = space < 0 ? raw : raw[..space];
            string rest = space < 0 ? "" : raw[(space + 1)..].Trim();
            switch (keyword)
            {
                case "UNITS":
                    // The STEP importer's lesson: a file's unit is not optional
                    // information. v1 writes millimetres only, so anything else is
                    // refused rather than silently mis-scaled.
                    if (!string.Equals(rest, "MM", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new BrepArchiveException(
                            $"EngrCAD BREP archive declares units '{rest}'; version {FormatVersion} "
                            + "reads millimetres (MM) only.");
                    }
                    break;
                case "NAME":
                    _name = Unquote(rest);
                    break;
                case "GENERATOR":
                    break;
                case "ROOT":
                    _roots.Add(Resolve<BrepSolid>(ParseReference(rest), "ROOT"));
                    break;
                default:
                    _diagnostics.Add($"line {_line + 1}: ignoring unknown header keyword '{keyword}'.");
                    break;
            }
        }

        private void ReadEntity(string raw)
        {
            int equals = raw.IndexOf('=');
            if (equals < 0)
                throw Malformed("an entity line must read '#<id> = Keyword(...)'.");
            int id = ParseReference(raw[..equals].Trim());
            string body = raw[(equals + 1)..].Trim();

            int open = body.IndexOf('(');
            if (open < 0 || !body.EndsWith(')'))
                throw Malformed($"entity #{id} is not of the form 'Keyword(...)'.");
            string keyword = body[..open].Trim();
            var args = ParseArguments(body[(open + 1)..^1]);

            if (_entities.ContainsKey(id))
                throw Malformed($"entity #{id} is defined twice.");
            _entities[id] = Build(keyword, args, id);
        }

        private object Build(string keyword, IReadOnlyList<Node> a, int id) => keyword switch
        {
            // curves
            "Line" => new Line3d(Vec(a, 0), Vec(a, 1)),
            "Circle" => new Circle3d(Vec(a, 0), Vec(a, 1), Vec(a, 2), Num(a, 3)),
            "Ellipse" => new Ellipse3d(Vec(a, 0), Vec(a, 1), Vec(a, 2)),
            "Parabola" => new Parabola3d(Vec(a, 0), Vec(a, 1), Vec(a, 2), Num(a, 3), Range(a, 4)),
            "Hyperbola" => new Hyperbola3d(Vec(a, 0), Vec(a, 1), Vec(a, 2), Range(a, 3)),
            "Nurbs" => new NurbsCurve(Int(a, 0), Points(a, 1), Numbers(a, 2), Numbers(a, 3)),
            // The two trailing surface references are the tracer's carrier pair, written
            // only when the curve has one — pre-carrier files carry two arguments and
            // load exactly as before.
            "Polyline" => new PolylineCurve3d(Points(a, 1), Bool(a, 0),
                a.Count >= 4 ? (Ref<Surface>(a, 2), Ref<Surface>(a, 3)) : null),
            "Helix" => new Helix3d(Frame(a, 0), Num(a, 1), Num(a, 2), Num(a, 3)),
            // Four arguments is the planar cap cut (and every pre-conical file); six adds
            // the axial law that makes it a conical spiral.
            "SpiralArc" => a.Count >= 6
                ? new SpiralArc3d(Frame(a, 0), Num(a, 1), Num(a, 2), Num(a, 4), Num(a, 5), Range(a, 3))
                : new SpiralArc3d(Frame(a, 0), Num(a, 1), Num(a, 2), Range(a, 3)),
            "HelicalArcCut" => new HelicalArcCut3d(
                Frame(a, 0), Vec2(a, 1), Num(a, 2), Num(a, 3), Num(a, 4), Num(a, 5), Num(a, 6),
                Int(a, 7), Range(a, 8)),
            "Offset" => new OffsetCurve3d(Ref<Curve3d>(a, 0), Vec(a, 1), Num(a, 2)),
            "Segment" => new CurveSegment(Ref<Curve3d>(a, 0), Num(a, 1), Num(a, 2)),
            "Reversed" => new ReversedCurve(Ref<Curve3d>(a, 0)),
            "Transformed" => new TransformedCurve(Ref<Curve3d>(a, 0), Mat(a, 1)),
            "PhaseShifted" => new PhaseShiftedCurve(Ref<Curve3d>(a, 0), Num(a, 1)),
            "LoftRail" => new LoftRailCurve(Ref<LoftedSurface>(a, 0), Num(a, 1)),
            "SweptRail" => new SweptRailCurve(Ref<SweptSurface>(a, 0), Vec2(a, 1)),
            "TwistRail" => new TwistedRailCurve(Ref<TwistedSurface>(a, 0), Vec(a, 1)),

            // surfaces
            "Plane" => new PlaneSurface(Vec(a, 0), Vec(a, 1), Vec(a, 2)),
            "Cylinder" => new CylinderSurface(Vec(a, 0), Vec(a, 1), Vec(a, 2), Num(a, 3)),
            "Sphere" => new SphereSurface(Vec(a, 0), Num(a, 1)),
            "NurbsSurface" => NurbsSurfaceFrom(a),
            "Extruded" => new ExtrudedSurface(Ref<Curve3d>(a, 0), Vec(a, 1)),
            "Revolved" => new RevolvedSurface(Ref<Curve3d>(a, 0), Vec(a, 1), Vec(a, 2), Num(a, 3)),
            "Swept" => new SweptSurface(Ref<Curve3d>(a, 0), Ref<Curve3d>(a, 1), Vec(a, 2), Int(a, 3)),
            "Twisted" => new TwistedSurface(
                Ref<Curve3d>(a, 0), Frame(a, 1), Num(a, 2), Num(a, 3), Vec2(a, 4)),
            "Helical" => new HelicalSurface(Frame(a, 0), Vec2(a, 1), Vec2(a, 2), Num(a, 3), Range(a, 4)),
            "HelicalArc" => new HelicalSurface(
                Frame(a, 0), Vec2(a, 1), Num(a, 2), Num(a, 3), Num(a, 4), Num(a, 5), Range(a, 6)),
            "Lofted" => new LoftedSurface(Refs<Curve3d>(a, 0), Numbers(a, 1)),

            // topology
            "Vertex" => new BrepVertex(Vec(a, 0)),
            "Edge" => new BrepEdge(Ref<Curve3d>(a, 0), Range(a, 1), Ref<BrepVertex>(a, 2), Ref<BrepVertex>(a, 3)),
            "Coedge" => new BrepCoedge(Ref<BrepEdge>(a, 0), Bool(a, 1)),
            "Loop" => new BrepLoop(Refs<BrepCoedge>(a, 0)),
            "Face" => new BrepFace(Ref<Surface>(a, 0), Refs<BrepLoop>(a, 1), Bool(a, 2)),
            "Shell" => new BrepShell(Refs<BrepFace>(a, 0)),
            "Solid" => new BrepSolid(Refs<BrepShell>(a, 0)),

            _ => throw Malformed(
                $"entity #{id} has unknown keyword '{keyword}'. "
                + "This build reads EngrCAD BREP format version " + FormatVersion + "."),
        };

        private NurbsSurface NurbsSurfaceFrom(IReadOnlyList<Node> a)
        {
            int degreeU = Int(a, 0), degreeV = Int(a, 1), countU = Int(a, 2), countV = Int(a, 3);
            var points = Points(a, 4);
            var weights = Numbers(a, 5);
            if (points.Count != countU * countV || weights.Count != countU * countV)
            {
                throw Malformed(
                    $"a NurbsSurface declares a {countU}x{countV} grid but carries {points.Count} "
                    + $"control point(s) and {weights.Count} weight(s).");
            }
            var grid = new Vector3d[countU, countV];
            var weightGrid = new double[countU, countV];
            for (int i = 0, k = 0; i < countU; i++)
            {
                for (int j = 0; j < countV; j++, k++)
                {
                    grid[i, j] = points[k];
                    weightGrid[i, j] = weights[k];
                }
            }
            return new NurbsSurface(degreeU, degreeV, grid, weightGrid, Numbers(a, 6), Numbers(a, 7));
        }

        // ---- argument access ----

        private Node Arg(IReadOnlyList<Node> a, int index) => index < a.Count
            ? a[index]
            : throw Malformed($"expected at least {index + 1} argument(s), found {a.Count}.");

        private double Num(IReadOnlyList<Node> a, int index) => Arg(a, index) is NumberNode n
            ? n.Value
            : throw Malformed($"argument {index} should be a number.");

        private int Int(IReadOnlyList<Node> a, int index)
        {
            double value = Num(a, index);
            return value == Math.Floor(value) && Math.Abs(value) < int.MaxValue
                ? (int)value
                : throw Malformed($"argument {index} should be an integer, found {value}.");
        }

        private bool Bool(IReadOnlyList<Node> a, int index) => Arg(a, index) is BoolNode b
            ? b.Value
            : throw Malformed($"argument {index} should be true or false.");

        private double[] Tuple(IReadOnlyList<Node> a, int index, int arity) =>
            Arg(a, index) is TupleNode t && t.Values.Length == arity
                ? t.Values
                : throw Malformed($"argument {index} should be a {arity}-number tuple like ({string.Join(' ', Enumerable.Repeat("0", arity))}).");

        private Vector3d Vec(IReadOnlyList<Node> a, int index)
        {
            var v = Tuple(a, index, 3);
            return new Vector3d(v[0], v[1], v[2]);
        }

        private Vector2d Vec2(IReadOnlyList<Node> a, int index)
        {
            var v = Tuple(a, index, 2);
            return new Vector2d(v[0], v[1]);
        }

        private Interval Range(IReadOnlyList<Node> a, int index)
        {
            var v = Tuple(a, index, 2);
            return new Interval(v[0], v[1]);
        }

        /// <summary>A frame as origin + X + Y, rebuilt with
        /// <see cref="Frame3d.FromOrthonormal"/> — the only factory that stores X and Y
        /// VERBATIM and derives Z = X x Y. The re-deriving factories
        /// (<c>FromXY</c>/<c>FromNormal</c>/<c>FromZX</c>) would move the axes by ulps and
        /// the archive would stop being a fixed point under round-trip.</summary>
        private Frame3d Frame(IReadOnlyList<Node> a, int index)
        {
            var v = Tuple(a, index, 9);
            return Frame3d.FromOrthonormal(
                new Vector3d(v[0], v[1], v[2]),
                new Vector3d(v[3], v[4], v[5]),
                new Vector3d(v[6], v[7], v[8]));
        }

        private Matrix4d Mat(IReadOnlyList<Node> a, int index)
        {
            var v = Tuple(a, index, 16);
            return new Matrix4d(
                v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
                v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }

        private List<Node> Items(IReadOnlyList<Node> a, int index) => Arg(a, index) is ListNode l
            ? [.. l.Items]
            : throw Malformed($"argument {index} should be a [list].");

        private List<double> Numbers(IReadOnlyList<Node> a, int index) =>
            [.. Items(a, index).Select(n => n is NumberNode m
                ? m.Value
                : throw Malformed($"argument {index} should be a list of numbers."))];

        private List<Vector3d> Points(IReadOnlyList<Node> a, int index) =>
            [.. Items(a, index).Select(n => n is TupleNode { Values.Length: 3 } t
                ? new Vector3d(t.Values[0], t.Values[1], t.Values[2])
                : throw Malformed($"argument {index} should be a list of (x y z) points."))];

        private T Ref<T>(IReadOnlyList<Node> a, int index) where T : class => Arg(a, index) is RefNode r
            ? Resolve<T>(r.Id, $"argument {index}")
            : throw Malformed($"argument {index} should be a #reference.");

        private List<T> Refs<T>(IReadOnlyList<Node> a, int index) where T : class =>
            [.. Items(a, index).Select(n => n is RefNode r
                ? Resolve<T>(r.Id, $"argument {index}")
                : throw Malformed($"argument {index} should be a list of #references."))];

        private T Resolve<T>(int id, string where) where T : class
        {
            if (!_entities.TryGetValue(id, out var entity))
            {
                // Forward references are illegal by design: the object graph is a DAG and
                // the writer emits in dependency order, so a reference to something not
                // yet defined means the file is damaged or was reordered by hand.
                throw Malformed(
                    $"{where} references #{id}, which is not defined above this line. "
                    + "An EngrCAD BREP archive defines every entity before it is used.");
            }
            return entity as T
                ?? throw Malformed(
                    $"{where} references #{id}, which is a {entity.GetType().Name} where a "
                    + $"{typeof(T).Name} was expected.");
        }

        private BrepArchiveException Malformed(string message) =>
            new($"EngrCAD BREP archive, line {_line + 1}: {message}");

        // ---- tokenizer ----

        private abstract record Node;
        private sealed record NumberNode(double Value) : Node;
        private sealed record RefNode(int Id) : Node;
        private sealed record TupleNode(double[] Values) : Node;
        private sealed record ListNode(Node[] Items) : Node;
        private sealed record BoolNode(bool Value) : Node;

        private List<Node> ParseArguments(string body)
        {
            int index = 0;
            var args = new List<Node>();
            SkipSpace(body, ref index);
            while (index < body.Length)
            {
                args.Add(ParseNode(body, ref index));
                SkipSpace(body, ref index);
                if (index < body.Length && body[index] == ',')
                {
                    index++;
                    SkipSpace(body, ref index);
                }
            }
            return args;
        }

        private Node ParseNode(string s, ref int i)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length)
                throw Malformed("unexpected end of entity arguments.");
            switch (s[i])
            {
                case '#':
                {
                    int start = ++i;
                    while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                    return new RefNode(ParseReference("#" + s[start..i]));
                }
                case '(':
                {
                    i++;
                    var values = new List<double>();
                    SkipSpace(s, ref i);
                    while (i < s.Length && s[i] != ')')
                    {
                        values.Add(ParseNumber(s, ref i));
                        SkipSpace(s, ref i);
                    }
                    if (i >= s.Length)
                        throw Malformed("unterminated ( tuple.");
                    i++;
                    return new TupleNode([.. values]);
                }
                case '[':
                {
                    i++;
                    var items = new List<Node>();
                    SkipSpace(s, ref i);
                    while (i < s.Length && s[i] != ']')
                    {
                        items.Add(ParseNode(s, ref i));
                        SkipSpace(s, ref i);
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            SkipSpace(s, ref i);
                        }
                    }
                    if (i >= s.Length)
                        throw Malformed("unterminated [ list.");
                    i++;
                    return new ListNode([.. items]);
                }
                default:
                {
                    if (s.AsSpan(i).StartsWith("true"))
                    {
                        i += 4;
                        return new BoolNode(true);
                    }
                    if (s.AsSpan(i).StartsWith("false"))
                    {
                        i += 5;
                        return new BoolNode(false);
                    }
                    return new NumberNode(ParseNumber(s, ref i));
                }
            }
        }

        private double ParseNumber(string s, ref int i)
        {
            SkipSpace(s, ref i);
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '.' || s[i] == '+' || s[i] == '-'))
            {
                // An exponent's sign is part of the token; a following '-' is not, unless
                // it directly follows 'e' or 'E'.
                if ((s[i] == '-' || s[i] == '+') && !(s[i - 1] is 'e' or 'E'))
                    break;
                i++;
            }
            string token = s[start..i];
            return token switch
            {
                "inf" or "+inf" => double.PositiveInfinity,
                "-inf" => double.NegativeInfinity,
                "nan" => double.NaN,
                _ => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? value
                    : throw Malformed($"'{token}' is not a number."),
            };
        }

        private static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private int ParseReference(string token)
        {
            if (token.Length < 2 || token[0] != '#'
                || !int.TryParse(token[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                throw Malformed($"'{token}' is not an entity reference (#<id>).");
            }
            return id;
        }

        private static string Unquote(string raw)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
                return trimmed[1..^1].Replace("''", "'");
            return trimmed;
        }
    }
}

/// <summary>What reading an EngrCAD BREP archive produced.</summary>
/// <param name="Solids">The solids the file's ROOT declarations name, in file order.</param>
/// <param name="Name">The archive's NAME header, if it carried one.</param>
/// <param name="Version">The format version the file declared.</param>
/// <param name="Diagnostics">Anything skipped or assumed — the
/// <c>StepReadResult.Diagnostics</c> convention: reported as data, never as a log line.</param>
public sealed record BrepArchiveResult(
    IReadOnlyList<BrepSolid> Solids, string? Name, int Version, IReadOnlyList<string> Diagnostics)
{
    /// <summary>The single solid, or a throw naming how many there actually are.</summary>
    public BrepSolid Single() => Solids.Count == 1
        ? Solids[0]
        : throw new BrepArchiveException(
            $"Expected one solid in the archive, found {Solids.Count}.");
}

/// <summary>A malformed, unreadable or wrong-version EngrCAD BREP archive.</summary>
public sealed class BrepArchiveException(string message) : Exception(message);
