using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// Where a component's 3D model sits relative to the FOOTPRINT ORIGIN — the KiCad
/// <c>(model … (offset) (rotate) (scale))</c> convention. The model origin IS the footprint
/// origin, and this positions the body relative to it: an <see cref="Offset"/> (mm), a
/// per-axis <see cref="RotationDegrees"/> (degrees, applied X then Y then Z) and a
/// <see cref="Scale"/>.
///
/// <para><b>The struct default is the IDENTITY placement.</b> A default (or explicitly zero)
/// <see cref="Scale"/> component reads as unit — the identity is offset (0,0,0), rotation
/// (0,0,0), scale (1,1,1), which <see cref="Identity"/> spells and which
/// <c>default(ModelPlacement)</c> resolves to (no scale collapses geometry, so a stored zero can
/// only mean "unspecified"). This is why a placement serializes/omits cleanly — a placement that
/// is identity writes nothing.</para>
///
/// <para><b>A quarter turn is exact.</b> <see cref="ToMatrix"/> builds a rotation that is an
/// exact multiple of 90° from sign swaps ((x, y) → (−y, x)), never a <c>cos</c>, so a 90° rotate
/// about Z transposes the footprint-plane bounds to the last bit.</para>
/// </summary>
/// <param name="Offset">The model's translation relative to the footprint origin (mm).</param>
/// <param name="RotationDegrees">Per-axis rotation about X, Y, Z (degrees, CCW).</param>
/// <param name="Scale">Per-axis scale; a zero component reads as unit (see the type remarks).</param>
public readonly record struct ModelPlacement(Vector3d Offset, Vector3d RotationDegrees, Vector3d Scale)
{
    private static readonly Vector3d One = new(1, 1, 1);

    /// <summary>The identity placement: no offset, no rotation, unit scale.</summary>
    public static readonly ModelPlacement Identity = new(Vector3d.Zero, Vector3d.Zero, One);

    /// <summary>A placement from an offset and rotation with unit scale.</summary>
    public ModelPlacement(Vector3d offset, Vector3d rotationDegrees)
        : this(offset, rotationDegrees, One) { }

    /// <summary>A placement that only translates.</summary>
    public ModelPlacement(Vector3d offset) : this(offset, Vector3d.Zero, One) { }

    /// <summary>The scale with a zero component read as unit — the value the matrix uses, so a
    /// <c>default(ModelPlacement)</c> (zero scale) behaves as the identity rather than collapsing
    /// geometry to a point.</summary>
    public Vector3d EffectiveScale => new(
        Scale.X == 0 ? 1 : Scale.X,
        Scale.Y == 0 ? 1 : Scale.Y,
        Scale.Z == 0 ? 1 : Scale.Z);

    /// <summary>True when this places the body exactly where an un-placed body would sit
    /// (no offset, no rotation, unit effective scale). The seating path skips the transform
    /// entirely for an identity placement, which is what keeps the legacy body path
    /// bit-identical.</summary>
    public bool IsIdentity =>
        Offset == Vector3d.Zero && RotationDegrees == Vector3d.Zero && EffectiveScale == One;

    /// <summary>The affine transform this placement applies to the model's local geometry:
    /// translate · rotate · scale (scale first). Rotations that are exact multiples of 90° are
    /// built from sign swaps, so a quarter turn is exact.</summary>
    public Matrix4d ToMatrix()
    {
        var scale = Matrix4d.CreateScale(EffectiveScale);
        var rotation =
            AxisRotation(2, RotationDegrees.Z)
            * AxisRotation(1, RotationDegrees.Y)
            * AxisRotation(0, RotationDegrees.X);
        var translation = Matrix4d.CreateTranslation(Offset);
        return translation * rotation * scale;
    }

    // A single-axis rotation, EXACT for multiples of 90° (entries in {-1, 0, 1}), else cos/sin.
    private static Matrix4d AxisRotation(int axis, double degrees)
    {
        double norm = degrees % 360.0;
        if (norm < 0) norm += 360.0;
        if (norm == 0.0 || norm == 90.0 || norm == 180.0 || norm == 270.0)
        {
            int quarter = (int)Math.Round(norm / 90.0);           // 0..3
            double c = quarter == 0 ? 1 : quarter == 2 ? -1 : 0;
            double s = quarter == 1 ? 1 : quarter == 3 ? -1 : 0;
            return ExactAxisRotation(axis, c, s);
        }
        double r = degrees * Math.PI / 180.0;
        return axis switch
        {
            0 => Matrix4d.CreateRotationX(r),
            1 => Matrix4d.CreateRotationY(r),
            _ => Matrix4d.CreateRotationZ(r),
        };
    }

    private static Matrix4d ExactAxisRotation(int axis, double c, double s) => axis switch
    {
        0 => new Matrix4d(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1),   // X
        1 => new Matrix4d(c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1),   // Y
        _ => new Matrix4d(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),   // Z
    };
}

