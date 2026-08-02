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

### Roller and offset followers, and the pressure angle

A real follower is rarely a knife edge. A **roller** follower's centre does not ride
the profile — it rides the profile's *planar offset* at the roller radius, and a
planar offset is not a radial one: the shortcut r(θ) + R is wrong by O(R·r′²/r²),
worst exactly where the cam is steepest. `CamLaw.FromSketch` takes a `CamFollower`
and reads the offset **exactly**, with no offset curve ever built: the sketch's
signed distance is a true planar distance outside the profile, so the roller centre
is the outermost crossing of the isolevel `sd = R` along the follower's travel line —
the same march and bisection the point follower gets. An **offset** follower moves
the travel line off the pivot (positive to the *right* of the travel direction),
which is the knob a designer turns to improve the **pressure angle** — reported by
`CamLaw.PressureAngle` from the law's own slope via the instant-centre relation
tan φ = (slope − offset)/distance.

The eccentric circle makes every claim checkable in closed form, because the offset
of a circle is a circle:

```csharp run:mechanism-cam-followers
var profile = Sketch.Circle(new Vector2d(3, 0), 8);        // radius 8, centre 3 off the pivot
const double a = 8, e = 3, r = 2;

// Roller compensation: the centre rides the circle of radius a + r, so the law is
// e·cosθ + √((a+r)² − e²·sin²θ) — NOT the point law plus r, which misses by 0.12
// at θ = π/2 on this very fixture.
var roller = CamLaw.FromSketch(profile, CamFollower.Roller(r));
roller.Evaluate(Math.PI / 2, out double lift, out _, out _);
if (Math.Abs(lift - Math.Sqrt((a + r) * (a + r) - e * e)) > 1e-6)
    throw new Exception("the roller centre rides the planar offset");
var point = CamLaw.FromSketch(profile);
point.Evaluate(Math.PI / 2, out double radial, out _, out _);
if (Math.Abs(radial + r - lift) < 0.1)
    throw new Exception("the radial shortcut should measurably disagree here");

// An offset follower: travel line 2.5 to the right of the pivot, and the pressure
// angle from the law's own slope. On the rise, the offset earns its keep.
var follower = CamFollower.Roller(r, angle: 0, offset: 2.5);
var offsetLaw = CamLaw.FromSketch(profile, follower);
double theta = 4.5;                                        // on the rise (slope > 0)
double improved = Math.Abs(offsetLaw.PressureAngle(theta, follower));
double plain = Math.Abs(roller.PressureAngle(theta, CamFollower.Roller(r)));
if (improved >= plain)
    throw new Exception("a positive offset reduces the rise-side pressure angle");
```

