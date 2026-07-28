using EngrCAD.Core;

namespace EngrCAD.Mesh;

// Simulation results as data on a mesh. This is the seam between a solver and
// everything that displays or exports one: a solver produces MeshFields over a vertex
// set, the document model attaches them to a Part, the viewer colour-maps them and the
// VTU writer exports them. Nothing here knows about elements, materials or physics —
// deliberately, because that keeps the FEA project (which does) on the *producing* side
// of a plain-array boundary and leaves the viewer free of any dependency on it.
//
// It lives in EngrCAD.Mesh rather than in EngrCAD.Modeling for the same reason
// StlWriter/OffWriter do: a field is a property of a MESH, the writers that consume it
// are here, and a future tet mesher (EngrCAD.Fea) can produce fields without taking a
// reference on the whole modelling API.

/// <summary>
/// A closed interval of scalar values — the min/max of a field, and the mapping a
/// colour map is evaluated over. <see cref="Empty"/> is the identity for
/// <see cref="Union"/>, spelled the way <c>Aabb.Empty</c> is (min above max).
/// </summary>
/// <param name="Min">Lowest value.</param>
/// <param name="Max">Highest value.</param>
public readonly record struct FieldRange(double Min, double Max)
{
    /// <summary>The empty range: contains nothing, and is the identity for
    /// <see cref="Union(FieldRange)"/>.</summary>
    public static readonly FieldRange Empty = new(double.PositiveInfinity, double.NegativeInfinity);

    /// <summary>True when no value has been seen (see <see cref="Empty"/>).</summary>
    public bool IsEmpty => Min > Max;

    /// <summary>Max minus min (0 for a constant field, negative only when empty).</summary>
    public double Span => Max - Min;

    /// <summary>
    /// Position of <paramref name="value"/> in [0, 1], clamped at both ends.
    /// <para>A range with <b>zero span</b> (a constant field, or a single sample) maps
    /// everything to <b>0.5</b> — the middle of the colour map. That is the honest
    /// answer: with no variation there is no position to report, and 0 or 1 would
    /// paint a uniform field in an extreme colour and read as a hot spot.</para>
    /// </summary>
    public double Normalize(double value)
    {
        double span = Span;
        if (!(span > 0))
            return 0.5;
        return Math.Clamp((value - Min) / span, 0, 1);
    }

    /// <summary>The smallest range containing both.</summary>
    public FieldRange Union(FieldRange other) =>
        new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));

    /// <summary>The smallest range containing this and <paramref name="value"/>.</summary>
    public FieldRange Union(double value) => new(Math.Min(Min, value), Math.Max(Max, value));

    /// <summary>
    /// The symmetric range <c>[-m, +m]</c> with <c>m = max(|Min|, |Max|)</c> — what a
    /// <b>diverging</b> colour map needs, since its neutral midpoint only means "zero"
    /// when the range is centred on zero. Deliberately explicit rather than applied
    /// automatically when a diverging map is chosen: silently widening a range would
    /// change what the numbers on the legend mean.
    /// </summary>
    public FieldRange SymmetricAboutZero()
    {
        if (IsEmpty)
            return this;
        double m = Math.Max(Math.Abs(Min), Math.Abs(Max));
        return new FieldRange(-m, m);
    }

    /// <summary>The range of a value sequence (<see cref="Empty"/> when it is empty).
    /// NaNs are skipped rather than poisoning the range — a solver that leaves a value
    /// undefined should still get a usable legend for the rest.</summary>
    public static FieldRange Of(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var range = Empty;
        foreach (double value in values)
        {
            if (!double.IsNaN(value))
                range = range.Union(value);
        }
        return range;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        IsEmpty ? "empty" : $"{Min:G6} .. {Max:G6}";
}

