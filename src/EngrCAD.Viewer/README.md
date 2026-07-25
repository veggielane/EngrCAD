# EngrCAD.Viewer

Cross-platform viewer **library**: Avalonia UI with an OpenGL viewport rendering kernel
geometry. The only project allowed UI/rendering dependencies (Avalonia, Silk.NET).

## Usage

Design code builds an `EngrCAD.Modeling.Scene` (parts grouped into tabs) and hands it
over:

```csharp
var scene = new Scene();
scene.Add(new Part("bracket", myShape, Palette.Steel));   // default "Model" tab
var drill = scene.AddTab("drill jig");
drill.Add(new Part("jig", jigSolid));
EngrCad.Show(scene, "My design");   // blocks until the window closes
```

Multi-tab scenes get a tab strip; each tab remembers its own camera (auto-framed on
first visit). Picking reports part names in the title bar.

## The CAD chrome

Dark-themed layout around one shared GL viewport:

- **Toolbar**: Fit (zoom to visible parts), Front/Top/Right/Iso standard views (each
  a cube direction resolved by `ViewCubeMath.PoseFor` — the view cube's own pose
  source), a
  perspective/**orthographic** toggle (the ortho frustum keeps the target plane's
  apparent size, so toggling doesn't jump), the **view-style dropdown** (see below),
  an **AO** toggle (ambient occlusion, on by default — see below), a **Section**
  toggle with an **X/Y/Z axis cycler** button beside it (see below), an **Annot**
  toggle (3D annotations, on by default — see below), and a **Measure** toggle
  (interactive dimensioning — see below).
- **Ambient occlusion** (`ViewportControl.AmbientOcclusion`, the toolbar **AO**
  toggle, `EngrCadOptions.AmbientOcclusion` / `.WithAmbientOcclusion(...)` /
  `--ao on|off`; **on by default**): pockets, blind holes, rib roots and the contact
  ring where one feature meets another go darker, which is the depth cue a single
  directional light cannot give. It is **baked, not screen-space**
  (`AmbientOcclusion.cs`): for each display-mesh vertex a deterministic
  cosine-weighted hemisphere of 16 rays is cast against the part's own triangles
  (`Bvh` + Moller-Trumbore, its own *bounded* traversal — `Bvh.Query(ray, …)` walks an
  infinite ray and is far too slow here), hits attenuate linearly to nothing at 15% of
  the mesh diagonal, and the open-sky fraction becomes the `aOcclusion` vertex
  attribute that the mesh shader multiplies into ambient + diffuse (never into the
  specular, and never into section cut material, which is a flat fill by design).
  - *Why baked*: the AO is vertex **data**, so the window and the headless pass upload
    identical floats and shade with the identical shader — parity by construction,
    with no FBO, no depth/normal prepass, and no blur that would resolve differently
    at the offscreen pass's 2x supersampled size. It also costs nothing per frame and
    survives section planes, translucency, edges and annotations untouched.
  - *Cost*: a one-off CPU bake, cached per display mesh and run on the same worker
    thread as `Scene.PreMesh` (so a scene load or hot reload never stalls the render
    thread) — a few ms for a simple part, ~0.3 s for a busy multi-part tab at 48
    segments/circle. Two deterministic guards bound it: a **ray budget** (2M rays per
    bake) halves the per-vertex ray count on very high vertex counts, and meshes above
    **80k triangles skip the bake entirely** — in lattice-like geometry every ray walks
    a labyrinth instead of escaping and the per-ray cost climbs by an order of
    magnitude (a 100k-triangle gyroid measured ~10 s), which is not worth a stall
    before the window appears. Both rules are pure functions of the mesh, so they
    cannot make the window and the headless render disagree.
  - Two details keep vertex-resolution shading honest. Vertices are grouped by
    position **and smoothing group** (a 50-degree crease), so a hole rim's top-face
    copy stays bright while its bore-wall copy darkens — averaging the two used to drag
    the whole surrounding face down and paint its triangulation across it as streaks —
    and ray origins are lifted along the direction that leaves *every* incident face,
    because a concave corner's own normal runs parallel to the neighbouring wall and
    would leave the origin exactly in its plane, where the wall that physically blocks
    half the hemisphere registers as no occlusion at all. The shader then mixes the
    result in at **half strength** (`AmbientOcclusion.Strength`) so whatever
    interpolation remains stays below the noise of the shading.
  - *Limitation (v1)*: occlusion is **per part, in its own space** — instances share
    one bake and no part shadows another — and its resolution is the display mesh's,
    so a plain **through-bore** (a two-row band whose only vertices sit at the open
    rims) barely darkens, while pockets and blind holes do. Turning AO off reproduces
    the previous flat-lit look exactly (the shader factor becomes exactly 1.0), and a
    convex part renders bit-identically either way (nothing can occlude it).
- **Global view style** (`ViewportControl.ViewStyle`, the toolbar dropdown): the
  classic CAD display-style selector — **Points / Wireframe / Shaded / Shaded +
  Edges** — applied to the whole viewport. Precedence rule (one place:
  `RenderModes.Resolve` in `RenderCore.cs`, shared verbatim with the offscreen
  pass): the global style decides how parts render *by default*; a part whose
  `Part.DisplayMode` is explicitly **non-default** (Wireframe or Translucent)
  overrides the global style for that part. `DisplayMode.Shaded` IS the default, so
  it cannot override — parts left at the default follow the global style.
  - *Shaded + Edges* (default) — the current lit-fill + feature-edge look.
  - *Shaded* — the same fills with the feature-edge overlay suppressed (explicit
    Translucent parts keep their silhouette edges — the override wins wholly).
  - *Wireframe* — every part as mesh-edge lines in its color ("mesh" view).
  - *Points* — vertex point sprites (round dots via `gl_PointCoord`, section-clipped
    like everything else; a dedicated point program in `ViewerShaders` — desktop GL
    needs `GL_PROGRAM_POINT_SIZE` enabled, GLES has it always on). Dots are the
    mesh's *actual vertices*: dense on tessellated curved surfaces, sparse on large
    flat faces.
- **Section plane**: the classic CAD section view — an **axis-aligned clip plane**
  (X, Y, or Z — `ViewportControl.SectionAxis`, default Z) at an adjustable offset
  (`SectionOffset`) hides everything beyond it so you can see inside (bores,
  cavities, fillets in cross-section). Implemented as a fragment-shader `discard`
  (`dot(worldPos, uSectionAxis) > uSectionOffset`) in the mesh, line, and point
  shaders; face culling stays off, and the interior surfaces the cut exposes are
  backfaces, which the shader detects via `gl_FrontFacing` and renders as a flat
  darker warm tint — the standard "cut material" cue (axis-agnostic by
  construction). When first enabled the plane defaults to the middle of the parts'
  bounds along the active axis; **changing the axis re-centers it** (an offset on
  one axis is meaningless on another); `[` / `]` move it by 2% of the scene extent
  along the active axis per press (current axis+offset shown in the status bar).
  Feature edges and wireframes are clipped consistently with the fills (they belong
  to the model); the ground grid and world axes are scene furniture and stay
  unclipped. Custom hosts drive `SectionEnabled` / `SectionAxis` / `SectionOffset`
  (`SectionHeight` remains as a delegating legacy alias from the Z-only days).
  **Picking and hover honor the cut**: a surface the plane removed cannot be clicked
  through, so the ray lands on the interior the section exposed instead of the shell
  in front of it. The CPU test is `SectionClip.Hides` — deliberately the shaders' own
  discard rule (`dot(world, axis) > offset`) in one place, so the visible and the
  pickable surface cannot drift apart; the exposed cut face (exactly on the plane)
  stays pickable, matching the strict `>`.
- **SDF isolines on the section plane** (automatic when available): when the section
  plane cuts a part whose geometry is an `Sdf` — or a `Shape` whose implicit lowering
  exists (`CanConvertTo(Implicit)`; lowered once and cached per part, never per
  frame) — iso-distance contours of the field are overlaid on the cut. The **gold**
  d = 0 contour is the exact surface cross-section; **cool blue** positive and
  **warm orange** negative families at d = ±k·spacing visualize the field itself —
  wall thickness at a glance (count the warm rings), blend and offset debugging.
  Spacing is 1-2-5-rounded from the contributing parts' bounds (shown in the status
  bar; a wall thinner than one spacing simply shows no interior ring). Extraction is
  `SdfContours` in EngrCAD.Interop (marching squares over one batch-`Evaluate` grid,
  ~160 cells across, per part per rebuild); it reruns only when the section height,
  scene, or visibility changes — never per frame. Lines draw through the shared line
  program, pulled 1% of the spacing to the visible side of the clip so the fragment
  discard never eats them; depth-tested like feature edges (polygon-offset fills lose
  to coincident lines). The plumbing is plane-general (`SectionContours.PlaneFrame`
  takes the clip rule `dot(p, axis) > offset`) and follows the active
  `SectionAxis`. Raw B-Rep/mesh parts show no isolines (wrap them in
  `Shape.From(...)` if the implicit bridge is wanted); a part whose implicit
  lowering *fails* (e.g. an open mesh behind `Shape.From`) reports the failure once
  through the status overlay instead of being silently isoline-less. Offscreen
  section renders
  draw the same isolines through the same `SectionContourRenderer` (one-shot — the
  staleness caching only matters in the window), so headless cutaways match the
  viewport exactly.
- **View cube** (top-right of the viewport): the standard CAD orientation widget — a
  small labeled cube (FRONT/BACK/LEFT/RIGHT/TOP/BOTTOM) that always mirrors the
  orbit camera's rotation, so it doubles as a live orientation indicator. Clicking a
  **face** animates the camera to that orthogonal view, an **edge** to the 45° view
  between its two faces, and a **corner** to the iso view of its three faces (the
  front-right-top corner is exactly the toolbar's Iso); the transition is a ~250 ms
  smoothstep-eased move along the shortest yaw path with distance and target kept.
  Hovering brightens the face/edge/corner under the cursor (its whole face set
  lightens) so the click target reads before clicking. Dragging that starts on the
  cube orbits the main camera like anywhere else and then **rotate-snaps** on release:
  the view settles onto the nearest of the 26 standard orientations
  (`ViewCubeMath.NearestStandardDirection` — the direction closest to the camera's
  view direction, idempotent so an already-standard view does not drift), the way
  commercial cubes finish a drag. Dragging anywhere else in the viewport still orbits
  freely. Clicks inside the cube's square region never pick parts through the widget.
  `ViewportControl.SnapViewCube()` drives the snap directly.
  **`ViewCubeMath.PoseFor` is the single pose source**: the toolbar's
  Front/Top/Right/Iso buttons are named cube directions passed through it, so the
  buttons and the widget can never disagree (Top/Bottom keep the current yaw, as a
  TOP face click already did — yaw is unconstrained at the poles). Implementation (all in
  `ViewCube.cs`): drawn after the scene into its own ~104-DIP sub-viewport with the
  depth buffer cleared (always on top), reusing the existing flat-color line
  program — face fills are 6 flat-shaded tones (top lightest), edges and labels are
  lines, and the labels are polyline lettering from the shared **`StrokeFont`**
  (no text renderer, no new shaders). The mini-projection is **always orthographic**
  regardless of the main perspective/ortho toggle (standard for orientation
  widgets), which also makes the screen-space hit test an exact ortho
  ray-vs-unit-cube slab test with band classification (|face coordinate| > 0.55
  joins the adjacent face → edge/corner). The animation is driven from the render
  loop (`RequestNextFrameRendering` while in flight — no timers). Custom hosts and
  tests can invoke the pick path directly via `ViewportControl.ViewCubeClick(point)`
  (synthetic mouse input does not reach Avalonia). The cube is interactive window
  chrome: **headless offscreen renders exclude it by design** (docs images and
  pixel tests see only the model).
- **Hover highlight**: moving the pointer over a part (not dragging, not over the
  view cube) tints it with a fainter version of the selection highlight — the
  pre-selection affordance — and shows its occurrence path in the status bar.
  One shader knob does both states: `uHighlight` at 1.0 is the selection gold,
  at 0.35 the hover tint; a hovered *selected* part just shows selection.
  Wireframe-mode parts blend their line color toward the highlight instead.
  Implementation: the existing pick raycast (per-part BVH + Möller–Trumbore) re-runs
  on pointer move, **throttled** to every 4+ DIPs of travel (`HoverThrottle` in
  `ViewCube.cs`, unit-tested); redraws happen only when the hovered index actually
  changes, and hover clears when a drag/press starts or the pointer leaves the
  viewport or enters the cube region. Hover shares the pick raycast, so it honors the
  section plane exactly as clicking does.
- **3D annotations (PMI)**: parts annotated in Modeling (`Part.Annotate` —
  selector-measured `LinearDimension`/`RadialDimension`, `LeaderNote`,
  `DatumLabel`, hole/thread callouts; see the Modeling README) render as classic
  dimension graphics: extension lines with a gap at the model and an overshoot past
  the dimension line, arrowheads, radial/note leaders, datum boxes, and **billboarded
  screen-constant text** from the shared **`StrokeFont`** (`StrokeFont.cs`: digits,
  A-Z, and the dimension symbols — diameter, degree, plus-minus, depth, counterbore,
  countersink — as polyline glyphs; the view cube's labels use the same table).
  Implementation is self-contained in `AnnotationLayer.cs` following the
  isoline/cube precedents: `AnnotationGeometry` is pure math (unit-tested without
  GL), billboarding is CPU-side and rebuilt **only when the camera pose, viewport,
  or annotation set changes** (value-equality on `AnnotationCamera` is the cache
  key — a static view costs one struct comparison per frame), and the batch draws
  through the existing line program. Depth behavior is **always-on-top** in v1 (the
  pass disables the depth test — dimensions read from any angle; occlusion-aware is
  a follow-up), and annotations are never section-clipped (documentation, not model
  geometry). Annotations pose with the instance transform, so assembly instances
  show their part's annotations in place; per-part resolution failures (a selector
  broken by an edit) surface in the status bar instead of killing the scene. The
  toolbar **Annot** toggle (`ViewportControl.ShowAnnotations`, default on) hides
  them, and **hiding a part hides its annotations with it** (the overlay's item set is
  rebuilt from the visible instances, so dimensions never float over an absent part). **Unlike the view cube, annotations DO render in headless offscreen output**
  — they are documentation content, so docs images can carry dimensions.
- **Measure tool** (toolbar **Measure**, `ViewportControl.MeasureMode`): while on,
  clicks pick **surface points** (the existing pick raycast, returning the exact
  hit point) instead of selecting parts; two picks create a **transient
  point-to-point dimension** shown immediately (the same `LinearDimension`
  machinery, world-anchored). Escape clears the measurement, toggling off clears
  and exits, and a new pair replaces the last. Tests and custom hosts drive it via
  `ViewportControl.MeasurePick(point)` (synthetic mouse input does not reach
  Avalonia). Persistent face-selector dimensions are authored in code in v1.
- **Model tree** (left): the current tab's loose parts and **assembly hierarchies** —
  assembly/sub-assembly header rows with their occurrences indented one level per
  depth (always expanded in v1; the tree walks the tab exactly like
  `Tab.Instances()`, so row order matches viewport instance indices). Visibility
  checkboxes exist at every level: a part row toggles that instance, an assembly row
  hides its whole subtree (effective visibility = own checkbox AND all ancestors;
  unchecking a parent does not touch the children's own state). Visibility is
  remembered per occurrence path, so it survives expanding a construction row, a tab
  switch, or a live reload — the tree hands the resolved visibility to the viewport
  *with* the instance list (`SetInstances(..., visible:)`), since the swap happens on
  the render thread and later per-row calls would land on the outgoing list. Clicking a name
  selects the *occurrence* (bold + gold in the viewport), and viewport picks
  highlight the tree row — selection stays in sync both ways and reports occurrence
  paths ("stack/clamp.2/bolt") in the title/status bar. Each part row also has a
  small **display-mode cycler** (`shade` / `wire` / `glass`) that steps the part
  through Shaded → Wireframe → Translucent (see below); the mode lives on the
  shared `Part`, so every instance of that part changes together.
- **Construction tree** (the disclosure triangle on a part row): expands a part into
  **how it was built** — for a `Shape` part the operation graph as nested rows
  (booleans, drills, rims and patterns showing their operands as children, and a
  **sketch row** under every sketch-driven extrude/revolve/sweep); for a
  `FeatureHistory` part the ordered feature list with names, suppression state, and
  `[Param]` values. Rows come straight from `Part.ConstructionTree()` in
  EngrCAD.Modeling (see its README) — the viewer adds no naming of its own.
  **Clicking a row previews it in the viewport**: a sketch draws its curves on its
  `SketchPlane` in 3D, any other row draws the feature edges of that sub-graph's
  geometry — i.e. the model *as of that step*, a rollback view — in construction cyan,
  drawn over the model (depth test off) so it reads against the finished part.
  Clicking the showing row again clears it. The lowering happens on a **background
  task** and is memoized per graph node (`ConstructionPreviewCache`), so the UI never
  stalls and a second click is instant; a step that cannot be lowered reports in the
  status bar instead of throwing. Expansion state is keyed by occurrence path, so it
  survives tab switches and live reloads. Custom hosts drive the overlay directly via
  `ViewportControl.SetConstructionPreview(segments, world)`. (Rollback bars,
  suppress-from-tree, and `[Param]` editing are follow-ups.)
- **Per-part display modes** (`Part.DisplayMode`, default `Shaded`): design code sets
  it (`part.DisplayMode = DisplayMode.Translucent`) and the tree's per-row cycler
  changes it live; custom hosts drive `ViewportControl.SetDisplayMode(index, mode)`.
  - *Shaded* — lit fill with the feature-edge overlay (the normal CAD look).
  - *Wireframe* — every mesh edge drawn as a line, no fill, in the part's color
    (selection turns it gold). Reuses the line program over the half-edge mesh's
    edges (`WireframeEdges`).
  - *Translucent* — alpha-blended fill (α ≈ 0.4) so you can see through to interior
    geometry, with the feature edges drawn opaque on top for a readable silhouette.
    Draw ordering (v1, honest limitations): opaque and shaded parts draw first, then
    translucent parts sorted **back-to-front by part center** with depth-writes off.
    This is correct for separated parts and for a translucent shell over opaque
    contents, but it is a *per-part* sort, not per-triangle — two interpenetrating or
    mutually-overlapping translucent parts, or a single non-convex translucent part
    seen through itself, can show blend-order artifacts. Section mode remains the tool
    for exact interior inspection.
- **Screenshot** (toolbar `Capture`, or `ViewportControl.SaveScreenshot(path?)`): saves
  the current framebuffer as a PNG and reports the path in the status bar. The pixels
  are read with `glReadPixels` *inside* the render pass (the only place GL calls are
  legal), then row-flipped (GL is bottom-up), forced opaque (framebuffer alpha is
  compositing residue), and encoded by a dependency-free `PngWriter` (8-bit RGBA,
  filter None, `System.IO.Compression` deflate) off the render thread. With no path
  the file lands in `Pictures/EngrCAD/engrcad-<timestamp>.png` (falling back to the
  working directory).
- **Properties** (right): occurrence path (plus the part name when they differ),
  kind (Shape/B-Rep/mesh/SDF), face count, closed, volume, surface area, world size,
  and world position of the selected instance.
- **Viewport dressing**: vertical-gradient background, adaptive ground grid on z = 0
  (1-2-5 spacing from the scene size) with RGB world axes, and a **feature-edge
  overlay** drawn over polygon-offset fills — the classic shaded-with-edges CAD
  look. Edge geometry comes from `Part.GetFeatureEdges()` (cached, primed by
  `Scene.PreMesh` off the render thread): B-Rep-backed parts use their **actual
  B-Rep edges** sampled at display resolution (`BrepFeatureEdges` in Interop — a
  bore rim stays a smooth circle however coarse the mesh; smooth seams like
  wrap-split junctions are classified by exact surface normals and omitted), other
  parts fall back to mesh dihedrals (`MeshFeatureEdges`). The edges, the display
  mesh, selector annotations, construction previews, and STEP export all share the
  ONE solid `Part.TryGetSolid()` caches — a Shape part is no longer lowered once per
  consumer (see the Modeling README; `Scene.PreMesh` of a heavy Shape scene measured
  32.8 s before, 10.1 s after).
- **Status bar** (bottom): last input on the left, control hints on the right.

`EngrCad.Show` may be called once per process (Avalonia allows a single application
lifetime). Custom hosts pass an `onViewportReady` callback, capture the
`ViewportControl`, and later call its thread-safe `SetInstances` (posed
`PartInstance` list — `Tab.Instances()`; `SetParts` remains as the loose-part
convenience) — GL resources are swapped inside the next render pass; auto-framing is
opt-in per call, so cameras survive updates. Parts should be pre-meshed
(`Scene.PreMesh()`) so tessellation stays off the render thread.

**Assembly instancing**: both render paths (window and offscreen) upload each
distinct `Part` once — one vertex/edge buffer set and one pick BVH per part,
however many occurrences place it — and draw every instance with its own composed
world matrix (`Frame3d` chain × `Part.Transform`). CPU prep (RenderMesh, feature
edges, BVH) is deduped by part reference. A future optimization is true GPU
instancing (one draw call per part with a matrix buffer); today it is one draw call
per instance over shared buffers, which is already flat in memory.

## On-demand tab meshing (the window opens immediately)

A document's tabs are meshed **when they are first viewed**, not before the window
opens (`EngrCadOptions.LazyTabMeshing`, **on by default**). Measured on
`samples/EngrCAD.Demo` — 8 tabs, 17 parts, two of which take ~14 s and ~9 s to lower
and tessellate — the window went from **~54 s to ~3.5 s**, and the tabs the user never
opens cost nothing at all.

What the user sees when a tab needs work:

- The tab's rows appear in the model tree **immediately** (the whole tab, including
  parts still to come), so the tree says what is loading.
- Parts appear **as they are meshed**, in tab order — the viewport is live and
  orbitable while the rest is still computing, not a frozen window.
- A **progress panel** at the bottom of the viewport: the honest count
  (`meshing 'sketch' — 3 of 3 parts: 'standard holes'`), a determinate bar, and a
  secondary line naming the route this part takes through the kernel
  (`Lowering to B-Rep...`, `Polygonizing the field...`, `Tessellating surfaces...`,
  and — only for geometry that genuinely carries NURBS, which sketch profiles and
  glyph outlines do — `Reticulating splines...`). The same text goes to the status bar.
- **Revisiting a tab is instant**: meshes are cached per `Part`, so the whole tab
  republishes in one go with no background work and no progress UI.

`TabMeshLoader.cs` is the state machine (Avalonia-free, so it is unit-tested
headlessly in `TabMeshLoaderTests`), and the rules it enforces are the ones such a
feature usually gets wrong:

- **Nothing heavy on the UI or render thread.** Preparation runs on a background task
  (`Part.Prepare` — mesh, feature edges, annotations — plus the AO bake), exactly the
  discipline `Scene.PreMesh` and the construction-tree previews already follow. The
  properties panel likewise reports `meshing...` for a part that isn't ready rather
  than blocking on `GetMesh`.
- **A stale result can never land in the wrong tab.** Every request takes the next
  generation token; the worker re-checks it between parts and every callback re-checks
  it after being posted to the UI thread, so switching tabs mid-job discards both the
  remaining work and any result already in flight.
- **Switching away cancels at the next part boundary**, not mid-part. `Part.GetMesh`
  passes the `ProgressCancel` to Surface Nets (which polls it and reports fractions, so
  SDF parts also give sub-part progress), but a B-Rep lowering is *not* interruptible:
  its result is cached inside `Part.TryGetSolid`, and abandoning one would leave that
  cache claiming a lowering it never produced. So a part in flight finishes, its mesh
  stays cached — returning to that tab is then instant — and only its *publication* is
  dropped.
- **A part that throws is named, not swallowed.** It drops out of the published
  instances (there is no geometry to upload), its tree row turns red with the reason as
  a tooltip, the failure goes to the status bar and the `IEngrCadLog`, and the rest of
  the tab still loads with the bar still reaching the end.
- **Hot reload keeps working**: after a `dotnet watch` patch the *current* tab
  re-meshes on the loader's task (camera preserved), and the other tabs stay lazy.

**The escape hatch** is one flag: `.WithLazyTabMeshing(false)`,
`EngrCadOptions.LazyTabMeshing = false`, or `--mesh all` on the command line restores
the eager behavior — `Scene.PreMesh` for the whole document before the first frame, no
progress UI, every tab instant once the window appears. (`--mesh lazy` is the default
spelling.) Headless paths are unaffected either way: `--export`, `--render` and
`RenderToImage` mesh exactly what they need, eagerly.

> **Custom hosts**: with lazy meshing on, `EngrCad.Show` no longer pre-meshes the
> document, so a host that drives `ViewportControl.SetParts`/`SetInstances` itself must
> prepare its own parts off the render thread first (`Part.Prepare`, `Tab.PreMesh` or
> `Scene.PreMesh`) — the contract `SetInstances` always documented — or opt out with
> `.WithLazyTabMeshing(false)`.

## The live-modeling loop

Hand `EngrCad.ShowLive` a scene *factory* and run the model under `dotnet watch`:

```csharp
return EngrCad.Run(args, BuildScene, "my bracket");   // ShowLive by default

static Scene BuildScene() { ... }                     // edit + save = live update
```

```
dotnet watch --project samples/EngrCAD.LiveDemo
```

`dotnet watch` hot-reloads method-body edits into the running process; a
`MetadataUpdateHandler` in this library re-invokes the factory and swaps the new scene
in — camera untouched, sub-second. If the factory throws, the last good scene stays and
the error shows in the overlay. Rude edits (signature changes) restart the process;
the camera pose is persisted per title (30-minute freshness window) so the view
survives those too. `EngrCad.Run` also gives every model program standard switches:
`--view` (static show) and `--export part.step|part.stl|part.obj` (headless: STEP for
B-Rep-representable parts, binary STL or OBJ merged with transforms applied —
CI/slicer-friendly, no window).

## Configuring: `EngrCad.Configure()`

`EngrCad.Run(args, factory)` works with zero configuration; when a program wants
host-level defaults, the fluent builder sets them once:

```csharp
return EngrCad.Configure()
    .WithTitle("bracket")
    .WithQuality(new MeshQuality { SegmentsPerCircle = 48 })   // display/export default
    .WithRenderSize(1920, 1080)                                // --render image size
    .WithViewStyle(ViewStyle.Shaded)                           // --render view style
    .WithSection(SectionAxis.Z, 6)                             // --render section plane
    .WithAmbientOcclusion(false)                               // baked AO (on by default)
    .WithLazyTabMeshing(false)                                 // mesh everything up front
    .WithLog(msg => logger.LogInformation("{Message}", msg))   // status/error seam
    .Run(args, BuildScene);
```

The builder accumulates an **`EngrCadOptions`** POCO (`Title`, `Quality`,
`RenderWidth`/`RenderHeight`, `RenderStyle`, `SectionAxis`/`SectionOffset`,
`AmbientOcclusion`, `LazyTabMeshing`, `Log`, `OnViewportReady`) and its terminal methods (`Run`, `Show`, `ShowLive`,
`RenderToImage`) mirror the static `EngrCad` entry points with those options
applied. The plain `EngrCad.Run/Show/ShowLive` overloads are unchanged and remain
the simple path.

- **Mesh-quality precedence** (`Scene.ResolveQuality` implements it): a `Scene`
  constructed with explicit options always wins > the `EngrCadOptions.Quality`
  default > `MeshQuality`'s built-in defaults. So a scene that deliberately chose
  its own quality is never silently overridden, while scenes that didn't care
  inherit the host's setting everywhere — display, `--export .stl/.obj`, `--render`,
  and hot reloads.
- **Logging seam** (`IEngrCadLog`: `Info`/`Error`): everything the entry points
  report — export confirmations, usage errors, headless-render results, and the
  live-reload status/error messages that appear in the overlay — goes through the
  configured log. The default is `EngrCadLog.Console` (Info → stdout, Error →
  stderr, the historical behavior). `WithLog(Action<string>)` adapts a plain
  delegate; `EngrCadLog.From(info, error)` keeps the two streams separate.

### `Microsoft.Extensions` friendliness (without the dependency)

The viewer deliberately does **not** reference `Microsoft.Extensions.*`.
`EngrCadOptions` is a plain mutable POCO, so it binds as
`IOptions<EngrCadOptions>` out of the box, and `EngrCad.Configure(EngrCadOptions)`
accepts the DI-provided instance directly:

```csharp
// In a generic-host app:
builder.Services.Configure<EngrCadOptions>(builder.Configuration.GetSection("EngrCad"));

// In the model program, with IOptions<EngrCadOptions> options and ILogger logger:
return EngrCad.Configure(options.Value)
    .WithLog(EngrCadLog.From(
        msg => logger.LogInformation("{Message}", msg),
        msg => logger.LogError("{Message}", msg)))
    .Run(args, BuildScene);
```

(Delegate/interface-typed properties are simply left unbound by configuration
binding — set `Log`/`OnViewportReady` in code.)

## Headless offscreen rendering (screenshots without a window)

For tests and AI agents that need to *see* a scene without opening a window,
`EngrCad.RenderToImage` renders straight to a PNG:

```csharp
EngrCad.RenderToImage(scene, "out.png", width: 1280, height: 800);   // auto-framed iso
EngrCad.RenderToImage(scene, "front.png", camera: someCameraState);  // explicit pose
EngrCad.RenderToImage(scene, "wire.png", style: ViewStyle.Wireframe);       // global view style
EngrCad.RenderToImage(scene, "cut.png",                                     // real section plane
    sectionAxis: SectionAxis.X, sectionOffset: 0.0);
```

Headless renders honor everything the window draws — **baked ambient occlusion**
(`ambientOcclusion:`, on by default, from the identical per-vertex bake the window
uploads), **per-part display modes**
(wireframe, translucent with the same shared back-to-front ordering and opaque
silhouette edges), the **global `ViewStyle`** with the same precedence rule,
**axis-aligned section planes** (`sectionAxis` + `sectionOffset`; enabled when the
offset is non-null) **including their SDF isoline overlays**, and **3D
annotations** (parts' dimensions/notes/callouts draw through the same
`AnnotationGeometry` the window uses, always on when present — annotations are
documentation, so docs renders carry them) — so a headless PNG matches what the
viewer shows (interactive selection/hover highlights and the view cube are the
deliberate exclusions), and docs cutaways use real section planes instead of
boolean-cut workarounds (DocsGen `render:` fences take `section:<x|y|z>,<offset>`
and `style:<name>` options for exactly this).

`EngrCad.Run` exposes it as a switch too: `--render out.png` renders and exits, no
window (alongside `--view` and `--export`), with `--render-style
points|wireframe|shaded|shaded-edges`, `--section x|y|z <offset>` (e.g.
`--section z 6`), and `--ao on|off` selecting the view style, section plane, and
ambient occlusion — CLI switches win over the builder's
`WithViewStyle`/`WithSection`/`WithAmbientOcclusion` defaults, and invalid values are
usage errors (exit 2). `EngrCadBuilder.RenderToImage` mirrors the static overload's
optional `style`/`sectionAxis`/`sectionOffset`/`ambientOcclusion` parameters, falling
back to the accumulated options when omitted. Check `EngrCad.CanRenderToImage` first to skip
gracefully on machines with no GPU/ANGLE.

- **No window, no Avalonia lifetime.** `OffscreenRenderer` renders into an offscreen
  **EGL pbuffer** created directly over the ANGLE runtime Avalonia already ships on
  Windows (`av_libglesv2.dll` from Avalonia.Angle.Windows.Natives exports both the GLES
  and the EGL entry points — the latter with an `EGL_` prefix). `EglContext` P/Invokes
  `eglGetPlatformDisplayEXT`/`ChooseConfig`/`CreatePbufferSurface`/`CreateContext`/
  `MakeCurrent`, preferring the D3D11 hardware backend, then D3D11-on-WARP (software —
  works on CI and locked sessions), then the default display. The same Silk.NET `GL`
  surface then draws the scene and `glReadPixels` reads it back.
- **The look matches the viewport by construction**: both passes draw with the shared
  render core in `RenderCore.cs` — `ViewerShaders` (ONE shader set; the only feature
  the offscreen pass neutralizes is the selection highlight, uHighlight 0 — there is
  no interactive selection offscreen), `RenderModes` (the global-style x per-part-mode
  precedence and the translucent back-to-front sort), `CameraMath`
  (LookAt/projection/column-major writer, the scene-scaled near/far frustum, and the
  auto-framing distance), and `RenderGeometry` (grid/axes builder, line/mesh upload).
  Evolve the look there and it lands in the window and in headless renders at once; do
  not re-fork per-pass copies. Shader sources must stay pure ASCII (ANGLE rejects the
  whole shader on any non-ASCII byte — this once black-screened the viewport).
- **`PngWriter`** is a tiny dependency-free 8-bit RGBA PNG encoder (one zlib IDAT via
  `System.IO.Compression`); no image library is pulled in.

## How it works

- **`ViewportControl`** extends Avalonia's `OpenGlControlBase` and adapts its
  proc-loader into a Silk.NET `GL` API object, giving the full modern GL surface over
  whatever context Avalonia provides — desktop OpenGL 3.3+ or, on Windows, OpenGL ES 3
  via ANGLE (shaders are compiled with a version header chosen at runtime).
- Meshes from any engine are turned into `RenderMesh` (flat-shaded) buffers and drawn
  with a simple directional-light shader.
- **Camera** (laptop-friendly): drag orbits, Shift+drag pans, Ctrl+drag or scroll zooms;
  right/middle-drag also pans. Keyboard works everywhere: arrows orbit, +/−
  (or PageUp/Down) zoom, WASD pans. Z is up.
- **Input plumbing**: all pointer/keyboard handlers are registered on the *window* with
  `handledEventsToo` — control-level handlers proved fragile (gesture recognizers and
  hit-testing over the GL surface starved the viewport of events, breaking trackpads).
  The overlay's second line reports the last input received, which makes input problems
  diagnosable at a glance.
- **Picking**: click selects the nearest part under the cursor (unprojected ray +
  per-object triangle BVH + Möller–Trumbore); the selection is highlighted gold and its
  *part name* shown in the title bar; clicking it again deselects. `ShowStatus(string)`
  lets a host surface messages (script errors) in the overlay.

## Demo

The showcase scene lives in `samples/EngrCAD.Demo` (a console app using the Scene API —
the exact consumer experience):

```
dotnet run --project samples/EngrCAD.Demo
```
