using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// Lowers a <see cref="Shape"/> graph to each representation. Transforms are never
/// applied to finished geometry when a target can do better: the walk accumulates a
/// matrix and bakes it into construction inputs (profiles, directions, axes), which
/// keeps B-Rep output exact under any affine map the constructors can express.
/// </summary>
internal static class ShapeCompiler
{
    // ---------------------------------------------------------------- classification

    internal static ConversionReport Classify(Shape shape, TargetRep target)
    {
        var entries = new List<ConversionEntry>();
        switch (target)
        {
            case TargetRep.Brep:
                ClassifyBrep(shape, Matrix4d.Identity, entries);
                break;
            case TargetRep.Implicit:
                ClassifyImplicit(shape, Matrix4d.Identity, entries);
                break;
            case TargetRep.Mesh:
                // A mesh can always be produced: what would be impossible in B-Rep is
                // polygonized from the SDF path instead.
                ClassifyBrep(shape, Matrix4d.Identity, entries);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Support == NodeSupport.Impossible)
                        entries[i] = entries[i] with
                        {
                            Support = NodeSupport.Bridged,
                            Detail = "polygonized from the signed distance field (Surface Nets)",
                        };
                }
                break;
        }
        return new ConversionReport(target, entries);
    }

    private static void ClassifyBrep(Shape shape, in Matrix4d m, List<ConversionEntry> entries)
    {
        switch (shape)
        {
            case BoxShape or CylinderShape or ExtrudeShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native));
                break;
            case SphereShape or TorusShape:
                entries.Add(m.TryDecomposeRigidUniformScale(out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear would need an ellipsoid surface"));
                break;
            case RevolveShape { Sketch: { } sketch } revolve:
                if (!m.TryDecomposeRigidUniformScale(out _, out _, out _))
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with this operation"));
                else if (revolve.IsFullTurn && sketch.Holes.Count > 0)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a full revolve of a sketch with holes produces multiple shells"));
                else if (revolve.IsFullTurn && sketch.Segments.Count == 1)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a full revolve of a single closed curve needs a multi-segment profile"));
                else
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native));
                break;

            case RevolveShape or SweepShape:
                entries.Add(m.TryDecomposeRigidUniformScale(out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with this operation"));
                break;
            case BooleanShape b:
                ClassifyBrep(b.A, m, entries);
                ClassifyBrep(b.B, m, entries);
                entries.Add(new ConversionEntry(b.Describe(), NodeSupport.Native));
                break;
            case SmoothShape or OffsetShape or ShellShape or LatticeShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "only expressible as a signed distance field, and meshes cannot be imported into B-Rep"));
                break;
            case RimShape rim:
                ClassifyBrep(rim.Child, m, entries);
                entries.Add(m.TryDecomposeRigidUniformScale(out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "planar-face rim feature; rim shape constraints validate at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with rim features"));
                break;

            case DrillShape drill:
                // A drill is its expansion (body minus revolved tools); the extra
                // far-face validation happens at lowering.
                ClassifyBrep(drill.Expanded, m, entries);
                break;
            case ThreadShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "helical thread surfaces have no B-Rep lowering yet (a true helical sweep is future work); use ToMesh or ToImplicit"));
                break;
            case TransformShape t:
                ClassifyBrep(t.Child, m * t.Matrix, entries);
                break;
            case SourceShape { Geometry: BrepSolid }:
                entries.Add(IsIdentity(m)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "transforming an already-built B-Rep solid is not supported yet"));
                break;
            case SourceShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "meshes and SDFs cannot be imported into B-Rep"));
                break;
            default:
                throw new NotSupportedException($"Unknown shape node {shape.GetType().Name}.");
        }
    }

    private static void ClassifyImplicit(Shape shape, in Matrix4d m, List<ConversionEntry> entries)
    {
        bool rigid = m.TryDecomposeRigidUniformScale(out _, out _, out _);
        switch (shape)
        {
            case BoxShape or SphereShape or CylinderShape or TorusShape:
                entries.Add(rigid
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                        "sheared primitives go through a tessellated mesh SDF"));
                break;
            case ExtrudeShape { Sketch: not null }:
                entries.Add(rigid
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native, "exact 2D sketch SDF, extruded")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                        "sheared subtree goes through a tessellated mesh SDF"));
                break;
            case RevolveShape { Sketch: not null } sketchRevolve when sketchRevolve.IsFullTurn:
                entries.Add(rigid
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native, "exact 2D sketch SDF, revolved")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                        "sheared subtree goes through a tessellated mesh SDF"));
                break;
            case ExtrudeShape or RevolveShape or SweepShape or RimShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "tessellated B-Rep wrapped in a mesh SDF"));
                break;
            case BooleanShape b:
                if (rigid)
                {
                    ClassifyImplicit(b.A, m, entries);
                    ClassifyImplicit(b.B, m, entries);
                    entries.Add(new ConversionEntry(b.Describe(), NodeSupport.Native));
                }
                else
                {
                    entries.Add(new ConversionEntry(b.Describe(), NodeSupport.Bridged,
                        "sheared subtree goes through a tessellated mesh SDF"));
                }
                break;
            case SmoothShape s:
                if (rigid)
                {
                    ClassifyImplicit(s.A, m, entries);
                    ClassifyImplicit(s.B, m, entries);
                    entries.Add(new ConversionEntry(s.Describe(), NodeSupport.Native));
                }
                else
                {
                    entries.Add(new ConversionEntry(s.Describe(), NodeSupport.Bridged,
                        "sheared subtree goes through a tessellated mesh SDF"));
                }
                break;
            case OffsetShape o when rigid:
                ClassifyImplicit(o.Child, m, entries);
                entries.Add(new ConversionEntry(o.Describe(), NodeSupport.Native));
                break;
            case ShellShape sh when rigid:
                ClassifyImplicit(sh.Child, m, entries);
                entries.Add(new ConversionEntry(sh.Describe(), NodeSupport.Native));
                break;
            case LatticeShape l when rigid:
                ClassifyImplicit(l.Child, m, entries);
                entries.Add(new ConversionEntry(l.Describe(), NodeSupport.Native));
                break;
            case OffsetShape or ShellShape or LatticeShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "sheared subtree goes through a tessellated mesh SDF"));
                break;
            case DrillShape drill:
                ClassifyImplicit(drill.Expanded, m, entries);
                break;
            case ThreadShape:
                entries.Add(rigid
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "exact-sign helical thread field")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                        "sheared thread goes through a polygonized, transformed mesh SDF"));
                break;
            case TransformShape t:
                ClassifyImplicit(t.Child, m * t.Matrix, entries);
                break;
            case SourceShape { Geometry: Sdf }:
                entries.Add(rigid
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                        "sheared SDF goes through a polygonized, transformed mesh SDF"));
                break;
            case SourceShape { Geometry: HalfEdgeMesh }:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native,
                    "exact signed distance to the mesh"));
                break;
            case SourceShape { Geometry: BrepSolid }:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "tessellated B-Rep wrapped in a mesh SDF"));
                break;
            default:
                throw new NotSupportedException($"Unknown shape node {shape.GetType().Name}.");
        }
    }

    private static bool CanBrep(Shape shape, in Matrix4d m)
    {
        var entries = new List<ConversionEntry>();
        ClassifyBrep(shape, m, entries);
        return entries.All(e => e.Support != NodeSupport.Impossible);
    }

    private static bool UsesImplicitOnlyOps(Shape shape) => shape switch
    {
        SmoothShape or OffsetShape or ShellShape or LatticeShape or ThreadShape => true,
        SourceShape { Geometry: Sdf } => true,
        BooleanShape b => UsesImplicitOnlyOps(b.A) || UsesImplicitOnlyOps(b.B),
        DrillShape d => UsesImplicitOnlyOps(d.Expanded),
        TransformShape t => UsesImplicitOnlyOps(t.Child),
        _ => false,
    };

    // ---------------------------------------------------------------- B-Rep lowering

    internal static BrepSolid LowerBrep(Shape shape, in Matrix4d m)
    {
        switch (shape)
        {
            case BoxShape box:
            {
                if (IsIdentity(m))
                    return SolidFactory.MakeBox(box.Bounds);
                if (IsTranslation(m, out var offset))
                    return SolidFactory.MakeBox(new Aabb(box.Bounds.Min + offset, box.Bounds.Max + offset));
                var (x0, y0, z0) = box.Bounds.Min;
                var (x1, y1, z1) = box.Bounds.Max;
                var profile = Profile.FromPoints(
                [
                    m.TransformPoint((x0, y0, z0)),
                    m.TransformPoint((x1, y0, z0)),
                    m.TransformPoint((x1, y1, z0)),
                    m.TransformPoint((x0, y1, z0)),
                ]);
                return SolidFactory.Extrude(profile, m.TransformVector((0, 0, z1 - z0)));
            }

            case SphereShape sphere:
            {
                Decompose(m, shape, out _, out var translation, out double scale);
                return SolidFactory.MakeSphere(sphere.Radius * scale, translation);
            }

            case CylinderShape cyl:
            {
                // Extruded circle/ellipse rather than MakeCylinder so any affine
                // placement bakes in exactly (circles promote back to cylinders).
                var baseCenter = new Vector3d(0, 0, -cyl.Height / 2);
                Curve3d rim;
                if (m.TryDecomposeRigidUniformScale(out var rotation, out _, out double s))
                {
                    rim = new Circle3d(
                        m.TransformPoint(baseCenter),
                        rotation.Rotate(Vector3d.UnitX), rotation.Rotate(Vector3d.UnitY),
                        cyl.Radius * s);
                }
                else
                {
                    rim = new Ellipse3d(
                        m.TransformPoint(baseCenter),
                        m.TransformVector(new Vector3d(cyl.Radius, 0, 0)),
                        m.TransformVector(new Vector3d(0, cyl.Radius, 0)));
                }
                return SolidFactory.Extrude(new Profile([rim]), m.TransformVector((0, 0, cyl.Height)));
            }

            case TorusShape torus:
            {
                Decompose(m, shape, out var rotation, out var translation, out double scale);
                return SolidFactory.MakeTorus(
                    torus.MajorRadius * scale, torus.MinorRadius * scale,
                    translation, rotation.Rotate(Vector3d.UnitZ));
            }

            case ExtrudeShape { Sketch: { } sketch } extrude:
            {
                var effective = m * extrude.PlaneMatrix;
                var (outer, holes) = sketch.ToProfiles();
                return SolidFactory.Extrude(
                    TransformProfile(outer, effective),
                    effective.TransformVector((0, 0, extrude.Height)),
                    TransformProfiles(holes, effective));
            }

            case ExtrudeShape extrude:
                return SolidFactory.Extrude(
                    TransformProfile(extrude.Profile!, m),
                    m.TransformVector(extrude.Direction),
                    TransformProfiles(extrude.Holes, m));

            case RevolveShape { Sketch: { } sketch } revolve:
            {
                Decompose(m, shape, out _, out _, out _); // rigid + uniform only
                var effective = m * revolve.PlaneMatrix;
                var (outer, holes) = sketch.ToProfiles();
                return SolidFactory.Revolve(
                    TransformProfile(outer, effective),
                    effective.TransformPoint(Vector3d.Zero),
                    effective.TransformVector((0, 1, 0)),   // the plane's y axis
                    revolve.Angle,
                    TransformProfiles(holes, effective));
            }

            case RevolveShape revolve:
            {
                Decompose(m, shape, out var rotation, out _, out _);
                return SolidFactory.Revolve(
                    TransformProfile(revolve.Profile!, m),
                    m.TransformPoint(revolve.AxisOrigin),
                    rotation.Rotate(revolve.AxisDirection),
                    revolve.Angle,
                    TransformProfiles(revolve.Holes, m));
            }

            case SweepShape { Sketch: { } sketch } sweep:
            {
                Decompose(m, shape, out _, out _, out _); // rigid + uniform only
                var effective = m * sweep.PlaneMatrix;
                var (outer, holes) = sketch.ToProfiles();
                var path = IsIdentity(m) ? sweep.Path : new TransformedCurve(sweep.Path, m);
                return SolidFactory.Sweep(
                    TransformProfile(outer, effective), path, TransformProfiles(holes, effective));
            }

            case SweepShape sweep:
            {
                Decompose(m, shape, out _, out _, out _); // rigid + uniform only
                var path = IsIdentity(m) ? sweep.Path : new TransformedCurve(sweep.Path, m);
                return SolidFactory.Sweep(
                    TransformProfile(sweep.Profile!, m), path, TransformProfiles(sweep.Holes, m));
            }

            case BooleanShape boolean:
            {
                var a = LowerBrep(boolean.A, m);
                var b = LowerBrep(boolean.B, m);
                return boolean.Op switch
                {
                    BooleanOp.Union => BrepBoolean.Union(a, b),
                    BooleanOp.Intersection => BrepBoolean.Intersection(a, b),
                    _ => BrepBoolean.Difference(a, b),
                };
            }

            case RimShape rim:
            {
                // Amounts scale with the accumulated uniform factor so a feature
                // authored before a Scale behaves as if scaled with the part.
                Decompose(m, shape, out _, out _, out double featureScale);
                var solid = LowerBrep(rim.Child, m);
                var selected = rim.Selector(solid).ToList();
                if (selected.Count == 0)
                    throw new InvalidOperationException(
                        $"{rim.Describe()}: the face selector matched nothing on the lowered solid.");
                foreach (var target in selected)
                {
                    solid = rim.IsFillet
                        ? Filleting.FilletRim(solid, target, rim.Amount * featureScale)
                        : Filleting.ChamferRim(solid, target, rim.Amount * featureScale, rim.SideAmount * featureScale);
                }
                return solid;
            }

            case DrillShape drill:
            {
                ValidateDrillDepth(drill, m);
                return LowerBrep(drill.Expanded, m);
            }

            case TransformShape t:
                return LowerBrep(t.Child, m * t.Matrix);

            case SourceShape { Geometry: BrepSolid solid } when IsIdentity(m):
                return solid;

            default:
                throw new ShapeConversionException(Classify(shape, TargetRep.Brep));
        }
    }

    // ------------------------------------------------------------- implicit lowering

    internal static Sdf LowerImplicit(Shape shape, in Matrix4d m, MeshQuality quality)
    {
        // A transform the SDF operators can't express: produce a mesh of the whole
        // (transformed) subtree and wrap it.
        if (!m.TryDecomposeRigidUniformScale(out var rotation, out var translation, out double scale))
            return BridgeToSdf(shape, m, quality);

        switch (shape)
        {
            case BoxShape box:
                return Place(Sdf.Box(box.Bounds.Size.X, box.Bounds.Size.Y, box.Bounds.Size.Z)
                        .Translate(box.Bounds.Center),
                    rotation, translation, scale);
            case SphereShape sphere:
                return Place(Sdf.Sphere(sphere.Radius), rotation, translation, scale);
            case CylinderShape cyl:
                return Place(Sdf.Cylinder(cyl.Radius, cyl.Height), rotation, translation, scale);
            case TorusShape torus:
                return Place(Sdf.Torus(torus.MajorRadius, torus.MinorRadius), rotation, translation, scale);

            case ExtrudeShape { Sketch: { } sketch } extrude:
            {
                // Exact: the sketch's own 2D SDF extruded, placed rigidly.
                var effective = m * extrude.PlaneMatrix; // plane is rigid, m decomposable ⇒ decomposable
                effective.TryDecomposeRigidUniformScale(out var q, out var t, out double s);
                return Place(Sdf.ExtrudedRegion(new SketchRegion(sketch), extrude.Height), q, t, s);
            }

            case RevolveShape { Sketch: { } sketch } revolve when revolve.IsFullTurn:
            {
                // Exact: the canonical solid of revolution lives in the XZ-plane frame;
                // re-place it with (m · plane · canonical⁻¹), which is rigid.
                var placement = m * revolve.PlaneMatrix * CanonicalRevolveInverse;
                placement.TryDecomposeRigidUniformScale(out var q, out var t, out double s);
                return Place(Sdf.RevolvedRegion(new SketchRegion(sketch, forRevolution: true)), q, t, s);
            }

            case ExtrudeShape or RevolveShape or SweepShape or RimShape:
            case SourceShape { Geometry: BrepSolid }:
                return BridgeToSdf(shape, m, quality);

            case BooleanShape b:
            {
                var left = LowerImplicit(b.A, m, quality);
                var right = LowerImplicit(b.B, m, quality);
                return b.Op switch
                {
                    BooleanOp.Union => left | right,
                    BooleanOp.Intersection => left & right,
                    _ => left - right,
                };
            }

            case SmoothShape s:
            {
                var left = LowerImplicit(s.A, m, quality);
                var right = LowerImplicit(s.B, m, quality);
                double blend = s.Blend * scale; // blend radius is a length: scales with the object
                return s.Op switch
                {
                    BooleanOp.Union => left.SmoothUnion(right, blend),
                    BooleanOp.Intersection => left.SmoothIntersect(right, blend),
                    _ => left.SmoothSubtract(right, blend),
                };
            }

            case OffsetShape o:
                return LowerImplicit(o.Child, m, quality).Offset(o.Distance * scale);
            case ShellShape sh:
                return LowerImplicit(sh.Child, m, quality).Shell(sh.Thickness * scale);
            case LatticeShape l:
                return LowerImplicit(l.Child, m, quality)
                    .Intersect(Place(l.Pattern, rotation, translation, scale));

            case DrillShape drill:
                // Exact SDF subtraction has no coplanar-face degeneracy: no validation.
                return LowerImplicit(drill.Expanded, m, quality);

            case ThreadShape thread:
                return Place(thread.ToSdf(), rotation, translation, scale);

            case TransformShape t:
                return LowerImplicit(t.Child, m * t.Matrix, quality);

            case SourceShape { Geometry: Sdf sdf }:
                return Place(sdf, rotation, translation, scale);
            case SourceShape { Geometry: HalfEdgeMesh mesh }:
                return Place(new MeshSdf(mesh), rotation, translation, scale);

            default:
                throw new NotSupportedException($"Unknown shape node {shape.GetType().Name}.");
        }
    }

    private static Sdf Place(Sdf sdf, in Quaterniond rotation, in Vector3d translation, double scale)
    {
        var result = sdf;
        if (Math.Abs(scale - 1) > 1e-12)
            result = result.Scale(scale);
        if (Math.Abs(rotation.W - 1) > 1e-12)
            result = result.Rotate(rotation);
        if (translation.LengthSquared > 0)
            result = result.Translate(translation);
        return result;
    }

    /// <summary>Wraps a mesh of the transformed subtree in a mesh SDF — the fallback
    /// for nodes (or transforms) with no exact SDF form.</summary>
    private static Sdf BridgeToSdf(Shape shape, in Matrix4d m, MeshQuality quality)
    {
        // Best: bake the transform into an exact B-Rep and tessellate that.
        if (CanBrep(shape, m))
            return new MeshSdf(Tessellate(LowerBrep(shape, m), quality));

        // Next: exact B-Rep at identity, transform the tessellation.
        if (CanBrep(shape, Matrix4d.Identity))
            return new MeshSdf(TransformMesh(Tessellate(LowerBrep(shape, Matrix4d.Identity), quality), m));

        // Last: polygonize the subtree's own SDF at identity, transform the mesh.
        var sdf = LowerImplicit(shape, Matrix4d.Identity, quality);
        return new MeshSdf(TransformMesh(SurfaceNets.Polygonize(sdf, quality.SdfResolution), m));
    }

    // ----------------------------------------------------------------- mesh lowering

    internal static HalfEdgeMesh ToMesh(Shape shape, MeshQuality quality)
    {
        // Highest fidelity first: one tessellation of an exact B-Rep.
        if (CanBrep(shape, Matrix4d.Identity))
            return Tessellate(LowerBrep(shape, Matrix4d.Identity), quality);

        // Blends/offsets/shells/lattices have no crisp form: polygonize the SDF.
        if (UsesImplicitOnlyOps(shape))
            return SurfaceNets.Polygonize(
                LowerImplicit(shape, Matrix4d.Identity, quality), quality.SdfResolution);

        // Mesh leaves mixed into boolean trees: per-node mesh operations.
        return LowerMesh(shape, Matrix4d.Identity, quality);
    }

    private static HalfEdgeMesh LowerMesh(Shape shape, in Matrix4d m, MeshQuality quality)
    {
        switch (shape)
        {
            case BooleanShape b:
            {
                var left = LowerMesh(b.A, m, quality);
                var right = LowerMesh(b.B, m, quality);
                return b.Op switch
                {
                    BooleanOp.Union => MeshBoolean.Union(left, right),
                    BooleanOp.Intersection => MeshBoolean.Intersection(left, right),
                    _ => MeshBoolean.Difference(left, right),
                };
            }

            case TransformShape t:
                return LowerMesh(t.Child, m * t.Matrix, quality);

            case SourceShape { Geometry: HalfEdgeMesh mesh }:
                return IsIdentity(m) ? mesh : TransformMesh(mesh, m);

            default:
                if (CanBrep(shape, m))
                    return Tessellate(LowerBrep(shape, m), quality);
                if (CanBrep(shape, Matrix4d.Identity))
                    return TransformMesh(Tessellate(LowerBrep(shape, Matrix4d.Identity), quality), m);
                return TransformMesh(
                    SurfaceNets.Polygonize(
                        LowerImplicit(shape, Matrix4d.Identity, quality), quality.SdfResolution),
                    m);
        }
    }

    /// <summary>
    /// Rejects a drill whose tool's flat bottom is coplanar with a planar face of the
    /// body: coplanar face pairs are unsupported boolean input (the v1 transversality
    /// contract), and without this guard the failure surfaces as a deep tessellation
    /// error ("Directed edge appears twice"). Follows the rim features' precedent of
    /// validating against the lowered solid. Only exact coplanarity (within tolerance)
    /// throws — a bottom safely short of the far face is a legitimate blind hole.
    /// </summary>
    private static void ValidateDrillDepth(DrillShape drill, in Matrix4d m)
    {
        if (!CanBrep(drill.Child, m))
            return; // the expansion will produce its own conversion report
        var body = LowerBrep(drill.Child, m);
        var effective = m * drill.PlaneMatrix;
        var drillNormal = effective.TransformVector((0, 0, 1)).Normalized();
        const double tolerance = 1e-6;

        foreach (var point in drill.Points)
        {
            var bottom = effective.TransformPoint(new Vector3d(point.X, point.Y, -drill.Depth));
            foreach (var face in body.Faces)
            {
                if (!face.IsPlanar(out var origin, out var normal))
                    continue;
                if (Math.Abs(normal.Normalized().Dot(drillNormal)) < 1 - 1e-6)
                    continue;
                if (Math.Abs(drillNormal.Dot(origin - bottom)) <= tolerance)
                    throw new ArgumentException(
                        $"Drill depth {drill.Depth:g6} puts the tool's flat bottom coplanar with a planar " +
                        $"face of the body (hole at {point}); increase depth so the tool clears the far " +
                        "face, or reduce it for a blind hole.");
            }
        }
    }

    // ---------------------------------------------------------------------- helpers

    private static HalfEdgeMesh Tessellate(BrepSolid solid, MeshQuality quality) =>
        BRepTessellator.Tessellate(solid, quality.SegmentsPerCircle, quality.CurveSamples);

    internal static HalfEdgeMesh TransformMesh(HalfEdgeMesh mesh, in Matrix4d m)
    {
        var (positions, faces) = mesh.ToIndexed();
        var transformed = new Vector3d[positions.Length];
        for (int i = 0; i < positions.Length; i++)
            transformed[i] = m.TransformPoint(positions[i]);
        IEnumerable<IReadOnlyList<int>> orderedFaces = m.Determinant < 0
            ? faces.Select(f => (IReadOnlyList<int>)[.. f.Reverse()])
            : faces;
        return HalfEdgeMesh.Build(transformed, orderedFaces);
    }

    private static Profile TransformProfile(Profile profile, in Matrix4d m)
    {
        if (IsIdentity(m))
            return profile;
        var matrix = m;
        return new Profile([.. profile.Segments.Select(s => (Curve3d)new TransformedCurve(s, matrix))]);
    }

    private static IReadOnlyList<Profile>? TransformProfiles(IReadOnlyList<Profile>? profiles, in Matrix4d m)
    {
        if (profiles is null || IsIdentity(m))
            return profiles;
        var matrix = m;
        return [.. profiles.Select(p => TransformProfile(p, matrix))];
    }

    private static void Decompose(
        in Matrix4d m, Shape shape,
        out Quaterniond rotation, out Vector3d translation, out double scale)
    {
        if (!m.TryDecomposeRigidUniformScale(out rotation, out translation, out scale))
            throw new ShapeConversionException(Classify(shape, TargetRep.Brep));
    }

    private static bool IsIdentity(in Matrix4d m) => m.Equals(Matrix4d.Identity);

    /// <summary>Inverse of the XZ sketch plane's placement — the frame
    /// <see cref="Sdf.RevolvedRegion"/> is canonical in (sketch x = radius, y = world z).</summary>
    private static readonly Matrix4d CanonicalRevolveInverse = InvertRigid(SketchPlane.XZ.ToMatrix());

    private static Matrix4d InvertRigid(in Matrix4d m)
    {
        if (!m.TryInvert(out var inverse))
            throw new InvalidOperationException("Sketch plane matrix must be invertible.");
        return inverse;
    }

    private static bool IsTranslation(in Matrix4d m, out Vector3d offset)
    {
        offset = new Vector3d(m.M14, m.M24, m.M34);
        return m.M11 == 1 && m.M12 == 0 && m.M13 == 0
            && m.M21 == 0 && m.M22 == 1 && m.M23 == 0
            && m.M31 == 0 && m.M32 == 0 && m.M33 == 1
            && m.M41 == 0 && m.M42 == 0 && m.M43 == 0 && m.M44 == 1;
    }
}
