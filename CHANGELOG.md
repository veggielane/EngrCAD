---
title: Changelog
description: Notable changes to EngrCAD, newest first.
---

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Links go to the documentation page for each feature. Everything below sits under
`Unreleased` because nothing has been published to nuget.org yet — the packages build
and pack at `0.1.0`, but no version has shipped, so there is no released version to
date-stamp.

## [Unreleased]

### Added

#### Geometry kernel

- Core math kernel — `Vector3d`/`Matrix4d`/`Quaterniond`/`Aabb`/`Ray3d`, BVH and octree,
  a central tolerance policy, and adaptive-exact Shewchuk predicates in 2D and 3D.
- Half-edge mesh engine with booleans, Loop subdivision and QEM decimation.
- Exact mesh booleans by imprint and winding-number classification, replacing the earlier
  BSP clipper.
- Implicit engine — an SDF AST of primitives and operators, polygonised by manifold
  Surface Nets, with sharp features preserved by dual contouring. See
  [The SDF vocabulary](docs/examples/sdf-vocabulary.md),
  [Blends](docs/examples/blends.md), [Offset](docs/examples/offset.md) and
  [Shell](docs/examples/shell.md).
- B-Rep engine — analytic curves and surfaces, NURBS, topology, and surface–surface
  intersection with analytic pairs plus a marching tracer for the rest.
- The conversion triangle — implicit→mesh, B-Rep→mesh and mesh→implicit. See
  [Three representations](docs/examples/representations.md).
- LINQ spatial queries over a BVH-backed `IQueryable`. See
  [LINQ spatial queries](docs/examples/queries.md).
- Automatic B-Rep booleans (union, intersection, difference) with topologically sealed
  seams. See [Booleans](docs/examples/booleans.md).
- Exact curved 2D booleans — the arrangement carries circular arcs unflattened, so a disc
  measures πr² rather than a polygon's approximation of it. See
  [2D sketch booleans](docs/examples/sketch-booleans.md).
- Coplanar face fusion, so flush embossing and stacked plates fuse into one solid.
- Lattices — eight triply periodic minimal surfaces (Schwarz P and D, gyroid, Neovius,
  I-WP, Lidinoid, Fischer–Koch S, Split P) as sheets or networks, plus six strut
  lattices, sized by volume fraction. See [Lattices](docs/examples/lattices.md).
- Space-filling curves (Hilbert, Moore, Peano, Gosper, Z-order) and a 2D infill consumer.
  See [Space-filling curves & 2D infill](docs/examples/infill.md).

#### Modelling

- The unified `Shape` API — model once, choose the representation at the end, with
  `Explain(target)` reporting each node as Native, Bridged or Impossible.
- Sketching with lines, arcs, béziers and elliptical arcs, exact in all three
  representations. See [Sketching](docs/examples/sketching.md).
- A variational sketch-constraint solver with a rank-revealing degrees-of-freedom report.
- Extrude, revolve and sweep, including twisted and tapered extrusion. See
  [Extrude, revolve, sweep](docs/examples/extrude-revolve-sweep.md).
- Chamfers and fillets — mitered rims, whole-solid rounding, variable-radius laws and
  partial runs. See [Chamfer & fillet](docs/examples/chamfer-fillet.md).
- Loft, draft and shell, including curved-face shelling and draft. See
  [Loft, draft & shell](docs/examples/loft-draft-shell.md).
- Holes with a standards catalogue, drill-tip angles and threaded holes. See
  [Holes & standard sizes](docs/examples/holes.md).
- Threads — ISO profiles, B-Rep-native rods and holes, left-hand threads, and a
  Native sub-depth lead-in chamfer. See [Threads](docs/examples/threads.md).
- Modelled text from TrueType and OpenType/CFF fonts, including text on a curve. See
  [Text](docs/examples/text.md).
- Heightmap terrain from `.dat` and grayscale PNG. See
  [Heightmap terrain](docs/examples/heightmaps.md).
- Sheet metal — a K-factor bend model, folded topology surgery and unfolding to a flat
  pattern. See [Sheet metal](docs/examples/sheet-metal.md).
- Frames and weldments — profiles on a skeleton with exact miters and cut lists. See
  [Frames & weldments](docs/examples/frames.md).
- Parametric features, a feature registry and whole-history persistence. See
  [Parametric features](docs/examples/features.md).
- A typed geometry-reference vocabulary for features. See
  [Geometry inputs for features](docs/examples/geometry-inputs.md).
- Face and edge selection as LINQ extension methods. See
  [Selecting faces & edges](docs/examples/selection.md).
- Topological naming by face provenance, carried through every rebuild site.
- Design studies — drive `[Param]` values against a measured objective. See
  [Design studies](docs/examples/design-studies.md).
- Assemblies, exploded views and mates, with mates reaching across assembly levels. See
  [Assemblies](docs/examples/assemblies.md).
- Standard components that modify the host model as they are placed. See
  [Standard components](docs/examples/components.md).
- Materials and mass properties, with one unit convention. See
  [Materials & mass](docs/examples/materials.md).
- Mechanisms — joints, drivers, couplings, cams, rates, limits and interference. See
  [Mechanisms](docs/examples/mechanisms.md).
- Gears — involute spur and helical, rack, worm, straight bevel, planetary, herringbone,
  crossed helical and cycloidal, each verified from contact. See
  [Gears](docs/examples/gears.md).
- Patterns, `LocationSet`, extrude-until and build-plate packing. See
  [Transforms & patterns](docs/examples/transforms-patterns.md) and
  [Packing a build plate](docs/examples/packing.md).
