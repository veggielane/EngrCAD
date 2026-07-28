using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>One placement in a <see cref="LocationSet"/>: a 2D point on the consuming
/// operation's sketch plane plus an in-plane rotation (radians, counter-clockwise about
/// the point). Operations on axisymmetric features (drilling) read only the point;
/// <see cref="Shape.Pattern"/> applies the rotation too.</summary>
public readonly record struct PlaneLocation(Vector2d Point, double Angle)
{
    public static implicit operator PlaneLocation(Vector2d point) => new(point, 0);
}

/// <summary>
/// An immutable, ordered set of in-plane placements — "put this feature at these N
/// poses" as a VALUE every placing operation accepts, instead of each operation growing
/// its own point-list convention. build123d spells this <c>GridLocations</c> /
/// <c>PolarLocations</c> / <c>HexLocations</c>, CadQuery <c>pushPoints</c> /
/// <c>rarray</c> / <c>polarArray</c>; here one <see cref="LocationSet"/> feeds
/// <see cref="Shape.Drill(HoleSpec, LocationSet, double, SketchPlane?)"/>,
/// <see cref="Shape.Pattern(LocationSet, SketchPlane?)"/> and
/// <c>ComponentAssembly.Place</c> — one idea instead of three.
///
/// <para><b>Composable</b>: <see cref="Translate"/>, <see cref="Rotate"/> and
/// <see cref="Concat"/> (or <c>+</c>) derive new sets; everything is immutable and
/// deterministic (grid points run x-fastest ascending, polar counter-clockwise from the
/// start angle).</para>
///
/// <para><b>Serializable</b>: <see cref="Descriptor"/> is a canonical, parseable
/// rendering (<c>grid(3,2,10,8)</c>, <c>polar(6,20,0,1)</c>,
/// <c>translate([5,0],hex(3,3,6))</c>) that <see cref="ToString"/> returns and
/// <see cref="Parse"/> reconstructs — the same descriptor-string pattern as
/// <see cref="GeometryRef"/>, so a <c>[Param]</c> location set contributes an honest
/// regeneration cache key and round-trips through feature JSON.</para>
/// </summary>
public sealed class LocationSet : IReadOnlyList<PlaneLocation>
{
    private readonly PlaneLocation[] _locations;

    private LocationSet(PlaneLocation[] locations, string descriptor)
    {
        _locations = locations;
        Descriptor = descriptor;
    }

    /// <summary>The canonical, parseable rendering of this set — its cache-key term and
    /// its serialized form. <see cref="ToString"/> returns it.</summary>
    public string Descriptor { get; }

    public override string ToString() => Descriptor;

    public int Count => _locations.Length;
    public PlaneLocation this[int index] => _locations[index];
    public IEnumerator<PlaneLocation> GetEnumerator() =>
        ((IEnumerable<PlaneLocation>)_locations).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _locations.GetEnumerator();

    /// <summary>The points alone — what axisymmetric consumers (drilling, component
    /// placement) read.</summary>
    public IReadOnlyList<Vector2d> Points => [.. _locations.Select(l => l.Point)];

    // ---- constructors ----

    /// <summary>Explicit points, rotation zero — CadQuery's <c>pushPoints</c>.</summary>
    public static LocationSet At(params Vector2d[] points) => At((IReadOnlyList<Vector2d>)points);

