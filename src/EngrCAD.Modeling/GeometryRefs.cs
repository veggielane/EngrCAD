using System.Globalization;
using System.Text;
using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>A declared geometry input could not be resolved against the model — the
/// reference names itself and says what it wanted versus what it found.</summary>
public sealed class GeometryInputException(string message) : InvalidOperationException(message);

/// <summary>How many entities a reference contracts to produce.</summary>
public enum RefCardinality
{
    /// <summary>Exactly one entity; more than one is as much a failure as none.</summary>
    ExactlyOne,

    /// <summary>One or more entities; an empty match is a failure.</summary>
    AtLeastOne,

    /// <summary>Zero or more entities; an empty match is legal.</summary>
    Any,

    /// <summary>Edges covering complete planar face rims — the set rim surgery accepts
    /// (see <see cref="Filleting.RimFacesFor"/>).</summary>
    CompleteRim,
}

/// <summary>
/// A declarative reference to geometry on the model a feature is being applied to — the
/// answer to "this feature needs a plane" or "this feature needs the bore's axis".
///
/// <para>A reference carries three things: <b>how to find</b> the geometry (a semantic
/// query over <c>BrepQueries</c>, or an explicit frame as the escape hatch), <b>what
/// cardinality</b> it contracts to (<see cref="Cardinality"/>), and — through the type it
/// resolves to — <b>what shape</b> the answer has. It carries no resolved geometry: a
/// reference is re-resolved against every regeneration's body, which is this kernel's
/// topological-naming story (semantic queries instead of persisted indices).</para>
///
/// <para><b>Timing.</b> Feature inputs resolve <em>lazily, per <c>Apply</c></em>: the
/// value is computed against the context in front of it and cached only for the lifetime
/// of that <see cref="FeatureContext"/>, never on the reference itself. (Mates take the
/// opposite, deliberately eager route — see <see cref="Mates"/> — because a mate is a
/// numerical constraint pinned at construction.)</para>
///
/// <para><b>Cache key and serialization are the same string.</b> <see cref="Descriptor"/>
/// is a canonical, parseable rendering of the whole query, and <see cref="ToString"/>
/// returns it — so a reference marked <c>[Param]</c> contributes an honest, value-stable
/// term to the regeneration cache key AND round-trips through
/// <c>SaveParameters</c>/<c>LoadParameters</c>. References built from a lambda print an
/// <c>opaque(label)</c> marker instead and are <see cref="IsSerializable"/> = false;
/// they are still safe as cache keys because the snapshot also carries the feature's
/// instance identity, and a fresh instance always re-runs.</para>
/// </summary>
public abstract class GeometryRef
{
    private protected GeometryRef() { }

    /// <summary>The canonical, parseable rendering of this reference — its cache-key
    /// term and its serialized form.</summary>
    public abstract string Descriptor { get; }

    /// <summary>The human phrase naming what this reference looks for ("planar face with
    /// outward normal (0, 0, 1)"), used to build failure messages.</summary>
    public abstract string Subject { get; }

    /// <summary>How many entities this reference contracts to produce.</summary>
    public abstract RefCardinality Cardinality { get; }

    /// <summary>False when any part of the query is a lambda: the descriptor is then a
    /// marker rather than a recipe, so the reference cannot be reconstructed from JSON.</summary>
    public abstract bool IsSerializable { get; }

    /// <summary>True when resolving needs the body lowered to B-Rep. Explicit frames and
    /// axes answer without one, so a feature can declare them before any geometry exists.</summary>
    public abstract bool RequiresBody { get; }

    public sealed override string ToString() => Descriptor;

    internal abstract object ResolveAgainst(BrepSolid? solid, string inputName);

    private protected static BrepSolid RequireSolid(BrepSolid? solid, string inputName, string subject) =>
        solid ?? throw new GeometryInputException(
            $"{inputName}: expected {subject}, but no body exists yet — this is the first feature.");

    /// <summary>Reconstructs a reference of <paramref name="type"/> from a
    /// <see cref="Descriptor"/> (the JSON round-trip entry point). Throws
    /// <see cref="FormatException"/> for opaque descriptors and unknown query names.</summary>
    public static GeometryRef Parse(string descriptor, Type type)
    {
        if (typeof(PlaneRef).IsAssignableFrom(type))
            return PlaneRef.Parse(descriptor);
        if (typeof(FaceRef).IsAssignableFrom(type))
            return FaceRef.Parse(descriptor);
        if (typeof(FaceSetRef).IsAssignableFrom(type))
            return FaceSetRef.Parse(descriptor);
        if (typeof(EdgeSetRef).IsAssignableFrom(type))
            return EdgeSetRef.Parse(descriptor);
        if (typeof(AxisRef).IsAssignableFrom(type))
            return AxisRef.Parse(descriptor);
        throw new FormatException($"unsupported geometry reference type {type.Name}");
    }

    /// <summary>Vector rendering for failure messages (the descriptor uses its own
    /// round-trippable form).</summary>
    private protected static string Describe(in Vector3d v) =>
        $"({v.X:g6}, {v.Y:g6}, {v.Z:g6})";
}

/// <summary>A <see cref="GeometryRef"/> that resolves to <typeparamref name="T"/>.</summary>
public abstract class GeometryRef<T> : GeometryRef
{
    private protected GeometryRef() { }

    /// <summary>Resolves against a lowered solid (null when no body exists yet), naming
    /// itself <paramref name="inputName"/> in any failure. Throws
    /// <see cref="GeometryInputException"/> when the cardinality contract is not met.</summary>
    public abstract T Resolve(BrepSolid? solid, string inputName);

