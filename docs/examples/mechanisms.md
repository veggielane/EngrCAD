# Mechanisms

A **mechanism is the same mate system, driven**. The
[mate solver](assemblies.md) already reports remaining degrees of freedom from a
rank-revealing factorization — a fully-constrained assembly is static, and a
mechanism is a mate system with DOF &gt; 0 plus a driver consuming them. So
mechanisms add no second solver: a vocabulary of **joints** over the existing
mates, scalar **couplings** (screws, gears, cams) beside them in the residual
vector, and a continuation loop around the solve.

| Joint | DOF | Made of |
| --- | --- | --- |
| `Joint.Revolute` | 1 | concentric + coincident (a hinge) |
| `Joint.Prismatic` | 1 | concentric + a spin lock (a slider) |
| `Joint.Cylindrical` | 2 | concentric (a pin in a bore) |
| `Joint.Spherical` | 3 | coincident (a ball joint) |
| `Joint.Planar` | 3 | a planar mate (a face sliding on a face) |
| `Joint.Screw` | 1 | concentric + the pitch coupling θ·P/2π = z |
| `Joint.Fixed` | 0 | a rigid connection |

Every joint's nominal DOF is **asserted against the solver's measured rank** when
it is added — a wrong joint definition fails immediately and by name, not three
sweeps later. Joint ends are ordinary `MateRef`s, so they can come from explicit
coordinates, semantic B-Rep selectors, or typed `FaceRef`/`AxisRef` queries.

## A four-bar linkage

Links are parts, joints are hinges, and posing the linkage is one `SolveAt` with
an angle driver:

```csharp render:mechanism-fourbar
// Crank 15, coupler 40, rocker 30 on a 45 ground span: a Grashof crank-rocker.
Part Bar(string name, double length, double z, PartColor color) => new(name,
    Shape.Extrude(Sketch.Slot(length + 8, 8), 2.5).Translate(length / 2, 0, z), color);

var rig = new Assembly("linkage");
var frame = rig.Add(Bar("frame", 45, -3.2, Palette.Slate));
var crank = rig.Add(Bar("crank", 15, 0, Palette.Coral));
var coupler = rig.Add(Bar("coupler", 40, 2.7, Palette.Sage));
var rocker = rig.Add(Bar("rocker", 30, 5.4, Palette.Sky));

// Author the links ROUGHLY on the elbow-up branch; Assemble polishes exactly.
Frame3d Posed(double x, double y, double angle) => Frame3d.FromXY((x, y, 0),
    (Math.Cos(angle), Math.Sin(angle), 0), (-Math.Sin(angle), Math.Cos(angle), 0));
crank.Frame = Posed(0, 0, 0);
coupler.Frame = Posed(15, 0, 0.84);
rocker.Frame = Posed(45, 0, 1.68);

var z = Vector3d.UnitZ;
var crankPin = Joint.Revolute(
    MateGeometry.Axis(frame, (0, 0, 0), z), MateGeometry.Axis(crank, (0, 0, 0), z), "crank pin");
var mechanism = new Mechanism(rig)
    .Ground(frame)
    .Add(crankPin)
    .Add(Joint.Revolute(MateGeometry.Axis(crank, (15, 0, 0), z), MateGeometry.Axis(coupler, (0, 0, 0), z)))
    .Add(Joint.Revolute(MateGeometry.Axis(coupler, (40, 0, 0), z), MateGeometry.Axis(rocker, (30, 0, 0), z)))
    .Add(Joint.Revolute(MateGeometry.Axis(frame, (45, 0, 0), z), MateGeometry.Axis(rocker, (0, 0, 0), z)));

var assembled = mechanism.Assemble();
if (assembled.RemainingDegreesOfFreedom != 1)     // the 1 DOF IS the mechanism
    throw new Exception(assembled.ToString());
mechanism.SolveAt(MechanismDriver.Angle(crankPin), 0.9);

var scene = new Scene();
scene.AddTab("linkage").Add(rig);
```

