using System.Reflection;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Constraint serialization: the declarations that built a <see cref="ConstrainedSketch"/>
/// ride as canonical token records, and <see cref="ConstrainedSketch.LoadConstraints"/>
/// REPLAYS them through the same public methods against the same drawn sketch — so the
/// loaded system is the built one by construction, and the checks with teeth are the byte
/// fixed point, the bit-identical replayed solve, and the reflection coverage that fails
/// when a new public method ships without a replay arm.
/// </summary>
public class SketchConstraintPersistenceTests
{
    /// <summary>A fixture exercising a broad slice of the vocabulary: an arch with a
    /// bézier top and a triangular hole, plus a circle hole for the arc dimensions.</summary>
    private static Sketch Fixture() =>
        Sketch.Start(0, 0)
            .LineTo((40, 0))
            .LineTo((40, 10))
            .BezierTo((30, 30), (10, 30), (0, 0))
            .Close()
            .WithHole(Sketch.Start(-6 + 20, 4).LineTo((6 + 20, 4)).LineTo((20, 9)).Close())
            .WithHole(Sketch.Circle(new Vector2d(8, 6), 2));

    private static ConstrainedSketch Constrained(Sketch drawn)
    {
        var cs = drawn.Constrain();
        cs.Fix(cs.Point(0))
          .Fix(cs.Point(1))
          .Horizontal(cs.Line(0))
          .Vertical(cs.Point(1), cs.Point(2))
          .Distance(cs.Point(0), cs.Point(1), 40)
          .Distance(cs.HolePoint(0, 2), cs.Line(0), 9)
          .PointOn(cs.CenterOf(cs.HoleArc(1, 0)), cs.Curve(2))
          .Radius(cs.HoleArc(1, 0), 2)
          .Tangent(cs.HoleLine(0, 0), cs.Curve(2))
          .Tangent(cs.Curve(2), SketchCurveEnd.End, cs.Line(0), perpendicular: false);
        return cs;
    }

    [Fact]
    public void SaveLoadSave_IsAByteFixedPoint()
    {
        var drawn = Fixture();
        string saved = Constrained(drawn).SaveConstraints();
        var loaded = ConstrainedSketch.LoadConstraints(drawn, saved);
        Assert.Equal(saved, loaded.SaveConstraints());
    }

    [Fact]
    public void AReplayedSystemSolvesBitIdentically()
    {
        // Replay goes through the SAME public methods against the SAME drawing, so the
        // rebuilt system is the built one — asserted the strong way, every solved
        // segment endpoint bit for bit, which an equivalent-but-reordered system fails.
        var drawn = Fixture();
        var original = Constrained(drawn);
        var loaded = ConstrainedSketch.LoadConstraints(drawn, original.SaveConstraints());

        var a = original.TrySolve();
        var b = loaded.TrySolve();
        Assert.True(a.Converged, a.ToString());
        Assert.True(b.Converged, b.ToString());
        Assert.Equal(a.RemainingDegreesOfFreedom, b.RemainingDegreesOfFreedom);

        var segmentsA = a.Sketch!.Segments;
        var segmentsB = b.Sketch!.Segments;
        Assert.Equal(segmentsA.Count, segmentsB.Count);
        for (int i = 0; i < segmentsA.Count; i++)
        {
            AssertBits(segmentsA[i].Start, segmentsB[i].Start);
            AssertBits(segmentsA[i].End, segmentsB[i].End);
        }
    }

    private static void AssertBits(in Vector2d expected, in Vector2d actual)
    {
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.X), BitConverter.DoubleToInt64Bits(actual.X));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.Y), BitConverter.DoubleToInt64Bits(actual.Y));
    }

    /// <summary>
    /// The coverage claim, held by reflection rather than by a fixture list: every
    /// public constraint method on <see cref="ConstrainedSketch"/> must have a record
    /// method the replay switch understands — the
    /// <c>EverySketchSegmentKind_HasAJsonForm</c> treatment, so a method added without
    /// a serialized form fails HERE rather than taking a document down at save time.
    /// </summary>
    [Fact]
    public void EveryPublicConstraintMethod_HasARecordForm()
    {
        var vocabulary = typeof(ConstrainedSketch)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(ConstrainedSketch))
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        // The one intentional indirection: the end-tangency overload records as
        // "TangentAtEnd" so the replay can tell it from the two-entity tangencies.
        var supported = ConstrainedSketch.SupportedRecordMethods
            .Select(n => n == "TangentAtEnd" ? "Tangent" : n)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(supported, vocabulary);
    }

    [Fact]
    public void EveryRecordedMethodNameIsSupported()
    {
        // The fixture's own records must all be replayable — which the round trip
        // proves operationally; this names the first offender when it fails.
        var drawn = Fixture();
        string saved = Constrained(drawn).SaveConstraints();
        var records = System.Text.Json.JsonSerializer.Deserialize<string[][]>(saved)!;
        Assert.All(records, r =>
            Assert.Contains(r[0], ConstrainedSketch.SupportedRecordMethods));
        Assert.Equal(10, records.Length);
    }

    [Fact]
    public void AnUnknownRecordMethodRefusesByName()
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            ConstrainedSketch.LoadConstraints(Fixture(), """[["Symmetric", "point(0)", "point(1)"]]"""));
        Assert.Contains("Symmetric", thrown.Message);
    }

    [Fact]
    public void AMalformedDescriptorRefusesByName()
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            ConstrainedSketch.LoadConstraints(Fixture(), """[["Fix", "notAThing"]]"""));
        Assert.Contains("notAThing", thrown.Message);
    }

    [Fact]
    public void ARefusedConstraintLeavesNoRecordBehind()
    {
        // Recording happens AFTER Add succeeds, so a refusal cannot smuggle a record
        // into the file — asserted through the save, which is what would carry it.
        var drawn = Fixture();
        var cs = drawn.Constrain();
        cs.Fix(cs.Point(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => cs.Radius(cs.HoleArc(1, 0), -1));
        var records = System.Text.Json.JsonSerializer.Deserialize<string[][]>(cs.SaveConstraints())!;
        Assert.Single(records);
        Assert.Equal("Fix", records[0][0]);
    }
}
