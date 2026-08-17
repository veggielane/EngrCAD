using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The time axis over a part's results: <see cref="ResultSequence"/> +
/// <see cref="Part.AddResultSequence"/>. The states themselves were always ordinary
/// results — what a saved document used to lose was the ORDER and the INSTANTS, which
/// lived only in the FieldSequenceTrack an application hand-built.
/// </summary>
public class ResultSequenceTests
{
    private static MeshField Field(double value) =>
        MeshField.Scalar("state", "K", [value, value + 1, value + 2]);

    [Fact]
    public void AddResultSequence_DerivesStepNames_AndAttachesTheFields()
    {
        var part = new Part("plate", Shape.Box(10, 10, 2));
        part.AddResultSequence("Temperature", [(Field(300), 0.0), (Field(320), 0.5), (Field(340), 2.0)]);

        var sequence = part.Sequence("Temperature");
        Assert.NotNull(sequence);
        Assert.Equal(
            ["Temperature @ 0s", "Temperature @ 0.5s", "Temperature @ 2s"],
            sequence!.Steps.Select(s => s.ResultName));
        Assert.Equal([0.0, 0.5, 2.0], sequence.Steps.Select(s => s.Seconds));

        // Every step is an ordinary result under its derived name, so FieldDisplay,
        // the Result dropdown and the VTU export see the states with nothing new.
        foreach (var (name, _) in sequence.Steps)
            Assert.NotNull(part.Result(name));
        Assert.Equal(320, part.Result("Temperature @ 0.5s")!.Values[0]);
    }

    [Fact]
    public void ReplacingASequence_RemovesTheStaleStepsAndKeepsTheReused()
    {
        var part = new Part("plate", Shape.Box(10, 10, 2));
        part.AddResultSequence("T", [(Field(1), 0.0), (Field(2), 1.0)]);
        part.AddResultSequence("T", [(Field(3), 0.0), (Field(4), 2.0)]);

        // The re-run reused t = 0 and moved the second instant: the stale "T @ 1s"
        // must be GONE — a re-solve updates the display instead of accumulating twins.
        Assert.Null(part.Result("T @ 1s"));
        Assert.Equal(3, part.Result("T @ 0s")!.Values[0]);
        Assert.NotNull(part.Result("T @ 2s"));
        Assert.Single(part.ResultSequences);
        Assert.Equal(2, part.Sequence("T")!.Steps.Count);
    }

    [Fact]
    public void ARefusedSequence_LeavesThePartUntouched()
    {
        var part = new Part("plate", Shape.Box(10, 10, 2));
        // Non-increasing times refuse BEFORE any mutation — all-or-nothing.
        Assert.Throws<ArgumentException>(() =>
            part.AddResultSequence("T", [(Field(1), 1.0), (Field(2), 1.0)]));
        Assert.Empty(part.Results);
        Assert.Empty(part.ResultSequences);
    }

    [Fact]
    public void TheSequenceItself_RefusesMalformedRunsByName()
    {
        Assert.Throws<ArgumentException>(() => new ResultSequence("", [("a", 0)]));
        Assert.Throws<ArgumentException>(() => new ResultSequence("T", []));
        Assert.Throws<ArgumentException>(() => new ResultSequence("T", [("", 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ResultSequence("T", [("a", double.NaN)]));
        var backwards = Assert.Throws<ArgumentException>(
            () => new ResultSequence("T", [("a", 1), ("b", 0.5)]));
        Assert.Contains("strictly increase", backwards.Message);
    }

    [Fact]
    public void StepNames_AreADeterministicFunctionOfTheInstant()
    {
        // "R" formatting: the name survives the document file bit-for-bit, so a loaded
        // sequence's steps still name the loaded results.
        Assert.Equal("T @ 0.1s", ResultSequence.StepName("T", 0.1));
        Assert.Equal("T @ 1E-06s", ResultSequence.StepName("T", 1e-6));
    }
}