![A four-bar linkage posed at a driven crank angle](images/mechanism-fourbar.png)

## Sweeping, velocities, mobility

`Sweep` samples a driver across a range with **continuation**: every step seeds
from the previous converged pose, never the assembled one — reseeding lets the
solver change branch mid-sweep (a four-bar flips elbow-up to elbow-down and the
motion tears). A failed step halves and retries (the solver writes nothing on
failure, so the last good pose is intact), and a sweep that cannot proceed
reports the parameter honestly.

`RatesAt` computes velocities and accelerations **from the analytic Jacobian**
(J·q̇ = −∂C/∂t, then J·q̈ = −r̈₀ with the second-order terms assembled exactly),
and `Mobility()` puts the Grübler/Kutzbach formula beside the measured rank —
disagreement is informative, not an error: overconstrained-but-mobile linkages
(a planar linkage built in space, Bennett, Sarrus) are exactly where the formula
lies and the rank is right.

```csharp run:mechanism-slider-crank
// A slider-crank (r = 5, l = 20), authored exactly assembled at crank angle 90°.
Frame3d Posed(double x, double y, double angle) => Frame3d.FromXY((x, y, 0),
    (Math.Cos(angle), Math.Sin(angle), 0), (-Math.Sin(angle), Math.Cos(angle), 0));
var rig = new Assembly("engine");
var ground = rig.Add(new Part("ground", Shape.Box(4, 2, 1)));
var crank = rig.Add(new Part("crank", Shape.Box(4, 2, 1)));
var rod = rig.Add(new Part("rod", Shape.Box(4, 2, 1)));
var slider = rig.Add(new Part("slider", Shape.Box(4, 2, 1)));
double x0 = Math.Sqrt(20 * 20 - 5 * 5);
crank.Frame = Posed(0, 0, Math.PI / 2);
rod.Frame = Posed(0, 5, Math.Atan2(-5, x0));
slider.Frame = Frame3d.FromXY((x0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);

var z = Vector3d.UnitZ;
var crankPin = Joint.Revolute(
    MateGeometry.Axis(ground, (0, 0, 0), z), MateGeometry.Axis(crank, (0, 0, 0), z), "crank pin");
var slide = Joint.Prismatic(
    MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
    MateGeometry.Axis(slider, (0, 0, 0), Vector3d.UnitX), "slide");
var mechanism = new Mechanism(rig)
    .Ground(ground)
    .Add(crankPin)
    .Add(Joint.Revolute(MateGeometry.Axis(crank, (5, 0, 0), z), MateGeometry.Axis(rod, (0, 0, 0), z)))
    .Add(Joint.Revolute(MateGeometry.Axis(rod, (20, 0, 0), z), MateGeometry.Axis(slider, (0, 0, 0), z)))
    .Add(slide);
mechanism.Assemble();

// A full crank cycle, 49 sampled frames of pure poses (the animation input format).
var study = mechanism.Sweep(MechanismDriver.Angle(crankPin), 0, 2 * Math.PI, frames: 49);
if (!study.Completed) throw new Exception(study.ToString());
if (Math.Abs(crankPin.Angle - 2 * Math.PI) > 1e-8)
    throw new Exception("the unwrapped angle should read a full turn");

// Velocity against the closed form x = R cos θ + √(L² − R² sin²θ), θ = π/2 + t.
const double omega = 2.0, t = 0.4;
var rates = mechanism.RatesAt(MechanismDriver.Angle(crankPin), t, rate: omega);
double theta = Math.PI / 2 + t, s = Math.Sin(theta), c = Math.Cos(theta);
double root = Math.Sqrt(20 * 20 - 5 * 5 * s * s);
double expected = omega * (-5 * s - 5 * 5 * s * c / root);
if (Math.Abs(rates.For("slider").Velocity.X - expected) > 1e-6)
    throw new Exception($"slider velocity {rates.For("slider").Velocity.X} vs closed form {expected}");

// Grübler says −2 (spatial formula, planar linkage); the measured rank says 1.
var mobility = mechanism.Mobility();
if (mobility.Agrees || mobility.MeasuredDegreesOfFreedom != 1)
    throw new Exception(mobility.ToString());
```

