# EngrCAD.Query

LINQ-native geometry querying: a custom `IQueryable` provider that answers spatial
predicates from a BVH index instead of scanning. Depends only on `EngrCAD.Core`.

## Usage

```csharp
var faces = mesh.Faces.ToSpatialCollection(f => f.Bounds);
var hits = faces.AsQueryable()
    .Where(f => f.Bounds.Within(region) && f.Index % 2 == 0)
    .ToList();          // spatial clause answered from the BVH in O(log n)
```

## How it works

- **`SpatialCollection<T>`** pairs items with a bounds *expression* (so the provider can
  recognize the accessor inside predicates) and builds a BVH over the compiled bounds.
- **`SpatialQueryable<T>` / provider** rewrite the expression tree on execution: a
  `Where` whose predicate contains a `SpatialPredicates` clause (`.Within(box)`,
  `.WithinDistance(point, d)`, `.HitBy(ray)`) applied to the registered bounds accessor
  has its source replaced by the BVH candidate set; the **full original predicate is
  re-applied**, so interception is purely an optimization and can never change results.
  Everything else falls back to LINQ-to-Objects. `LastQueryUsedIndex` is the diagnostic.
- **`SpatialPredicates`** exist because **expression trees cannot contain calls to
  methods with `in` parameters**, which the Core API uses throughout — these wrappers
  take parameters by value and double as the recognizable query vocabulary.

## Future

Metadata indexes for B-Rep feature queries (e.g. cylindrical faces by radius);
expression-tree → SDF compilation.