The roller radius never enters the pressure angle (the contact normal passes through
the roller's centre whatever its radius); for a zero-based catalogue rise over a
prime circle, pass `baseDistance: Math.Sqrt(primeRadius * primeRadius - offset * offset)`
so the denominator is the follower centre's true distance.

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

A swept volume built from the study's own frames inherits whatever frame count the
sweep happened to use, which is not a fidelity setting anyone chose. `maxTravel`
replaces it with a bound in **model units** — extra placements are rigidly
interpolated between the recorded frames until no point of the part moves further than
that between consecutive ones:

```csharp run:mechanism-adaptive-sweep
var rig = new Assembly("rig");
var ground = rig.Add(new Part("ground", Shape.Box(2, 2, 1)));
var arm = rig.Add(new Part("arm", Shape.Box(20, 2, 2)));
var pin = Joint.Revolute(
    MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
    MateGeometry.Axis(arm, (0, 0, 0), Vector3d.UnitZ), "pin");
var mechanism = new Mechanism(rig).Ground(ground).Add(pin);

// Nine frames over a full turn: 45 degrees between placements leaves visible scallops.
var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 9);
double disk = Math.PI * 100 * 2;                       // radius 10, thickness 2
double coarse = study.SweptVolume("arm").ToMesh().Volume();
double refined = study.SweptVolume("arm", maxTravel: 0.5).ToMesh().Volume();

if (!(coarse < refined && refined > 0.97 * disk))
    throw new Exception($"refinement should close the gap: {coarse:g6} -> {refined:g6} of {disk:g6}");
```

Travel is measured *exactly*, as the largest displacement of the part's own
bounding-box corners between two poses — not a rotation angle times an assumed radius
— so a body spinning about its own centre costs few extra placements and one on the end
of a long arm costs many. The recorded frames are all kept, so refining can only add
material, and omitting the bound leaves the geometry bit-identical.

## Driving two things at once

`SolveAt` and `Sweep` take a *list*, which is what a 2-DOF mechanism needs: with one
driver the pose is a family and the solver would be picking a member of it. A
cylindrical joint is the smallest honest case — spin and slide on one joint:

```csharp run:mechanism-multi-driver
var rig = new Assembly("rig");
var ground = rig.Add(new Part("base", Shape.Box(4, 2, 1)));
var sleeve = rig.Add(new Part("sleeve", Shape.Box(4, 2, 1)));
var joint = Joint.Cylindrical(
    MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
    MateGeometry.Axis(sleeve, (0, 0, 0), Vector3d.UnitZ));
var mechanism = new Mechanism(rig).Ground(ground).Add(joint);

var spin = MechanismDriver.Angle(joint);
var slide = MechanismDriver.Slide(joint);

var one = mechanism.SolveAt(spin, Math.PI / 4);
if (one.RemainingDegreesOfFreedom != 1) throw new Exception("one driver leaves the slide free");

var both = mechanism.SolveAt([(spin, Math.PI / 4), (slide, 3.5)]);
if (both.RemainingDegreesOfFreedom != 0) throw new Exception("two drivers pin a 2-DOF joint");

// A sweep moves every driver along ONE parameter -- a coordinated motion (here a
// helix: a full turn while sliding 10), not a grid of every combination.
var study = mechanism.Sweep([(spin, 0, 2 * Math.PI), (slide, 0, 10.0)], frames: 21);
if (!study.Completed) throw new Exception(study.ToString());
if (study.Frames[10].Values.Count != 2) throw new Exception("each frame records every driver");
```

Driving the same coordinate twice is refused by name — one coordinate cannot hold two
targets — while two drivers on one joint driving *different* variables is the whole
point. `MotionFrame.Values` carries every driver's value; `Value` stays the first
driver's, so code written for a single-driver study reads exactly what it did.

## Rack and pinion, and the cam-law catalogue

A rack and pinion is Δz = r·Δθ, which is a cam pair with a straight law — so that is
how it is built, rather than as a fourth kind of constraint. It reads the **unwrapped**
angle, so a rack driven through three turns keeps advancing instead of resetting at
every seam:

```csharp run:mechanism-rack
var rig = new Assembly("rig");
var ground = rig.Add(new Part("base", Shape.Box(4, 2, 1)));
var pinion = rig.Add(new Part("pinion", Shape.Cylinder(12.5, 4)));
var rack = rig.Add(new Part("rack", Shape.Box(120, 6, 4)));
var spin = Joint.Revolute(
    MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
    MateGeometry.Axis(pinion, (0, 0, 0), Vector3d.UnitZ), "pinion");
var slide = Joint.Prismatic(
    MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitX),
    MateGeometry.Axis(rack, (0, 0, 0), Vector3d.UnitX), "rack");
var mechanism = new Mechanism(rig).Ground(ground).Add(spin).Add(slide)
    .Add(Coupling.RackAndPinion(spin, slide, pitchRadius: 12.5));

mechanism.Sweep(MechanismDriver.Angle(spin), 0, 3 * 2 * Math.PI, frames: 25);
if (Math.Abs(slide.Displacement - 12.5 * 3 * 2 * Math.PI) > 1e-6)
    throw new Exception($"three turns should advance 3 x 2 pi r, got {slide.Displacement}");
```

For cams, the standard dwell–rise–dwell laws are a catalogue rather than an exercise.
What separates them is what happens where a rise meets a dwell — **cycloidal** and
**modified trapezoid** end with zero acceleration and join a dwell smoothly, while
**harmonic** steps (the classic source of cam noise) and buys the lowest peak velocity
in exchange. Peak acceleration factors are 2π, 8π/(2+π) = 4.888 and π²/2 respectively,
which is the number you choose between them on:

```csharp run:mechanism-cam-laws
double span = Math.PI;                                  // a 180-degree rise
double rise = 10;

var cycloidal = CamLaw.Cycloidal(rise, span);
var trapezoid = CamLaw.ModifiedTrapezoid(rise, span);
var harmonic = CamLaw.HarmonicRise(rise, span);

double Peak(CamLaw law)
{
    double peak = 0;
    for (int i = 0; i <= 4000; i++)
    {
        law.Evaluate(span * i / 4000.0, out _, out _, out double curvature);
        peak = Math.Max(peak, Math.Abs(curvature));
    }
    return peak / (rise / (span * span));
}

if (Math.Abs(Peak(cycloidal) - 2 * Math.PI) > 1e-3) throw new Exception("cycloidal peaks at 2 pi");
if (Math.Abs(Peak(trapezoid) - 4.8881) > 1e-3) throw new Exception("modified trapezoid peaks at 4.888");
if (Math.Abs(Peak(harmonic) - Math.PI * Math.PI / 2) > 1e-3) throw new Exception("harmonic peaks at pi^2/2");

// Chain them into a cycle. Spans are scaled to fill one turn, so a profile stated in
// degrees of its own cycle keeps its shape.
var profile = CamLaw.Segments(
    (90.0, CamLaw.Dwell()),                       // low dwell
    (90.0, CamLaw.Cycloidal(rise, 90)),           // rise
    (90.0, CamLaw.Dwell(rise)),                   // high dwell
    (90.0, CamLaw.Cycloidal(-rise, 90)));         // return

profile.Evaluate(Math.PI, out double top, out _, out _);
if (Math.Abs(top - rise) > 1e-6) throw new Exception("the cycle should be at full lift half way round");
```

A rise **clamps outside its own span** (zero before, its rise after, with zero slope
and curvature at both), which is exactly what lets `Segments` chain it without the
composer having to know anything about the laws it is chaining. Continuity across a
joint stays the segments' business: smoothing it centrally would hide the very property
the catalogue exists to let you choose.

## Involute gear geometry

`Coupling.Gear` constrains a ratio; `Gears.Spur` draws the teeth. A `GearSpec` is
the standard vocabulary — module, tooth count, pressure angle (20° default),
profile shift — with the ISO 53 basic rack (profile A) proportions as overridable
coefficients, and its derived properties state the base-circle identities as
arithmetic: base pitch = π·m·cos α, base diameter = z·m·cos α, tooth thickness
= m·(π/2 + 2x·tan α).

The flank is the closed-form involute of the base circle, entered into the sketch
vocabulary as a tangent-continuous **biarc chain with the deviation reported**
(`GearProfile.MaxFitDeviation`, `BiArcFit`'s convention) — at the default
tolerance of module·10⁻⁴ a flank costs about 16 arcs. Everything else in the
outline is exact by construction: the tip and root arcs, the root fillets (whose
involute tangency is a closed form), and the radial stretch below the base circle,
which meets the involute's cusp tangent-continuously because that cusp tangent
*is* radial. What the factory cannot stand behind it refuses by name: tooth
counts below the rack undercut limit z_min = 2(h_a* − x)/sin²α (where a
generating cutter would trochoid-trim the root), pointed teeth, and fillets that
do not fit their gap.

```csharp render:mechanism-involute
var pinionSpec = new GearSpec(module: 2, teeth: 18);
var wheelSpec = new GearSpec(module: 2, teeth: 28);

var pinion = Gears.Spur(pinionSpec);          // the profile, deviation reported
if (pinion.MaxFitDeviation > pinion.FitTolerance)
    throw new Exception("the fit must honor its stated tolerance");
if (Math.Abs(pinionSpec.BasePitch - Math.PI * pinionSpec.BaseDiameter / 18) > 1e-12)
    throw new Exception("base pitch is an identity");

// Standard centre distance; a wheel gap centred on the line of centres meshes
// with the pinion tooth on it.
double a = (pinionSpec.PitchDiameter + wheelSpec.PitchDiameter) / 2;
var pinionShape = Gears.SpurGear(pinionSpec, faceWidth: 8, boreDiameter: 8);
var wheelShape = Gears.SpurGear(wheelSpec, faceWidth: 8, boreDiameter: 12)
    .RotateZ(Math.PI - Math.PI / wheelSpec.Teeth)
    .Translate(a, 0, 0);

var scene = new Scene();
var tab = scene.AddTab("gears");
tab.Add(new Part("pinion", pinionShape, Palette.Coral));
tab.Add(new Part("wheel", wheelShape, Palette.Sky));
```

![An 18-tooth pinion meshing a 28-tooth wheel at standard centre distance](images/mechanism-involute.png)

The verification behind it is the law of gearing *asked rather than assumed*: two
generated gears are mounted at an extended centre distance (which an involute pair
tolerates — the ratio is set by the base circles, not the mounting) and rotated
into drive-flank contact by bisecting the pinion sketch's exact signed distance
over the wheel's outline. Through a sweep long enough to hand contact from tooth
pair to tooth pair, the measured transmission stays constant to ~9×10⁻⁶ rad —
and the same instrument reads 5.6×10⁻³ rad for a 25° wheel forced against a 20°
pinion, so it can see a wrong flank, not just a wrong ratio.

Because the tooth profile is lines and circular arcs, a spur gear is exact in all
three representations. `Gears.HelicalGear(spec, faceWidth, helixAngleDegrees)`
rides the twisted extrusion (the spur profile as the *transverse* section — mesh
and implicit only, `Explain` says so), and a bore is one `boreDiameter` argument;
keyways and internal gears are filed follow-ups.

## The rack, and why it is the definition

As the tooth count grows the base circle recedes and the involute flattens into a
**straight line** at the pressure angle. That limit is not merely one more member of
the family: it is the *basic rack*, the profile ISO 53 uses to define the whole tooth
system. `Gears.Rack` draws it with straight `Line2d` flanks and exact `Arc2d` root
fillets, so — unlike `GearProfile` — there is no fit deviation to report, and
`RackProfile` deliberately has no `MaxFitDeviation` for there to be.

`RackSpec` carries the same coefficients `GearSpec` does, and `MatingGear`/`For`
convert both ways so a pair cannot drift apart in the tooth system it claims to
share. The profile shift does not travel across that conversion, and the omission is
the point: a shift says where a *gear* sits against this rack, not what the rack is.
`MaximumRootFilletRadius` is ISO 53's ρ_fP,max = (π·m/4 − h_f·tan α)·cos α/(1 − sin α)
— 0.4719·m at the standard 20°/1.25 pair, which is why the standard 0.38·m fits, and
why the *same* 0.38 is refused by name at 25°, where the maximum falls to 0.318·m.

The bar spans a whole number of pitches beginning and ending at a tooth-**space**
centre, so two bars laid end to end at a `Length` offset form one continuous rack.

```csharp render:rack-and-pinion
var rackSpec = new RackSpec(module: 2);
var pinionSpec = rackSpec.MatingGear(teeth: 18);   // one tooth system, stated once

const int teeth = 12;
var rack = Gears.Rack(rackSpec, teeth, backHeight: 4);
if (Math.Abs(rackSpec.ToothThicknessAtPitch - rackSpec.CircularPitch / 2) > 1e-12)
    throw new Exception("a rack tooth is exactly half the pitch thick");
if (Math.Abs(rack.Sketch.Area() - rack.ClosedFormArea) > 1e-9)
    throw new Exception("the outline is exact, so the area is an equality");

// The pitch line is y = 0 and the teeth point +Y, so the pinion rolls on y = 0 with
// its centre one pitch radius above. x = 0 is a space centre, and an even tooth
// count puts another at the bar's midpoint - where a pinion tooth turned to point
// straight down will sit.
double midpoint = rack.Length / 2;
// Looking down the rack, slightly above the pitch line, so the engagement shows.
var camera = new CameraState(-Math.PI / 2 + 0.35, 0.55, 90, (midpoint, 6, 5));
var scene = new Scene();
var tab = scene.AddTab("rack and pinion");
tab.Add(new Part("rack", Gears.RackBar(rackSpec, teeth, faceWidth: 10, backHeight: 4),
    Palette.Sky));
tab.Add(new Part("pinion",
    Gears.SpurGear(pinionSpec, faceWidth: 10, boreDiameter: 8)
        .RotateZ(-Math.PI / 2)
        .Translate(midpoint, pinionSpec.PitchDiameter / 2, 0),
    Palette.Coral));
```

![A 12-tooth rack meshing an 18-tooth pinion](images/rack-and-pinion.png)

`Coupling.RackAndPinion` supplies the kinematics; the verification here is again the
law of gearing *measured from contact*, and it is the rack's own version of it — the
bar must advance exactly one pitch radius per radian of pinion rotation. The pinion
is lifted 0.4 mm to open real backlash, which is legal precisely because an involute
against a straight rack transmits the same ratio at *any* mounting height: the rack
flank's normal direction is fixed, so its supporting line is tangent to the base
circle and translates by exactly r_b·dφ. Measured over 1.2 tooth pitches, through
handover, the advance varied by **6.9×10⁻⁵ mm**. The same instrument reads
**1.2×10⁻¹ mm** — 1740× more — for a 25° rack forced against a 20° pinion, so it
sees flank *form* and not merely that contact happened.

## Worm and worm wheel

**The worm is a thread.** A cylindrical worm of the ZA form is straight-sided in the
*axial* plane, so its body is one helical sweep of a trapezoidal (radius, axial)
profile — exactly the family `SolidFactory.MakeThreadedRod` already builds every
modelled thread from, with the axial module taking a pitch's place. It is
boolean-free for the same reason a thread is: the root lands are part of the sweep,
so no core cylinder and no coaxial tangent seam ever exists. Multi-start is not a
different construction either — a helical sweep repeats every **lead**, so the
profile simply contains z₁ teeth.

A worm's "one tooth" is one **start**, and the reduction ratio is the wheel's tooth
count over that number: a two-start worm on a 40-tooth wheel is 20:1, not 40:1.
Unlike a gear, the worm's pitch diameter is a free choice — it sets the lead angle
tan γ = lead/(π·d₁) = z₁/q, hence the efficiency and whether the drive self-locks.

**The wheel is honestly an approximation, and the caveat is the design.** A true worm
wheel is *throated*: its teeth wrap the worm and their surface is the **envelope** of
the worm's motion — hobbing kinematics, with no closed form to draw. What
`Gears.WormWheel` gives instead is an ordinary helical gear whose helix angle equals
the worm's **lead** angle, which is the exact geometry of a crossed-helical (screw)
pair. It meshes, it transmits the stated ratio, and it touches the worm at a
**point** rather than along a line — right for a motion drive, a print or a layout,
and wrong for a load-carrying reducer, where the throat is what carries the contact.

Two identities make the pairing work, and both are asserted rather than assumed: the
worm's **axial** pitch is the wheel's **transverse** circular pitch, and at a 90°
shaft angle the worm's axial plane *is* the wheel's transverse plane at the central
point — so the wheel's transverse pressure angle is the worm's axial one, with
nothing to convert.

```csharp render:mechanism-worm
var wormSpec = new WormSpec(axialModule: 2, starts: 2, pitchDiameter: 24);
var pair = Gears.WormPair(wormSpec, wheelTeeth: 26);

if (Math.Abs(wormSpec.AxialPitch - pair.Wheel.CircularPitch) > 1e-12)
    throw new Exception("the worm's axial pitch IS the wheel's transverse pitch");
if (Math.Abs(wormSpec.HelixAngleDegrees + pair.WheelHelixAngleDegrees - 90) > 1e-12)
    throw new Exception("the shaft angle is the sum of the two helix angles");
if (Math.Abs(pair.GearRatio - 26.0 / 2) > 1e-12)
    throw new Exception("starts, not teeth: 26 teeth over 2 starts is 13:1");

// Worm along X (an integer number of axial pitches, so a crest lands on x = 0, and
// pre-spun a quarter turn so the profile facing the wheel is the drawn one); wheel
// flat about Z at the centre distance along Y. The wheel is then turned to put a
// tooth SPACE on the line of centres, less half its own twist so the section that
// meets the worm is the section that was drawn.
double px = wormSpec.AxialPitch;
const double faceWidth = 12;
double twist = faceWidth * Math.Tan(pair.WheelHelixAngleDegrees * Math.PI / 180)
    / (pair.WheelPitchDiameter / 2);

var scene = new Scene();
var tab = scene.AddTab("worm drive");
tab.Add(new Part("worm",
    Gears.Worm(wormSpec, length: 6 * px)
        .Translate(0, 0, -3 * px)
        .RotateZ(Math.PI / 2)
        .RotateY(Math.PI / 2),
    Palette.Coral));
tab.Add(new Part("wheel",
    Gears.WormWheel(pair, faceWidth, boreDiameter: 12)
        .Translate(0, 0, -faceWidth / 2)
        .RotateZ(-Math.PI / 2 - Math.PI / pair.WheelTeeth - twist / 2)
        .Translate(0, pair.CentreDistance, 0),
    Palette.Sky));
```

![A two-start worm driving a 26-tooth crossed-helical wheel](images/mechanism-worm.png)

The worm's own geometry is verified from its **field** rather than restated. The
axial tooth thickness at three radii lands one-sided against a *derived* chord bias
(the helical bands are chorded in phase only, since the generator is straight and a
v-chord is exactly on the surface): 0.0267 mm predicted at r = 7.6 and the default 32
segments per circle, against 0.0275 measured. The axial flank angle follows from how
fast that thickness closes with radius — the ZA property itself — reading 20.10°
against 20.00°. And the **lead and the hand** come from the tooth *centre* at
azimuths a quarter and a half turn apart, which reads to **10⁻¹⁴**: a centre is the
mean of two flank crossings, so the chord bias cancels exactly where a thickness
doubles it.

That handedness check is worth its keep for a specific reason. The worm is a helical
sweep in the B-Rep kernel and the wheel is a twisted extrusion in the modelling
layer — two independent constructions that must agree about what "right-handed"
means, or a correctly specified pair would be *built* meshing the wrong way. Both
are read off the geometry, and a left-hand worm takes a left-hand wheel (at a 90°
shaft angle the two members always match).

Volume is Pappus over the sweep — V = L·(2π/lead)·∫½R² dz over one lead, so any
length works and the phase washes out — cross-checked against a numerical integral
over one *axial pitch*, a different decomposition since the radius has period p_x
while the sweep has period lead.

`Gears.Worm` refuses by name what it cannot draw: a non-positive root diameter
(naming the diameter factor), a pointed thread, and adjacent starts overlapping at
the root cylinder.

## Straight bevel gears

`BevelPair` turns two tooth counts and a shaft angle into the two pitch cone angles
— `tan δ₁ = sin Σ / (z₂/z₁ + cos Σ)`, which at the usual 90° is just `z₁/z₂` — and
the wheel takes the complement, `δ₁ + δ₂ = Σ`. `BevelGears.StraightGear` then draws
each member: the tooth section at the heel, lofted toward the shared pitch apex.

```csharp render:mechanism-bevel
var pair = new BevelPair(pinionTeeth: 20, wheelTeeth: 30);
if (Math.Abs(pair.PinionConeAngleDegrees + pair.WheelConeAngleDegrees - 90) > 1e-12)
    throw new Exception("the cone angles must sum to the shaft angle");

var spec = new GearSpec(module: 3, teeth: pair.PinionTeeth);
var profile = BevelGears.Straight(spec, pair.PinionConeAngleDegrees, faceWidth: 14);

// The section reproduces the standard cone angles as an identity, read back off
// its own radii rather than off a stored number.
double faceCone = Math.Atan(profile.SectionTipRadius / profile.HeelPlaneZ) * 180 / Math.PI;
if (Math.Abs(faceCone - profile.FaceConeAngleDegrees) > 1e-9)
    throw new Exception("face cone angle is an identity");

var pinion = BevelGears.StraightGear(spec, pair.PinionConeAngleDegrees, 14, boreDiameter: 12);
var wheel = BevelGears.StraightGear(
    new GearSpec(3, pair.WheelTeeth), pair.WheelConeAngleDegrees, 14, boreDiameter: 16);

// Both apexes sit at the origin and the wheel's axis is the pinion's turned by the
// shaft angle, so the two pitch cones share the element at delta1 from +Z. TOOTH
// phasing across a bevel pair is the caller's (see below): contact here is at the
// pinion's own 90 deg azimuth, where 20 teeth put a tooth centre and 30 teeth put a
// space centre, so these two counts need no extra rotation.
if (pair.PinionTeeth % 4 != 0 || pair.WheelTeeth % 4 != 2)
    throw new Exception("this placement relies on the tooth counts phasing at 90 deg");

var scene = new Scene();
var tab = scene.AddTab("bevel pair");
tab.Add(new Part("pinion", pinion, Palette.Coral));
tab.Add(new Part("wheel", wheel.RotateX(-Math.PI / 2), Palette.Sky));
```

![A 20-tooth bevel pinion meshing a 30-tooth wheel on perpendicular shafts](images/mechanism-bevel.png)

The construction is **Tredgold's back-cone approximation, and the docs say so**.
The virtual spur gear on the back cone has `z_v = z / cos δ` teeth at the same
module — generally not a whole number, which is why it is a construction rather
than a gear — and its involute is wrapped onto the back cone and projected from
the pitch apex onto the section plane. Everything a bevel is dimensioned by comes
out exact: the pitch diameter, the arc tooth thickness `πm/2`, and the face and
root cone angles `δ ± arctan(h/R_e)` to machine precision. What is approximate is
the flank's shape away from the pitch point, measured at **7×10⁻⁴ to 3×10⁻² of a
module** against the true spherical involute over a family spanning m 1–5, z 12–60
and δ 20–70°. Treat these as modelling-grade teeth — right pitch, right cone
angles, sound for kinematics, assembly, casting patterns and printing — and not as
a substitute for a generated flank on a ground gear (production straight bevels
are octoid, which is neither of these curves).

Two limits are reported rather than hidden. The end faces are **planes**, not the
back and front cones, because a loft section must be planar — so the model's heel
section is deeper than a real back-cone tooth, and both `SectionTipRadius` and the
standard `BackConeTipRadius` are there to compare. That same depth is what caps the
cone angle near 68° with the ISO 53 profile-A root fillet; past it the refusal names
the cause and the remedy (a smaller `RootFilletCoefficient` — 0.30 reaches 75°, 0.20
reaches 80°), which is what a 3:1 or 4:1 pair's wheel needs. Undercut is decided by
the *virtual* tooth count, so a bevel tolerates fewer real teeth than a spur gear:
13 teeth is undercut as a spur at 20° and perfectly fine on a 45° cone.

**Spiral bevel and hypoid gears are refused by name.** A spiral flank is the
envelope swept by a face-mill or face-hob cutter under a generating machine's
motion — cutter radius, machine settings and roll ratio all enter the surface — not
a closed-form curve this kernel could fit and stand behind; a hypoid's axes do not
even intersect, so there is no common apex and the pitch surfaces are hyperboloids.
Transcribing a published approximation would be a guess wearing a standard's name.

## Planetary gear sets

A planetary set is an *arrangement* rather than a tooth form, and `PlanetarySet`
owns the arithmetic that makes ordinary involute gears fit together: the ring count
is **derived** from coaxiality (`z_ring = z_sun + 2·z_planet`), so an inconsistent
set cannot be spelled, and two conditions are checked and refused by name — equally
spaced planets need `(z_sun + z_ring)` divisible by the planet count, and adjacent
planets must clear each other along the centre chord.

```csharp render:mechanism-planetary
var set = new PlanetarySet(module: 2, sunTeeth: 24, planetTeeth: 18, planetCount: 3);
if (set.RingTeeth != 60 || (set.SunTeeth + set.RingTeeth) % set.PlanetCount != 0)
    throw new Exception("the assembly condition must hold");

// Every member drawn and placed, phases solved so each planet meshes with BOTH
// the sun and the ring at its own angular position.
var members = PlanetaryGears.Layout(
    set, faceWidth: 8, sunBore: 10, planetBore: 6);

var scene = new Scene();
var tab = scene.AddTab("planetary");
var colors = new[] { Palette.Coral, Palette.Sky, Palette.Sky, Palette.Sky, Palette.Sage };
for (int i = 0; i < members.Count; i++)
    tab.Add(new Part(members[i].Name, members[i].Shape, colors[i % colors.Length]));
```

![A 24-tooth sun, three 18-tooth planets and a 60-tooth internal ring](images/mechanism-planetary.png)

The **internal ring needs no boolean at all**. An internal gear's tooth *space* is
bounded by involutes of the same base circle as an external gear of the same
module, tooth count and pressure angle, with the same arc thickness at the pitch
circle — only the tip and root swap roles, the tips pointing inward. So the ring's
bore is exactly the outline of a "cutter" gear whose addendum reaches the ring's
root, and the ring is that outline used as a hole in a disc: lines and arcs, exact
in all three representations. Two consequences are stated rather than hidden — the
ring's tooth tips carry the cutter's root fillet (rounded, which is closer to a
real chamfered tip than a sharp corner), its roots are sharp, and the internal-mesh
interference conditions (tip, involute and trimming) are not checked in v1.

Getting the **phases** right is the substance. Each mesh fixes a relation — along
the line of centres the sun shows a tooth where the planet shows a space, and the
planet shows a tooth where the ring shows a space — and solving the pair gives each
planet's own rotation. It is verified from *contact*, by measuring each placed
planet's outline against the sun's and the ring's material with the sketches' own
exact signed distance: every planet touches within the flank fit's own deviation,
while a quarter-pitch phase error buries it 1.5 mm deep.

### The Willis equation, as a test rather than a constraint

`PlanetaryGears.Mechanism` builds the kinematics from **one `Coupling.Gear` per
mesh** — sun to each planet, each planet to the ring — and states no train ratio
anywhere. What makes that work is which bodies the joints connect: the sun and the
ring pin to the **carrier**, not to the housing, so every coupling is written on
angles that are already relative to the rotating line of centres. (Pinning the sun
to the housing instead would constrain ω_sun where the mesh constrains
ω_sun − ω_carrier, and the set would run happily and return a wrong ratio.)

```csharp run:mechanism-willis
var set = new PlanetarySet(module: 2, sunTeeth: 24, planetTeeth: 18, planetCount: 3);

var rig = new Assembly("planetary");
Part Body(string n) => new(n, MeshPrimitives.Box(4, 2, 1));
var housing = rig.Add(Body("housing"));
var carrier = rig.Add(Body("carrier"));
var sun = rig.Add(Body("sun"));
var ring = rig.Add(Body("ring"));
var planets = new List<Occurrence>();
for (int k = 0; k < set.PlanetCount; k++)
{
    double a = set.PlanetAzimuth(k);
    planets.Add(rig.Add(Body($"planet.{k + 1}"), Frame3d.FromXY(
        (set.CentreDistance * Math.Cos(a), set.CentreDistance * Math.Sin(a), 0),
        Vector3d.UnitX, Vector3d.UnitY)));
}

var planetary = PlanetaryGears.Mechanism(set, rig, housing, carrier, sun, ring, planets);
planetary.Mechanism.Ground(ring);          // the ordinary reduction drive

// One degree of freedom, though Kutzbach predicts a deeply negative number:
// three planets carry the same relation three times, which is load sharing.
if (planetary.Mechanism.Assemble().RemainingDegreesOfFreedom != 1)
    throw new Exception("a planetary set with a held ring has one DOF");

planetary.Mechanism.SolveAt(MechanismDriver.Angle(planetary.CarrierPin), 0.4);

// The Willis relation - EMERGENT from the composed couplings, not enforced.
double willis = planetary.SunPin.Angle / planetary.RingPin.Angle;
if (Math.Abs(willis - set.WillisRatio) > 1e-9)
    throw new Exception($"Willis: {willis} != {set.WillisRatio}");

// ... and the familiar held-ring reduction 1 + z_ring/z_sun = 3.5.
double sunAbsolute = planetary.SunPin.Angle + planetary.CarrierPin.Angle;
if (Math.Abs(sunAbsolute / planetary.CarrierPin.Angle - set.RingHeldRatio) > 1e-9)
    throw new Exception("the held-ring ratio must emerge too");
```

Asserting the Willis relation against the solver is not circular, which is the
whole reason it is worth doing: `Coupling.Gear` does enforce a ratio, but only for
*one mesh at a time*, and the train value is what those compose to through a third
body neither of them mentions. A wrong choice of which body a joint hangs from
would leave every individual coupling satisfied and the assembled ratio wrong.

One characterization is worth knowing because the failure looks like a modelling
error: a gear coupling is written on the *change* in each joint's coordinate, so a
cold solve has to cross the **largest** change in the train rather than the driven
one. Here the planet turns 3.33× the carrier, so driving the carrier 0.8 rad
converges while 1.0 rad — asking the planet for more than half a turn in one
step — does not. Use `Sweep`, which is continuation and seeds each step from the
previous converged pose.

A helical pair's conjugate action needs no second instrument. At every transverse
section the pair **is** a spur pair, since a helical gear's section at height z is
its own spur profile rotated by ψ(z) and a meshing pair's two members are rotated
by +ψ(z) and −ψ(z); rotating both members rigidly moves the *phase* of contact and
not the ratio. So what the tests measure is the half that could actually be wrong
— that a real transverse section of the built solid, rotated back by ψ(z), lands
on the exact spur region's zero level, within a bound derived from the three error
sources (arc flattening, the wall-panel chord, the biarc fit) and mutation-checked
against a 5% twist error.

### Herringbone (double-helical)

Two helical halves of *opposite hand* in one solid. Their axial thrusts are equal
and opposite, so they cancel in the bearings — which is the whole reason the form
exists, and the geometric statement behind it is that the mid-plane is a plane of
**exact mirror symmetry**.

That symmetry is how the solid is built rather than a property checked afterwards.
Both halves share the same transverse section at the apex — the twist law is
Λ-shaped in z — so `HerringboneGears.Herringbone` sweeps the lower half through
the ordinary twisted extrusion, reflects its mesh in z = W/2 and welds the two
**by index**: the apex ring's vertices are exact fixed points of the reflection,
so nothing is welded by tolerance and the two coincident cap facets are simply
dropped. A union of two separately built halves would hand a large coincident
planar region to a boolean for an answer the symmetry already gives.

```csharp render:mechanism-herringbone
var spec = new GearSpec(module: 2, teeth: 24);
double width = 18, beta = 25;

var gear = HerringboneGears.Herringbone(spec, width, beta, boreDiameter: 12);

// The two halves' helix angles are equal and opposite: the section rotation law
// is Lambda-shaped, so it depends on z only through |z - W/2|.
double below = HerringboneGears.SectionAngleAt(spec, width, beta, 4);
double above = HerringboneGears.SectionAngleAt(spec, width, beta, width - 4);
if (Math.Abs(below - above) > 1e-15)
    throw new Exception("the mid-plane must be a mirror plane");

var scene = new Scene();
scene.AddTab("herringbone").Add(new Part("gear", gear, Palette.Brass));
```

![A 24-tooth herringbone gear, its two opposite-hand halves meeting at the mid-plane](images/mechanism-herringbone.png)

The **apex relief groove** a hobbed double-helical gear carries is deliberately
not a parameter yet, and the reason is a measurement rather than an omission: a
groove is material genuinely *removed*, so it wants a boolean rather than another
weld, and subtracting an axial band from a gear fails in both engines — the exact
mesh boolean's imprint at every relief diameter, gap width and density tried, and
the B-Rep boolean as an unclosed solid with 1522 unpaired edges for the same band
against an ordinary spur gear. What the groove wants instead is a mixed-section
ring stack (a helical toothed run, an annular transition face, a plain relief
band, then the mirror), which is a construction rather than an argument, and it is
filed with those figures.

### Crossed helical (screw) gears

Two ordinary helical gears on **skew** shafts. The geometry is nothing new, so
what `CrossedHelicalPair` carries is the pairing arithmetic and the placement:
the two members must share a **normal** module and normal pressure angle (the same
hob cuts them), the shaft angle is Σ = β₁ + β₂ over *signed* helix angles, and the
centre distance is the sum of the pitch radii, m_n/2·(z₁/cos β₁ + z₂/cos β₂).

The signed form is one rule where the textbook states two: "β₁ + β₂ for the same
hand, β₁ − β₂ for opposite hands" is what Σ = β₁ + β₂ says once the second gear's
hand rides in the sign of its own angle.

> [!WARNING]
> **Crossed helical gears make POINT contact, not line contact.** Two helicoids on
> skew axes touch at a single point which travels across the flank as the pair
> turns, so the contact stress is concentrated and the load capacity is a small
> fraction of an equivalent parallel-axis pair's. These are for light drives,
> instrument trains and motion transfer between skew shafts — not for power. The
> wear-in that broadens the point into a patch is why they are usually run in
> dissimilar materials.

```csharp render:mechanism-crossed-helical
// A right-angle screw pair: 45 degrees on each member, same hand.
var pair = CrossedHelicalPair.Create(
    normalModule: 2, teeth1: 18, teeth2: 24,
    helixAngle1Degrees: 45, helixAngle2Degrees: 45);

if (Math.Abs(pair.ShaftAngleDegrees - 90) > 1e-12)
    throw new Exception("shaft angle is the signed sum of the helix angles");

// The ratio follows the TEETH, never the pitch radii - on skew axes those differ.
if (Math.Abs(pair.Ratio - 24.0 / 18.0) > 1e-12)
    throw new Exception("ratio is z2/z1");

var scene = new Scene();
var tab = scene.AddTab("screw pair");
tab.Add(new Part("driver", pair.FirstGear(faceWidth: 10, boreDiameter: 10), Palette.Coral));
tab.Add(new Part("driven", pair.SecondGear(faceWidth: 10, boreDiameter: 10), Palette.Sky));
```

![Two 45-degree helical gears on shafts crossed at a right angle](images/mechanism-crossed-helical.png)

Two things the arithmetic gets right that a habit gets wrong. The **ratio is
z₂/z₁ and not the pitch-radius ratio** — on parallel axes those coincide and the
habit is harmless, but r = m_n·z/(2·cos β), so a pair at 20° and 50° has radii out
by cos β₁/cos β₂ = 1.46, a 46% error for anyone who reads the radii. And every
**per-module coefficient scales by cos β** when a gear ordered in normal terms is
turned into a `GearSpec`, which reads them against the *transverse* module: the
addendum, dedendum, root fillet radius and profile shift are all radial *lengths*,
so `HelicalGearGeometry.FromNormal` divides each by m_t/m_n. Unscaled, a 0.38·m
root fillet reads 1.34× too large at 45° and a 24-tooth member is refused outright
for overlapping root fillets — a plausible-looking pair that cannot be drawn.

The pair is placed at the correct centre distance and shaft angle with its pitch
cylinders tangent at `ContactPoint`; the angular *phase* that would put a tooth of
one in the gap of the other is not solved, because that is a mate or a mechanism
driver rather than a property of the pairing.

## Cycloidal gear geometry

The clock and instrument tooth form. Above the pitch circle the flank is an
**epicycloid** — the trace of a point on a circle rolling *outside* the pitch
circle — and below it a **hypocycloid**, the same circle rolling *inside*. Both
curves leave the pitch circle at a cusp whose tangent is exactly radial, so face
and flank meet tangent-continuously there with nothing to arrange. Each enters the
sketch vocabulary the way the involute does: a biarc chain against the closed form
with the deviation reported.

What a cycloidal system carries instead of a pressure angle is the **generating
(describing) circle**, and it belongs to the *pair* rather than to one gear: a
wheel's epicycloidal face rolls against a pinion's hypocycloidal flank only if one
circle traced both, so `CycloidalGears.Mesh` refuses two gears whose circles differ.
`CycloidalGears.Pair` defaults to the classic clock choice — half the pinion's pitch
diameter — which is the ρ = r/2 identity that makes the pinion's hypocycloidal
flanks **exactly straight radial lines**, the leaves a clockmaker cuts. Nothing in
the factory special-cases that: the general cycloid formula and the general biarc
fit reach it on their own, and the test measures it off the generated sketch (the
boundary crossings sit on one ray to better than 10⁻¹² rad, and the fitted pieces
come back as literal straight segments).

```csharp render:mechanism-cycloidal
var mesh = CycloidalGears.Pair(module: 2, pinionTeeth: 10, wheelTeeth: 30);
if (!mesh.Pinion.HasRadialFlanks || mesh.Wheel.HasRadialFlanks)
    throw new Exception("the classic choice gives the PINION radial leaves, not the wheel");

var pinion = CycloidalGears.Spur(mesh.Pinion);   // the profile, deviation reported
if (pinion.MaxFitDeviation > pinion.FitTolerance)
    throw new Exception("the fit must honor its stated tolerance");

// A cycloidal pair runs at its DESIGN centre distance, and only there.
var pinionShape = CycloidalGears.SpurGear(mesh.Pinion, faceWidth: 8, boreDiameter: 8);
var wheelShape = CycloidalGears.SpurGear(mesh.Wheel, faceWidth: 8, boreDiameter: 16)
    .RotateZ(Math.PI - Math.PI / mesh.Wheel.Teeth)
    .Translate(mesh.CentreDistance, 0, 0);

var scene = new Scene();
var tab = scene.AddTab("cycloidal");
tab.Add(new Part("pinion", pinionShape, Palette.Brass));
tab.Add(new Part("wheel", wheelShape, Palette.Sky));

// Near-overhead: the tooth FORM is the subject, and it is a plane curve.
var camera = new CameraState(-Math.PI / 2, 1.35, 100, (26, 0, 4));
```

![A 10-leaf cycloidal pinion with radial flanks meshing a 30-tooth wheel](images/mechanism-cycloidal.png)

Conjugate action is asked rather than assumed, and by the same instrument the
involute uses — the pinion sketch's exact signed distance over the wheel's outline,
bisected to the touching angle — but at the **design** centre distance, with
backlash coming from *thinning* the teeth instead of from mounting the pair long.
Thinning is exact here: rotating a cycloid about its own pitch circle's centre is
the same cycloid at another phase. Through a sweep long enough to hand contact from
tooth pair to tooth pair the measured transmission stays constant to ~4×10⁻⁶ rad,
and a wheel cut with a 1.6× describing circle reads ~300× that on the same
instrument.

And here is the honest contrast with the involute, measured rather than warned
about. An involute pair is centre-distance invariant, because its ratio is set by
the base circles rather than by the mounting; a cycloidal pair's describing circle
has to roll on *both* pitch circles at once, so it is not. Mounted 0.3 mm long, the
same 10/30 pair still runs and its transmission ripples by 1.3×10⁻³ rad — three
hundred times the design-distance figure. That is a property of the tooth form, and
it is why cycloidal gearing wants accurate centres and involute gearing forgives
them.

`ClockGearProportions` supplies the BS 978-2 horological addendum table (⚠ a
transcription — verify against the current standard). Only the addendum columns are
stored: each member's dedendum is *derived* as its mate's addendum plus a clearance,
since a second stored column could only drift from that.

## Cycloidal drives

The same curve family, offset. A cycloidal drive's disc has one lobe fewer than the
ring has pins, rides an eccentric, and rolls backwards one lobe per input turn.

The curve the pin *centres* ride in the disc's own frame is derived rather than
transcribed, and the derivation pays for itself three times: with the disc centre at
`e·(cos φ, sin φ)` and the disc turning at `λφ`, pin *j* appears at
`Rot(−λφ)(P_j − O(φ))`, which collapses to `C(s) = R(cos s, sin s) − e(cos Ns, sin Ns)`
for **every pin at once** exactly when `λ = −1/(N−1)`. Out of that fall the lobe
count `N − 1`, a peak-to-valley depth of exactly `2e`, and the counter-rotating rate
— none of them asserted. It also settles the scope: repeat the derivation for a lobe
difference *d* and the pin phase is `2πj/d`, a whole number of turns for every pin
only at *d* = 1, so any other difference is refused by name as structural rather than
as a v1 gap.

The cut profile is that curve offset by the pin radius, and it reuses the cam
machinery's finding — an offset curve's unit tangent *is* the base curve's, since
`D′ = (1 − R_r·κ)·C′`. So the biarc fit gets exact tangents for nothing, and the same
factor states the validity condition: the pin must be smaller than the lobe tip's
radius of curvature `(R + eN)²/(R + eN²)`, or the offset cusps and the disc
self-intersects.

```csharp render:cycloidal-drive
var drive = new CycloidalDiscSpec(pins: 11, pinCircleRadius: 50, pinRadius: 3, eccentricity: 1.5);
if (drive.Lobes != 10 || drive.ReductionRatio != 10 || drive.RingOutputRatio != 11)
    throw new Exception("the two arrangements give different ratios off one geometry");
if (drive.DiscTurnsPerInputTurn >= 0)
    throw new Exception("the disc counter-rotates - the sign is the trap");

var profile = CycloidalDrives.Disc(drive);
if (Math.Abs(drive.LobeDepth - 2 * drive.Eccentricity) > 1e-12)
    throw new Exception("lobe depth is exactly twice the eccentricity");

// The construction pose: input angle 0 puts the disc centre on the eccentric.
var disc = CycloidalDrives.DiscShape(drive, thickness: 8, boreDiameter: 20)
    .Translate(drive.Eccentricity, 0, 0);

var scene = new Scene();
var tab = scene.AddTab("reducer");
tab.Add(new Part("disc", disc, Palette.Coral));
var pins = CycloidalDrives.PinShapes(drive, length: 8);
for (int j = 0; j < pins.Count; j++)
    tab.Add(new Part($"pin {j + 1}", pins[j], Palette.Steel));

var camera = new CameraState(-Math.PI / 2, 1.35, 145, (0, 0, 4));
```

![An 11-pin cycloidal drive disc with ten lobes, shown at input angle zero](images/cycloidal-drive.png)

The verification is the derivation's own identity and it is exact: because every pin
lies *on* the roller-centre curve at every input angle, the disc sketch's signed
distance reads exactly the pin radius at every pin through a full input rotation.
The measured residual is 3.06×10⁻⁴ against a fit deviation of 3.06×10⁻⁴ — the pose
relation contributes nothing — and that one number is simultaneously the clash check
(no pin ever reads *less*) and the ratio measurement: sweeping candidate rates −1/8,
−1/9, −1/11, −1/12 and +1/10 drives the pins hundreds of times the fit deviation into
the disc, so only the derived rate holds contact.

Two ratios are named rather than one being called *the* ratio, because the same
geometry gives different numbers in the two classic arrangements: pins fixed with the
disc as output is `z_lobes/(z_pins − z_lobes)` = 10, counter-rotating, and the disc
held with the ring as output is `z_pins/(z_pins − z_lobes)` = 11, co-rotating.

v1 draws the lobe profile and an optional central bore; output roller holes, a
running clearance and the eccentric shaft are filed follow-ups.

## Saving a mechanism

`Mechanism.SaveMechanism()` writes the whole joint layer as one JSON envelope —
grounds, raw mates, joints, couplings — and `LoadMechanism` reads it back with
warnings (never exceptions) for anything the model no longer matches. Joint *mates*
are not restated (they are a deterministic function of the joint's two ends); what
cannot be re-derived rounds trip as data: the axis joints' perpendicular reference
directions, and the **unwrapped angle history** — a crank saved after two full turns
reloads at 4π and keeps counting, which no fresh construction at the same pose could
recover. Cam laws save their factory kind and arguments (a `FromSketch` law saves its
samples; a `FromFunction` lambda saves an `opaque` marker that loads as a warning
unless a `resolveOpaqueLaw` hook supplies it), and `save → load → save` is a
byte-identical fixed point:

```csharp run:mechanism-persistence
var rig = new Assembly("gearbox");
var housing = rig.Add(new Part("housing", MeshPrimitives.Box(4, 2, 1)));
var gearA = rig.Add(new Part("gearA", MeshPrimitives.Box(4, 2, 1)));
var gearB = rig.Add(new Part("gearB", MeshPrimitives.Box(4, 2, 1)),
    Frame3d.FromXY((30, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
var z = Vector3d.UnitZ;
var pinA = Joint.Revolute(
    MateGeometry.Axis(housing, (0, 0, 0), z), MateGeometry.Axis(gearA, (0, 0, 0), z), "pin A");
var pinB = Joint.Revolute(
    MateGeometry.Axis(housing, (30, 0, 0), z), MateGeometry.Axis(gearB, (0, 0, 0), z), "pin B");
var mechanism = new Mechanism(rig).Ground(housing).Add(pinA).Add(pinB)
    .Add(Coupling.Gear(pinA, pinB, teethA: 20, teethB: 40));

// Two full turns of history, then save mid-motion.
mechanism.Sweep(MechanismDriver.Angle(pinA), 0, 4 * Math.PI, frames: 17);
string file = mechanism.SaveMechanism();

var reloaded = new Mechanism(rig);
var warnings = reloaded.LoadMechanism(file);
if (warnings.Count != 0) throw new Exception(string.Join("; ", warnings));
if (reloaded.SaveMechanism() != file) throw new Exception("save-load-save must be a fixed point");

var crank = (RevoluteJoint)reloaded.Joints[0];
if (Math.Abs(crank.Angle - 4 * Math.PI) > 1e-8)
    throw new Exception("the unwrapped history must survive: two turns is 4 pi, not 0");
reloaded.SolveAt(MechanismDriver.Angle(crank), 4 * Math.PI + 0.5);   // and keeps counting
```

Loading **re-adds** every joint, which re-asserts its nominal DOF against the
solver's measured rank — so a file that was valid when written can legitimately load
with a warning if the model changed underneath it; the joint is skipped and any
coupling referencing it is skipped by name, never guessed at.
