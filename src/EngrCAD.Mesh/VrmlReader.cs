using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// VRML97 (<c>.wrl</c>) import — KiCad's default 3D component-model format. The covered subset is
/// the MESH content: every <c>Shape</c> whose geometry is an <c>IndexedFaceSet</c>, gathered
/// through the <c>Transform</c>/<c>Group</c> hierarchy with translation/rotation/scale/center
/// composed per the spec (<c>scaleOrientation</c> is ignored with a note), <c>DEF</c>/<c>USE</c>
/// instancing re-emitting the shared node under each use's own transform, <c>Switch</c> honouring
/// <c>whichChoice</c> and <c>LOD</c> taking its most-detailed level. Appearance, materials,
/// normals, colours and texture coordinates are ignored (a mesh reader reads the mesh); a
/// non-mesh geometry node (<c>Box</c>, <c>Cylinder</c>, <c>IndexedLineSet</c>, …) and an external
/// <c>Inline</c> are skipped WITH A NAMED WARNING, never silently.
///
/// <para><b>Coordinates are read VERBATIM</b> — VRML is unitless and this reader invents no
/// factor. The KiCad convention (1 VRML unit = 0.1 inch = 2.54 mm) belongs to the consumer that
/// knows the file came from KiCad, which is where it is applied
/// (<c>ComponentModel3D</c>).</para>
///
/// <para>Refused BY NAME: a missing or non-2.0 <c>#VRML</c> header (a V1.0 file is a different
/// grammar), <c>PROTO</c>/<c>EXTERNPROTO</c> (a prototype defines its own node vocabulary), and a
/// truncated file (unbalanced braces). Dirty per-element content — an out-of-range coordinate
/// index, a <c>USE</c> of an undefined name, a malformed field — is reported and skipped (the
/// readers-never-throw culture); the result is the ordinary <see cref="MeshReadResult"/> soup +
/// diagnostics, so the repair pipeline and <c>Shape.From</c> compose unchanged.</para>
/// </summary>
public static class VrmlReader
{
    /// <param name="path">File to read (.wrl).</param>
    /// <param name="weldTolerance">Distance under which vertices weld to one representative;
    /// defaults to the 1e-9 absolute weld tier (<c>Tolerance.Default.Linear</c>).</param>
    public static MeshReadResult ReadFile(string path, double weldTolerance = 1e-9) =>
        Read(File.ReadAllText(path), weldTolerance);

    /// <summary>Reads a VRML97 scene from its text. See <see cref="ReadFile"/> for semantics.</summary>
    public static MeshReadResult Read(string text, double weldTolerance = 1e-9)
    {
        ArgumentNullException.ThrowIfNull(text);
        RequireHeader(text);

        var warnings = new List<string>();
        var once = new HashSet<string>(StringComparer.Ordinal);
        void Note(string message) { if (once.Add(message)) warnings.Add(message); }

        var parser = new Parser(Tokenize(text), Note);
        var roots = parser.ParseFile();

        var positions = new List<Vector3d>();
        var faces = new List<int[]>();
        foreach (var root in roots)
            Walk(root, Matrix4d.Identity, positions, faces, Note);
        if (positions.Count == 0)
            Note("The VRML scene carries no IndexedFaceSet geometry.");

        return ObjReader.BuildFromIndexed(positions, faces, weldTolerance, warnings);
    }

    private static void RequireHeader(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        string first = (end < 0 ? text : text[..end]).Trim();
        if (!first.StartsWith("#VRML", StringComparison.Ordinal))
            throw new FormatException(
                "Not a VRML file: the required '#VRML V2.0 utf8' header line is missing.");
        if (first.Contains("V1.0", StringComparison.Ordinal))
            throw new FormatException(
                "This is a VRML 1.0 file — a different grammar this reader does not cover. "
                + "Only VRML97 ('#VRML V2.0 utf8') is read.");
        if (!first.Contains("V2.0", StringComparison.Ordinal))
            throw new FormatException(
                $"Unsupported VRML version in header '{first}'; only V2.0 (VRML97) is read.");
    }

