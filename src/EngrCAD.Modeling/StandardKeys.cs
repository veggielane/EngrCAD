namespace EngrCAD.Modeling;

/// <summary>
/// A parallel-key seat's cross-section as the HUB sees it: the key width b and the hub
/// keyway depth t2 — the notch in a gear or pulley bore reaches t2 above the bore wall
/// on the keyway centreline (the DIN 6885 datum). The SHAFT half (the shaft keyseat
/// depth t1, the key height h) is the shaft's business and deliberately not carried:
/// a hub feature should not restate dimensions it does not cut.
/// </summary>
public readonly record struct KeywaySpec
{
    public KeywaySpec(double width, double hubDepth)
    {
        if (!(width > 0) || !double.IsFinite(width))
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!(hubDepth > 0) || !double.IsFinite(hubDepth))
            throw new ArgumentOutOfRangeException(nameof(hubDepth));
        Width = width;
        HubDepth = hubDepth;
    }

    /// <summary>Key (and keyway) width b, mm.</summary>
    public double Width { get; }

    /// <summary>Hub keyway depth t2, mm — how far past the bore wall the notch reaches
    /// on the keyway centreline.</summary>
    public double HubDepth { get; }
}

/// <summary>
/// DIN 6885-1 parallel-key sizes by shaft diameter — the ⚠ verify-against-datasheet
/// transcription convention (`StandardHoles`, `SheetMaterials`): nominal table figures,
/// stored in the form the datasheet prints so a transcription slip is checkable, and
/// the authority is the standard for your fit class, not this file.
/// </summary>
public static class StandardKeys
{
    // (over, upTo, width b, hub depth t2) — DIN 6885-1, keys b×h with t2 the hub depth.
    private static readonly (double Over, double UpTo, double Width, double HubDepth)[] Rows =
    [
        (6, 8, 2, 1.0),      // 2 x 2
        (8, 10, 3, 1.4),     // 3 x 3
        (10, 12, 4, 1.8),    // 4 x 4
        (12, 17, 5, 2.3),    // 5 x 5
        (17, 22, 6, 2.8),    // 6 x 6
        (22, 30, 8, 3.3),    // 8 x 7
        (30, 38, 10, 3.3),   // 10 x 8
        (38, 44, 12, 3.3),   // 12 x 8
        (44, 50, 14, 3.8),   // 14 x 9
        (50, 58, 16, 4.3),   // 16 x 10
        (58, 65, 18, 4.4),   // 18 x 11
        (65, 75, 20, 4.9),   // 20 x 12
    ];

    /// <summary>The DIN 6885-1 keyway for a shaft of <paramref name="shaftDiameter"/> —
    /// the "over 6 up to and including 8" table convention, refused by name outside the
    /// transcribed 6–75 mm range.</summary>
    public static KeywaySpec For(double shaftDiameter)
    {
        foreach (var (over, upTo, width, hubDepth) in Rows)
        {
            if (shaftDiameter > over && shaftDiameter <= upTo)
                return new KeywaySpec(width, hubDepth);
        }
        throw new ArgumentOutOfRangeException(nameof(shaftDiameter),
            $"DIN 6885-1 is transcribed here for shafts over 6 up to 75 mm; " +
            $"Ø{shaftDiameter:0.###} is outside that range.");
    }
}
