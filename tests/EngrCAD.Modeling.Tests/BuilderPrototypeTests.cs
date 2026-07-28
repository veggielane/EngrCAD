using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The builder-style-authoring PROTOTYPE (todo.md, build123d/CadQuery parity): a scoped
/// context accumulating add/subtract/intersect, evaluated against a real bracket beside
/// the incumbent algebra. This file IS the deliverable — the design verdict (an honest
/// no; see design.md §6b) points here, so the comparison stays compilable and the next
/// person to propose a builder starts from evidence rather than taste.
///
/// <para>The three spellings below are structurally identical models. What the exercise
/// established: C# already HAS the accumulate-without-naming-intermediates mode —
/// a mutable local plus compound assignment (<c>bracket |= boss; bracket -= pocket;</c>)
/// — so a builder class adds a lambda, an indentation level and a second vocabulary
/// while removing nothing. The one C#-specific trap the builder would fix, operator
/// precedence (<c>a | b - c</c> parses as <c>a | (b - c)</c>), is already fixed by the
/// named <c>Union/Subtract/Intersect</c> methods, which chain fluently and bind
/// left-to-right by construction.</para>
/// </summary>
public class BuilderPrototypeTests
{
    // ---- the prototype: a delegate-scoped builder (NOT product API) ----

    /// <summary>What build123d's <c>BuildPart</c> would look like here. Deliberately
    /// delegate-scoped: C# <c>using</c> gives no ambient "active builder" for nested
    /// calls the way Python context managers do, so the instance must be passed —
    /// ambient thread-static state was rejected outright (todo.md's "implicit pending
    /// state" refusal: it breaks composition and parallel model construction).</summary>
    private sealed class ShapeBuilder
    {
        private Shape? _current;

        public static Shape Build(Action<ShapeBuilder> model)
        {
            var builder = new ShapeBuilder();
            model(builder);
            return builder._current
                ?? throw new InvalidOperationException("The builder scope added no geometry.");
        }

        public void Add(Shape shape) =>
            _current = _current is null ? shape : _current.Union(shape);

        public void Subtract(Shape shape) =>
            _current = _current?.Subtract(shape)
                ?? throw new InvalidOperationException("Subtract before any Add.");

        public void Intersect(Shape shape) =>
            _current = _current?.Intersect(shape)
                ?? throw new InvalidOperationException("Intersect before any Add.");
    }

    // ---- one real model, three spellings ----

    private static readonly SketchPlane Top =
        SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY);

    private static Shape Boss(double x) =>
        // Centered primitive: z spans 4..20, overlapping the plate transversally
        // (a bottom flush with the plate's would be a coplanar boolean).
        Shape.Cylinder(6, 16).Translate(x, 10, 12);

    private static Shape Pocket() =>
        Shape.Box(30, 16, 12).Translate(0, -8, 8);

    /// <summary>(a) The incumbent algebra: operators + a mutable local. The local IS the
    /// builder — compound assignment is C#'s native accumulation mode.</summary>
    private static Shape BracketAlgebra()
    {
        var bracket = Shape.Extrude(Sketch.Rectangle(60, 40), 8);
        foreach (double x in new[] { -20.0, 20.0 })
            bracket |= Boss(x);
        bracket -= Pocket();
        bracket = bracket.Drill(StandardHoles.Clearance(5),
            LocationSet.At(new Vector2d(-20, 10), new Vector2d(20, 10)), 30, Top);
        return bracket;
    }

    /// <summary>(b) Fluent named methods — same left-to-right order, no precedence to
    /// know, still no builder class.</summary>
    private static Shape BracketFluent() =>
        Shape.Extrude(Sketch.Rectangle(60, 40), 8)
            .Union(Boss(-20))
            .Union(Boss(20))
            .Subtract(Pocket())
            .Drill(StandardHoles.Clearance(5),
                LocationSet.At(new Vector2d(-20, 10), new Vector2d(20, 10)), 30, Top);

    /// <summary>(c) The prototype builder. Note what it costs — a lambda, an indent, a
    /// second vocabulary for the same three verbs — and what it buys over (a): nothing.
    /// Operations that are not booleans (Drill, rim features) do not fit the
    /// add/subtract scope at all and must run outside or after it, which is where the
    /// transliteration visibly breaks down.</summary>
    private static Shape BracketBuilder()
    {
        var body = ShapeBuilder.Build(b =>
        {
            b.Add(Shape.Extrude(Sketch.Rectangle(60, 40), 8));
            foreach (double x in new[] { -20.0, 20.0 })
                b.Add(Boss(x));
            b.Subtract(Pocket());
        });
        return body.Drill(StandardHoles.Clearance(5),
            LocationSet.At(new Vector2d(-20, 10), new Vector2d(20, 10)), 30, Top);
    }

    [Fact]
    public void AllThreeSpellings_ProduceTheSameBracket()
    {
        double algebra = BracketAlgebra().ToMesh().Volume();
        double fluent = BracketFluent().ToMesh().Volume();
        double builder = BracketBuilder().ToMesh().Volume();

        // Identical operation graphs lower identically — exact equality, not tolerance.
        Assert.Equal(algebra, fluent);
        Assert.Equal(algebra, builder);

        // And the model is real: base + two bosses - pocket - two through-holes.
        Assert.InRange(algebra, 1, 60 * 40 * 8 + 2 * Math.PI * 36 * 16);
    }

    [Fact]
    public void BuilderScope_WithNoGeometry_RefusesLoudly()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ShapeBuilder.Build(_ => { }));
        Assert.Contains("no geometry", exception.Message);
    }
}