/// <summary>
/// A named scalar or vector field sampled at a mesh's <b>vertices</b> — a simulation
/// result (von Mises stress, temperature, displacement), a measured quantity, or an
/// analytic function evaluated over the mesh.
///
/// <para><b>Immutable and deterministic.</b> The values are copied at construction and
/// never handed back as a mutable array, so a field is a value: two consumers cannot
/// disagree about it, and a cached colour buffer keyed on it cannot go stale behind
/// their backs.</para>
///
/// <para><b>Vertex association only, in v1.</b> A field's <see cref="Count"/> is a
/// vertex count, and the consumer that displays one checks it against the mesh it is
/// shown on. Cell-associated fields are a documented gap, not a half-supported mode —
/// <see cref="VtuWriter"/> writes point data only for the same reason.</para>
///
/// <para><b>Scalar reading.</b> Everything that needs one number per vertex — colour
/// mapping, the legend, the range — goes through <see cref="ScalarAt"/>, which is the
/// value itself for a scalar field and the <i>magnitude</i> for a vector one. That is
/// what makes "colour the part by its displacement" work with no extra call, while
/// <see cref="Magnitude"/> still exists for callers that want the derived field as a
/// first-class object (to export it, or to give it its own legend).</para>
/// </summary>
public sealed class MeshField
{
    private readonly double[] _values;
    private FieldRange? _range;