    /// <summary>Resolves against a feature's context, reusing the resolution any earlier
    /// up-front validation already computed for this context (and replaying its failure
    /// verbatim). Nothing is memoized on the reference itself, so the next regeneration
    /// starts from scratch.</summary>
    public T Resolve(FeatureContext context, string? inputName = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (T)context.ResolveInput(this, inputName);
    }

    internal sealed override object ResolveAgainst(BrepSolid? solid, string inputName) =>
        Resolve(solid, inputName)!;
}

// ---------------------------------------------------------------------------
// Descriptor syntax
// ---------------------------------------------------------------------------

/// <summary>
/// The descriptor grammar, shared by every reference type so nesting composes:
/// <c>term := ident [ '(' arg { ',' arg } ')' ]</c>, where an argument is a number, a
/// <c>[x,y,z]</c> vector, a bare label, or another term. Numbers use the "R"
/// round-trip format, so an explicit frame survives a JSON round trip bit for bit.
/// </summary>
internal static class RefSyntax
{
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Vector(in Vector3d v) => $"[{Number(v.X)},{Number(v.Y)},{Number(v.Z)}]";

    public static string Call(string name, params string[] arguments) =>
        arguments.Length == 0 ? name : $"{name}({string.Join(",", arguments)})";

    /// <summary>A label for an opaque (lambda-backed) query, reduced to characters that
    /// neither break the grammar nor get escaped on the way into a parameter file —
    /// System.Text.Json's default encoder escapes quotes and angle brackets, which would
    /// turn the marker into unreadable \u-escapes in saved JSON.</summary>
    public static string Label(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '.' or '-')
                builder.Append(c);
        }
        return builder.Length == 0 ? "?" : builder.ToString();
    }
}

/// <summary>A cursor over a descriptor string. Every reference type parses from the
/// shared cursor, so nested terms need no substring splitting.</summary>
internal sealed class RefLexer(string text)
{
    private int _index;

    public char Peek => _index < text.Length ? text[_index] : '\0';

    public bool TryConsume(char c)
    {
        if (Peek != c)
            return false;
        _index++;
        return true;
    }

    public void Expect(char c)
    {
        if (!TryConsume(c))
            throw new FormatException($"expected '{c}' at position {_index} of \"{text}\"");
    }

    public string ReadIdentifier()
    {
        int start = _index;
        while (_index < text.Length && (char.IsLetterOrDigit(text[_index]) || text[_index] == '_'))
            _index++;
        if (_index == start)
            throw new FormatException($"expected a query name at position {_index} of \"{text}\"");
        return text[start.._index];
    }

