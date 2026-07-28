# Tetrahedral meshing for FEA

`TetMesher` fills a closed surface with tetrahedra. Any EngrCAD representation reaches a
surface mesh — `Shape.ToMesh()`, a B-Rep tessellation, Surface Nets over an SDF, an imported
STL — so anything you can model, you can mesh.

```csharp run:fea-first-mesh
var surface = Shape.Box(40, 30, 6)
    .Subtract(Shape.Cylinder(6, 20))
    .ToMesh();

var tets = TetMesher.Mesh(surface, null, out var report);

Console.WriteLine(report);
// e.g. 1284 tets from 224 triangles / 62 patches / 116 vertices;
//      0 boundary + 0 quality Steiner points, 0 recovery round(s); volume residual 3.55E-15

if (report.VolumeResidual > 1e-9)
    throw new Exception($"the tet mesh does not fill its own surface: {report.VolumeResidual:E2}");
if (Math.Abs(tets.Volume - surface.Volume()) > 1e-6)
    throw new Exception("volume identity failed");
```

The **volume identity** is the check to reach for first: the sum of the elements' volumes
equals the input surface's enclosed volume. Every element is stored positively oriented
(verified with exact arithmetic at construction), so that sum has no cancellation in it and a
crack or an inverted element cannot hide.

## Seeing the elements

The boundary of the tet mesh is the input surface — that is the contract — so rendering it
alone tells you nothing new. `TetMesh.SurfaceOf` takes a predicate over elements and returns
the outer surface of just those, which is how you look *inside* the mesh.

```csharp render:fea-tet-cutaway style:shaded-edges
var surface = Shape.Box(40, 30, 8)
    .Subtract(Shape.Cylinder(7, 30))
    .ToMesh(new MeshQuality { SegmentsPerCircle = 24 });

var tets = TetMesher.Mesh(surface, new TetMeshOptions
{
    RefineQuality = true,
    RadiusEdgeRatio = 2.0,
    MaxElementSize = 6.0,
});

// Keep the elements on one side of a plane: the cut face shows real tetrahedra.
var cutaway = tets.SurfaceOf(t =>
{
    var e = tets.GetTet(t);
    var centroid = (tets.Position(e.A) + tets.Position(e.B)
                  + tets.Position(e.C) + tets.Position(e.D)) * 0.25;
    return centroid.Y < 0;
});

var scene = new Scene();
scene.Add(new Part("tet mesh", cutaway) { Color = new PartColor(0.55f, 0.68f, 0.85f) });
```

![A drilled plate meshed into tetrahedra, cut open to show the interior elements](images/fea-tet-cutaway.png)

## Quality — and why one number is never enough

```csharp run:fea-quality
var surface = MeshPrimitives.Box(new Aabb((0, 0, 0), (20, 20, 20)));
var tets = TetMesher.Mesh(surface, new TetMeshOptions
{
    RefineQuality = true,
    RadiusEdgeRatio = 2.0,
    MaxElementSize = 4.0,
});

var quality = TetQuality.Analyze(tets);
Console.WriteLine(quality.ToText());

if (quality.MaxRadiusEdgeRatio > 2.5)
    throw new Exception($"radius-edge target missed: {quality.MaxRadiusEdgeRatio:F2}");
if (quality.MeanMinDihedralDegrees < 25)
    throw new Exception($"mesh is too flat on average: {quality.MeanMinDihedralDegrees:F1} deg");
```

`TetQualityReport` carries **two** shape measures, and the reason is worth knowing before you
trust either.

The **radius-edge ratio** (circumradius ÷ shortest edge) is what Delaunay refinement can
bound. Bounding it excludes every badly shaped tetrahedron *except one*: the **sliver** — four
nearly-coplanar vertices, whose circumradius and shortest edge are both perfectly ordinary. A
mesh can have a flawless radius-edge histogram and still be useless for analysis.

The **minimum dihedral angle** is what actually governs the stiffness matrix's conditioning,
and it is the number that sees slivers. `SliverCount` counts elements below a threshold you
choose, so you can ask the question your solver cares about rather than the one the mesher
finds flattering.

