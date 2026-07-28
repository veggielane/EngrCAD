using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshFieldTests
{
    [Fact]
    public void Scalar_CarriesNameUnitsAndValues()
    {
        var field = MeshField.Scalar("von Mises", "MPa", [1, 2, 3]);

        Assert.Equal("von Mises", field.Name);
        Assert.Equal("MPa", field.Units);
        Assert.Equal("von Mises [MPa]", field.Label);
        Assert.Equal(1, field.Components);
        Assert.False(field.IsVector);
        Assert.Equal(3, field.Count);
        Assert.Equal(2, field.ValueAt(1));
    }

    [Fact]
    public void Field_IsImmutable_MutatingTheSourceArrayDoesNotReachIt()
    {
        double[] values = [1, 2, 3];
        var field = MeshField.Scalar("s", "", values);
        values[1] = 99;

        Assert.Equal(2, field.ValueAt(1));
        Assert.Equal(new FieldRange(1, 3), field.Range);
    }

    [Fact]
    public void Vector_ScalarReadingIsTheMagnitude()
    {
        var field = MeshField.Vector("displacement", "mm",
            [new Vector3d(3, 4, 0), new Vector3d(0, 0, 2)]);

        Assert.True(field.IsVector);
        Assert.Equal(2, field.Count);
        Assert.Equal(new Vector3d(3, 4, 0), field.VectorAt(0));
        Assert.Equal(5, field.ScalarAt(0), 12);
        Assert.Equal(2, field.ScalarAt(1), 12);
        Assert.Equal(new FieldRange(2, 5), field.Range);
    }

    [Fact]
    public void ValueAt_OnAVectorField_ThrowsRatherThanReturningAComponent()
    {
        var field = MeshField.Vector("d", "mm", [new Vector3d(1, 0, 0)]);
        var thrown = Assert.Throws<InvalidOperationException>(() => field.ValueAt(0));
        Assert.Contains("vector field", thrown.Message);
    }

    [Fact]
    public void Magnitude_OfAVectorField_IsAScalarFieldOfTheSameLength()
    {
        var field = MeshField.Vector("displacement", "mm",
            [new Vector3d(3, 4, 0), new Vector3d(0, 0, 2)]);
        var magnitude = field.Magnitude();

        Assert.False(magnitude.IsVector);
        Assert.Equal("|displacement|", magnitude.Name);
        Assert.Equal("mm", magnitude.Units);
        Assert.Equal(2, magnitude.Count);
        Assert.Equal(5, magnitude.ValueAt(0), 12);
        Assert.Equal(field.Range, magnitude.Range);
    }

    [Fact]
    public void Magnitude_OfAScalarField_ReturnsTheSameInstance()
    {
        var field = MeshField.Scalar("s", "", [1, 2]);
        Assert.Same(field, field.Magnitude());
    }

    [Fact]
    public void Component_ExtractsOneAxisWithAnAxisSuffixedName()
    {
        var field = MeshField.Vector("u", "mm", [new Vector3d(1, 2, 3), new Vector3d(4, 5, 6)]);

        Assert.Equal("u.X", field.Component(0).Name);
        Assert.Equal("u.Z", field.Component(2).Name);
        Assert.Equal([3.0, 6.0], field.Component(2).Values);
    }

    [Fact]
    public void Scaled_MultipliesEveryComponent()
    {
        var field = MeshField.Vector("u", "mm", [new Vector3d(1, 2, 3)]).Scaled(10);
        Assert.Equal(new Vector3d(10, 20, 30), field.VectorAt(0));
        Assert.Equal("u", field.Name);
    }

    [Fact]
    public void Sample_EvaluatesInVertexIndexOrder()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2)));
        var field = MeshField.Sample(box, "height", "mm", p => p.Z);

        Assert.Equal(box.VertexCount, field.Count);
        for (int v = 0; v < box.VertexCount; v++)
            Assert.Equal(box.GetPosition(v).Z, field.ValueAt(v));
        Assert.Equal(new FieldRange(0, 2), field.Range);
    }

    [Fact]
    public void SampleVector_BuildsAVectorFieldOverTheSameVertexSet()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 2, 2)));
        var field = MeshField.SampleVector(box, "u", "mm", p => new Vector3d(0, 0, p.Z * 0.1));

        Assert.True(field.IsVector);
        Assert.Equal(box.VertexCount, field.Count);
        Assert.Equal(new FieldRange(0, 0.2), field.Range);
    }

    [Fact]
    public void Construction_RefusesARaggedValueArray()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => new MeshField("u", "mm", 3, [1, 2, 3, 4]));
        Assert.Contains("whole number", thrown.Message);
    }

    [Fact]
    public void Construction_RefusesAnUnsupportedComponentCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeshField("t", "", 2, [1, 2]));
    }

    [Fact]
    public void Range_SkipsNaN_SoOneUndefinedValueDoesNotPoisonTheLegend()
    {
        var field = MeshField.Scalar("s", "", [1, double.NaN, 3]);
        Assert.Equal(new FieldRange(1, 3), field.Range);
    }
}

public class FieldRangeTests
{
    [Fact]
    public void Empty_IsTheIdentityForUnion()
    {
        Assert.True(FieldRange.Empty.IsEmpty);
        Assert.Equal(new FieldRange(2, 7), FieldRange.Empty.Union(new FieldRange(2, 7)));
        Assert.Equal(new FieldRange(2, 7), new FieldRange(2, 7).Union(FieldRange.Empty));
    }

    [Fact]
    public void Normalize_MapsEndpointsAndClamps()
    {
        var range = new FieldRange(10, 20);
        Assert.Equal(0, range.Normalize(10));
        Assert.Equal(1, range.Normalize(20));
        Assert.Equal(0.5, range.Normalize(15), 12);
        Assert.Equal(0, range.Normalize(-100));
        Assert.Equal(1, range.Normalize(1e9));
    }

    [Fact]
    public void Normalize_OverAZeroSpan_IsTheMiddleOfTheMap()
    {
        // A constant field has no position to report; 0 or 1 would paint it in an
        // extreme colour and read as a hot spot.
        Assert.Equal(0.5, new FieldRange(4, 4).Normalize(4));
        Assert.Equal(0.5, new FieldRange(4, 4).Normalize(-1));
    }

    [Fact]
    public void SymmetricAboutZero_TakesTheLargerMagnitudeBothWays()
    {
        Assert.Equal(new FieldRange(-7, 7), new FieldRange(-3, 7).SymmetricAboutZero());
        Assert.Equal(new FieldRange(-9, 9), new FieldRange(-9, 2).SymmetricAboutZero());
        Assert.True(FieldRange.Empty.SymmetricAboutZero().IsEmpty);
    }

    [Fact]
    public void Of_SkipsNaNAndReturnsEmptyForNoValues()
    {
        Assert.Equal(new FieldRange(-1, 5), FieldRange.Of([1, -1, double.NaN, 5]));
        Assert.True(FieldRange.Of([]).IsEmpty);
        Assert.True(FieldRange.Of([double.NaN]).IsEmpty);
    }
}