    public double ReadNumber()
    {
        int start = _index;
        while (_index < text.Length &&
               (char.IsDigit(text[_index]) || text[_index] is '+' or '-' or '.' or 'e' or 'E'))
            _index++;
        if (!double.TryParse(text[start.._index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new FormatException($"expected a number at position {start} of \"{text}\"");
        return value;
    }

    public Vector3d ReadVector()
    {
        Expect('[');
        double x = ReadNumber();
        Expect(',');
        double y = ReadNumber();
        Expect(',');
        double z = ReadNumber();
        Expect(']');
        return new Vector3d(x, y, z);
    }

    public void ExpectEnd()
    {
        if (_index != text.Length)
            throw new FormatException($"unexpected trailing text at position {_index} of \"{text}\"");
    }

    public static FormatException Opaque(string kind) => new(
        $"a lambda-backed {kind} reference prints as an opaque marker and cannot be rebuilt from it; " +
        "use a named query (or set the input in code) instead");
}

// ---------------------------------------------------------------------------
// Faces
// ---------------------------------------------------------------------------

/// <summary>
/// A reference to a SET of faces — "the upward planar faces", "every bore of radius 3",
/// "the rims covering these edges". Resolution is <b>all-or-nothing</b>: the whole
/// selection is produced before a caller touches it, so a partial match cannot leave
/// half-edited geometry behind (rim surgery rewrites loops in place).
/// </summary>
public abstract class FaceSetRef : GeometryRef<IReadOnlyList<BrepFace>>
{
    private protected FaceSetRef() { }

    public override RefCardinality Cardinality => RefCardinality.AtLeastOne;
    public override bool RequiresBody => true;

    /// <summary>The faces matching this query on <paramref name="solid"/>, before the
    /// cardinality contract is applied. <paramref name="inputName"/> travels down so a
    /// nested query's failure still names the property the user declared.</summary>
    private protected abstract IEnumerable<BrepFace> Select(BrepSolid solid, string inputName);

    /// <summary>The raw match, cardinality contract NOT applied — how a wrapper
    /// (<see cref="Optional"/>, <see cref="FaceRef"/>) reaches inside another query.</summary>
    private protected static IEnumerable<BrepFace> RawMatch(FaceSetRef reference, BrepSolid solid, string inputName) =>
        reference.Select(solid, inputName);

    internal IReadOnlyList<BrepFace> Match(BrepSolid? solid, string inputName) =>
        [.. Select(RequireSolid(solid, inputName, Subject), inputName)];

    public sealed override IReadOnlyList<BrepFace> Resolve(BrepSolid? solid, string inputName)
    {
        var faces = Select(RequireSolid(solid, inputName, Subject), inputName).ToList();
        if (faces.Count == 0 && Cardinality != RefCardinality.Any)
            throw new GeometryInputException($"{inputName}: expected at least one {Subject}, found none.");
        return faces;
    }

    /// <summary>This query as the <c>Func</c> the <see cref="Shape"/> rim operations take.
    /// The lambda resolves against whatever solid the lowering hands it, so a feature can
    /// hold a declarative reference and still feed a deferred selector.</summary>
    public Func<BrepSolid, IEnumerable<BrepFace>> AsSelector(string inputName) =>
        solid => Resolve(solid, inputName);

    /// <summary>The same query with an empty match accepted.</summary>
    public FaceSetRef Optional() => this is OptionalFaces ? this : new OptionalFaces(this);

    // ---- named queries ----

    /// <summary>Every face of the solid.</summary>
    public static FaceSetRef All { get; } = new AllFaces();

    /// <summary>Planar faces whose OUTWARD normal points along
    /// <paramref name="normal"/> (<see cref="BrepQueries.PlanarFacesWithNormal"/>).</summary>
    public static FaceSetRef PlanarWithNormal(in Vector3d normal) => new PlanarFaces(normal);

    /// <summary>Cylindrical faces (bores and bosses), optionally of a given radius.</summary>
    public static FaceSetRef Cylindrical(double? radius = null) => new CylindricalFaces(radius);

    /// <summary>The planar face rims that exactly cover <paramref name="edges"/> — the
    /// <see cref="RefCardinality.CompleteRim"/> contract, refused by name when the
    /// selection stops partway along a rim.</summary>
    public static FaceSetRef RimFacesOf(EdgeSetRef edges) => new RimFaces(edges);

    /// <summary>Faces of a semantic <see cref="SurfaceKind"/>
    /// (<see cref="BrepSelection.FilterBy"/> — planar, cylindrical, conical, toroidal...).</summary>
    public static FaceSetRef OfKind(SurfaceKind kind) => new KindFaces(kind);

    /// <summary>The cylindrical faces carrying the <paramref name="index"/>-th smallest
    /// distinct radius (<see cref="BrepSelection.NthByRadius(IEnumerable{BrepFace}, int)"/>;
    /// 0 = smallest, −1 = largest). "The middle bore size" without typing its radius.</summary>
    public static FaceSetRef NthByRadius(int index) => new RadiusIndexFaces(index);

    /// <summary>The <paramref name="index"/>-th group of <paramref name="faces"/> ranked
    /// along <paramref name="direction"/>
    /// (<see cref="BrepSelection.GroupAlong(IEnumerable{BrepFace}, Vector3d, double)"/>;
    /// groups ascending, 0 = furthest against the direction, −1 = furthest along it).
    /// "The faces on the second step" without naming its height.</summary>
    public static FaceSetRef GroupAlong(FaceSetRef faces, in Vector3d direction, int index) =>
        new GroupAlongFaces(faces, direction, index);

    /// <summary>Escape hatch: any lambda over the lowered solid. Subsumes the raw
    /// selector overloads on <see cref="Shape"/>; not serializable.</summary>
    public static FaceSetRef From(string label, Func<BrepSolid, IEnumerable<BrepFace>> selector) =>
        new OpaqueFaces(label, selector ?? throw new ArgumentNullException(nameof(selector)));

    /// <summary>Escape hatch: a per-face predicate (the <c>Draft</c>/<c>Shelling</c>
    /// selector shape); not serializable.</summary>
    public static FaceSetRef Where(string label, Func<BrepFace, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new OpaqueFaces(label, solid => solid.Faces.Where(predicate));
    }

    // ---- parsing ----

    public static FaceSetRef Parse(string descriptor)
    {
        var lexer = new RefLexer(descriptor);
        var reference = ParseFrom(lexer);
        lexer.ExpectEnd();
        return reference;
    }

    internal static FaceSetRef ParseFrom(RefLexer lexer)
    {
        string name = lexer.ReadIdentifier();
        switch (name)
        {
            case "all":
                return All;
            case "planar":
            {
                lexer.Expect('(');
                var normal = lexer.ReadVector();
                lexer.Expect(')');
                return PlanarWithNormal(normal);
            }
            case "cylindrical":
            {
                if (!lexer.TryConsume('('))
                    return Cylindrical();
                double radius = lexer.ReadNumber();
                lexer.Expect(')');
                return Cylindrical(radius);
            }
            case "rimFacesOf":
            {
                lexer.Expect('(');
                var edges = EdgeSetRef.ParseFrom(lexer);
                lexer.Expect(')');
                return RimFacesOf(edges);
            }
            case "kind":
            {
                lexer.Expect('(');
                string kindName = lexer.ReadIdentifier();
                lexer.Expect(')');
                if (!Enum.TryParse<SurfaceKind>(kindName, ignoreCase: true, out var kind))
                    throw new FormatException($"unknown surface kind '{kindName}'");
                return OfKind(kind);
            }
            case "nthByRadius":
            {
                lexer.Expect('(');
                int index = (int)lexer.ReadNumber();
                lexer.Expect(')');
                return NthByRadius(index);
            }
            case "groupAlong":
            {
                lexer.Expect('(');
                var inner = ParseFrom(lexer);
                lexer.Expect(',');
                var direction = lexer.ReadVector();
                lexer.Expect(',');
                int index = (int)lexer.ReadNumber();
                lexer.Expect(')');
                return GroupAlong(inner, direction, index);
            }
            case "optional":
            {
                lexer.Expect('(');
                var inner = ParseFrom(lexer);
                lexer.Expect(')');
                return inner.Optional();
            }
            case "opaque":
                throw RefLexer.Opaque("face");
            default:
                throw new FormatException($"unknown face query '{name}'");
        }
    }

    // ---- implementations ----

    private sealed class AllFaces : FaceSetRef
    {
        public override string Descriptor => "all";
        public override string Subject => "face";
        public override bool IsSerializable => true;
        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName) => solid.Faces;
    }

    private sealed class PlanarFaces(Vector3d normal) : FaceSetRef
    {
        public override string Descriptor => RefSyntax.Call("planar", RefSyntax.Vector(normal));
        public override string Subject => $"planar face with outward normal {Describe(normal)}";
        public override bool IsSerializable => true;
        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName) =>
            solid.PlanarFacesWithNormal(normal);
    }

    private sealed class CylindricalFaces(double? radius) : FaceSetRef
    {
        public override string Descriptor => radius is { } r
            ? RefSyntax.Call("cylindrical", RefSyntax.Number(r))
            : "cylindrical";

        public override string Subject => radius is { } r
            ? $"cylindrical face of radius {r:g6}"
            : "cylindrical face";

        public override bool IsSerializable => true;

        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName)
        {
            foreach (var face in solid.Faces)
            {
                if (!face.IsCylindrical(out _, out _, out double found))
                    continue;
                // Radii come off exactly-constructed surfaces (a bore's cylinder carries
                // the drill's radius verbatim), so this is the 1e-9 weld tier, not a
                // measurement tolerance.
                if (radius is null || Tolerance.Default.AreEqual(found, radius.Value))
                    yield return face;
            }
        }
    }

