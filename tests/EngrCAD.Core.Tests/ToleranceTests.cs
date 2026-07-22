using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class ToleranceTests
{
    [Fact]
    public void AreEqual_WithinLinearTolerance()
    {
        var tol = new Tolerance(1e-6, 1e-6);
        Assert.True(tol.AreEqual(1.0, 1.0 + 5e-7));
        Assert.False(tol.AreEqual(1.0, 1.0 + 5e-6));
    }

    [Fact]
    public void IsZero_UsesLinearTolerance()
    {
        var tol = new Tolerance(1e-6, 1e-6);
        Assert.True(tol.IsZero(-5e-7));
        Assert.False(tol.IsZero(2e-6));
    }

    [Fact]
    public void Compare_TreatsNearValuesAsEqual()
    {
        var tol = new Tolerance(1e-6, 1e-6);
        Assert.Equal(0, tol.Compare(1.0, 1.0 + 1e-7));
        Assert.Equal(-1, tol.Compare(1.0, 2.0));
        Assert.Equal(1, tol.Compare(2.0, 1.0));
    }

    [Fact]
    public void IsLessAndIsGreater_RespectTolerance()
    {
        var tol = new Tolerance(1e-6, 1e-6);
        Assert.False(tol.IsLess(1.0, 1.0 + 1e-7));
        Assert.True(tol.IsLess(1.0, 1.0 + 1e-5));
        Assert.False(tol.IsGreater(1.0 + 1e-7, 1.0));
        Assert.True(tol.IsGreater(1.0 + 1e-5, 1.0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1e-9)]
    public void Constructor_RejectsNonPositiveTolerances(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tolerance(bad, 1e-9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tolerance(1e-9, bad));
    }
}
