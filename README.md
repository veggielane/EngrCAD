# EngrCAD

A CAD kernel for modern .NET built around a **hybrid geometry engine** that natively
supports three representations:

- **B-Rep** — parametric surfaces (planes, conics, NURBS) wrapped in topology, for
  precision modeling and STEP exchange.
- **Implicit** — signed distance fields (SDF) composed as an AST of primitives and
  operators, for lattices, shells, and organic blends.
- **Mesh** — discrete half-edge triangle meshes, for rendering, FEA, and 3D printing.

The unified `Shape` API lets you model once with one vocabulary and choose the
representation at the end:

```csharp
var body = Shape.Box(40, 30, 10) - Shape.Cylinder(4, 12).Translate(10, 8, 0);

BrepSolid    exact = body.ToBrep();      // precision modeling, STEP export
Sdf          field = body.ToImplicit();  // blends, shells, lattices
HalfEdgeMesh mesh  = body.ToMesh();      // rendering, FEA, 3D printing
```

On top of the kernel sit a LINQ-native spatial/topology query provider, a
FeatureScript-style parametric feature history, a finite-element suite (structural,
thermal, modal, buckling, harmonic, transient, fatigue, topology optimisation), a
library-style OpenGL viewer (desktop and WebAssembly), an MCP server, and a
code-defined ECAD stack (schematic → board → routing → Gerber/Excellon fabrication,
plus enclosure fit, thermal coupling, and 3D surface routing on moulded parts).

## Documentation

The documentation is a set of **executable examples** — every code snippet is compiled,
run, and rendered by the documentation build itself, so the examples cannot drift from
the code. See the docs site (built from `docs/`), the design rationale in
[`design.md`](design.md), and the per-project `README.md` files under `src/`.

## Building

.NET 10 SDK. `dotnet build EngrCAD.slnx`, test with `dotnet test EngrCAD.slnx`.

## A note on how this was built

EngrCAD is written by one person, with substantial help from AI coding assistants, and
I would rather say so plainly than have you guess.

A hybrid geometry kernel of this scope — three interoperating engines, a full FEA suite,
a renderer, and an ECAD stack — is normally the work of a team over many years, and the
research behind almost any single part of it is worth **multiple PhD-years** on its own
(surface–surface intersection, robust boolean operations, exact geometric predicates,
tetrahedral meshing, the SIMP method, involute gear conjugacy, and so on). **I am one
person.** AI assistance is what made attempting that breadth realistic at all.

The counterweight is verification, and it is deliberate. Nothing here is trusted because
it "looks right": every algorithm is checked against closed-form solutions, exact
identities, twin-decoder round-trips, and measured convergence orders, and those checks
live in the test suite and in the executable documentation. Where the kernel cannot do
something exactly, it is designed to **refuse by name** rather than return a plausible
wrong answer. That said — read the code and the results with their origin in mind, and
please report anything that looks off.

## License

MIT © Chris Lane. Package metadata (license, URLs) is provisional at `0.1.0` and is not
yet published to nuget.org.