    /// <inheritdoc cref="At(Vector2d[])"/>
    public static LocationSet At(IReadOnlyList<Vector2d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("A location set needs at least one point.", nameof(points));
        var builder = new StringBuilder("at(");
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(RefSyntax.Vector2(points[i]));
        }
        builder.Append(')');
        return new LocationSet([.. points.Select(p => new PlaneLocation(p, 0))], builder.ToString());
    }

    /// <summary>A line of <paramref name="count"/> locations stepping by
    /// <paramref name="step"/> from the origin (the 2D counterpart of
    /// <see cref="Shape.PatternLinear"/>).</summary>
    public static LocationSet Linear(int count, in Vector2d step)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        var locations = new PlaneLocation[count];
        for (int i = 0; i < count; i++)
            locations[i] = new PlaneLocation(step * i, 0);
        return new LocationSet(locations,
            $"linear({RefSyntax.Number(count)},{RefSyntax.Vector2(step)})");
    }

    /// <summary>
    /// A rectangular grid of <paramref name="countX"/> × <paramref name="countY"/>
    /// locations at the given spacings, CENTRED on the origin (build123d's
    /// <c>GridLocations</c> convention — a 2×2 grid at spacing 10 sits at ±5). Points
    /// run x-fastest, both axes ascending.
    /// </summary>
    public static LocationSet Grid(int countX, int countY, double spacingX, double spacingY)
    {
        if (countX < 1) throw new ArgumentOutOfRangeException(nameof(countX));
        if (countY < 1) throw new ArgumentOutOfRangeException(nameof(countY));
        var locations = new PlaneLocation[countX * countY];
        int k = 0;
        for (int j = 0; j < countY; j++)
        {
            double y = (j - (countY - 1) * 0.5) * spacingY;
            for (int i = 0; i < countX; i++)
            {
                double x = (i - (countX - 1) * 0.5) * spacingX;
                locations[k++] = new PlaneLocation(new Vector2d(x, y), 0);
            }
        }
        return new LocationSet(locations, RefSyntax.Call("grid",
            RefSyntax.Number(countX), RefSyntax.Number(countY),
            RefSyntax.Number(spacingX), RefSyntax.Number(spacingY)));
    }

    /// <summary>
    /// <paramref name="count"/> locations evenly spaced on a full circle of
    /// <paramref name="radius"/> about the origin, counter-clockwise from
    /// <paramref name="startAngle"/> (radians; the full-turn end point is not repeated).
    /// With <paramref name="rotate"/> (the default) each location also carries its polar
    /// angle, so a patterned feature turns with its position — build123d's
    /// <c>PolarLocations</c>; pass false to keep every copy upright.
    /// </summary>
    public static LocationSet Polar(int count, double radius, double startAngle = 0, bool rotate = true)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        var locations = new PlaneLocation[count];
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + 2 * Math.PI * i / count;
            locations[i] = new PlaneLocation(
                new Vector2d(radius * Math.Cos(angle), radius * Math.Sin(angle)),
                rotate ? angle : 0);
        }
        return new LocationSet(locations, RefSyntax.Call("polar",
            RefSyntax.Number(count), RefSyntax.Number(radius),
            RefSyntax.Number(startAngle), rotate ? "1" : "0"));
    }

    /// <summary>
    /// <paramref name="count"/> locations spread over an arc: from
    /// <paramref name="startAngle"/> sweeping <paramref name="sweep"/> radians
    /// counter-clockwise, BOTH end points included (count ≥ 2).
    /// </summary>
    public static LocationSet PolarArc(
        int count, double radius, double startAngle, double sweep, bool rotate = true)
    {
        if (count < 2)
            throw new ArgumentOutOfRangeException(nameof(count),
                "An arc needs at least 2 locations (both end points are included).");
        var locations = new PlaneLocation[count];
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + sweep * i / (count - 1);
            locations[i] = new PlaneLocation(
                new Vector2d(radius * Math.Cos(angle), radius * Math.Sin(angle)),
                rotate ? angle : 0);
        }
        return new LocationSet(locations, RefSyntax.Call("arc",
            RefSyntax.Number(count), RefSyntax.Number(radius), RefSyntax.Number(startAngle),
            RefSyntax.Number(sweep), rotate ? "1" : "0"));
    }

    /// <summary>
    /// A hex-packed field of locations (build123d's <c>HexLocations</c>):
    /// <paramref name="countY"/> rows of <paramref name="countX"/>, adjacent centres
    /// <paramref name="pitch"/> apart within a row, odd rows offset by half a pitch,
    /// rows pitch·√3/2 apart — the closest packing of circles of diameter
    /// <paramref name="pitch"/>. Centred on the origin by the point set's extents;
    /// points run x-fastest, rows ascending.
    /// </summary>
    public static LocationSet Hex(int countX, int countY, double pitch)
    {
        if (countX < 1) throw new ArgumentOutOfRangeException(nameof(countX));
        if (countY < 1) throw new ArgumentOutOfRangeException(nameof(countY));
        if (!(pitch > 0)) throw new ArgumentOutOfRangeException(nameof(pitch));

        double rowStep = pitch * Math.Sqrt(3) / 2;
        var locations = new PlaneLocation[countX * countY];
        int k = 0;
        for (int j = 0; j < countY; j++)
        {
            double rowOffset = (j & 1) == 1 ? pitch / 2 : 0;
            for (int i = 0; i < countX; i++)
                locations[k++] = new PlaneLocation(new Vector2d(i * pitch + rowOffset, j * rowStep), 0);
        }

        // Centre by extents (not the mean), so symmetric fields land on round numbers.
        double minX = locations.Min(l => l.Point.X), maxX = locations.Max(l => l.Point.X);
        double minY = locations.Min(l => l.Point.Y), maxY = locations.Max(l => l.Point.Y);
        var centre = new Vector2d((minX + maxX) / 2, (minY + maxY) / 2);
        for (int i = 0; i < locations.Length; i++)
            locations[i] = new PlaneLocation(locations[i].Point - centre, 0);

        return new LocationSet(locations, RefSyntax.Call("hex",
            RefSyntax.Number(countX), RefSyntax.Number(countY), RefSyntax.Number(pitch)));
    }

    // ---- composition ----

    /// <summary>Every location shifted by <paramref name="offset"/> (rotations kept).</summary>
    public LocationSet Translate(in Vector2d offset)
    {
        var moved = new PlaneLocation[_locations.Length];
        for (int i = 0; i < moved.Length; i++)
            moved[i] = new PlaneLocation(_locations[i].Point + offset, _locations[i].Angle);
        return new LocationSet(moved,
            RefSyntax.Call("translate", RefSyntax.Vector2(offset), Descriptor));
    }

    /// <summary>The whole set rotated by <paramref name="angle"/> radians about the
    /// origin — points rotate AND each location's own rotation advances by the same
    /// angle, so a rotated polar pattern stays a polar pattern.</summary>
    public LocationSet Rotate(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        var turned = new PlaneLocation[_locations.Length];
        for (int i = 0; i < turned.Length; i++)
        {
            var p = _locations[i].Point;
            turned[i] = new PlaneLocation(
                new Vector2d(c * p.X - s * p.Y, s * p.X + c * p.Y),
                _locations[i].Angle + angle);
        }
        return new LocationSet(turned,
            RefSyntax.Call("rotate", RefSyntax.Number(angle), Descriptor));
    }

    /// <summary>This set followed by <paramref name="other"/> — bolt circle plus dowel
    /// pair as one value.</summary>
    public LocationSet Concat(LocationSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new LocationSet([.. _locations, .. other._locations],
            RefSyntax.Call("concat", Descriptor, other.Descriptor));
    }

    public static LocationSet operator +(LocationSet a, LocationSet b) => a.Concat(b);

    // ---- parsing ----

    /// <summary>Reconstructs a set from a <see cref="Descriptor"/>. Round-trips exactly:
    /// numbers use the "R" format and every constructor is deterministic, so
    /// <c>Parse(x.Descriptor)</c> has the same descriptor AND the same locations bit for
    /// bit.</summary>
    public static LocationSet Parse(string descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var lexer = new RefLexer(descriptor);
        var set = ParseFrom(lexer);
        lexer.ExpectEnd();
        return set;
    }

    private static LocationSet ParseFrom(RefLexer lexer)
    {
        string name = lexer.ReadIdentifier();
        switch (name)
        {
            case "at":
            {
                lexer.Expect('(');
                var points = new List<Vector2d> { lexer.ReadVector2() };
                while (lexer.TryConsume(','))
                    points.Add(lexer.ReadVector2());
                lexer.Expect(')');
                return At(points);
            }
            case "linear":
            {
                lexer.Expect('(');
                int count = (int)lexer.ReadNumber();
                lexer.Expect(',');
                var step = lexer.ReadVector2();
                lexer.Expect(')');
                return Linear(count, step);
            }
            case "grid":
            {
                lexer.Expect('(');
                int countX = (int)lexer.ReadNumber();
                lexer.Expect(',');
                int countY = (int)lexer.ReadNumber();
                lexer.Expect(',');
                double spacingX = lexer.ReadNumber();
                lexer.Expect(',');
                double spacingY = lexer.ReadNumber();
                lexer.Expect(')');
                return Grid(countX, countY, spacingX, spacingY);
            }
            case "polar":
            {
                lexer.Expect('(');
                int count = (int)lexer.ReadNumber();
                lexer.Expect(',');
                double radius = lexer.ReadNumber();
                lexer.Expect(',');
                double startAngle = lexer.ReadNumber();
                lexer.Expect(',');
                bool rotate = lexer.ReadNumber() != 0;
                lexer.Expect(')');
                return Polar(count, radius, startAngle, rotate);
            }
            case "arc":
            {
                lexer.Expect('(');
                int count = (int)lexer.ReadNumber();
                lexer.Expect(',');
                double radius = lexer.ReadNumber();
                lexer.Expect(',');
                double startAngle = lexer.ReadNumber();
                lexer.Expect(',');
                double sweep = lexer.ReadNumber();
                lexer.Expect(',');
                bool rotate = lexer.ReadNumber() != 0;
                lexer.Expect(')');
                return PolarArc(count, radius, startAngle, sweep, rotate);
            }
            case "hex":
            {
                lexer.Expect('(');
                int countX = (int)lexer.ReadNumber();
                lexer.Expect(',');
                int countY = (int)lexer.ReadNumber();
                lexer.Expect(',');
                double pitch = lexer.ReadNumber();
                lexer.Expect(')');
                return Hex(countX, countY, pitch);
            }
            case "translate":
            {
                lexer.Expect('(');
                var offset = lexer.ReadVector2();
                lexer.Expect(',');
                var inner = ParseFrom(lexer);
                lexer.Expect(')');
                return inner.Translate(offset);
            }
            case "rotate":
            {
                lexer.Expect('(');
                double angle = lexer.ReadNumber();
                lexer.Expect(',');
                var inner = ParseFrom(lexer);
                lexer.Expect(')');
                return inner.Rotate(angle);
            }
            case "concat":
            {
                lexer.Expect('(');
                var first = ParseFrom(lexer);
                lexer.Expect(',');
                var second = ParseFrom(lexer);
                lexer.Expect(')');
                return first.Concat(second);
            }
            default:
                throw new FormatException($"unknown location-set form '{name}'");
        }
    }

    // ---- placement math ----

    /// <summary>
    /// The world transform that stamps a copy at <paramref name="location"/> on
    /// <paramref name="plane"/>: geometry modeled at the plane's origin is moved to the
    /// location's point and rotated by its angle about the plane normal, in the plane's
    /// own coordinates (a conjugation, so it works on any plane).
    /// </summary>
    public static Matrix4d PoseAt(in PlaneLocation location, SketchPlane plane)
    {
        var local = Matrix4d.CreateTranslation(new Vector3d(location.Point.X, location.Point.Y, 0));
        if (location.Angle != 0) // exact-zero semantic test: identity rotation adds nothing
            local *= Matrix4d.CreateRotationZ(location.Angle);
        var frame = plane.Frame.ToMatrix();
        return frame * local * plane.Frame.Inverse().ToMatrix();
    }
}