/// <summary>
/// A component's 3D MODEL — a first-class peer of its <see cref="Symbol"/> and its
/// <see cref="Footprint"/>. It unifies a BODY SOURCE with a <see cref="ModelPlacement"/>
/// relative to the footprint origin, and it comes in two source kinds.
///
/// <list type="bullet">
/// <item><b>File-referenced</b> (<see cref="FromFile"/>): a path to a mesh (<c>.stl</c>/<c>.obj</c>/
/// <c>.off</c>) or a STEP (<c>.step</c>/<c>.stp</c>) file. This travels through the schematic/board
/// file as DATA (the path plus the placement) and is loadable on demand. The path is RECORDED even
/// when the format cannot be loaded — a <c>.wrl</c> (VRML, KiCad's default 3D model format) or an
/// <c>.igs</c>/<c>.iges</c> is refused by name at load time but the reference still round-trips.</item>
/// <item><b>Shape/code</b> (<see cref="FromShape"/>): a <c>Func&lt;Shape&gt;</c> — code, exactly like the
/// legacy <see cref="PartDefinition.Body"/>, so it stays OPAQUE and is NOT serialized (re-attached from
/// a <see cref="PartLibrary"/> by definition name). The legacy <see cref="PartDefinition.Body"/> is the
/// spelling of a code model with the identity placement.</item>
/// </list>
///
/// <para><b>Loading is an explicit act.</b> Constructing a model never touches the filesystem, so a
/// data-only load that only references a path is honest and complete for persistence and connectivity;
/// <see cref="TryLoad"/> (soft) and <see cref="Load"/> (throws) read the geometry when asked, and a
/// missing, unreadable or unsupported file is a not-loaded reference (a reason), never a data-load
/// crash — the "readers never throw on dirty geometry" culture.</para>
/// </summary>
public sealed class ComponentModel3D
{
    private readonly string? _path;
    private readonly Func<Shape>? _builder;

    private ComponentModel3D(string? path, Func<Shape>? builder, ModelPlacement placement)
    {
        _path = path;
        _builder = builder;
        Placement = placement;
    }

    /// <summary>How the body sits relative to the footprint origin.</summary>
    public ModelPlacement Placement { get; }

    /// <summary>The referenced file path, or null for a code model.</summary>
    public string? FilePath => _path;

    /// <summary>True for a file-referenced model (the path travels as data).</summary>
    public bool IsFileReferenced => _path is not null;

    /// <summary>True for a code (shape-builder) model — opaque, not serialized.</summary>
    public bool IsCode => _builder is not null;

    /// <summary>A file-referenced model: a path (loaded on demand) plus a placement.</summary>
    /// <param name="path">A mesh (<c>.stl</c>/<c>.obj</c>/<c>.off</c>) or STEP (<c>.step</c>/<c>.stp</c>)
    /// file. Other formats (<c>.wrl</c>, <c>.igs</c>/<c>.iges</c>) are recorded but not loaded.</param>
    /// <param name="placement">Where the model sits relative to the footprint origin (default identity).</param>
    public static ComponentModel3D FromFile(string path, ModelPlacement placement = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new ComponentModel3D(path, null, placement);
    }