    private sealed class RimFaces(EdgeSetRef edges) : FaceSetRef
    {
        public override string Descriptor => RefSyntax.Call("rimFacesOf", edges.Descriptor);
        public override string Subject => $"complete planar face rim over the {edges.Subject}s";
        public override RefCardinality Cardinality => RefCardinality.CompleteRim;
        public override bool IsSerializable => edges.IsSerializable;

        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName)
        {
            var selected = edges.Resolve(solid, inputName);
            try
            {
                return Filleting.RimFacesFor(solid, selected);
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
            {
                // RimFacesFor already names the stranded edge; prefix the input so the
                // message reads as one sentence about the declared property.
                throw new GeometryInputException($"{inputName}: {exception.Message}");
            }
        }
    }

    private sealed class KindFaces(SurfaceKind kind) : FaceSetRef
    {
        public override string Descriptor =>
            RefSyntax.Call("kind", kind.ToString().ToLowerInvariant());

        public override string Subject => $"{kind.ToString().ToLowerInvariant()} face";
        public override bool IsSerializable => true;

        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName) =>
            solid.Faces.FilterBy(kind);
    }

    private sealed class RadiusIndexFaces(int index) : FaceSetRef
    {
        public override string Descriptor => RefSyntax.Call("nthByRadius", RefSyntax.Number(index));
        public override string Subject => $"cylindrical face at distinct-radius index {index}";
        public override bool IsSerializable => true;

        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName)
        {
            try
            {
                return solid.Faces.NthByRadius(index);
            }
            catch (InvalidOperationException exception)
            {
                // NthByRadius already names the radii that exist; prefix the input.
                throw new GeometryInputException($"{inputName}: {exception.Message}");
            }
        }
    }

    private sealed class GroupAlongFaces(FaceSetRef faces, Vector3d direction, int index) : FaceSetRef
    {
        public override string Descriptor => RefSyntax.Call(
            "groupAlong", faces.Descriptor, RefSyntax.Vector(direction), RefSyntax.Number(index));

        public override string Subject =>
            $"{faces.Subject} in rank group {index} along {Describe(direction)}";

        public override bool IsSerializable => faces.IsSerializable;

        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName)
        {
            var groups = faces.Resolve(solid, inputName).GroupAlong(direction);
            int resolved = index < 0 ? groups.Count + index : index;
            if (resolved < 0 || resolved >= groups.Count)
                throw new GeometryInputException(
                    $"{inputName}: rank-group index {index} is out of range — the {faces.Subject}s " +
                    $"form {groups.Count} group(s) along {Describe(direction)}.");
            return groups[resolved];
        }
    }

    private sealed class OpaqueFaces(string label, Func<BrepSolid, IEnumerable<BrepFace>> selector) : FaceSetRef
    {
        public override string Descriptor => RefSyntax.Call("opaque", RefSyntax.Label(label));
        public override string Subject => $"face matching '{label}'";
        public override bool IsSerializable => false;
        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName) => selector(solid);
    }

    private sealed class OptionalFaces(FaceSetRef inner) : FaceSetRef
    {
        public override string Descriptor => RefSyntax.Call("optional", inner.Descriptor);
        public override string Subject => inner.Subject;
        public override RefCardinality Cardinality => RefCardinality.Any;
        public override bool IsSerializable => inner.IsSerializable;
        private protected override IEnumerable<BrepFace> Select(BrepSolid solid, string inputName) =>
            RawMatch(inner, solid, inputName);
    }
}

/// <summary>
/// A reference to exactly ONE face. The cardinality lives in the type: a feature that
/// declares a <see cref="FaceRef"/> cannot be handed an ambiguous selection, and the
/// failure says how many it found rather than silently taking the first.
/// </summary>
public abstract class FaceRef : GeometryRef<BrepFace>
{
    private protected FaceRef() { }

    public override RefCardinality Cardinality => RefCardinality.ExactlyOne;
    public override bool RequiresBody => true;

    /// <summary>The one face matching <paramref name="faces"/>; fails when the query is
    /// ambiguous.</summary>
    public static FaceRef One(FaceSetRef faces) => new UniqueFace(faces);

