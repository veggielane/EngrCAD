# Parametric features

A feature is a class with `[Param]` properties and a pure `Apply` body; a model is an
ordered `FeatureHistory` that regenerates on change — FeatureScript, but plain C#.

```csharp render:feature-history
sealed class BasePlate : Feature
{
    [Param(Min = 20, Units = "mm")] public double Width { get; init; } = 48;
    [Param(Min = 20, Units = "mm")] public double Depth { get; init; } = 48;
    [Param(Min = 4, Units = "mm")] public double Thickness { get; init; } = 8;

    public override Shape Apply(FeatureContext c) =>
        Shape.Extrude(Sketch.RoundedRectangle(Width, Depth, 6), Thickness);
}

sealed class BoltCircle : Feature
{
    [Param(Min = 2, Max = 24)] public int Count { get; init; } = 6;
    [Param(Min = 5, Units = "mm")] public double Radius { get; init; } = 16;

    public override Shape Apply(FeatureContext c)
    {
        var points = Enumerable.Range(0, Count)
            .Select(i => 2 * Math.PI * i / Count)
            .Select(a => new Vector2d(Radius * Math.Cos(a), Radius * Math.Sin(a)))
            .ToList();
        return c.Body!.Drill(StandardHoles.Counterbored(4), points, depth: 12, c.TopPlane);
    }
}

var history = new FeatureHistory();
history.Add(new BasePlate());
history.Add(new BoltCircle { Count = 8 });

var scene = new Scene();
scene.Add(history.ToPart("bracket", Palette.Steel));
```

![A rounded plate with eight counterbored holes on a bolt circle](images/feature-history.png)

`FeatureContext` gives `Apply` the running body (`c.Body`), a lazily lowered B-Rep
for [selector queries](chamfer-fillet.md) (`c.Lowered`), and the current top plane
(`c.TopPlane`). Features can also *declare* the geometry they need — a plane, a face,
an axis — as typed properties that validate up front and re-resolve every
regeneration: see [geometry inputs](geometry-inputs.md).

## Regeneration

`FeatureHistory.Regenerate` replays the list with **prefix caching** — editing
feature 5 re-runs only 5..n, keyed by instance identity + a `[Param]` snapshot (a
fresh instance always re-runs, which safely covers non-param inputs like sketches and
selectors). Validation runs first (`Min`/`Max` ranges); the first failure keeps the
last good prefix and skips the rest, and per-feature statuses report what happened.
Features can be suppressed (`Suppressed = true`), and `Feature.FromFunc` handles
one-off steps without a class.

Keep `Apply` a **pure function** of its parameters and context — the cache assumes it.

## JSON parameters

`SaveParameters` / `LoadParameters` round-trip every `[Param]` value, so a design is
re-tunable without recompiling:

```csharp run:feature-json
sealed class Plate : Feature
{
    [Param(Min = 10, Max = 100, Units = "mm")] public double Width { get; init; } = 40;

    public override Shape Apply(FeatureContext c) =>
        Shape.Extrude(Sketch.Rectangle(Width, 20), 5);
}

var history = new FeatureHistory();
history.Add(new Plate());
var result = history.Regenerate();
if (!result.Succeeded) throw new Exception(result.ToString());

var json = history.SaveParameters();          // {"Plate":{"Width":40}} (shape may evolve)
Console.WriteLine(json);

var warnings = history.LoadParameters(json);  // re-applies values; reports unknown keys
if (warnings.Count != 0) throw new Exception(string.Join("; ", warnings));
if (!history.Regenerate().Succeeded) throw new Exception("regeneration after load failed");
```

Standard features (`ExtrudeSketchFeature`, `RevolveSketchFeature`, `HoleFeature`,
`FilletRimFeature`, `ChamferRimFeature`, `BooleanFeature`, linear/circular pattern
features) cover simple histories out of the box. Their geometry inputs — the drilling
plane, the rim faces, the pattern axis — are [typed
references](geometry-inputs.md) that round-trip through the same JSON.

## The feature registry and whole-history JSON

`SaveParameters` re-tunes an existing history; **`SaveHistory` / `LoadHistory` rebuild
the history itself** — ordered records of type, name, suppression, constructor inputs
and `[Param]` values. The construction side is `FeatureRegistry`: it lists every known
feature type with its parameter metadata (the UI-insertion catalogue) and says honestly
which are **data-constructible** — `[Param]`-only features (fillets, chamfers,
patterns) construct directly, and holes and sketch extrude/revolves reconstruct from
their saved inputs (sketches serialize through the exact public `Curve2d` vocabulary,
so lines, arcs and Béziers round-trip with nothing flattened).

```csharp run:feature-history-roundtrip
var plate = Sketch.RoundedRectangle(40, 30, 5);
var history = new FeatureHistory();
history.Add(new ExtrudeSketchFeature(plate) { Height = 8, Name = "Plate" });
history.Add(new HoleFeature(StandardHoles.Counterbored(5), [new(-12, 0), new(12, 0)])
    { Depth = 9, Name = "Bolt holes" });
history.Add(new ChamferRimFeature { Setback = 0.5 });

string json = history.SaveHistory();
var loaded = FeatureHistory.LoadHistory(json);      // FeatureRegistry.Default
if (!loaded.Complete) throw new Exception(string.Join("; ", loaded.Warnings));

// The loaded model IS the model — same regeneration, same geometry...
double a = history.Regenerate().Body!.ToMesh().Volume();
double b = loaded.History.Regenerate().Body!.ToMesh().Volume();
if (a != b) throw new Exception("round-trip changed the geometry");
// ...and the file is a fixed point: save -> load -> save is byte-identical.
if (loaded.History.SaveHistory() != json) throw new Exception("unstable serialized form");
```

Your own `[Param]`-only feature classes join with
`registry.Register<MyFeature>()` (or `Register(type, factory)` plus a
`Feature.SaveInputs` override for constructor inputs). **What cannot round-trip, and
why:** `BooleanFeature` (an arbitrary `Shape` graph has no serialized form),
`VariableChamferRimFeature` (its setback law is code), `ComponentFeature` (a catalogue
`HardwareComponent` is a code object), and `Feature.FromFunc` lambdas. `SaveHistory`
still *writes* them — type, name, parameters — and `LoadHistory` either skips them
with a warning naming each, or hands the record to your `resolveOpaque` hook to supply
the instance.
