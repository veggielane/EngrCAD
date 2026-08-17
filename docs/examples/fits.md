---
title: "Fits & tolerance stackups"
---

`Iso286` answers the question every mating pair raises — *what limits do I put on
this bore and this shaft?* — from the ISO 286-1 tables: standard tolerance grades
IT5–IT12 and the fundamental deviations of shaft letters **a through h, js, k, m,
n, p and r through z**, plus the holes of **both systems** — the basic hole H and
JS, and the shaft-basis holes A–G — for sizes over 1 up to 500 mm. That covers the
whole preferred-fit range, from H11/c11 loose running to H7/u6 force. The tables are
transcriptions carrying the ⚠ verify-against-datasheet flag (`StandardHoles`'
convention): they are stored in the standard's own micrometres — the form a human
checks against the printed chart — and converted to model millimetres at the API.

```csharp run:fits-limits
// The classic sliding fit at Ø40: H7 bore +25/0 µm, g6 shaft −9/−25 µm.
var bore  = Iso286.Limits(40, "H7");
var shaft = Iso286.Limits(40, "g6");
if (Math.Abs(bore.Upper - 0.025) > 1e-12 || bore.Lower != 0)
    throw new Exception("H7 at 40 is +0.025/0");
if (Math.Abs(shaft.Upper - -0.009) > 1e-12 || Math.Abs(shaft.Lower - -0.025) > 1e-12)
    throw new Exception("g6 at 40 is -0.009/-0.025");

// A fit derives its kind from the clearance EXTREMES, never from the letter.
var sliding = Iso286.Fit(40, "H7", "g6");
if (sliding.Kind != FitKind.Clearance)
    throw new Exception("H7/g6 always clears");
if (Math.Abs(sliding.MinClearance - 0.009) > 1e-12 || Math.Abs(sliding.MaxClearance - 0.050) > 1e-12)
    throw new Exception("the handbook numbers: 9 to 50 µm");

var press = Iso286.Fit(40, "H7", "p6");
if (press.Kind != FitKind.Interference)
    throw new Exception("H7/p6 always binds");
var locational = Iso286.Fit(40, "H7", "k6");
if (locational.Kind != FitKind.Transition)
    throw new Exception("H7/k6 can do either");
```

## The wide-clearance and interference letters

Letters a–c (large clearance) and r–z (interference) carry their deviations on the
standard's own **finer size steps** — c changes at 40 *inside* the 30–50 range the
grades use, and t, u, v, x, y, z each take a new value there too — so they read from
a 25-row table rather than the grades' 13. Which letters split where is not uniform:
r and s carry one value across 30–50 while their neighbours do not.

```csharp run:fits-interference
// H11/c11 — the loose running fit: at Ø40 the shaft sits 120 µm below the bore at
// its closest and 440 µm at its loosest.
var loose = Iso286.Fit(40, "H11", "c11");
if (loose.Kind != FitKind.Clearance)
    throw new Exception("H11/c11 always clears");
if (Math.Abs(loose.MinClearance - 0.120) > 1e-12 || Math.Abs(loose.MaxClearance - 0.440) > 1e-12)
    throw new Exception("the chart row: 120 to 440 microns");

// H7/s6 — the medium drive fit: ALWAYS interference, 18 to 59 microns of it.
var drive = Iso286.Fit(40, "H7", "s6");
if (drive.Kind != FitKind.Interference)
    throw new Exception("H7/s6 always binds");
if (Math.Abs(drive.MinClearance - -0.059) > 1e-12 || Math.Abs(drive.MaxClearance - -0.018) > 1e-12)
    throw new Exception("the chart row: 18 to 59 microns of interference");

// The three interference letters ORDER as the standard intends at one size.
var press = Iso286.Fit(40, "H7", "r6");   // light press
var force = Iso286.Fit(40, "H7", "u6");   // force fit
if (!(force.MaxClearance < drive.MaxClearance && drive.MaxClearance < press.MaxClearance))
    throw new Exception("r lighter than s lighter than u");

// The finer steps are visible: c takes a new cell at 40, s does not.
if (Math.Abs(Iso286.Limits(45, "c11").Upper - -0.130) > 1e-12)
    throw new Exception("c changes inside the 30-50 grade range");
if (Iso286.Limits(45, "s6").Lower != Iso286.Limits(40, "s6").Lower)
    throw new Exception("s carries one value across 30-50");
```