    /// <summary>
    /// A field over <paramref name="values"/>, interleaved by vertex
    /// (<c>values[vertex * components + component]</c>).
    /// </summary>
    /// <param name="name">Display name, e.g. "von Mises". Used on the legend and as the
    /// VTU array name, and it is how <c>Part.FieldDisplay</c> refers to a result.</param>
    /// <param name="units">Unit label, e.g. "MPa" or "mm"; empty for dimensionless.</param>
    /// <param name="components">1 for a scalar field, 3 for a vector field.</param>
    /// <param name="values">Interleaved values; length must be a multiple of
    /// <paramref name="components"/>.</param>
    public MeshField(string name, string units, int components, IReadOnlyList<double> values)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A field needs a non-empty name.", nameof(name));
        if (components is not (1 or 3))
            throw new ArgumentOutOfRangeException(nameof(components),
                $"A field is scalar (1 component) or vector (3); got {components}.");
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count % components != 0)
            throw new ArgumentException(
                $"Field '{name}' has {values.Count} values, which is not a whole number of " +
                $"{components}-component vertices.", nameof(values));

        Name = name;
        Units = units ?? string.Empty;
        Components = components;
        _values = [.. values];
    }

    /// <summary>Display name (legend title, VTU array name, <c>FieldDisplay.Field</c> key).</summary>
    public string Name { get; }

    /// <summary>Unit label ("MPa", "mm", "K"); empty when dimensionless.</summary>
    public string Units { get; }

    /// <summary>1 = scalar, 3 = vector.</summary>
    public int Components { get; }

    /// <summary>Number of vertices the field covers.</summary>
    public int Count => _values.Length / Components;

    /// <summary>True for a 3-component field.</summary>
    public bool IsVector => Components == 3;

    /// <summary>The raw interleaved values (read-only view).</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>A scalar field.</summary>
    public static MeshField Scalar(string name, string units, IReadOnlyList<double> values) =>
        new(name, units, 1, values);

    /// <summary>A vector field, from one <see cref="Vector3d"/> per vertex.</summary>
    public static MeshField Vector(string name, string units, IReadOnlyList<Vector3d> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var interleaved = new double[values.Count * 3];
        for (int i = 0; i < values.Count; i++)
        {
            interleaved[i * 3] = values[i].X;
            interleaved[i * 3 + 1] = values[i].Y;
            interleaved[i * 3 + 2] = values[i].Z;
        }
        return new MeshField(name, units, 3, interleaved);
    }

    /// <summary>A scalar field built by evaluating <paramref name="value"/> at every
    /// vertex of <paramref name="mesh"/>, in vertex-index order — the analytic route
    /// (distance from a datum, an <c>Sdf</c> sampled on the surface, a hand-written load
    /// case) and the one used to demonstrate the pipeline without a solver.</summary>
    public static MeshField Sample(
        HalfEdgeMesh mesh, string name, string units, Func<Vector3d, double> value)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(value);
        var values = new double[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
            values[v] = value(mesh.GetPosition(v));
        return Scalar(name, units, values);
    }

    /// <summary>A vector field built by evaluating <paramref name="value"/> at every
    /// vertex of <paramref name="mesh"/>, in vertex-index order (a displacement field
    /// for the deformed-shape overlay, for instance).</summary>
    public static MeshField SampleVector(
        HalfEdgeMesh mesh, string name, string units, Func<Vector3d, Vector3d> value)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(value);
        var values = new Vector3d[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
            values[v] = value(mesh.GetPosition(v));
        return Vector(name, units, values);
    }

    /// <summary>The value at a vertex of a SCALAR field.</summary>
    /// <exception cref="InvalidOperationException">The field is a vector field (use
    /// <see cref="VectorAt"/> or <see cref="ScalarAt"/>).</exception>
    public double ValueAt(int vertex)
    {
        if (IsVector)
            throw new InvalidOperationException(
                $"Field '{Name}' is a vector field; use VectorAt or ScalarAt (magnitude).");
        return _values[vertex];
    }

    /// <summary>The value at a vertex of a VECTOR field.</summary>
    /// <exception cref="InvalidOperationException">The field is a scalar field.</exception>
    public Vector3d VectorAt(int vertex)
    {
        if (!IsVector)
            throw new InvalidOperationException($"Field '{Name}' is a scalar field; use ValueAt.");
        return new Vector3d(_values[vertex * 3], _values[vertex * 3 + 1], _values[vertex * 3 + 2]);
    }

    /// <summary>The one number per vertex everything that colours or ranges a field
    /// reads: the value for a scalar field, the magnitude for a vector one.</summary>
    public double ScalarAt(int vertex) => IsVector
        ? Math.Sqrt(
            _values[vertex * 3] * _values[vertex * 3]
            + _values[vertex * 3 + 1] * _values[vertex * 3 + 1]
            + _values[vertex * 3 + 2] * _values[vertex * 3 + 2])
        : _values[vertex];

    /// <summary>The range of <see cref="ScalarAt"/> over every vertex, computed once and
    /// cached (the field is immutable, so it cannot go stale).</summary>
    public FieldRange Range => _range ??= ComputeRange();

    private FieldRange ComputeRange()
    {
        var range = FieldRange.Empty;
        for (int v = 0; v < Count; v++)
        {
            double s = ScalarAt(v);
            if (!double.IsNaN(s))
                range = range.Union(s);
        }
        return range;
    }

    /// <summary>
    /// The magnitude of a vector field as a scalar field of its own (named
    /// <c>|Name|</c> unless <paramref name="name"/> says otherwise, units unchanged).
    /// A scalar field returns itself, so callers need no branch.
    /// </summary>
    public MeshField Magnitude(string? name = null)
    {
        if (!IsVector)
            return name is null || name == Name ? this : Renamed(name);
        var values = new double[Count];
        for (int v = 0; v < values.Length; v++)
            values[v] = ScalarAt(v);
        return Scalar(name ?? $"|{Name}|", Units, values);
    }

    /// <summary>
    /// One component of a vector field as a scalar field, named <c>Name.X</c> /
    /// <c>.Y</c> / <c>.Z</c> unless <paramref name="name"/> says otherwise.
    /// </summary>
    public MeshField Component(int component, string? name = null)
    {
        if (!IsVector)
            throw new InvalidOperationException($"Field '{Name}' is a scalar field; it has no components.");
        ArgumentOutOfRangeException.ThrowIfNegative(component);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(component, 2);
        var values = new double[Count];
        for (int v = 0; v < values.Length; v++)
            values[v] = _values[v * 3 + component];
        return Scalar(name ?? $"{Name}.{"XYZ"[component]}", Units, values);
    }

    /// <summary>The same values under a different name (unit label kept).</summary>
    public MeshField Renamed(string name) => new(name, Units, Components, _values);

    /// <summary>Every value scaled by <paramref name="factor"/> — unit conversion, or a
    /// displacement exaggerated for a deformed-shape view.</summary>
    public MeshField Scaled(double factor, string? units = null)
    {
        var values = new double[_values.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = _values[i] * factor;
        return new MeshField(Name, units ?? Units, Components, values);
    }

    /// <summary>"name [units]" — the legend title and the properties-panel label.</summary>
    public string Label => Units.Length == 0 ? Name : $"{Name} [{Units}]";

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Label}: {(IsVector ? "vector" : "scalar")} over {Count} vertices, {Range}";
}