> [!NOTE]
> `RadiusEdgeRatio` defaults to exactly **2.0** because that is the bound below which Delaunay
> refinement is not guaranteed to terminate. Smaller values are allowed; the Steiner-point
> budget is what catches a run that will not converge, and it refuses by name rather than
> returning a half-refined mesh.

### Refinement is not optional on curved bodies

Measured on a Ø20 UV sphere (win-x64, Release):

| | elements | mean min-dihedral | slivers below 10° |
| --- | ---: | ---: | ---: |
| conforming only | 3 402 | 5.5° | 85.9% |
| `RefineQuality`, `MaxElementSize = 2.5` | 14 583 | 39.8° | 4.8% |

A sphere's tessellation vertices are **all exactly cospherical**, so a tetrahedralization
built from them alone has no interior vertices at all — every element spans the whole body and
the result is slivers by construction. Refinement adds the interior points that give the mesh
a shape. This is a property of the input, not a defect in the mesher, and it is why the
quality report exists.

## Sizing fields

A sizing field asks for smaller elements where the analysis needs them. It is a plain
`Func<Vector3d, double>` returning the desired element size at a point, so an `Sdf` composes
into it directly — which is the natural way to grade away from a feature.

```csharp render:fea-sizing-field style:shaded-edges
var bore = Sdf.Cylinder(5, 40);
var surface = Shape.Box(40, 18, 8).Subtract(Shape.Cylinder(5, 40))
    .ToMesh(new MeshQuality { SegmentsPerCircle = 20 });

var tets = TetMesher.Mesh(surface, new TetMeshOptions
{
    RefineQuality = true,
    RadiusEdgeRatio = 2.0,
    // Fine at the bore wall, coarsening with distance from it.
    SizingField = p => 3.0 + 0.9 * Math.Max(0, bore.Evaluate(p)),
});

var cutaway = tets.SurfaceOf(t =>
{
    var e = tets.GetTet(t);
    var centroid = (tets.Position(e.A) + tets.Position(e.B)
                  + tets.Position(e.C) + tets.Position(e.D)) * 0.25;
    return centroid.Y < 0;
});

var scene = new Scene();
scene.Add(new Part("graded mesh", cutaway) { Color = new PartColor(0.85f, 0.62f, 0.35f) });
```

![A plate meshed with elements graded finer towards the bore](images/fea-sizing-field.png)

`MaxElementSize` is the uniform form of the same idea; supply both and the smaller wins.

## Boundary facet tags — the seam for boundary conditions

Every boundary facet names the input triangle it came from
(`TetFacet.SourceTriangle`). Supply `FacetTags` — one tag per input triangle, typically a
B-Rep face id — and the facets come back carrying it. Refinement may **subdivide** an input
triangle, but the mapping is many-to-one and never many-to-many, so a tag survives.

```csharp run:fea-facet-tags
var surface = Shape.Box(30, 20, 5).ToMesh().Triangulated();
var (positions, faces) = surface.ToIndexed();

// Tag 0 = top face, 1 = bottom face, 2 = the sides.
var tags = new int[faces.Count];
for (int f = 0; f < faces.Count; f++)
{
    var c = (positions[faces[f][0]] + positions[faces[f][1]] + positions[faces[f][2]]) / 3.0;
    tags[f] = c.Z > 2.49 ? 0 : c.Z < -2.49 ? 1 : 2;
}

var tets = TetMesher.Mesh(surface, new TetMeshOptions { FacetTags = tags });

// A load on the top face is now just "the facets tagged 0".
double topArea = 0;
foreach (var facet in tets.BoundaryFacets)
{
    if (facet.SourceTriangle != 0) continue;
    var a = tets.Position(facet.V0);
    var b = tets.Position(facet.V1);
    var c = tets.Position(facet.V2);
    topArea += 0.5 * (b - a).Cross(c - a).Length;
}

if (Math.Abs(topArea - 30 * 20) > 1e-9)
    throw new Exception($"top face area came back as {topArea}");
```

## Multiple bodies, one mesh

Pass several disjoint closed bodies and each element is tagged with the body it fills
(`TetMesh.RegionOf`) — the seam for per-material properties.