    /// <summary>A code model: a shape builder (opaque, not serialized) plus a placement.</summary>
    /// <param name="builder">Builds the body <see cref="Shape"/> in the model's own frame.</param>
    /// <param name="placement">Where the model sits relative to the footprint origin (default identity).</param>
    public static ComponentModel3D FromShape(Func<Shape> builder, ModelPlacement placement = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new ComponentModel3D(null, builder, placement);
    }

    /// <summary>Whether this model's source is a kind that can be loaded (a code builder, or a
    /// supported file extension). File existence is checked at <see cref="TryLoad"/>, not here.</summary>
    public bool CanLoad => _builder is not null || (_path is not null && ExtensionSupported(_path));

    /// <summary>Loads the model's body geometry (in the model's own frame — the
    /// <see cref="Placement"/> is applied by the seating, not here). A code model runs its builder;
    /// a file model reads its file. Returns null with a <paramref name="error"/> reason on any
    /// failure (missing, unreadable, or an unsupported/not-loaded format) rather than throwing —
    /// data-only inspection is safe.</summary>
    public Shape? TryLoad(out string? error)
    {
        error = null;
        if (_builder is not null)
        {
            try
            {
                return _builder();
            }
            catch (Exception ex)
            {
                error = $"the 3D model builder threw: {ex.Message}";
                return null;
            }
        }

        string path = _path!;
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".stl":
            case ".obj":
            case ".off":
                if (!System.IO.File.Exists(path))
                {
                    error = $"the 3D model file '{path}' does not exist.";
                    return null;
                }
                try
                {
                    return Shape.From(path);
                }
                catch (Exception ex)
                {
                    error = $"could not read the 3D model '{path}': {ex.Message}";
                    return null;
                }

            case ".step":
            case ".stp":
                if (!System.IO.File.Exists(path))
                {
                    error = $"the 3D model file '{path}' does not exist.";
                    return null;
                }
                try
                {
                    var read = StepReader.ReadFile(path);
                    if (read.Solids.Count == 0)
                    {
                        error = $"the STEP model '{path}' contains no solids.";
                        return null;
                    }
                    // A multi-solid STEP model unions its solids (disjoint bodies take the boolean's
                    // fast path); a single-solid model is that one solid.
                    Shape shape = Shape.From(read.Solids[0]);
                    for (int i = 1; i < read.Solids.Count; i++)
                        shape = shape.Union(Shape.From(read.Solids[i]));
                    return shape;
                }
                catch (Exception ex)
                {
                    error = $"could not read the STEP model '{path}': {ex.Message}";
                    return null;
                }

            case ".wrl":
                // KiCad's default 3D model format. There is no VRML reader in EngrCAD, so the
                // reference is recorded but not loaded — refused by name.
                error = $"VRML ('{path}') has no reader in EngrCAD; the reference is recorded but not loaded.";
                return null;

            case ".igs":
            case ".iges":
                error = $"IGES 3D-model loading ('{path}') is not implemented (an IGES import is a face "
                    + "soup needing ShapeHealing); the reference is recorded but not loaded.";
                return null;

            default:
                error = $"unsupported 3D model format '{ext}' ('{path}'); the reference is recorded but not loaded.";
                return null;
        }
    }

    /// <summary>Loads the model's body geometry, throwing on any failure — the explicit hard-load act.</summary>
    /// <exception cref="InvalidOperationException">The model cannot be loaded (missing, unreadable, or an
    /// unsupported/not-loaded format), naming the reason.</exception>
    public Shape Load()
    {
        var shape = TryLoad(out var error);
        return shape ?? throw new InvalidOperationException(
            $"The 3D model could not be loaded: {error}");
    }

    private static bool ExtensionSupported(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".stl" or ".obj" or ".off" or ".step" or ".stp";
    }
}
