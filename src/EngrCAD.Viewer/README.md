# EngrCAD.Viewer

Cross-platform viewer **library**: Avalonia UI with an OpenGL viewport rendering kernel
geometry. The only project allowed UI/rendering dependencies (Avalonia, Silk.NET).

## Where the render model lives: `EngrCAD.Viewer.Core`

The UI-free half of the render core is **not in this project**. It sits in
[`src/EngrCAD.Viewer.Core`](../EngrCAD.Viewer.Core/README.md) — same `EngrCAD.Viewer`
namespace, separate assembly, **no Avalonia and no Silk.NET** — so a third front end
(the Blazor WebAssembly client, whose GL is WebGL2 through JS interop) can *share* it
instead of copying it. Copying is precisely the failure `RenderCore.cs` was created to
stop: the window and offscreen passes once duplicated ~150 lines and drifted silently.

Over there: `ViewStyle`, `SectionAxis`/`SectionPlane`/`SectionCombine`, `SectionClip`
(`Hides`/`Siblings`), `EffectiveMode`/`RenderModes`, `ViewerShaders` (the GLSL sources
and `MaxSectionPlanes`; `Header(es)` already emits both an ES3 and a desktop 3.3
header, and WebGL2 wants the ES3 one), `CameraMath`, and the pure half of
`RenderGeometry` (`BuildGridAndAxes`, `NiceStep`, `SegmentVertices`).

**Step 2 of the extraction moved the widgets' pure halves too** (same rule, same
namespace): `ViewCubeMath`/`ViewCubeAnimation`/`ViewCubeGeometry` (pose table, hit
test, rotate-snap, the 250 ms transition, and the cube's fill/edge/label arrays with
their palette), `StrokeFont`, `AnnotationItem`/`AnnotationCamera`/`AnnotationGeometry`
(with the overlay colour), `SectionContours`/`SectionContourGeometry` (with the three
isoline family colours), and `TabMeshLoader` + `MeshFlavor` (Avalonia-free, though the
loader stays thread-model-bound — the browser keeps its own single-threaded loader).
This project keeps their GL halves: `ViewCube`, `AnnotationLayer`,
`SectionContourRenderer`.

Still here, in `RenderCore.cs`, because each one takes a Silk.NET `GL`:
`ViewerPrograms` (`LinkProgram`/`CompileShader`), `SectionUniforms` (the single place
either pass writes the section uniforms), and `RenderUploads` (`UploadMesh`,
`UploadLines`, `UploadOcclusion`, `SetDefaultOcclusion`).

Consumers see no difference: the namespace did not move, so `using EngrCAD.Viewer;`
still resolves `SectionPlane` and `ViewStyle`, and `EngrCAD.Viewer` references
`EngrCAD.Viewer.Core` transitively.

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
first visit). Picking reports part names in the title bar; the raycast itself is
`ScenePick` in **EngrCAD.Viewer.Core**, so `HitTest` here is three lines of adapting
the parallel instance/visibility lists into `PickInstance`s and the browser client
answers a click with the same maths.

## The CAD chrome

Dark-themed layout around one shared GL viewport:

- **Toolbar**: Fit (zoom to visible parts), Front/Top/Right/Iso standard views (each
  a cube direction resolved by `ViewCubeMath.PoseFor` — the view cube's own pose
  source), a
  perspective/**orthographic** toggle (the ortho frustum keeps the target plane's
  apparent size, so toggling doesn't jump), the **view-style dropdown** (see below),
  an **AO** toggle (ambient occlusion, on by default — see below), a **Section**
  toggle with an **X/Y/Z axis cycler** button beside it (see below), a **Cut@View**
  button (an *oblique* section plane from the current view: through the orbit target,
  normal = the camera's eye direction, so it clips away everything between the viewer
  and the view centre — the minimal toolbar affordance for planes the axis model
  cannot express; `[`/`]` still nudge it along its own normal, and hosts keep the
  full `ViewportControl.SectionPlanes` API), an **Annot**
  toggle (3D annotations, on by default — see below), a **Fields** toggle
  (simulation results, on by default — see below), a **Measure** toggle
  (interactive dimensioning — see below), an **Explode** toggle with a factor slider
  (see below), a **BOM** button (see below), and a **Check** button (the model
  validation report — `SceneReport` in Modeling: per-part watertightness, volume,
  area, bounds, with notes for open meshes, meshing failures and active debug
  modifiers, shown in a window BOM-style). Part-level **debug modifiers**
  (`Part.Ghost`/`Hidden`/`Isolated`, rules in Modeling's `DebugFilter`) are honored
  by the viewport and every render/export path: ghosts draw translucent via
  `Part.EffectiveDisplayMode`, hidden parts are skipped by `EffectiveVisibility`,
  and an active isolate shows only isolated parts.
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
  - *Cost, and why it **streams***: the bake is honest ray casting and it is not cheap —
    measured on the demo scene at **12.3 s total**, of which the M8 stud is 3.0 s and
    the tapped block 5.5 s — and it already saturates every core, so more parallelism
    has nothing left to give. Nothing therefore waits for it. The window shows the
    scene **immediately, flat-lit**, and each part's occlusion arrives as its own bake
    finishes (`AmbientOcclusion.BakeInBackground`, **cheapest part first** so most of a
    scene lights up in the first moments, with the whole job reported once in the status
    bar and **per-part progress in the model tree**: a row carries a small italic "ao"
    badge until its part's bake lands — `ViewportControl.OcclusionBaked` raises the
    part on the UI thread as each result publishes). This is not a placeholder state: a mesh VAO with no occlusion buffer reads the
    context constant 1.0, which is *exactly* the AO-off shading, so an unbaked part is
    the correct flat-lit render of that part and the only thing the bake changes is that
    crevices darken. The bake queue follows the **visible tab**, so a tab you never open
    is never baked. Measured on the demo (Debug): time-to-window **27.1 s → 14.2 s**,
    with the first tab's occlusion landing 0.2 s later and the threads tab's 5.7 s after
    you switch to it. `TryGet` (a pure cache read) is the only lookup on the render
    thread — a bake can never land there. Two deterministic guards bound the work
    itself: a **ray budget** (2M rays per bake) halves the per-vertex ray count on very
    high vertex counts, and meshes above **80k triangles skip the bake entirely** — in
    lattice-like geometry every ray walks a labyrinth instead of escaping and the
    per-ray cost climbs by an order of magnitude (a 100k-triangle gyroid measured
    ~10 s). Both rules are pure functions of the mesh, so they cannot make the window
    and the headless render disagree.
  - *Headless is still eager*: `RenderToImage` bakes inline, because it is one-shot and
    must be deterministic. The streamed and the inline paths produce the **same floats**
    (same cache, same `Bake`), so window/offscreen parity holds — the window simply
    reaches it a second or two after it opens.
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
  `RenderModes.Resolve` in `EngrCAD.Viewer.Core`, shared verbatim with the offscreen
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
  **Up to four planes** can be active at once (`SectionPlane(Normal, Offset)` —
  general normals; `SectionPlane.On(axis, offset)` builds the axis-aligned ones the
  toolbar and CLI expose) combined by `SectionCombine`: **`Intersection`** clips only
  where *every* plane excludes — two perpendicular planes give the classic **quarter
  cut**, three an **octant** — while **`Union`** clips where *any* does, the
  single-plane behavior generalized. With one plane the two rules coincide, so
  single-plane output is unchanged. `SectionClip.Hides` carries the combine rule too,
  since otherwise picking and rendering would disagree about which corner a quarter
  cut removes.
  **Arbitrary orientation**: `SectionPlane.On(Frame3d)` places a plane by a rigid frame
  (origin on the plane, +Z at the clipped side) and `SectionPlane.Through(point,
  normal)` by a point and a direction — so a cut can face anywhere, including *along a
  face* (`BrepQueries.Frame(face)`) or a sketch plane. Nothing downstream needed
  changing: the shaders, `SectionClip` and the isoline overlay have always taken a
  general normal; only the toolbar's axis cycler is restricted to X/Y/Z, and hosts reach
  past it with `ViewportControl.SectionPlanes` (or `RenderToImage(sectionPlanes:)`) —
  the toolbar's **Cut@View** button is the built-in shortcut, placing one oblique plane
  from the current camera.
  **Per-part opt-out**: `Part.ClippedBySection = false` makes a part render *and pick*
  whole inside a cutaway. That is the drafting convention every standard shares —
  shafts, bolts, nuts, washers, keys, pins and ribs are drawn unsectioned, because
  cutting a solid fastener lengthwise shows nothing and only clutters the section — and
  it gives assemblies the "cut the housing, keep the internals" view for free. It is
  implemented as the shader's own master switch flipped per draw group
  (`ViewportControl.SectionFor`, mirrored in the offscreen pass), with picking simply
  not consulting `SectionClip` for such a part, so the clickable and the visible surface
  stay the same one; an exempt part also contributes no isolines, having no cut face to
  draw them on. The model tree carries a per-row **cut/whole** toggle beside the
  display-mode cycler (`ViewportControl.SetClippedBySection` — writes through the part,
  so sibling rows and every instance follow; the isoline overlay detects the changed
  flag itself, the same self-detection visibility changes use). With no section active
  the flag changes nothing at all (renders are
  byte-identical), so design code can set it unconditionally.
- **SDF isolines on the section plane** (automatic when available): when the section
  plane cuts a part whose geometry is an `Sdf` — or a `Shape` whose implicit lowering
  exists (`CanConvertTo(Implicit)`; lowered once and cached per part, never per
  frame) — iso-distance contours of the field are overlaid on the cut. The **gold**
  d = 0 contour is the exact surface cross-section; **cool blue** positive and
  **warm orange** negative families at d = ±k·spacing visualize the field itself —
  wall thickness at a glance (count the warm rings), blend and offset debugging.
  The lowering is `Part.TryGetSdf` — cached **on the part**, beside the B-Rep lowering
  `Part.TryGetSolid` caches, so toggling the section off and on, switching tabs, or
  hiding a part no longer re-lowers (a bridged shape's implicit lowering can build a
  `MeshSdf`, which is far too expensive to repeat).
  Spacing is 1-2-5-rounded from the contributing parts' bounds (shown in the status
  bar; a wall thinner than one spacing simply shows no interior ring). Extraction is
  `SdfContours` in EngrCAD.Interop (marching squares over one batch-`Evaluate` grid,
  ~160 cells across, per part per rebuild); it reruns only when the section height,
  scene, or visibility changes — never per frame — and in the window it runs **on a
  background task** (`SectionContourWorker`, the `AmbientOcclusion.BakeInBackground`
  precedent): the first section-enabled frame no longer stalls on the marching
  squares plus a bridged shape's first `TryGetSdf` lowering; the previous contours
  (or nothing, on first enable) draw until the new ones land, generation-stamped so
  a scene swap or a superseding nudge can never adopt a stale build (the
  `TabMeshLoader` rule). Lines draw through the shared line
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
  With **several planes active** each plane gets its own contour set, clipped by its
  **siblings** so it covers only the part of that plane which is actually an exposed
  cut face. The rule lives in `SectionClip.Siblings`, next to `Hides`, and is stated
  there as one sentence: a point on plane *i* is on the visible cut face iff the drawn
  line survives the full clip rule *and* the material just past the plane does not
  (`!Hides(p) && Hides(p + eps*n)`). That single sentence yields both modes — under
  `Intersection` the siblings are applied **flipped** (the face is exposed only where
  every other plane excludes), under `Union` unflipped (the face is exposed wherever
  no other plane removes the point). Without it a quarter cut draws each plane's
  contours across its full extent; the visible symptom is the positive family fanning
  out past the silhouette on the half that is buried in material (the buried half
  *inside* the silhouette is hidden by depth anyway, which is why the Union case and
  the outside-the-silhouette bands are what the regression tests assert).
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
  Those three values are `Highlight` in **EngrCAD.Viewer.Core**, shared with the
  browser client, so the two front ends cannot disagree about what selection looks
  like.
  Implementation: the existing pick raycast (`ScenePick`, also in Viewer.Core —
  per-part BVH + Möller–Trumbore over an unprojected ray) re-runs
  on pointer move, **throttled** to every 4+ DIPs of travel (`HoverThrottle`, moved
  to Viewer.Core with the pick maths); redraws happen only when the hovered index actually
  changes, and hover clears when a drag/press starts or the pointer leaves the
  viewport or enters the cube region. Hover shares the pick raycast, so it honors the
  section plane exactly as clicking does.
- **3D annotations (PMI)**: parts annotated in Modeling (`Part.Annotate` —
  selector-measured `LinearDimension`/`RadialDimension`/`AngularDimension`,
  `LeaderNote`,
  `DatumLabel`, hole/thread callouts, hole tables; see the Modeling README) render as
  classic
  dimension graphics: extension lines with a gap at the model and an overshoot past
  the dimension line, arrowheads, radial/note leaders, datum boxes, **angular arcs**
  (extension rays + a 5°-chorded arc with tangent arrowheads + degree text outside
  its midpoint; the arc radius is the author's `Offset` length, else ¾ of the shorter
  ray), and **billboarded
  screen-constant text** from the shared **`StrokeFont`** (`StrokeFont.cs`: digits,
  A-Z, and the dimension symbols — diameter, degree, plus-minus, depth, counterbore,
  countersink — as polyline glyphs; the view cube's labels use the same table).
  Text may be **multi-line** (`'\n'`): billboarded blocks center their lines, leader
  text stacks continuation lines below the tail line (hole callouts put their
  counterbore/countersink continuations there), and a datum box grows to span every
  line — single-line output is bit-identical to the pre-multi-line layout, which the
  committed docs PNGs hang off.
  **Annotations are pickable**: a click within `AnnotationGeometry.PickRadiusPx`
  (8 style px) of any of an annotation's drawn segments selects it — drawn again in
  the one selection gold (`Highlight.Selection`), text reported in the status bar;
  clicking it again or empty space deselects; a claimed click never falls through to
  the part behind. The pick is `AnnotationGeometry.Pick` (pure math — the same
  `Build` segments, so what you see is exactly what you can click) and is
  **depth-blind on purpose**, matching the always-on-top draw; drive it directly via
  `ViewportControl.PickAnnotation(point)` / read `SelectedAnnotationText`.
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
  depth (the tree walks the tab exactly like `Tab.Instances()`, so row order matches
  viewport instance indices). Assembly rows carry a **disclosure triangle**
  (default expanded, state remembered per assembly path across rebuilds/tab
  switches); collapsing is pure UI state — the subtree's rows are still built and
  registered so viewport visibility and instance indices never shift, they are just
  not attached to the panel — which is why collapsing an assembly hides nothing in
  the viewport and re-expanding restores exactly what was there. Visibility
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
  shared `Part`, so every instance of that part changes together. Beside it sit the
  **cut/whole** section-exemption toggle (`Part.ClippedBySection` — see the section
  planes above) and the transient **"ao" badge** that marks a part whose ambient
  occlusion is still baking in the background.
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
  survives tab switches and live reloads — and the **preview itself is restored by
  path** after a live reload or a tab revisit (`RestorePreview`: node references
  change with the fresh scene, but the occurrence path and the construction path in
  the preview key do not; a key that no longer resolves restores nothing). Custom
  hosts drive the overlay directly via
  `ViewportControl.SetConstructionPreview(segments, world)`.
  **Feature rows are editable**: each row of a `FeatureHistory` part carries a
  **suppress toggle** ("sup"/"uns" — a suppressed feature passes the body through
  untouched) and a **rollback marker** ("‖" — suppresses every feature below it;
  clicking a later row moves the bar down, restoring what it suppressed above, and
  the last row's marker restores the whole history; the flag logic is the UI-free
  `FeatureRollback`, which records what the *bar* suppressed so it never restores a
  feature the user suppressed deliberately). Clicking a feature row also opens its
  **`[Param]` values as editable fields in the properties panel** — Enter applies the
  value through the SAME JSON seam `SaveParameters`/`LoadParameters` (and the MCP
  `set_param` tool) use, so accepted spellings cannot drift, then regenerates via
  `Part.Regenerate()` on a background task: a successful regeneration republishes the
  tab (the loader re-meshes exactly the changed part), a failed one keeps the
  previous geometry and names the failing feature in the status bar, exactly the
  feature-tree semantics.
  **Headless renders draw previews too** — `EngrCad.RenderToImage(..., preview:
  new ConstructionPreviewRequest(part, node))` puts one row's rollback view into a
  still image, through the same `PreviewLayer` the window uses, so the colour, the
  always-on-top depth rule and the never-section-clipped rule cannot drift between the
  two paths. A row identifies itself as *(part, node)* because a `ConstructionNode`
  carries no back-reference to its part; that pairing also lets the build reuse the
  part's cached solid for the root row. Building lowers geometry, so it happens on the
  caller's thread before any GL exists (the headless mirror of the window's
  background-task rule), and a row that cannot be previewed **throws** rather than
  rendering a silently empty overlay — a docs page must not claim a preview it never
  made. (Rollback bars, suppress-from-tree, and `[Param]` editing are follow-ups.)
- **Per-part display modes** (`Part.DisplayMode`, default `Shaded`): design code sets
  it (`part.DisplayMode = DisplayMode.Translucent`) and the tree's per-row cycler
  changes it live; custom hosts drive `ViewportControl.SetDisplayMode(index, mode)`.
  - *Shaded* — lit fill with the feature-edge overlay (the normal CAD look).
  - *Wireframe* — every mesh edge drawn as a line, no fill, in the part's color
    (selection turns it gold). Reuses the line program over the half-edge mesh's
    edges (`WireframeEdges`, which now lives in `EngrCAD.Viewer.Core` — it has no GL
    in it, and the browser front end draws the same segments in the same order).
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

**Re-posing without re-uploading**: `SetInstancePoses(instances)` replaces only the
per-instance world matrices of the list already shown. It exists for the exploded
view, where `Tab.Instances(factor)` returns the same parts in the same order at every
factor — going through `SetInstances` there would delete and re-upload every buffer on
each slider tick. It validates the list part-for-part and falls back to a full
`SetInstances` if the document changed underneath (a live reload mid-animation).

## Simulation results (colour maps, legend, deformed shape)

A part that carries `MeshField` results and states a `Part.FieldDisplay` (both in
EngrCAD.Modeling) is drawn through a colour map, with a legend and — where the display
asks for it — displaced by a displacement result with the original ghosted behind it.
The toolbar **Fields** toggle (on by default) switches it off, `RenderToImage(…,
fields: false)` does the same headlessly, and the properties panel shows the part's
results, which one is displayed, its min/max and the deformation scale.

**Colour is a vertex attribute, deformation is geometry** — and the two behave
differently on purpose:

- Colours ride `aFieldColor` (attribute 3) under the exact rule baked occlusion follows:
  a mesh uploaded with no colour buffer reads a context constant, the shader's
  `uFieldColor` strength is 0, and `mix(uColor, vFieldColor, 0.0)` is `uColor` bit for
  bit. **A part with no results therefore renders byte-identically** to before field
  display existed — proved by the docs suite (all 87 rendered PNGs unchanged across the
  shader change), not merely intended.
- A deformed shape is different geometry, not a different pose, so it cannot ride the
  matrices-only `SetInstancePoses` path the exploded view and the animation transport
  use. It **re-uploads**, deliberately and explicitly, which is why `ShowFields` is a
  mode switch and not something on the animation path. Facet normals are recomputed from
  the displaced positions (carrying the originals over would make the deformed shape look
  exactly like the original), the undeformed shape draws as an extra translucent body at
  `FieldRendering.GhostAlpha`, and a deformed part gets **no feature-edge overlay** — its
  exact B-Rep edges describe geometry that has moved.

Picking follows what is drawn: a deformed part's pick BVH is built over the displaced
mesh, so a click selects it where it is on screen.

The **legend** is `FieldLegend` (EngrCAD.Viewer.Core) drawn by `FieldLegendLayer` — a
colour bar of flat-coloured bands, tick numbers and a title in the stroke font, on the
left edge (the cube owns the top-right, the meshing panel the bottom centre), depth test
off. Unlike the view cube it **is** drawn in headless renders: a legend is documentation,
the same argument that puts dimensions in a docs render, and a colour plot without its
scale is a picture of nothing in particular. One legend, from the first visible part that
resolves a display — several parts on different scales under one bar would be a legend
that lies, which is what an explicit `FieldDisplay.Range` is for.

## Exploded views

The **Explode** toolbar toggle plus its factor slider pull an assembly apart. The
factor is a scalar 0 → 1 composed into the flattening in EngrCAD.Modeling
(`Occurrence.ExplodeOffset`, `Assembly.Flatten(factor)`, `Tab.Instances(factor)`), so
the viewer holds no explode state beyond the number: dragging the slider re-flattens
and calls `SetInstancePoses`, which touches matrices only. Turning the toggle on
derives the offsets once via `Assembly.AutoExplode` on a **background task** — it reads
the instances' bounds, which means meshing, and that must never happen on the UI thread
(the same rule construction previews follow). The controls are disabled for a tab with
no assemblies: a loose part belongs to no assembly and has nothing to explode away from.

Headless has the same knob and therefore the same result by construction:
`EngrCad.RenderToImage(scene, path, …, explode: 1)`, `--explode <factor>`, and
`EngrCad.Configure().WithExplode(f)`. A non-zero factor derives the offsets itself if
the design has not set them; a zero factor never touches the document, and an exploded
render at factor 0 is byte-identical to a plain one.

## Animation playback

The toolbar grows a transport — **Play/Pause**, **Loop**, and a time scrubber — when
the host gives the window an animation: `EngrCad.Configure().WithAnimation(scene =>
new Animation(...)...)`. The factory runs per scene, INCLUDING per live reload (tracks
pose the occurrences they captured, and a hot reload remakes the scene), on a
background task because track construction may read bounds and mesh parts — the same
rule `AutoExplode` follows; a stale result is dropped by comparing the scene reference,
the TabMeshLoader generation lesson one token cheaper.

The layering rule: **evaluation and transport state live in `EngrCAD.Viewer.Core`**
(`Animation.At(t)` is pure; `AnimationPlayback` is the play/pause/loop/seek machine),
and `SceneHost` owns only a `DispatcherTimer` and the widgets. Each tick advances the
clock by REAL elapsed time (not the timer interval, so playback speed is honest under
load), evaluates the sample, and applies it: pose tracks re-pose the current tab's
instances **matched by occurrence path** (a whole-scene track may carry other tabs'
instances — ignored; unmatched instances keep their document pose) through the same
`SetInstancePoses` matrices-only route the explode slider uses, and camera tracks set
`Viewport.Camera`. Scrubbing while paused renders the same frames playback would — one
evaluation path, which is the point. The web viewport gets the same reuse for free when
its transport lands (filed in todo.md).

## Animated export

`animation.RenderApng(scene, path, frames, width, height, camera?)` renders the same
pure `Animation.At(t)` the window scrubs, frame by frame through `OffscreenRenderer`,
into an **APNG** — `ApngWriter` is three chunk types (`acTL`/`fcTL`/`fdAT`) over the
machinery `PngWriter` already had, dependency-free, lossless and full colour (a shaded
CAD render is mostly smooth gradients, exactly what GIF's 256 colours band on). Every
frame is a full-size replace and each frame's data is its own complete zlib
datastream; the first frame is the PNG default image, so a non-APNG viewer shows a
valid still, and the file is written as `.png` because it *is* one. Per-frame delay =
`Duration / frames` (playback time matches the animation), infinite loop; `loop: true`
samples `t = i/frames` so a turntable's last frame is not a duplicate of its first,
`loop: false` samples `t = i/(frames−1)` so the final pose is shown exactly. With no
camera track, the clip uses ONE camera framed over the union of the first and last
frames' bounds — never per-frame framing (a camera chasing the geometry is unusable,
the explode slider's lesson). `animation.RenderFrames(scene, directory, ...)` always
offers the **PNG frame sequence** (`frame-0000.png` …), the zero-risk escape hatch
into ffmpeg for MP4/WebM, which no dependency-free encoder reaches.

`animation.RenderGif(...)` is second, because GIF is what pastes everywhere — and that
is its only virtue here. `GifWriter` is a per-frame median-cut quantizer + GIF-variant
LZW, dependency-free; **expect banding on shaded renders** (256 colours, no alpha: the
background gradient, smooth shading and AO band visibly, and dithering — deliberately
not done — would fight the clean look). Wireframe or flat-shaded clips GIF far better.
Quantizer detail worth keeping: the median-cut PARTITION is the pixel mapping (every
distinct colour lands in one box whose palette entry is the box average), so no
nearest-palette search exists to disagree with the split, and an image with ≤256
distinct colours reproduces exactly. The LZW encoder is locked by a round-trip against
an independently written decoder, including the 4096-entry table reset.

## Bill of materials

The **BOM** toolbar button shows the current tab's parts list — quantities per distinct
part, catalogue items marked, and where the occurrences are — in a small window, and
writes a CSV to the temp directory, reporting the path in the status bar (the same
"write a file and name it" convention as **Capture**). All the counting lives in
EngrCAD.Modeling's `Bom`, over the same flattening the viewport renders; the viewer
only renders the table.

## STEP export of assemblies

`--export part.step` now writes **one assembly file** when the scene has more than one
solid: one `PRODUCT` per distinct part, one `NEXT_ASSEMBLY_USAGE_OCCURRENCE` per
placement, poses as `CONTEXT_DEPENDENT_SHAPE_REPRESENTATION`s. (It previously wrote one
file per part, un-posed.) A single-solid scene still writes the plain
`MANIFOLD_SOLID_BREP` file. Parts with no exact B-Rep are named on the log rather than
silently dropped. The machinery is `StepAssembly` in EngrCAD.Modeling over
`StepWriter.WriteAssembly` — see those READMEs.

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
  a tooltip, the failure goes to the status bar and the `ILogger`, and the rest of
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

`EngrCad.NotifySourceChanged()` is the same reload path for hosts whose model SOURCE
is data rather than compiled code: it re-invokes the live factory exactly as a hot
reload patch does (same debounce, same keep-the-last-good-scene error handling), and
is a no-op unless a `ShowLive` window is active. `tools/EngrCAD.Script` — the `.csx`
model runner (see `docs/examples/scripting.md`) — watches its script file and calls
it on save, which is what makes editing a script feel identical to editing a watched
project.

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
    .WithExplode(1)                                            // --render exploded view
    .WithAmbientOcclusion(false)                               // baked AO (on by default)
    .WithLazyTabMeshing(false)                                 // mesh everything up front
    .WithLogger(logger)                                        // any ILogger
    .Run(args, BuildScene);
```

The builder accumulates an **`EngrCadOptions`** POCO (`Title`, `Quality`,
`RenderWidth`/`RenderHeight`, `RenderStyle`, `SectionAxis`/`SectionOffset`, `Explode`,
`AmbientOcclusion`, `LazyTabMeshing`, `Logger`, `OnViewportReady`) and its terminal methods (`Run`, `Show`, `ShowLive`,
`RenderToImage`) mirror the static `EngrCad` entry points with those options
applied. The plain `EngrCad.Run/Show/ShowLive` overloads are unchanged and remain
the simple path.

- **Mesh-quality precedence** (`Scene.ResolveQuality` implements it): a `Scene`
  constructed with explicit options always wins > the `EngrCadOptions.Quality`
  default > `MeshQuality`'s built-in defaults. So a scene that deliberately chose
  its own quality is never silently overridden, while scenes that didn't care
  inherit the host's setting everywhere — display, `--export .stl/.obj`, `--render`,
  and hot reloads.
- **Logging** (`ILogger`): everything the entry points report — export
  confirmations, usage errors, headless-render results, and the live-reload
  status/error messages that appear in the overlay — goes through the configured
  `ILogger`. `WithLogger(ILogger)` or `WithLoggerFactory(ILoggerFactory)` (category
  `EngrCAD`) set it; `NullLogger.Instance` silences it.

### Logging: `Microsoft.Extensions.Logging.Abstractions`

This **reverses an earlier deliberate decision.** The viewer used to define its own
two-method `IEngrCadLog` seam specifically to avoid a `Microsoft.Extensions.*`
dependency, with adapter snippets in this README for consumers who wanted `ILogger`.
That shim is gone: Chris approved taking the dependency, and the trade actually
favours it — nearly every .NET host already has an `ILogger`, so the shim was making
*everyone* write an adapter in order to save a reference that most of them already
had transitively. The package taken is **`Microsoft.Extensions.Logging.Abstractions`**
— abstractions only, no provider — so consumers still choose their own sink, and the
kernel-projects-carry-no-UI-dependency rule is untouched (a logging abstraction is
not UI; the kernel projects take no reference at all).

What that buys, beyond deleting a shim:

- **Levels**, so a partial success can say so. `skipping 'x': not B-Rep-representable`
  is now a *Warning* rather than sharing one `Error` channel with "nothing exported".
- **Structured messages.** Every message is a source-generated `[LoggerMessage]`
  template with named placeholders (`Logging.cs` holds the whole vocabulary), so a
  structured sink receives `Path`/`PartCount`/`PartName` as fields instead of a
  pre-baked string, and disabled levels allocate nothing.
- **Stable event IDs** (10s usage, 20s export/render, 40s live reload, 50s display,
  60s MCP) that sinks and dashboards can key on as message text evolves.

**The default is the console, not `NullLogger`.** A library defaults to silence; a
program's front door does not — and `EngrCad.Run` *is* the front door of a model
program, where "wrote part.step" and the usage errors are that program's console
output. `EngrCadLoggers.Console` (Information → stdout, Warning and above → stderr)
is therefore what an unconfigured entry point uses, exactly reproducing the historical
behavior. Pass `NullLogger.Instance` for silence — deliberately, not by accident.
`EngrCadLoggers.StandardError` puts everything on stderr, which is what
`EngrCAD.Mcp` wants (stdout carries the protocol).

### `IOptions<EngrCadOptions>` friendliness

`EngrCadOptions` is a plain mutable POCO, so it binds as `IOptions<EngrCadOptions>`
out of the box, and `EngrCad.Configure(EngrCadOptions)` accepts the DI-provided
instance directly:

```csharp
// In a generic-host app:
builder.Services.Configure<EngrCadOptions>(builder.Configuration.GetSection("EngrCad"));

// In the model program, with IOptions<EngrCadOptions> options and ILogger<Thing> logger:
return EngrCad.Configure(options.Value)
    .WithLogger(logger)
    .Run(args, BuildScene);
```

(Delegate/interface-typed properties are simply left unbound by configuration
binding — set `Logger`/`OnViewportReady` in code.)

## Remote control (drive a RUNNING window)

Opt-in, off by default: `WithRemoteControl(port, token)` (or `--rpc [port]`, plus
`--rpc-token <t>`) makes the viewer expose a **loopback-only** TCP endpoint carrying
newline-delimited JSON-RPC 2.0 once the window opens — the actual port is reported in
the log (event 70) and the status bar. Methods: `ping`, `list_parts`, `set_view`,
`fit`, `set_section`, `set_display_mode`, `set_view_style`, `select_part`,
`get_selection`, `measure`, `screenshot`. The MCP server bridges to it from a separate
process (`EngrCadMcp.Run` with `--mcp --viewer <port>`), which is how an AI assistant
drives the window the user is looking at.

Three layers in `RemoteControl.cs`, separable on purpose: `RemoteControlServer`
(transport: framing, the token gate, error envelopes — binds `IPAddress.Loopback`
with no way to bind wider), `RemoteViewerDispatcher` (the method vocabulary over
`IRemoteViewer`, pure translation), and `ViewportRemoteViewer` (the only layer that
knows Avalonia — **every call marshals through `Dispatcher.UIThread`**, and GL is
never touched from the RPC thread: `screenshot` rides
`ViewportControl.SaveScreenshot`'s capture-on-next-frame path). Transport and
vocabulary are locked by headless tests over real sockets with a stub viewer; only the
thin `ViewportRemoteViewer` wiring needs a live window.

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
  render core, which now lives in its own assembly — **`EngrCAD.Viewer.Core`** —
  `ViewerShaders` (ONE shader set; the only feature
  the offscreen pass neutralizes is the selection highlight, uHighlight 0 — there is
  no interactive selection offscreen), `RenderModes` (the global-style x per-part-mode
  precedence and the translucent back-to-front sort), `CameraMath`
  (LookAt/projection/column-major writer, the scene-scaled near/far frustum, and the
  auto-framing distance), and `RenderGeometry` (grid/axes builder, segment flattening).
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
  (or PageUp/Down) zoom, WASD pans. Z is up. The handlers here only *classify* the
  gesture — every pose change goes through `CameraMath` (EngrCAD.Viewer.Core), which the
  Blazor viewport calls too, so there is one implementation of what a drag does and the
  two front ends cannot come to feel different.
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