    /// <summary>The face of <paramref name="faces"/> sitting furthest along
    /// <paramref name="direction"/> — the disambiguation rule behind "the top face".
    /// Planar faces are ranked by their plane's offset; anything else by its bounds
    /// centre. Ties keep the first face in the solid's order.</summary>
    public static FaceRef Extreme(FaceSetRef faces, in Vector3d direction) => new ExtremeFace(faces, direction);

    /// <summary>The highest upward-facing planar face — what "the top of the part" means
    /// everywhere in this kernel.</summary>
    public static FaceRef Top { get; } = Extreme(FaceSetRef.PlanarWithNormal(Vector3d.UnitZ), Vector3d.UnitZ);

    /// <summary>The lowest downward-facing planar face.</summary>
    public static FaceRef Bottom { get; } = Extreme(FaceSetRef.PlanarWithNormal(-Vector3d.UnitZ), -Vector3d.UnitZ);

    /// <summary>The face of <paramref name="faces"/> with the largest area
    /// (<see cref="BrepSelection.LargestByArea"/> — exact for planar faces, quadrature for
    /// curved ones; ties keep the first in the solid's order). "The main face" without
    /// measuring it.</summary>
    public static FaceRef LargestByArea(FaceSetRef faces) => new LargestFace(faces);

    /// <summary>The largest face of the whole solid.</summary>
    public static FaceRef Largest { get; } = LargestByArea(FaceSetRef.All);

    public static FaceRef Parse(string descriptor)
    {
        var lexer = new RefLexer(descriptor);
        var reference = ParseFrom(lexer);
        lexer.ExpectEnd();
        return reference;
    }

    internal static FaceRef ParseFrom(RefLexer lexer)
    {
        string name = lexer.ReadIdentifier();
        switch (name)
        {
            case "one":
            {
                lexer.Expect('(');
                var faces = FaceSetRef.ParseFrom(lexer);
                lexer.Expect(')');
                return One(faces);
            }
            case "extreme":
            {
                lexer.Expect('(');
                var faces = FaceSetRef.ParseFrom(lexer);
                lexer.Expect(',');
                var direction = lexer.ReadVector();
                lexer.Expect(')');
                return Extreme(faces, direction);
            }
            case "largest":
            {
                lexer.Expect('(');
                var faces = FaceSetRef.ParseFrom(lexer);
                lexer.Expect(')');
                return LargestByArea(faces);
            }
            default:
                throw new FormatException($"unknown single-face query '{name}'");
        }
    }

    /// <summary>Rank of a face along a direction: the plane offset for planar faces (so
    /// "highest +Z face" is exactly the plane's z), the bounds centre otherwise.</summary>
    private protected static double OffsetAlong(BrepFace face, in Vector3d direction) =>
        face.IsPlanar(out var origin, out _)
            ? origin.Dot(direction)
            : face.Bounds().Center.Dot(direction);

    private sealed class UniqueFace(FaceSetRef faces) : FaceRef
    {
        public override string Descriptor => RefSyntax.Call("one", faces.Descriptor);
        public override string Subject => faces.Subject;
        public override bool IsSerializable => faces.IsSerializable;

        public override BrepFace Resolve(BrepSolid? solid, string inputName)
        {
            var matches = faces.Match(solid, inputName);
            if (matches.Count != 1)
                throw new GeometryInputException(
                    $"{inputName}: expected exactly one {Subject}, found {matches.Count}." +
                    (matches.Count > 1 ? " Narrow the query, or pick one with FaceRef.Extreme." : ""));
            return matches[0];
        }
    }

    private sealed class LargestFace(FaceSetRef faces) : FaceRef
    {
        public override string Descriptor => RefSyntax.Call("largest", faces.Descriptor);
        public override string Subject => $"largest {faces.Subject}";
        public override bool IsSerializable => faces.IsSerializable;

        public override BrepFace Resolve(BrepSolid? solid, string inputName)
        {
            var matches = faces.Match(solid, inputName);
            if (matches.Count == 0)
                throw new GeometryInputException(
                    $"{inputName}: expected the {Subject}, found no {faces.Subject} at all.");
            return matches.LargestByArea();
        }
    }

    private sealed class ExtremeFace(FaceSetRef faces, Vector3d direction) : FaceRef
    {
        public override string Descriptor =>
            RefSyntax.Call("extreme", faces.Descriptor, RefSyntax.Vector(direction));

        public override string Subject => $"{faces.Subject} furthest along {Describe(direction)}";
        public override bool IsSerializable => faces.IsSerializable;

        public override BrepFace Resolve(BrepSolid? solid, string inputName)
        {
            var matches = faces.Match(solid, inputName);
            BrepFace? best = null;
            double bestOffset = double.NegativeInfinity;
            foreach (var face in matches)
            {
                double offset = OffsetAlong(face, direction);
                // Strictly greater: ties keep the first in the solid's face order, which
                // is what the ordered-query spelling this replaced also did.
                if (offset > bestOffset)
                {
                    bestOffset = offset;
                    best = face;
                }
            }
            return best ?? throw new GeometryInputException(
                $"{inputName}: expected the outermost {faces.Subject} along {Describe(direction)}, found none.");
        }
    }
}

// ---------------------------------------------------------------------------
// Planes
// ---------------------------------------------------------------------------

/// <summary>
/// A reference to a sketch placement — the answer to "this feature requires a plane".
/// Resolves to a <see cref="SketchPlane"/>, so everything that already consumes one
/// (<c>Shape.Extrude/Revolve/Sweep/Drill</c>, <c>ComponentSite</c>) takes a
/// <see cref="PlaneRef"/> unchanged.
///
/// <para>A <see cref="SketchPlane"/> converts implicitly, and so does a
/// <c>SketchPlane?</c> whose null means "the body's top plane" — the incumbent spelling
/// on <c>HoleFeature.Plane</c> and <c>ComponentFeature.Face</c>.</para>
/// </summary>
public abstract class PlaneRef : GeometryRef<SketchPlane>
{
    private protected PlaneRef() { }

