// The parametric live-modeling loop. Run with:
//
//     dotnet watch --project samples/EngrCAD.LiveDemo
//
// then edit a [Param] default below and SAVE — the history regenerates (cached
// prefix skipped) and the viewer updates in place. bracket.params.json overrides
// parameter values WITHOUT recompiling. Headless: dotnet run ... -- --export x.stl
using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

return EngrCad.Run(args, BuildScene, "EngrCAD parametric bracket");

static Scene BuildScene()
{
    var history = new FeatureHistory();
    history.Add(new BasePlate());
    history.Add(new BoltCircle());
    history.Add(new FilletRimFeature { Name = "TopFillet", Radius = 2 });

    var overrides = Path.Combine(AppContext.BaseDirectory, "bracket.params.json");
    if (File.Exists(overrides))
    {
        foreach (var warning in history.LoadParameters(File.ReadAllText(overrides)))
            Console.Error.WriteLine($"params: {warning}");
    }

    Console.WriteLine(history.Regenerate().ToString());

    var scene = new Scene(new MeshQuality { SegmentsPerCircle = 48 });
    scene.Add(history.ToPart("bracket", Palette.Steel));
    return scene;
}

/// <summary>The stock: a rounded-rectangle plate.</summary>
sealed class BasePlate : Feature
{
    [Param(Min = 20, Max = 120, Units = "mm")]
    public double Width { get; init; } = 48;

    [Param(Min = 15, Max = 90, Units = "mm")]
    public double Depth { get; init; } = 36;

    [Param(Min = 2, Max = 20, Units = "mm")]
    public double Thickness { get; init; } = 8;

    [Param(Min = 1, Max = 10, Units = "mm")]
    public double CornerRadius { get; init; } = 5;

    public override Shape Apply(FeatureContext context) =>
        Shape.Extrude(Sketch.RoundedRectangle(Width, Depth, CornerRadius), Thickness);
}

/// <summary>Counterbored fasteners on a bolt circle, drilled on the top face.</summary>
sealed class BoltCircle : Feature
{
    [Param(Min = 2, Max = 16)]
    public int Count { get; init; } = 5;

    [Param(Min = 4, Max = 50, Units = "mm")]
    public double CircleRadius { get; init; } = 11;

    [Param(Min = 2, Max = 12, Units = "mm", Description = "Metric fastener nominal (M4 = 4)")]
    public double FastenerSize { get; init; } = 4;

    public override Shape Apply(FeatureContext context)
    {
        var points = Enumerable.Range(0, Count)
            .Select(i => 2 * Math.PI * i / Count)
            .Select(a => new Vector2d(CircleRadius * Math.Cos(a), CircleRadius * Math.Sin(a)))
            .ToList();
        return context.Body!.Drill(StandardHoles.Counterbored(FastenerSize), points, 30, context.TopPlane);
    }
}
