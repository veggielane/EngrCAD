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

| Form | Route | Exact in |
|---|---|---|
| **Spur** (involute) | biarc-fitted involute flanks, deviation reported | all three representations |
| **Helical** | the spur profile as the *transverse* section, twisted extrusion | mesh and implicit |

Every profile enters the `Sketch` vocabulary as lines and circular arcs, which is
what makes a spur gear exact everywhere rather than a tessellation: the flank is a
tangent-continuous **biarc chain whose deviation from the closed-form involute is
measured and reported** (`GearProfile.MaxFitDeviation`), and the tip arcs, root
arcs and root fillets are exact by construction.

## Spur gears

`GearSpec` carries module, tooth count, pressure angle and profile shift, with the
ISO 53 basic-rack proportions as overridable coefficients. Its derived properties
state the base-circle identities as arithmetic — base pitch = π·m·cos α, base
diameter = z·m·cos α, tooth thickness = m·(π/2 + 2x·tan α) — so a design can check
itself without re-deriving them.

See [the involute section in mechanisms.md](mechanisms.md) for the meshing pair,
its render, and the conjugate-action measurement behind it.

What the factory cannot stand behind, it refuses by name: tooth counts below the
undercut limit z_min = 2(h_a\* − x)/sin²α (where a generating cutter would
trochoid-trim the root), pointed teeth, and root fillets that do not fit their gap.

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