    public override RefCardinality Cardinality => RefCardinality.ExactlyOne;

    /// <summary>An explicit placement — the escape hatch, and the only form that needs no
    /// body.</summary>
    public static PlaneRef At(in SketchPlane plane) => new ExplicitPlane(plane);

    /// <summary>A sketch placement ON a face: <see cref="SketchPlane.On(BrepFace)"/>'s
    /// conventions verbatim (the surface's own X/Y, the face's outward normal, the outer
    /// loop's centroid as origin).</summary>
    public static PlaneRef On(FaceRef face) => new FacePlane(face);

    /// <inheritdoc cref="On(FaceRef)"/>
    public static PlaneRef On(FaceSetRef faces) => new FacePlane(FaceRef.One(faces));

    /// <summary>
    /// The body's top plane: a WORLD-axis-aligned placement at (0, 0, z) where z is the
    /// height of the highest upward-facing planar face. This is
    /// <see cref="FeatureContext.TopPlane"/> made expressible — the same convention, so
    /// existing drill coordinates are unchanged. Use <see cref="OnTopFace"/> for the
    /// variant whose origin and axes come from the face itself.
    /// </summary>
    public static PlaneRef TopPlane { get; } = new TopPlaneRef();

    /// <summary>The top face's own frame — same face as <see cref="TopPlane"/>, but the
    /// origin is the face's centroid and the axes the surface's own, so 2D coordinates are
    /// measured from the face rather than from the world origin.</summary>
    public static PlaneRef OnTopFace { get; } = On(FaceRef.Top);

    public static PlaneRef WorldXY { get; } = At(SketchPlane.XY);
    public static PlaneRef WorldXZ { get; } = At(SketchPlane.XZ);
    public static PlaneRef WorldYZ { get; } = At(SketchPlane.YZ);

    public static implicit operator PlaneRef(SketchPlane plane) => At(plane);

    /// <summary>The incumbent nullable spelling: null means the body's
    /// <see cref="TopPlane"/>, re-resolved every regeneration.</summary>
    public static implicit operator PlaneRef(SketchPlane? plane) =>
        plane is { } value ? At(value) : TopPlane;

    public static PlaneRef Parse(string descriptor)
    {
        var lexer = new RefLexer(descriptor);
        var reference = ParseFrom(lexer);
        lexer.ExpectEnd();
        return reference;
    }

    internal static PlaneRef ParseFrom(RefLexer lexer)
    {
        string name = lexer.ReadIdentifier();
        switch (name)
        {
            case "topPlane":
                return TopPlane;
            case "on":
            {
                lexer.Expect('(');
                var face = FaceRef.ParseFrom(lexer);
                lexer.Expect(')');
                return On(face);
            }
            case "plane":
            {
                lexer.Expect('(');
                var origin = lexer.ReadVector();
                lexer.Expect(',');
                var x = lexer.ReadVector();
                lexer.Expect(',');
                var y = lexer.ReadVector();
                lexer.Expect(')');
                return At(SketchPlane.At(origin, x, y));
            }
            default:
                throw new FormatException($"unknown plane query '{name}'");
        }
    }

    private sealed class ExplicitPlane(SketchPlane plane) : PlaneRef
    {
        public override string Descriptor => RefSyntax.Call("plane",
            RefSyntax.Vector(plane.Origin), RefSyntax.Vector(plane.XAxis), RefSyntax.Vector(plane.YAxis));

        public override string Subject =>
            $"the explicit plane at {Describe(plane.Origin)} with normal {Describe(plane.Normal)}";

        public override bool IsSerializable => true;
        public override bool RequiresBody => false;
        public override SketchPlane Resolve(BrepSolid? solid, string inputName) => plane;
    }

    private sealed class FacePlane(FaceRef face) : PlaneRef
    {
        public override string Descriptor => RefSyntax.Call("on", face.Descriptor);
        public override string Subject => $"a sketch plane on the {face.Subject}";
        public override bool IsSerializable => face.IsSerializable;
        public override bool RequiresBody => true;

        public override SketchPlane Resolve(BrepSolid? solid, string inputName) =>
            SketchPlane.On(face.Resolve(solid, inputName));
    }

    private sealed class TopPlaneRef : PlaneRef
    {
        public override string Descriptor => "topPlane";
        public override string Subject => "the highest planar face with outward normal (0, 0, 1)";
        public override bool IsSerializable => true;
        public override bool RequiresBody => true;

        public override SketchPlane Resolve(BrepSolid? solid, string inputName)
        {
            var top = FaceRef.Top.Resolve(solid, inputName);
            top.IsPlanar(out var origin, out _);
            // World-axis-aligned on purpose: drill coordinates on the top plane are
            // measured from the world origin, not from the face's centroid.
            return SketchPlane.At((0, 0, origin.Z), Vector3d.UnitX, Vector3d.UnitY);
        }
    }
}

// ---------------------------------------------------------------------------
// Edges
// ---------------------------------------------------------------------------

/// <summary>A reference to a set of edges — the selection vocabulary rim features
/// consume (via <see cref="FaceSetRef.RimFacesOf"/>) and dimensions measure.</summary>
public abstract class EdgeSetRef : GeometryRef<IReadOnlyList<BrepEdge>>
{
    private protected EdgeSetRef() { }

