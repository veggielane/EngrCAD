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

### Optional parameters

A parameter that means "not stated — inherit it from somewhere else" is spelt with the
nullable type. `null` is a value JSON already has, so it round-trips; the regeneration
cache key renders it `"null"`, so clearing one re-runs the feature; and a
`[Param(Min=, Max=)]` range does not fire on it, because a value that is not there
cannot be out of range.

```csharp run:feature-optional-params
sealed class Spacer : Feature
{
    [Param(Min = 1, Units = "mm")] public double Height { get; init; } = 10;

    // null = "take the standard bore", not a magic 0.
    [Param(Min = 0.1, Units = "mm", Description = "Bore radius override; empty inherits")]
    public double? BoreRadius { get; init; }

    public override Shape Apply(FeatureContext c) =>
        Shape.Cylinder(8, Height)
        - Shape.Cylinder(BoreRadius ?? 3, Height * 2).Translate(0, 0, -Height / 2);
}

var history = new FeatureHistory();
history.Add(new Spacer { BoreRadius = 2.5 });
if (!history.Regenerate().Succeeded) throw new Exception("regeneration failed");

string saved = history.SaveParameters();                   // {"Spacer":{...,"BoreRadius":2.5}}
history.LoadParameters("""{ "Spacer": { "BoreRadius": null } }""");
if (((Spacer)history.Features[0]).BoreRadius is not null) throw new Exception("not cleared");
if (!history.Regenerate().Succeeded) throw new Exception("null failed the Min = 0.1 range");

history.LoadParameters(saved);                             // and back again
if (((Spacer)history.Features[0]).BoreRadius != 2.5) throw new Exception("value lost");
```

**Which spelling to reach for is decided by the editor, not by taste.** The properties
panel offers a *slider* exactly when `[Param(Min=, Max=)]` is finite at both ends, and a
slider is a total function onto its range with no way to say "unset" — so a parameter
behind one can be moved off "inherit" and never back. The rule: *a parameter whose editor
can express absence (a text box: empty shows it, `null` sets it) takes the nullable type;
one whose editor cannot keeps a sentinel outside its own legal range.*
`EdgeFlangeFeature` applies both halves — `Width` and `BendRadius` are `double?`, while
`KFactor` keeps a sentinel `0` (which a K-factor may never be) because its range is
finite and its slider's minimum *is* 0.

## The feature registry and whole-history JSON

`SaveParameters` re-tunes an existing history; **`SaveHistory` / `LoadHistory` rebuild
the history itself** — ordered records of type, name, suppression, constructor inputs
and `[Param]` values. The construction side is `FeatureRegistry`: it lists every known
feature type with its parameter metadata (the UI-insertion catalogue) and says honestly
which are **data-constructible** — `[Param]`-only features (fillets, chamfers,
patterns) construct directly, and holes and sketch extrude/revolves reconstruct from
their saved inputs (sketches serialize through the exact public `Curve2d` vocabulary,
so lines, circular and elliptical arcs and Béziers round-trip with nothing flattened —
a coverage the test suite enumerates from the segment types, because the writer's
default case throws and `Document.Save` does not catch it).

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

A placed [standard component](components.md) is data too: `ComponentFeature` writes its
catalogue item as a **kind plus the factory arguments that built it**, never as its
`Designation` — "ISO 4762 M6×20" says nothing about the clearance fit, the seating or
whether the socket is modelled, and a lossy key is how a reload comes back as a
plausible *different* screw. So a host prepared by placed fasteners reopens parametric
and its bores move when the plate does.

Your own `[Param]`-only feature classes join with
`registry.Register<MyFeature>()` (or `Register(type, factory)` plus a
`Feature.SaveInputs` override for constructor inputs). **What cannot round-trip, and
why:** `BooleanFeature` (an arbitrary `Shape` graph has no serialized form),
`VariableChamferRimFeature` (its setback law is code), a `ComponentFeature` holding a
component outside the built-in catalogue (your own `HardwareComponent` subclass — it is
refused at *save* time rather than written as something a load rebuilds wrong), and
`Feature.FromFunc` lambdas. `SaveHistory`
still *writes* them — type, name, parameters — and `LoadHistory` either skips them
with a warning naming each, or hands the record to your `resolveOpaque` hook to supply
the instance. One place that still bites: `ComponentAssembly(name, shape)` seeds its
history with a lambda over an arbitrary `Shape`, so a host built *that* way keeps one
opaque record — the `BooleanFeature` limitation showing through, not a component one.