```csharp run:fea-regions
var steel = MeshPrimitives.Box(new Aabb((0, 0, 0), (10, 10, 10)));
var alloy = MeshPrimitives.Box(new Aabb((14, 0, 0), (24, 10, 10)));

var tets = TetMesher.Mesh([steel, alloy], null, out var report);

Console.WriteLine($"regions: {string.Join(", ", tets.Regions)}");
foreach (int region in tets.Regions)
{
    double volume = 0;
    for (int t = 0; t < tets.TetCount; t++)
        if (tets.RegionOf(t) == region)
            volume += tets.TetVolume(t);
    if (Math.Abs(volume - 1000) > 1e-6)
        throw new Exception($"region {region} has volume {volume}, expected 1000");
}
```

Overlapping bodies are refused by name rather than meshed wrongly.

## Second-order (10-node) elements

`QuadraticTetMesh.From` adds mid-edge nodes for second-order analysis. It is a **pure
function** of the linear mesh — nothing re-meshes and no corner moves — so the geometry is
unchanged and the volume is an exact identity.

```csharp run:fea-quadratic
var surface = Shape.Box(20, 10, 5).Subtract(Shape.Cylinder(3, 20)).ToMesh();
var linear = TetMesher.Mesh(surface);
var quadratic = QuadraticTetMesh.From(linear);

Console.WriteLine($"{linear.TetCount} elements, {linear.VertexCount} corner nodes " +
                  $"-> {quadratic.NodeCount} total nodes");

if (Math.Abs(quadratic.Volume - linear.Volume) > Math.Abs(linear.Volume) * 1e-12)
    throw new Exception("straight-sided quadratic elements must reproduce the linear volume");
if (quadratic.CornerNodeCount != linear.VertexCount)
    throw new Exception("corner nodes must keep their linear indices");
```

Mid-edge nodes are **shared**, keyed on the canonical `(min, max)` corner pair, so two
elements meeting on an edge get the same node and the assembled system is continuous. Corner
nodes keep their linear indices, so anything you computed per corner on the linear mesh
transfers with no mapping. Node ordering is the Abaqus C3D10 / VTK `VTK_QUADRATIC_TETRA`
convention.

## What the mesher refuses

It never returns a mesh it cannot stand behind. Each refusal names the specific thing that
failed:

| Input | Response |
| --- | --- |
| Open shell | Refuses, pointing at `MeshRepair.AutoRepair` / `HoleFiller.FillAll` |
| Inward-wound surface | Refuses, naming the non-positive enclosed volume |
| Duplicate vertices | Refuses, pointing at `MeshRepair.Clean` |
| A patch it cannot recover | Refuses, naming the patch, its area, how much was covered, and the input triangles it spans |
| Steiner budget exhausted | Refuses, naming the phase and the option to raise |
| Overlapping bodies | Refuses, naming both bodies |

```csharp run:fea-refusals
var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
var (positions, faces) = box.Triangulated().ToIndexed();
faces.RemoveAt(0);
var open = HalfEdgeMesh.Build(positions, faces);

try
{
    TetMesher.Mesh(open);
    throw new Exception("an open shell should not have meshed");
}
catch (TetMeshException ex)
{
    Console.WriteLine(ex.Message);
    // "Body 0 is not CLOSED: an open shell has no inside to fill. Run MeshRepair.AutoRepair ..."
}
```

## Determinism

Insertion order is a fixed spatial (Morton) sort of the coordinates, point location is a
deterministic walk, and there is no RNG anywhere. Two runs on the same input produce
bit-identical output, including the order of the elements — so a mesh can be a regression
baseline.

## Preparing a surface

Meshing quality follows the surface it is given. If the tessellation has slivers or wildly
uneven triangles, [remeshing](remeshing.md) first is the tool:

```csharp run:fea-remesh-first
var raw = Shape.Cylinder(10, 20).ToMesh(new MeshQuality { SegmentsPerCircle = 48 });
var even = Remesher.Remesh(raw, new RemeshOptions(TargetEdgeLength: 3.0)
{
    Iterations = 12,
    FeatureAngleDegrees = 30,
});

var tets = TetMesher.Mesh(even.Mesh, null, out var report);
Console.WriteLine($"{tets.TetCount} elements, volume residual {report.VolumeResidual:E2}");
if (report.VolumeResidual > 1e-9)
    throw new Exception("remeshed surface failed the volume identity");
```