    public override RefCardinality Cardinality => RefCardinality.AtLeastOne;
    public override bool RequiresBody => true;

    private protected abstract IEnumerable<BrepEdge> Select(BrepSolid solid, string inputName);

    /// <summary>The raw match, cardinality contract NOT applied.</summary>
    private protected static IEnumerable<BrepEdge> RawMatch(EdgeSetRef reference, BrepSolid solid, string inputName) =>
        reference.Select(solid, inputName);

    public sealed override IReadOnlyList<BrepEdge> Resolve(BrepSolid? solid, string inputName)
    {
        var edges = Select(RequireSolid(solid, inputName, Subject), inputName).ToList();
        if (edges.Count == 0 && Cardinality != RefCardinality.Any)
            throw new GeometryInputException($"{inputName}: expected at least one {Subject}, found none.");
        return edges;
    }

    /// <summary>This query as the <c>Func</c> <c>Shape.FilletEdges</c>/<c>ChamferEdges</c>
    /// take.</summary>
    public Func<BrepSolid, IEnumerable<BrepEdge>> AsSelector(string inputName) =>
        solid => Resolve(solid, inputName);

    /// <summary>The same query with an empty match accepted.</summary>
    public EdgeSetRef Optional() => this is OptionalEdges ? this : new OptionalEdges(this);

    /// <summary>The outer-loop edges of the selected faces
    /// (<see cref="BrepQueries.RimEdges"/>).</summary>
    public static EdgeSetRef RimOf(FaceSetRef faces) => new RimEdges(faces);

    /// <summary>The solid's convex edges — the ones a chamfer or fillet removes material
    /// from.</summary>
    public static EdgeSetRef Convex { get; } = new ConvexEdges();

    /// <summary>Circular edges (bore rims), optionally of a given radius.</summary>
    public static EdgeSetRef Circular(double? radius = null) => new CircularEdges(radius);

    /// <summary>The circular edges carrying the <paramref name="index"/>-th smallest
    /// distinct radius (<see cref="BrepSelection.NthByRadius(IEnumerable{BrepEdge}, int)"/>;
    /// 0 = smallest, −1 = largest) — "the rims of the middle hole size".</summary>
    public static EdgeSetRef NthByRadius(int index) => new RadiusIndexEdges(index);

    /// <summary>Escape hatch: any lambda over the lowered solid; not serializable.</summary>
    public static EdgeSetRef From(string label, Func<BrepSolid, IEnumerable<BrepEdge>> selector) =>
        new OpaqueEdges(label, selector ?? throw new ArgumentNullException(nameof(selector)));

    public static EdgeSetRef Parse(string descriptor)
    {
        var lexer = new RefLexer(descriptor);
        var reference = ParseFrom(lexer);
        lexer.ExpectEnd();
        return reference;
    }

    internal static EdgeSetRef ParseFrom(RefLexer lexer)
    {
        string name = lexer.ReadIdentifier();
        switch (name)
        {
            case "convex":
                return Convex;
            case "circular":
            {
                if (!lexer.TryConsume('('))
                    return Circular();
                double radius = lexer.ReadNumber();
                lexer.Expect(')');
                return Circular(radius);
            }
            case "rimOf":
            {
                lexer.Expect('(');
                var faces = FaceSetRef.ParseFrom(lexer);
                lexer.Expect(')');
                return RimOf(faces);
            }
            case "nthByRadius":
            {
                lexer.Expect('(');
                int index = (int)lexer.ReadNumber();
                lexer.Expect(')');
                return NthByRadius(index);
            }
            case "optional":
            {
                lexer.Expect('(');
                var inner = ParseFrom(lexer);
                lexer.Expect(')');
                return inner.Optional();
            }
            case "opaque":
                throw RefLexer.Opaque("edge");
            default:
                throw new FormatException($"unknown edge query '{name}'");
        }
    }

    private sealed class RimEdges(FaceSetRef faces) : EdgeSetRef
    {
        public override string Descriptor => RefSyntax.Call("rimOf", faces.Descriptor);
        public override string Subject => $"rim edge of a {faces.Subject}";
        public override bool IsSerializable => faces.IsSerializable;

        private protected override IEnumerable<BrepEdge> Select(BrepSolid solid, string inputName) =>
            faces.Resolve(solid, inputName).SelectMany(f => f.RimEdges()).Distinct();
    }

    private sealed class ConvexEdges : EdgeSetRef
    {
        public override string Descriptor => "convex";
        public override string Subject => "convex edge";
        public override bool IsSerializable => true;
        private protected override IEnumerable<BrepEdge> Select(BrepSolid solid, string inputName) => solid.ConvexEdges();
    }

    private sealed class CircularEdges(double? radius) : EdgeSetRef
    {
        public override string Descriptor => radius is { } r
            ? RefSyntax.Call("circular", RefSyntax.Number(r))
            : "circular";

        public override string Subject => radius is { } r
            ? $"circular edge of radius {r:g6}"
            : "circular edge";

        public override bool IsSerializable => true;

        private protected override IEnumerable<BrepEdge> Select(BrepSolid solid, string inputName)
        {
            foreach (var edge in solid.Edges)
            {
                // Weld tier: an arc's radius is exactly the one it was constructed with.
                if (edge.IsCircular(out _, out _, out double found) &&
                    (radius is null || Tolerance.Default.AreEqual(found, radius.Value)))
                    yield return edge;
            }
        }
    }