## Gears, belts, cams

Higher pairs are **scalar couplings between joint coordinates** — one residual
row each, no new solver. A gear ratio is Δθ₂ = ∓(N₁/N₂)·Δθ₁ (external mesh
counter-rotates), a belt the same with pitch radii, a screw's pitch the same idea
inside `Joint.Screw`:

```csharp run:mechanism-gear
var rig = new Assembly("gearbox");
var housing = rig.Add(new Part("housing", Shape.Box(50, 20, 4)));
var gearA = rig.Add(new Part("gearA", Shape.Cylinder(10, 4)));
var gearB = rig.Add(new Part("gearB", Shape.Cylinder(20, 4)),
    Frame3d.FromXY((30, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
var z = Vector3d.UnitZ;
var pinA = Joint.Revolute(
    MateGeometry.Axis(housing, (0, 0, 0), z), MateGeometry.Axis(gearA, (0, 0, 0), z), "pin A");
var pinB = Joint.Revolute(
    MateGeometry.Axis(housing, (30, 0, 0), z), MateGeometry.Axis(gearB, (0, 0, 0), z), "pin B");
var mechanism = new Mechanism(rig).Ground(housing).Add(pinA).Add(pinB)
    .Add(Coupling.Gear(pinA, pinB, teethA: 20, teethB: 40));

mechanism.SolveAt(MechanismDriver.Angle(pinA), Math.PI / 2);
if (Math.Abs(pinB.Angle + Math.PI / 4) > 1e-8)
    throw new Exception($"20:40 external mesh should counter-rotate at half speed, got {pinB.Angle}");

var rates = mechanism.RatesAt(MechanismDriver.Angle(pinA), Math.PI / 2, rate: 3);
if (Math.Abs(rates.For(pinB).AngleRate + 1.5) > 1e-8)
    throw new Exception("gear rates should follow the ratio exactly");
```

A cam is a coupling whose law is a **profile**: `CamLaw.FromSketch` reads a
radial cam drawn as a `Sketch` about its pivot — every sampled radius is the
outermost crossing of the sketch's *exact* signed distance, and the law between
samples is a C² periodic spline whose slope and curvature feed the Jacobian and
the acceleration analysis. Here the textbook eccentric circular cam lifts a
follower:

```csharp render:mechanism-cam
var profile = Sketch.Circle(new Vector2d(3, 0), 9);        // radius 9, centre 3 off the pivot
var cam = new Part("cam", Shape.Extrude(profile, 4), Palette.Steel);
var follower = new Part("follower",
    Shape.Box(8, 14, 8).Translate(0, 7, 2) | Shape.Box(3, 26, 3).Translate(0, 26, 2),
    Palette.Brass);

var rig = new Assembly("camshaft");
var camOcc = rig.Add(cam);
// The follower rides along +Y; at cam angle 0 the profile reaches √(9² − 3²) up.
var followerOcc = rig.Add(follower,
    Frame3d.FromXY((0, Math.Sqrt(81 - 9), 0), Vector3d.UnitX, Vector3d.UnitY));

var camPin = Joint.Revolute(
    MateGeometry.World((0, 0, 0), Vector3d.UnitZ),
    MateGeometry.Axis(camOcc, (0, 0, 0), Vector3d.UnitZ), "cam pin");
var followerSlide = Joint.Prismatic(
    MateGeometry.World((0, 0, 0), Vector3d.UnitY),
    MateGeometry.Axis(followerOcc, (0, 0, 0), Vector3d.UnitY), "follower");
var mechanism = new Mechanism(rig)
    .Add(camPin)
    .Add(followerSlide)
    .Add(Coupling.Cam(camPin, followerSlide,
        CamLaw.FromSketch(profile, followerAngle: Math.PI / 2)));

mechanism.SolveAt(MechanismDriver.Angle(camPin), 2.2);     // the follower rides the profile

var scene = new Scene();
scene.AddTab("cam").Add(rig);
```

