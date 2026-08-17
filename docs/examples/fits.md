---
title: "Fits & tolerance stackups"
---

`Iso286` answers the question every mating pair raises — *what limits do I put on
this bore and this shaft?* — from the ISO 286-1 tables: standard tolerance grades
IT5–IT12 and the fundamental deviations of the common shaft letters (d, e, f, g, h,
js, k, m, n, p) plus the basic hole H, for sizes over 1 up to 500 mm. That is the
**hole-basis system** the standard itself prefers, and it covers the preferred fits
(H7/g6, H7/h6, H8/f7, H9/d9, H7/k6, H7/n6, H7/p6). The tables are transcriptions
carrying the ⚠ verify-against-datasheet flag (`StandardHoles`' convention): they are
stored in the standard's own micrometres — the form a human checks against the
printed chart — and converted to model millimetres at the API.

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

Letters a–c and r–z split their deviations at sub-range boundaries the main table
does not have, so they are **refused by name** rather than approximated — as are
holes other than H (the shaft-basis system) and sizes outside the 1–500 mm table.
A refusal that names its reason beats a plausible number from the wrong row.

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
