namespace EngrCAD.Core;

/// <summary>A closed 1D parameter interval [Start, End], used for curve and surface domains.</summary>
public readonly struct Interval : IEquatable<Interval>
{
    public double Start { get; }
    public double End { get; }

    public Interval(double start, double end)
    {
        if (end < start)
            throw new ArgumentException($"Interval end {end} precedes start {start}.");
        Start = start;
        End = end;
    }

    public static readonly Interval Unit = new(0, 1);

    public double Length => End - Start;
    public double Mid => (Start + End) * 0.5;

    /// <summary>Maps a normalized parameter t ∈ [0, 1] into this interval.</summary>
    public double ParameterAt(double t) => Start + (End - Start) * t;

    /// <summary>Normalizes a value in this interval to [0, 1].</summary>
    public double NormalizedParameterOf(double value) => (value - Start) / (End - Start);

    public bool Contains(double value, in Tolerance tolerance) =>
        value >= Start - tolerance.Linear && value <= End + tolerance.Linear;

    public double Clamp(double value) => Math.Clamp(value, Start, End);

    public bool Equals(Interval other) => Start.Equals(other.Start) && End.Equals(other.End);
    public override bool Equals(object? obj) => obj is Interval i && Equals(i);
    public override int GetHashCode() => HashCode.Combine(Start, End);
    public static bool operator ==(Interval a, Interval b) => a.Equals(b);
    public static bool operator !=(Interval a, Interval b) => !a.Equals(b);

    public override string ToString() => $"[{Start:G17}, {End:G17}]";
}