![An eccentric circular cam lifting a point follower](images/mechanism-cam.png)

## Limits, dead centres, honest stops

`WithLimits` puts hard stops on a revolute (degrees) or prismatic (length)
joint. A solve past a stop is **rolled back completely** and refused naming the
joint; a sweep walks up to the stop and reports it. At a **dead centre** the
Jacobian loses rank along the driven direction — the sweep detects it (the same
rank machinery behind the DOF report, with a widened threshold for the
diagnosis), names the driven joint and the parameter, and *refuses to guess a
branch*; the same pose driven from a different joint is harmless and says
nothing.

```csharp run:mechanism-limits
var rig = new Assembly("rig");
var fixedOne = rig.Add(new Part("base", Shape.Box(4, 2, 1)));
var moving = rig.Add(new Part("door", Shape.Box(4, 2, 1)));
var hinge = Joint.Revolute(
    MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
    MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ), "hinge").WithLimits(-45, 45);
var mechanism = new Mechanism(rig).Ground(fixedOne).Add(hinge);

var study = mechanism.Sweep(MechanismDriver.Angle(hinge), 0, Math.PI / 2, frames: 19);
if (study.Completed) throw new Exception("the sweep should stop at the 45° stop");
if (!study.Diagnostics.Any(d => d.Contains("past its stop") && d.Contains("hinge")))
    throw new Exception("the stop should be reported by joint name");
if (Math.Abs(hinge.AngleDegrees - 45) > 0.5)
    throw new Exception("the assembly should be left ON the stop");
```

## Interference and swept volume

`CheckInterference` runs per-frame clash detection over a study's sampled poses:
instance bounds are the broad phase, transversal mesh crossing the narrow phase —
so parts *resting* on each other are not clashes — with exact intersection
volumes opt-in per confirmed range. `SweptVolume` turns the motion itself into a
`Shape`: implicit-**native** (the part's field lowered once and placed per pose),
mesh via Surface Nets, B-Rep honestly impossible.

```csharp render:mechanism-swept
var rig = new Assembly("rig");
var ground = rig.Add(new Part("ground", Shape.Cylinder(3, 2), Palette.Slate));
var arm = rig.Add(new Part("arm",
    Shape.Extrude(Sketch.Slot(47, 7), 4).Translate(20, 0, 3), Palette.Coral));
rig.Add(new Part("post", Shape.Box(4, 4, 18).Translate(0, 0, 9), Palette.Sky),
    Frame3d.FromXY((30, 18, 0), Vector3d.UnitX, Vector3d.UnitY));

var pin = Joint.Revolute(
    MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
    MateGeometry.Axis(arm, (0, 0, 0), Vector3d.UnitZ), "pin");
var mechanism = new Mechanism(rig).Ground(ground).Add(pin);

var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 61);
var clashes = study.CheckInterference();
if (clashes.Clear) throw new Exception("the arm sweeps through the post — that must be reported");

var swept = new Part("swept volume", study.SweptVolume("arm"), Palette.Teal)
    { DisplayMode = DisplayMode.Translucent };

var scene = new Scene();
var tab = scene.AddTab("sweep");
tab.Add(rig);
tab.Add(swept);
```

![A spinning arm, its translucent swept volume, and the post it clashes with](images/mechanism-swept.png)

The clash report names the pair and the driver-parameter ranges
(`rig/arm × rig/post: [0.42, 0.63]`-style); pairs directly connected by a joint
are skipped by default, because a pin modeled at its bore's exact diameter
interpenetrates once tessellated.