## Shaft basis

Where a bought-in ground shaft is the fixed member, the fit is cut into the hole
instead: the shaft is the basic **h** and the hole takes the letter. `Iso286` reads
the shaft-basis holes **A–G** by the standard's own mirror rule — `EI = −es` of the
same-letter shaft, with no correction at any grade — and that rule has a consequence
worth checking rather than asserting: **G7/h6 carries exactly H7/g6's clearances**,
because the two IT widths are the same two numbers added in the other order.

```csharp run:fits-shaft-basis
// The same joint, described from either side.
var holeBasis  = Iso286.Fit(40, "H7", "g6");
var shaftBasis = Iso286.Fit(40, "G7", "h6");
if (holeBasis.MinClearance != shaftBasis.MinClearance ||
    holeBasis.MaxClearance != shaftBasis.MaxClearance)
    throw new Exception("the IT widths commute, so the two systems agree exactly");

// G7 at Diameter 40 is +34/+9 microns — g6's -9 mirrored, plus IT7.
var g7 = Iso286.Limits(40, "G7");
if (Math.Abs(g7.Upper - 0.034) > 1e-12 || Math.Abs(g7.Lower - 0.009) > 1e-12)
    throw new Exception("G7 at 40 is +0.034/+0.009");
```

What stays **refused by name**: the holes J and K–ZC, because those carry the
`Δ = IT(n) − IT(n−1)` correction for fine grades — with tabulated exceptions and an
IT3/IT4 dependence the grade table does not hold — and half-transcribing it is
exactly the plausible-wrong-row failure the ⚠ flag exists to prevent; the
hole-basis spelling of the same fit (H7/s6 for S7/h6) is supported. Also refused:
the intermediate letters cd, ef, fg and j, the extreme za–zc, sizes outside the
1–500 mm table, and **t, v and y below 24, 14 and 18 mm** — the standard's own
empty cells, named as such rather than interpolated. A refusal that names its
reason beats a plausible number from the wrong row.

## Stackups

`ToleranceStackup` sums a chain of toleranced dimensions **worst-case** and
**root-sum-square**. The chain is the caller's design statement — which dimensions
contribute, in which direction — because nothing in the model carries it: mates
constrain poses and hold no toleranced dimensions, so a stackup derived from the
mate graph would be a guess about intent.

Asymmetric tolerances are handled the textbook way, stated rather than implied:
worst-case uses each contribution's own signed band ends, while RSS re-centres each
contribution on its **mid** value and root-sum-squares the half-widths — so the RSS
mean shifts away from the nominal exactly when a band is asymmetric.

```csharp run:fits-stackup
// A housing pocket 50 ±0.1 holds a bearing 20 +0/−0.05 and a spacer 10 ±0.02.
// How much gap is left, and how bad can it get?
var gap = new ToleranceStackup()
    .Add("pocket", 50, 0.1, 0.1)
    .Subtract("bearing", 20, 0, 0.05)
    .Subtract("spacer", 10, 0.02, 0.02)
    .Evaluate();

if (Math.Abs(gap.Nominal - 20.0) > 1e-12)
    throw new Exception("50 - 20 - 10");
if (Math.Abs(gap.WorstCaseMin - 19.88) > 1e-12 || Math.Abs(gap.WorstCaseMax - 20.17) > 1e-12)
    throw new Exception("every dimension at its worst end");
// RSS lands strictly inside worst case — the whole reason the method exists —
// and its mean carries the bearing's asymmetric band (+0.025 off the nominal).
if (gap.RssMin <= gap.WorstCaseMin || gap.RssMax >= gap.WorstCaseMax)
    throw new Exception("RSS inside worst case");
if (Math.Abs(gap.RssMean - 20.025) > 1e-12)
    throw new Exception("the asymmetric band shifts the statistical centre");

// A fit's clearance enters a chain like any other dimension: the pin's float.
var withPin = new ToleranceStackup()
    .Add("pin float", Iso286.Fit(40, "H7", "g6"))
    .Evaluate();
if (Math.Abs(withPin.WorstCaseMax - 0.050) > 1e-12)
    throw new Exception("the fit's own extremes carry through");
```

The units are model millimetres throughout, like everything else in the kernel; the
µm figures in the prose are the standard's own spelling of the same numbers.
