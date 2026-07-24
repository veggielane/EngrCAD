using Xunit;

namespace EngrCAD.Core.Tests;

public class IntegerTypesTests
{
    [Fact]
    public void Vector3i_OperatorsAndConversions()
    {
        Vector3i v = (1, 2, 3); // tuple conversion
        Assert.Equal(new Vector3i(1, 2, 3), v);
        Assert.Equal((5, 7, 9), v + (4, 5, 6));
        Assert.Equal((-3, -3, -3), (Vector3i)(1, 2, 3) - (4, 5, 6));
        Assert.Equal((2, 4, 6), v * 2);
        Assert.Equal((2, 4, 6), 2 * v);
        Assert.Equal((-1, -2, -3), -v);
        Assert.Equal(2, v[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => v[3]);
        Assert.Equal(6L, v.ComponentProduct);
        Assert.Equal(new Vector3d(1, 2, 3), v.ToVector3d());
        Assert.Equal((1, 2, 3), Vector3i.Min((1, 5, 3), (4, 2, 9)));
        Assert.Equal((4, 5, 9), Vector3i.Max((1, 5, 3), (4, 2, 9)));

        // No overflow in the long product.
        Assert.Equal(8_000_000_000_000_000_000L, new Vector3i(2_000_000, 2_000_000, 2_000_000).ComponentProduct);

        var (x, y, z) = v;
        Assert.Equal((1, 2, 3), (x, y, z));
    }

    [Fact]
    public void Vector2i_OperatorsAndConversions()
    {
        Vector2i v = (3, 4);
        Assert.Equal((4, 6), v + (1, 2));
        Assert.Equal((6, 8), v * 2);
        Assert.Equal(12L, v.ComponentProduct);
        Assert.Equal(new Vector2d(3, 4), v.ToVector2d());
        Assert.True(v == new Vector2i(3, 4));
        Assert.True(v != Vector2i.Zero);
    }

    [Fact]
    public void Interval1i_IsInclusiveAndEnumerable()
    {
        var interval = new Interval1i(2, 5);
        Assert.Equal(4L, interval.Count);
        Assert.True(interval.Contains(2));
        Assert.True(interval.Contains(5));
        Assert.False(interval.Contains(6));
        Assert.Equal([2, 3, 4, 5], interval.ToList());

        var single = new Interval1i(7, 7);
        Assert.Equal(1L, single.Count);
        Assert.Equal([7], single.ToList());

        Assert.Equal(new Interval1i(0, 9), Interval1i.FromCount(10));
        Assert.Throws<ArgumentException>(() => new Interval1i(3, 2));

        Assert.Equal(new Interval1i(3, 5), new Interval1i(0, 5).Intersect(new Interval1i(3, 9)));
        Assert.False(new Interval1i(0, 2).Overlaps(new Interval1i(3, 4)));
        Assert.Throws<InvalidOperationException>(() => new Interval1i(0, 2).Intersect(new Interval1i(3, 4)));
    }

    [Fact]
    public void Interval1i_EnumeratesToIntMaxWithoutWrapping()
    {
        var top = new Interval1i(int.MaxValue - 2, int.MaxValue);
        Assert.Equal([int.MaxValue - 2, int.MaxValue - 1, int.MaxValue], top.ToList());
        Assert.Equal(3L, top.Count);
    }

    [Fact]
    public void AxisAlignedBox3i_InclusiveSemantics()
    {
        var box = new AxisAlignedBox3i((0, 0, 0), (2, 3, 4));
        Assert.Equal((3, 4, 5), box.Counts);
        Assert.Equal(60L, box.Count);
        Assert.True(box.Contains((2, 3, 4)));   // Max is a valid index
        Assert.False(box.Contains((3, 0, 0)));
        Assert.Equal(new Interval1i(0, 2), box.RangeX);
        Assert.Equal(new Interval1i(0, 4), box.RangeZ);

        Assert.Equal(box, AxisAlignedBox3i.FromCounts((3, 4, 5)));
        Assert.Throws<ArgumentException>(() => new AxisAlignedBox3i((0, 0, 0), (-1, 0, 0)));

        var other = new AxisAlignedBox3i((2, 2, 2), (9, 9, 9));
        Assert.True(box.Overlaps(other));
        Assert.Equal(new AxisAlignedBox3i((2, 2, 2), (2, 3, 4)), box.Intersect(other));
        Assert.False(box.Overlaps(new AxisAlignedBox3i((5, 5, 5), (6, 6, 6))));

        Assert.Equal(new AxisAlignedBox3i((-1, -1, -1), (3, 4, 5)), box.Expanded(1));
        Assert.True(box.Contains(new AxisAlignedBox3i((1, 1, 1), (2, 2, 2))));
    }
}