    private sealed class RadiusIndexEdges(int index) : EdgeSetRef
    {
        public override string Descriptor => RefSyntax.Call("nthByRadius", RefSyntax.Number(index));
        public override string Subject => $"circular edge at distinct-radius index {index}";
        public override bool IsSerializable => true;

        private protected override IEnumerable<BrepEdge> Select(BrepSolid solid, string inputName)
        {
            try
            {
                return solid.Edges.NthByRadius(index);
            }
            catch (InvalidOperationException exception)
            {
                throw new GeometryInputException($"{inputName}: {exception.Message}");
            }
        }
    }

    private sealed class OpaqueEdges(string label, Func<BrepSolid, IEnumerable<BrepEdge>> selector) : EdgeSetRef
    {
        public override string Descriptor => RefSyntax.Call("opaque", RefSyntax.Label(label));
        public override string Subject => $"edge matching '{label}'";
        public override bool IsSerializable => false;
        private protected override IEnumerable<BrepEdge> Select(BrepSolid solid, string inputName) => selector(solid);
    }

    private sealed class OptionalEdges(EdgeSetRef inner) : EdgeSetRef
    {
        public override string Descriptor => RefSyntax.Call("optional", inner.Descriptor);
        public override string Subject => inner.Subject;
        public override RefCardinality Cardinality => RefCardinality.Any;
        public override bool IsSerializable => inner.IsSerializable;
        private protected override IEnumerable<BrepEdge> Select(BrepSolid solid, string inputName) =>
            RawMatch(inner, solid, inputName);
    }
}

// ---------------------------------------------------------------------------
// Axes
// ---------------------------------------------------------------------------

/// <summary>
/// A reference to an axis — an origin plus a unit direction, resolved as a
/// <see cref="Ray3d"/>. Patterns and revolves take one; naming the bore instead of typing
/// its coordinates is what keeps a circular pattern centred when the bore moves.
/// </summary>
public abstract class AxisRef : GeometryRef<Ray3d>
{
    private protected AxisRef() { }

    public override RefCardinality Cardinality => RefCardinality.ExactlyOne;

    /// <summary>An explicit axis; needs no body.</summary>
    public static AxisRef Of(in Vector3d origin, in Vector3d direction) => new ExplicitAxis(origin, direction);

    /// <summary>The axis of a cylindrical face — "pattern about that bore".</summary>
    public static AxisRef OfCylindrical(FaceRef face) => new CylinderAxis(face);

    /// <inheritdoc cref="OfCylindrical(FaceRef)"/>
    public static AxisRef OfCylindrical(FaceSetRef faces) => new CylinderAxis(FaceRef.One(faces));

    public static AxisRef X { get; } = Of(Vector3d.Zero, Vector3d.UnitX);
    public static AxisRef Y { get; } = Of(Vector3d.Zero, Vector3d.UnitY);
    public static AxisRef Z { get; } = Of(Vector3d.Zero, Vector3d.UnitZ);

    public static AxisRef Parse(string descriptor)
    {
        var lexer = new RefLexer(descriptor);
        var reference = ParseFrom(lexer);
        lexer.ExpectEnd();
        return reference;
    }

    internal static AxisRef ParseFrom(RefLexer lexer)
    {
        string name = lexer.ReadIdentifier();
        switch (name)
        {
            case "axis":
            {
                lexer.Expect('(');
                var origin = lexer.ReadVector();
                lexer.Expect(',');
                var direction = lexer.ReadVector();
                lexer.Expect(')');
                return Of(origin, direction);
            }
            case "axisOf":
            {
                lexer.Expect('(');
                var face = FaceRef.ParseFrom(lexer);
                lexer.Expect(')');
                return OfCylindrical(face);
            }
            default:
                throw new FormatException($"unknown axis query '{name}'");
        }
    }

    private sealed class ExplicitAxis : AxisRef
    {
        private readonly Vector3d _origin;
        private readonly Vector3d _direction;

        public ExplicitAxis(in Vector3d origin, in Vector3d direction)
        {
            if (!direction.TryNormalize(Tolerance.Default, out var unit))
                throw new ArgumentException("An axis needs a nonzero direction.", nameof(direction));
            // A direction is dimensionless, so the absolute 1e-9 weld tier applies to its
            // length directly: one that is ALREADY unit is kept verbatim rather than
            // divided again. That is what makes the descriptor a fixed point under a
            // round trip — re-normalizing a unit vector moves it by an ulp, and the
            // descriptor is the cache key.
            _direction = Tolerance.Default.AreEqual(direction.LengthSquared, 1) ? direction : unit;
            _origin = origin;
        }

        public override string Descriptor =>
            RefSyntax.Call("axis", RefSyntax.Vector(_origin), RefSyntax.Vector(_direction));

        public override string Subject => $"the explicit axis at {Describe(_origin)} along {Describe(_direction)}";
        public override bool IsSerializable => true;
        public override bool RequiresBody => false;
        public override Ray3d Resolve(BrepSolid? solid, string inputName) => new(_origin, _direction);
    }

    private sealed class CylinderAxis(FaceRef face) : AxisRef
    {
        public override string Descriptor => RefSyntax.Call("axisOf", face.Descriptor);
        public override string Subject => $"the axis of the {face.Subject}";
        public override bool IsSerializable => face.IsSerializable;
        public override bool RequiresBody => true;

        public override Ray3d Resolve(BrepSolid? solid, string inputName)
        {
            var target = face.Resolve(solid, inputName);
            if (!target.IsCylindrical(out var origin, out var direction, out _))
                throw new GeometryInputException(
                    $"{inputName}: expected {Subject}, but the face it selected is not cylindrical.");
            return new Ray3d(origin, direction);
        }
    }
}
