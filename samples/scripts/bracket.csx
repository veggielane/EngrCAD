// A parametric bracket as a .csx model script — the OpenSCAD-style loop with C# as the
// language. Run it live (save the file and the window updates in place):
//
//   dotnet run --project tools/EngrCAD.Script -- samples/scripts/bracket.csx
//
// or headless:
//
//   dotnet run --project tools/EngrCAD.Script -- samples/scripts/bracket.csx --export bracket.step
//   dotnet run --project tools/EngrCAD.Script -- samples/scripts/bracket.csx --render bracket.png

// ---- parameters: edit and save, the window follows ----
double width = 64;
double depth = 42;
double thickness = 8;
double bossDiameter = 18;
double boltSize = 4;          // M4

// ---- a reusable parametric component is a plain C# method returning Shape ----
Shape BossedPlate(double w, double d, double t, double bossD)
{
    var plate = Shape.Extrude(Sketch.RoundedRectangle(w, d, 6), t);
    // The boss reaches from mid-plate to one thickness above it: overlapping INTO the
    // plate keeps the union transverse (never a coplanar face pair).
    var boss = Shape.Cylinder(bossD / 2, 1.5 * t).Translate(0, 0, 1.25 * t);
    return plate | boss;
}

IReadOnlyList<Vector2d> BoltCircle(double w, double d, double margin) =>
[
    new(-w / 2 + margin, -d / 2 + margin),
    new(w / 2 - margin, -d / 2 + margin),
    new(w / 2 - margin, d / 2 - margin),
    new(-w / 2 + margin, d / 2 - margin),
];

var body = BossedPlate(width, depth, thickness, bossDiameter)
    // Margin 12 keeps the Ø8 counterbores clear of the Ø12 rounded corners — close
    // enough to overlap the corner arcs and the boolean refuses the breakout.
    .Drill(StandardHoles.Counterbored(boltSize), BoltCircle(width, depth, 12),
           thickness * 1.05, SketchPlane.At((0, 0, thickness), Vector3d.UnitX, Vector3d.UnitY))
    // Blind tap into the boss — NOT a full thickness deep, which would end the tool
    // exactly on the plate's top plane (a coplanar boolean, refused by name).
    .Drill(StandardHoles.Tapped(6), [new(0, 0)],
           thickness * 0.75, SketchPlane.At((0, 0, thickness * 2), Vector3d.UnitX, Vector3d.UnitY));

var scene = new Scene();
scene.Add(new Part("bracket", body, Palette.Steel));
