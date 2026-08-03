---
title: "LINQ spatial queries"
---

`EngrCAD.Query` makes geometry LINQ-queryable *with an index*: a
`SpatialCollection<T>` pairs items with a bounds expression and builds a BVH; its
custom `IQueryable` provider recognizes spatial predicates inside `Where` clauses and
answers them from the BVH instead of scanning.

```csharp run:spatial-query
var mesh = Shape.Sphere(10).ToMesh();

// Index the mesh faces by their bounding boxes.
var faces = mesh.Faces.ToSpatialCollection(f => f.Bounds);

// The .Within clause is routed to the BVH; the rest of the predicate still applies.
var region = new Aabb((0, 0, 5), (10, 10, 10));
var hits = faces.AsQueryable()
    .Where(f => f.Bounds.Within(region))
    .ToList();

Console.WriteLine($"{hits.Count} of {faces.Count} faces intersect the region");
if (!faces.LastQueryUsedIndex)
    throw new Exception("expected the BVH to answer the spatial clause");
if (hits.Count == 0 || hits.Count == faces.Count)
    throw new Exception("candidate set should be a proper subset");

// Distance and ray predicates use the same index. Build the arguments *outside*
// the predicate — expression trees cannot contain tuple conversions, so the
// tuple-to-vector shorthand is unavailable inside a Where lambda.
var northPole = new Vector3d(0, 0, 10);
var downRay = new Ray3d((0, 0, 30), (0, 0, -1));
var nearOrigin = faces.AsQueryable()
    .Where(f => f.Bounds.WithinDistance(northPole, 2.0))
    .ToList();
var underRay = faces.AsQueryable()
    .Where(f => f.Bounds.HitBy(downRay))
    .ToList();
Console.WriteLine($"{nearOrigin.Count} near the north pole, {underRay.Count} under the ray");
if (nearOrigin.Count == 0 || underRay.Count == 0)
    throw new Exception("expected nonempty results");
```

## How it works

- The predicate vocabulary is `SpatialPredicates`: `.Within(box)`,
  `.WithinDistance(point, distance)`, `.HitBy(ray)` applied to the registered bounds
  accessor. These exist because expression trees cannot contain calls to methods with
  `in` parameters, which the kernel API uses — the wrappers take parameters by value.
- Interception is a **pure optimization**: the full original predicate is re-applied
  over the BVH candidates, so results can never differ from LINQ-to-Objects; anything
  the provider doesn't recognize falls back to LINQ-to-Objects entirely.
  `LastQueryUsedIndex` is the diagnostic for whether the index was used.
- B-Rep topology has its own LINQ vocabulary (`BrepQueries`: `IsPlanar`,
  `IsCylindrical`, `IsCircular`, `Length`, `face.Edges()`, `solid.FacesOf(edge)`),
  which powers the [chamfer & fillet selectors](chamfer-fillet.md); mesh handles
  support topology traversal (`vertex.OutgoingHalfEdges()`, `face.AdjacentFaces()`).

No screenshot on this page — the interesting output is the query plan, not pixels.