    // ---- tokenizer -----------------------------------------------------------
    // Comments run '#' to end of line; commas are whitespace; braces/brackets are their own
    // tokens; a double-quoted string is one token (kept with a marker so a url cannot be
    // mistaken for a field name).

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '#')
            {
                while (i < n && text[i] != '\n') i++;
            }
            else if (c is ' ' or '\t' or '\r' or '\n' or ',')
            {
                i++;
            }
            else if (c is '{' or '}' or '[' or ']')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c == '"')
            {
                int start = ++i;
                while (i < n && text[i] != '"')
                    i += text[i] == '\\' && i + 1 < n ? 2 : 1;
                tokens.Add("\"" + text[start..Math.Min(i, n)]);
                if (i < n) i++;                              // closing quote
            }
            else
            {
                int start = i;
                while (i < n && text[i] is not (' ' or '\t' or '\r' or '\n' or ','
                    or '{' or '}' or '[' or ']' or '"' or '#'))
                    i++;
                tokens.Add(text[start..i]);
            }
        }
        return tokens;
    }

    // ---- schema-free node parser ---------------------------------------------

    private sealed class VrmlNode(string type)
    {
        public string Type { get; } = type;
        public Dictionary<string, VrmlField> Fields { get; } = new(StringComparer.Ordinal);
    }

    private sealed class VrmlField
    {
        public List<double> Numbers { get; } = [];
        public List<VrmlNode> Nodes { get; } = [];
        public List<bool> Bools { get; } = [];
        public List<string> Strings { get; } = [];
    }

    private sealed class Parser(List<string> tokens, Action<string> note)
    {
        private readonly Dictionary<string, VrmlNode> _defs = new(StringComparer.Ordinal);
        private int _cursor;

        private bool AtEnd => _cursor >= tokens.Count;
        private string Peek => tokens[_cursor];

        private string Take() =>
            _cursor < tokens.Count
                ? tokens[_cursor++]
                : throw new FormatException(
                    "Truncated VRML file: the input ended inside a node (unbalanced braces).");

        public List<VrmlNode> ParseFile()
        {
            var roots = new List<VrmlNode>();
            while (!AtEnd)
            {
                var node = ParseStatement();
                if (node is not null)
                    roots.Add(node);
            }
            return roots;
        }

        /// <summary>One top-level or list-element statement: DEF/USE/node/ROUTE/NULL. PROTO is
        /// refused by name — a prototype defines its own node vocabulary, so reading past one
        /// would mean guessing what its instances mean.</summary>
        private VrmlNode? ParseStatement()
        {
            string token = Take();
            switch (token)
            {
                case "PROTO":
                case "EXTERNPROTO":
                    throw new FormatException(
                        $"This VRML file declares a {token} — prototype nodes define their own "
                        + "vocabulary and are not covered. Filed.");
                case "ROUTE":
                    Take(); Take(); Take();                  // from TO to
                    return null;
                case "NULL":
                    return null;
                case "DEF":
                {
                    string name = Take();
                    var node = ParseStatement();
                    if (node is not null)
                        _defs[name] = node;
                    return node;
                }
                case "USE":
                {
                    string name = Take();
                    if (_defs.TryGetValue(name, out var node))
                        return node;
                    note($"USE '{name}' names a node no DEF declared; it was skipped.");
                    return null;
                }
                default:
                    return ParseNodeBody(token);
            }
        }

        private VrmlNode ParseNodeBody(string type)
        {
            string open = Take();
            if (open != "{")
                throw new FormatException(
                    $"Malformed VRML: expected '{{' after node type '{type}', got '{open}'.");
            var node = new VrmlNode(type);
            while (true)
            {
                string token = Take();
                if (token == "}")
                    return node;
                ParseField(node, token);
            }
        }

        /// <summary>One field: the name then its value, whose extent is decided syntactically —
        /// a bracketed list, a run of numbers/bools/strings, or a child node.</summary>
        private void ParseField(VrmlNode node, string fieldName)
        {
            var field = new VrmlField();
            node.Fields[fieldName] = field;
            if (AtEnd)
                throw new FormatException(
                    $"Truncated VRML file: the input ended after field '{fieldName}'.");

            if (Peek == "[")
            {
                Take();
                while (true)
                {
                    if (AtEnd)
                        throw new FormatException(
                            $"Truncated VRML file: the input ended inside field '{fieldName}''s list.");
                    if (Peek == "]")
                    {
                        Take();
                        return;
                    }
                    ParseValue(field);
                }
            }
            // A scalar field consumes a RUN of same-kind values (e.g. "translation 1 2 3"),
            // stopping at the next identifier — which is the next field name or a node type.
            ParseValue(field);
            while (!AtEnd && (IsNumber(Peek) || Peek is "TRUE" or "FALSE" || Peek.StartsWith('"')))
                ParseValue(field);
        }

        private void ParseValue(VrmlField field)
        {
            string token = Peek;
            if (IsNumber(token))
            {
                Take();
                field.Numbers.Add(double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            else if (token is "TRUE" or "FALSE")
            {
                Take();
                field.Bools.Add(token == "TRUE");
            }
            else if (token.StartsWith('"'))
            {
                Take();
                field.Strings.Add(token[1..]);
            }
            else if (token == "IS")
            {
                Take(); Take();                              // PROTO interface (unreachable: PROTO refuses)
            }
            else
            {
                var node = ParseStatement();
                if (node is not null)
                    field.Nodes.Add(node);
            }
        }

        private static bool IsNumber(string token) =>
            token.Length > 0 && (char.IsAsciiDigit(token[0]) || token[0] is '-' or '+' or '.');
    }

    // ---- the scene walk ------------------------------------------------------

    private static void Walk(
        VrmlNode node, in Matrix4d matrix,
        List<Vector3d> positions, List<int[]> faces, Action<string> note)
    {
        switch (node.Type)
        {
            case "Transform":
                var local = matrix * LocalTransform(node, note);
                WalkChildren(node, "children", local, positions, faces, note);
                break;
            case "Group":
            case "Billboard":
            case "Collision":
            case "Anchor":
                WalkChildren(node, "children", matrix, positions, faces, note);
                break;
            case "Switch":
            {
                // VRML97 Switch: children in "choice", the active one named by whichChoice
                // (default −1 = none).
                int which = node.Fields.TryGetValue("whichChoice", out var w) && w.Numbers.Count > 0
                    ? (int)Math.Round(w.Numbers[0]) : -1;
                if (node.Fields.TryGetValue("choice", out var choice)
                    && which >= 0 && which < choice.Nodes.Count)
                    Walk(choice.Nodes[which], matrix, positions, faces, note);
                break;
            }
            case "LOD":
                // The first level is the most detailed — the right one for an import.
                if (node.Fields.TryGetValue("level", out var levels) && levels.Nodes.Count > 0)
                    Walk(levels.Nodes[0], matrix, positions, faces, note);
                break;
            case "Shape":
                if (node.Fields.TryGetValue("geometry", out var g) && g.Nodes.Count > 0)
                {
                    var geometry = g.Nodes[0];
                    if (geometry.Type == "IndexedFaceSet")
                        EmitFaceSet(geometry, matrix, positions, faces, note);
                    else
                        note($"A Shape's geometry '{geometry.Type}' was skipped — only "
                            + "IndexedFaceSet meshes are read.");
                }
                break;
            case "Inline":
                note("An external Inline scene was skipped — this reader reads one file.");
                break;
            default:
                // An unknown grouping node still gets its children walked (harmless for a
                // sensor/interpolator, which has none).
                if (node.Fields.ContainsKey("children"))
                    WalkChildren(node, "children", matrix, positions, faces, note);
                break;
        }
    }

    private static void WalkChildren(
        VrmlNode node, string fieldName, in Matrix4d matrix,
        List<Vector3d> positions, List<int[]> faces, Action<string> note)
    {
        if (!node.Fields.TryGetValue(fieldName, out var children))
            return;
        foreach (var child in children.Nodes)
            Walk(child, matrix, positions, faces, note);
    }

    /// <summary>A Transform's local matrix, T · C · R · S · C⁻¹ per the spec, with
    /// scaleOrientation ignored (noted when present and non-identity).</summary>
    private static Matrix4d LocalTransform(VrmlNode node, Action<string> note)
    {
        var t = ReadVector(node, "translation", Vector3d.Zero);
        var c = ReadVector(node, "center", Vector3d.Zero);
        var s = ReadVector(node, "scale", new Vector3d(1, 1, 1));
        var m = Matrix4d.CreateTranslation(t) * Matrix4d.CreateTranslation(c);
        if (node.Fields.TryGetValue("rotation", out var r) && r.Numbers.Count >= 4
            && r.Numbers[3] != 0)
        {
            var axis = new Vector3d(r.Numbers[0], r.Numbers[1], r.Numbers[2]);
            if (axis.Length > 0)
                m *= Matrix4d.CreateFromAxisAngle(axis.Normalized(), r.Numbers[3]);
            else
                note("A Transform's rotation axis is the zero vector; the rotation was ignored.");
        }
        if (node.Fields.TryGetValue("scaleOrientation", out var so) && so.Numbers.Count >= 4
            && so.Numbers[3] != 0)
            note("A Transform's scaleOrientation was ignored (not covered).");
        m *= Matrix4d.CreateScale(s);
        m *= Matrix4d.CreateTranslation(-c);
        return m;
    }

    private static Vector3d ReadVector(VrmlNode node, string fieldName, in Vector3d fallback) =>
        node.Fields.TryGetValue(fieldName, out var f) && f.Numbers.Count >= 3
            ? new Vector3d(f.Numbers[0], f.Numbers[1], f.Numbers[2])
            : fallback;

    private static void EmitFaceSet(
        VrmlNode faceSet, in Matrix4d matrix,
        List<Vector3d> positions, List<int[]> faces, Action<string> note)
    {
        if (!faceSet.Fields.TryGetValue("coord", out var coordField) || coordField.Nodes.Count == 0)
        {
            note("An IndexedFaceSet has no Coordinate node; it was skipped.");
            return;
        }
        var coordinate = coordField.Nodes[0];
        if (!coordinate.Fields.TryGetValue("point", out var pointField))
        {
            note("An IndexedFaceSet's Coordinate has no points; it was skipped.");
            return;
        }
        var numbers = pointField.Numbers;
        int pointCount = numbers.Count / 3;
        if (numbers.Count % 3 != 0)
            note("A Coordinate's point list is not a multiple of 3; the remainder was dropped.");

        int baseIndex = positions.Count;
        for (int i = 0; i < pointCount; i++)
            positions.Add(matrix.TransformPoint(
                new Vector3d(numbers[3 * i], numbers[3 * i + 1], numbers[3 * i + 2])));

        // ccw defaults TRUE; a clockwise set is reversed so winding stays outward. A negative
        // determinant (a mirroring transform) flips it back, exactly as HalfEdgeMesh.Transformed
        // would — one XOR, so a mirrored instance stays outward too.
        bool ccw = !faceSet.Fields.TryGetValue("ccw", out var c) || c.Bools.Count == 0 || c.Bools[0];
        bool mirrored = matrix.Determinant < 0;
        bool reverse = ccw == mirrored;

        if (!faceSet.Fields.TryGetValue("coordIndex", out var indexField))
        {
            note("An IndexedFaceSet has no coordIndex; it was skipped.");
            return;
        }
        var loop = new List<int>();
        int outOfRange = 0;
        void Flush()
        {
            if (loop.Count >= 3)
            {
                var face = loop.ToArray();
                if (reverse)
                    Array.Reverse(face);
                faces.Add(face);
            }
            loop.Clear();
        }
        foreach (double raw in indexField.Numbers)
        {
            int index = (int)Math.Round(raw);
            if (index < 0)
            {
                Flush();
            }
            else if (index >= pointCount)
            {
                outOfRange++;
                loop.Clear();                                // the whole face is suspect
            }
            else
            {
                loop.Add(baseIndex + index);
            }
        }
        Flush();
        if (outOfRange > 0)
            note($"{outOfRange} coordIndex entries point past the Coordinate list; their faces "
                + "were skipped.");
    }
}
