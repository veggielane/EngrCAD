namespace EngrCAD.Modeling;

/// <summary>How roomy an ISO 273 clearance hole is.</summary>
public enum ClearanceFit
{
    Close,
    Normal,
    Loose,
}

/// <summary>
/// Standard metric hole recipes (dimensions in millimetres), for use with
/// <see cref="Shape.Drill"/>: ISO 273 clearance holes, DIN 974-style counterbores for
/// socket head cap screws, 90° countersinks for ISO 10642 flat-head screws, coarse-
/// pitch tap pilot holes, and Tappex Trisert® insert pilot holes.
/// Sizes are the metric nominal (3 = M3). <see cref="Tapped"/> produces only the
/// tap-drill pilot; <see cref="Shape.ThreadedHole"/> models the thread itself.
/// </summary>
public static class StandardHoles
{
    private sealed record MetricRow(
        double Close, double Normal, double Loose, double TapDrill,
        double CounterboreDiameter, double CountersinkDiameter,
        double TrisertDiameter, double TrisertLength);

    // Clearance: ISO 273. Tap drills: nominal − coarse pitch. Counterbores: DIN 974
    // (medium row). Countersinks: ISO 10642 head diameter + 0.4 allowance, 90°.
    // Trisert columns: VERIFY against the current Tappex datasheet before production
    // use — catalogue values vary by insert type and host material.
    private static readonly Dictionary<double, MetricRow> Table = new()
    {
        [2.0] = new(2.2, 2.4, 2.6, 1.60, 4.4, 4.6, 3.2, 4.0),
        [2.5] = new(2.7, 2.9, 3.1, 2.05, 5.0, 5.6, 4.0, 5.8),
        [3.0] = new(3.2, 3.4, 3.6, 2.50, 6.5, 7.1, 4.8, 6.4),
        [4.0] = new(4.3, 4.5, 4.8, 3.30, 8.0, 9.4, 6.4, 8.1),
        [5.0] = new(5.3, 5.5, 5.8, 4.20, 10.0, 11.6, 7.1, 9.5),
        [6.0] = new(6.4, 6.6, 7.0, 5.00, 11.0, 13.9, 8.7, 12.7),
        [8.0] = new(8.4, 9.0, 10.0, 6.80, 15.0, 18.4, 10.7, 12.7),
        [10.0] = new(10.5, 11.0, 12.0, 8.50, 18.0, 22.8, 13.1, 16.0),
        [12.0] = new(13.0, 13.5, 14.5, 10.20, 20.0, 27.3, 16.0, 19.0),
    };

    private static MetricRow Row(double size) =>
        Table.TryGetValue(size, out var row)
            ? row
            : throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the standard table (available: {string.Join(", ", Table.Keys.OrderBy(k => k).Select(k => $"M{k:g3}"))}).");

    private static double Bore(MetricRow row, ClearanceFit fit) => fit switch
    {
        ClearanceFit.Close => row.Close,
        ClearanceFit.Loose => row.Loose,
        _ => row.Normal,
    };

    /// <summary>ISO 273 clearance hole for an M<paramref name="size"/> fastener.</summary>
    public static HoleSpec Clearance(double size, ClearanceFit fit = ClearanceFit.Normal) =>
        HoleSpec.Simple(Bore(Row(size), fit));

    /// <summary>Counterbored clearance hole seating an ISO 4762 socket head cap screw
    /// flush (recess depth = head height + 0.5).</summary>
    public static HoleSpec Counterbored(double size, ClearanceFit fit = ClearanceFit.Normal)
    {
        var row = Row(size);
        return HoleSpec.Counterbore(Bore(row, fit), row.CounterboreDiameter, size + 0.5);
    }

    /// <summary>90° countersunk clearance hole seating an ISO 10642 flat-head screw flush.</summary>
    public static HoleSpec Countersunk(double size, ClearanceFit fit = ClearanceFit.Normal)
    {
        var row = Row(size);
        return HoleSpec.Countersink(Bore(row, fit), row.CountersinkDiameter);
    }

    /// <summary>Coarse-thread tap pilot hole (threads themselves are not modeled).</summary>
    public static HoleSpec Tapped(double size) => HoleSpec.Simple(Row(size).TapDrill);

    /// <summary>Pilot hole for a Tappex Trisert® threaded insert. ⚠ Verify diameters
    /// against the current Tappex datasheet for your insert type and material.</summary>
    public static HoleSpec Trisert(double size) => HoleSpec.Simple(Row(size).TrisertDiameter);

    /// <summary>Recommended minimum pilot depth for a Tappex Trisert® of this size
    /// (insert length + 2 mm bottom clearance); pass to <see cref="Shape.Drill"/>.</summary>
    public static double TrisertMinimumDepth(double size) => Row(size).TrisertLength + 2.0;
}
