# Gears

Gear geometry lives in `EngrCAD.Modeling`, beside the [mechanisms](mechanisms.md)
layer that drives it. The two are deliberately separate concerns: this page is
about **tooth form** — what the flanks are and how exactly they are represented —
while a `Coupling.Gear` in a `Mechanism` constrains the *ratio* and cares nothing
about whether a tooth was ever drawn.

That separation is also why the verification here never leans on the solver. A
gear coupling **enforces** the ratio a conjugacy test would be asserting, so
measuring transmission through one proves nothing about the flanks; every claim
below is measured from **contact**, by bisecting a generated sketch's exact signed
distance against its mate's outline.

## What is supported

| Form | Route | Exact in | The honest limit |
|---|---|---|---|
| **Spur** (involute) | biarc-fitted involute flanks, deviation reported | all three | — |
| **Helical** | the spur profile as the *transverse* section, twisted extrusion | mesh, implicit | a twist has no exact B-Rep form |
| **Herringbone** | two opposite-hand halves welded by index at a mirror plane | mesh, implicit | the apex relief groove is refused — see below |
| **Crossed helical** | two helicals on skew shafts, `Σ = β₁ + β₂` | mesh, implicit | **point** contact, so it carries little load |
| **Rack** | straight flanks — the involute's own limit | all three | — |
| **Worm** | a thread: one boolean-free helical sweep of a ZA profile | all three | — |
| **Worm wheel** | a helical gear at the worm's lead angle | mesh, implicit | the **crossed-helical approximation**, not a throated wheel |
| **Straight bevel** | Tredgold's back cone, lofted toward the apex | all three | an approximation *by construction*; ends are planes, not cones |
| **Spiral bevel / hypoid** | — | — | **refused by name**: machine-tool kinematics, not a profile |
| **Planetary** | an arrangement; internal ring needs no boolean | all three | interference checks are the external pair's |
| **Cycloidal** | epicycloid faces, hypocycloid flanks, fitted | all three | conjugate only at the **design centre distance** |
| **Cycloidal drive** | the same family offset by the roller radius | all three | one-lobe difference is a **theorem**, not a limit |

Every profile enters the `Sketch` vocabulary as lines and circular arcs, which is
what makes most of these exact in all three representations rather than a
tessellation: a fitted flank is a tangent-continuous **biarc chain whose deviation
from the closed form is measured and reported** (`MaxFitDeviation`), and the tip
arcs, root arcs and root fillets are exact by construction. Where a form is
mesh-only it is because of its *sweep* — a twist — and not its tooth form.

**The rack is the one member with no fit at all**, which is why `RackProfile`
carries no `MaxFitDeviation` for there to be: straight `Line2d` flanks and exact
`Arc2d` fillets. Its absence is the statement.

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
three representations; a bore is one `boreDiameter` argument, and keyways are a
filed follow-up. Everything below is this profile put to work — swept along a
helix, mirrored, laid flat, or replaced by a different flank curve entirely.

## Helical gears

`Gears.HelicalGear(spec, faceWidth, helixAngleDegrees, boreDiameter)` takes the
spur profile as the **transverse** section and rides the twisted extrusion, so the
helix angle is realised as a twist of `faceWidth·tan β / r_pitch` radians over the
face.

The representation cost is real and `Explain` states it: a twist has no exact
B-Rep form, so a helical gear is **mesh and implicit only** where a spur gear is
exact in all three. Helix angles are limited to ±60°.

```csharp render:gear-helical
// The same 24-tooth spec cut straight and at a 20-degree helix, side by side.
var spec = new GearSpec(module: 2.5, teeth: 24);

var spur = Gears.SpurGear(spec, faceWidth: 14, boreDiameter: 12);
var helical = Gears.HelicalGear(spec, faceWidth: 14, helixAngleDegrees: 20, boreDiameter: 12);

// The transverse section is the SAME profile, so both share every pitch-circle
// identity; only the axial sweep differs.
if (Math.Abs(spec.PitchDiameter - 60) > 1e-12)
    throw new Exception("m*z is the pitch diameter");

var scene = new Scene();
var tab = scene.AddTab("helix");
tab.Add(new Part("spur", spur, Palette.Sky, Matrix4d.CreateTranslation((-38, 0, 0))));
tab.Add(new Part("helical 20 deg", helical, Palette.Coral, Matrix4d.CreateTranslation((38, 0, 0))));
```

![A straight-cut spur gear beside the same tooth count cut at a 20-degree helix](images/gear-helical.png)

A helix buys quieter, more gradual tooth engagement — the contact line crosses the
face rather than arriving all at once — at the cost of an **axial thrust** the
bearings must carry, which is what a herringbone arrangement exists to cancel.

## Herringbone (double-helical)

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

## Crossed helical (screw) gears

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

## The honest boundaries

Two properties are worth stating because they decide which form a design should
use, and neither is visible in a picture:

- **An involute pair is centre-distance invariant.** The ratio is set by the base
  circles, not the mounting, so a mounting error changes the backlash and the
  pressure angle but *not* the transmission. That is the property that made the
  involute dominant, and it is verified here by measuring conjugacy at a
  deliberately **extended** centre distance.
- **A tooth form is not a load rating.** Nothing here computes bending or contact
  stress (Lewis, AGMA/ISO 6336), so a generated gear is geometry, not a
  qualified part. The [FEA pages](fea-structural.md) will integrate a real gear
  body, but the standard rating formulae are not implemented.
