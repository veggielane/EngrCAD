using System.Collections;

namespace EngrCAD.Core;

/// <summary>
/// Closed integer interval [Start, End] (both endpoints inclusive), for index ranges
/// over grids and arrays — the integer sibling of <see cref="Interval"/>. Enumerable
/// (allocation-free struct enumerator), so <c>foreach (int i in interval)</c> works.
/// geometry3Sharp: Interval1i.
/// </summary>
public readonly struct Interval1i : IEquatable<Interval1i>, IEnumerable<int>
{
    /// <summary>First index (inclusive).</summary>
    public int Start { get; }

    /// <summary>Last index (inclusive).</summary>
    public int End { get; }

    public Interval1i(int start, int end)
    {
        if (end < start)
            throw new ArgumentException($"Interval end {end} precedes start {start}.");
        Start = start;
        End = end;
    }

    /// <summary>The index range [0, count) as an inclusive interval — g3's Interval1i.Range.</summary>
    public static Interval1i FromCount(int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "Need at least one index.");
        return new Interval1i(0, count - 1);
    }

    /// <summary>Number of integers in the interval.</summary>
    public long Count => (long)End - Start + 1;

    public bool Contains(int value) => value >= Start && value <= End;

    public Interval1i Intersect(Interval1i other)
    {
        int start = Math.Max(Start, other.Start);
        int end = Math.Min(End, other.End);
        if (end < start)
            throw new InvalidOperationException($"Intervals {this} and {other} do not overlap.");
        return new Interval1i(start, end);
    }

    public bool Overlaps(Interval1i other) => Start <= other.End && other.Start <= End;

    public Enumerator GetEnumerator() => new(Start, End);
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<int>
    {
        private readonly int _end;
        private long _current; // long so End = int.MaxValue terminates

        internal Enumerator(int start, int end)
        {
            _end = end;
            _current = (long)start - 1;
        }

        public readonly int Current => (int)_current;
        readonly object IEnumerator.Current => Current;
        public bool MoveNext() => ++_current <= _end;
        public void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }

    public bool Equals(Interval1i other) => Start == other.Start && End == other.End;
    public override bool Equals(object? obj) => obj is Interval1i i && Equals(i);
    public override int GetHashCode() => HashCode.Combine(Start, End);
    public static bool operator ==(Interval1i a, Interval1i b) => a.Equals(b);
    public static bool operator !=(Interval1i a, Interval1i b) => !a.Equals(b);

    public override string ToString() => $"[{Start}, {End}]";
}