- Remeshing through the `Shape` API. See [Remeshing](docs/examples/remeshing.md).
- Manufacturability checks — draft, overhangs and wall thickness. See
  [Manufacturability checks](docs/examples/manufacturability.md).
- An anti-drill tamper mesh with a derived and measured drill guarantee. See
  [Anti-drill tamper mesh](docs/examples/tamper-mesh.md).
- Document persistence and undo/redo. See [Saving documents](docs/examples/documents.md).

#### Simulation

- A tetrahedral mesher with exact predicates, verified boundary recovery, quality
  refinement and anisotropic boundary layers. See
  [Tetrahedral meshing](docs/examples/fea-meshing.md).
- Linear-static structural analysis, verified against patch tests, Euler–Bernoulli and
  Kirsch. See [Structural analysis](docs/examples/fea-structural.md).
- Thermal analysis — steady, transient, and thermal–structural coupling. See
  [Thermal analysis](docs/examples/fea-thermal.md).
- Modal analysis by shift-and-invert Lanczos, with block Lanczos for repeated
  eigenvalues. See [Modal analysis](docs/examples/fea-modal.md).
- Buckling, stress stiffening, Rayleigh damping and harmonic response, plus a direct
  per-frequency solve for non-proportional damping. See
  [Buckling & frequency response](docs/examples/fea-buckling.md).
- Transient dynamics by Newmark/HHT direct time integration. See
  [Transient dynamics](docs/examples/fea-transient.md).
- Fatigue post-processing — S-N life, Goodman and Gerber, rainflow counting and Marin
  factors. See [Fatigue](docs/examples/fea-fatigue.md).
- Directional materials, superconvergent stress recovery and an energy-norm error
  estimate.
- Topology optimisation by SIMP with optimality criteria. See
  [Topology optimisation](docs/examples/fea-topology.md).
- Simulation results as fields on a mesh, with colour maps and deformed shapes. See
  [Results & fields](docs/examples/fields.md).
- `EngrCAD.Core.Solvers` — a dependency-free sparse library with CG, Cholesky with AMD
  ordering, and `SparseLdlt` for symmetric-indefinite systems.

#### Interchange

- STEP AP214 export and import, including assemblies.
- A native lossless B-Rep archive, glTF export and IGES import. See
  [Exports](docs/examples/exports.md) and [Importing meshes](docs/examples/import.md).
- STL, OBJ, OFF, 3MF, AMF and VTU export.
- DXF and SVG, both directions. See [DXF & SVG](docs/examples/dxf-svg.md).
- Engineering drawings — hidden-line removal, sheets, dimensions and PDF export. See
  [Drawings](docs/examples/drawings.md).
- 2D views — offset, section and silhouette. See [2D views](docs/examples/2d-views.md).

#### Viewer and tooling

- An OpenGL viewer as a library, with CAD chrome, section planes, display modes, matcap
  shading and a view cube. See [Viewer](docs/examples/viewer.md).
- 3D annotations (PMI) with hole tables and callouts. See
  [3D annotations](docs/examples/annotations.md).
- Animation — a pure-`t` timeline, playback transport and APNG/GIF export. See
  [Animation](docs/examples/animation.md).
- Live modelling by `dotnet watch` hot reload, and `.csx` scripting. See
  [Scripting](docs/examples/scripting.md).
- An MCP server, so a model program serves itself to AI assistants, plus loopback RPC
  control of a running viewer. See [AI assistants (MCP)](docs/examples/mcp.md).
- A Blazor WebAssembly viewer running the kernel in the browser. See
  [In the browser](docs/examples/web.md).
- Executable documentation — every example compiles, runs and renders, and 118 of them
  run live in the reader's browser.

### Changed

- The documentation site moved from DocFX to Astro Starlight, with DocFX reduced to
  generating the .NET API reference.
- `Material` moved into `EngrCAD.Core` and the unit convention was settled on
  mm/N/MPa/tonne/s, with readable units as accessors rather than a second convention.
- A grid quad's diagonal is chosen by geometry (the shorter 3D diagonal) rather than by
  corner order, shared by every consumer through one rule.
- Trimmed-face tessellation puts the natural grid's interior rows into the base
  triangulation, so refinement is residual duty rather than a convergence mechanism.
- `PreventLongEdgeFlips` became the remesher's default, because the tet mesher's boundary
  recovery needs a Delaunay-clean surface.
- Surface Nets streams a sliding window of slabs and culls blocks the surface cannot
  reach, cutting peak memory from O(volume) to O(cross-section).
- The half-edge builder resolves twins by counting sort rather than a dictionary.
- Stress recovery and thermal flux are assembled per material region, so an interface is
  no longer smoothed across.
- Allocation tests take the minimum over several batches, so a one-time JIT or GC artifact
  cannot fail them.

### Fixed

- Non-manifold pinch vertices from Surface Nets, split by cube face adjacency.
- The web viewport cleared depth while the depth mask was disabled, so enabling the
  annotation overlay erased the model.
- A cut pole cap was probed at its loop's average parameter instead of its closest
  approach, refusing a blind hole that breaks out of a face.
- An exact conic partially crossing a bounded planar patch is now clipped rather than
  deferred, so a cut can break out of more than one face.
- `Region2dOffset.Stroke` dropped the outer corner fill at every clockwise turn.
- A full circle's seam in `SketchRegion` left a band of ordinates no parity ray could
  cross, so a point well inside a disc could read as outside.
- Cross-drilled and sphere-piercing booleans, by sampling tracer polylines at their exact
  vertices.
- A non-ASCII character in GLSL source made the whole viewport render black.

[Unreleased]: https://github.com/veggielane/EngrCAD/commits/main
