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
                            Detail = MeshRoute(entries[i].Node),
                        };
                }
                break;
        }
        return new ConversionReport(target, entries);
    }

    /// <summary>
    /// How a node that B-Rep cannot express reaches a mesh. Most go through the SDF, but a
    /// couple are mesh CONSTRUCTIONS in their own right and saying "polygonized from the
    /// field" of those would be a plain untruth about which code ran.
    /// </summary>
    private static string MeshRoute(string node) => node switch
    {
        _ when node.StartsWith("Hull(", StringComparison.Ordinal) =>
            "quickhull over the operands' tessellated mesh vertices",
        _ when node.StartsWith("Remeshed(", StringComparison.Ordinal) =>
            "isotropic remesh of the child's mesh lowering, projected back onto it",
        _ when node.StartsWith("Smoothed(", StringComparison.Ordinal) =>
            "Laplacian fairing of the child's mesh lowering",
        _ when node.StartsWith("Extrude(twist", StringComparison.Ordinal) =>
            "section rings swept through the twist, caps triangulated once and shared by index",
        _ => "polygonized from the signed distance field (Surface Nets)",
    };

    private static void ClassifyBrep(Shape shape, in Matrix4d m, List<ConversionEntry> entries)
    {
        switch (shape)
        {
            // Boxes, cylinders, extrusions and wedges are all extrusions of a planar
            // profile, and any affine map bakes into the profile exactly.
            case BoxShape or CylinderShape or ExtrudeShape or WedgeShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native));
                break;
            // Spheres, tori, and cones are symmetric under reflection: any similarity,
            // proper or mirrored, re-places them exactly.
            case SphereShape or TorusShape:
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear would need an ellipsoid surface"));
                break;
            case ConeShape:
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear would need an elliptic cone surface"));
                break;
            case RevolveShape { Sketch: { } sketch } revolve:
                // Mirrored similarities included: a reflection conjugates the rotation —
                // F·Rot(d, φ)·F = Rot(−F·d, φ) — so a mirrored revolve is the same
                // sweep about the negated transformed axis, exactly.
                if (!TryDecomposeSimilarity(m, out _, out _, out _, out _))
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
                // Mirrored similarities included: revolves negate the transformed axis
                // (see the sketch case above); sweeps need no fix at all — the RMF
                // transport is intrinsic, so sweeping the reflected profile along the
                // reflected path IS the reflected sweep.
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with this operation"));
                break;
            case TwistExtrudeShape twisted:
                // A pure taper is exact: every straight side sweeps a plane through the
                // scaling centre, so the solid is a ruled loft between base and top. A
                // twisted side wall is no surface this kernel carries.
                if (twisted.IsTwisted)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a twisted side wall is not an analytic or ruled surface the B-Rep kernel carries; ToMesh sweeps section rings and ToImplicit wraps that mesh"));
                else
                    // Mirrored similarities included: this case IS a two-section loft, so
                    // it inherits the loft's isometry argument verbatim.
                    entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                        ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                            "ruled loft between the base section and the scaled top (SolidFactory.Loft)")
                        : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                            "a non-uniform scale or shear does not commute with the loft's parameterization"));
                break;
            case SheetMetalShape:
                // Similarities only, MIRRORED INCLUDED: thickness, bend radius and flange
                // length are all LENGTHS and every bend angle an ANGLE, both of which any
                // similarity preserves — while a shear or non-uniform scale would give the
                // sheet a different thickness in every direction and the bend a different
                // radius round its own arc. A reflection is re-DECLARED rather than
                // re-placed (SheetMetalBody.MirroredInPlane rebuilds the tree the other way
                // round), which is what the earlier refusal here was waiting for.
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "base flange extruded, each bend welded in as topology (SheetMetalSurgery); flange " +
                        "geometry validates at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a sheet's thickness, bend radius and flange lengths are LENGTHS, so only a similarity "
                        + "re-places them; a shear or non-uniform scale would give the sheet a different "
                        + "thickness in every direction and the bend a different radius round its own arc"));
                break;
            case LoftShape:
                // Similarities only (mirrored included): the loft's chord-length
                // parameterization and least-twist alignment are METRIC, so a shear would
                // skin DIFFERENT in-between geometry than shearing the skin — while an
                // isometry preserves every length and angle those two rules read, so
                // skinning the reflected sections IS the reflected skin.
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "skinned through the placed sections (SolidFactory.Loft); section compatibility validates at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with the loft's chord-length parameterization"));
                break;
            case BooleanShape b:
                ClassifyBrep(b.A, m, entries);
                ClassifyBrep(b.B, m, entries);
                entries.Add(new ConversionEntry(b.Describe(), NodeSupport.Native));
                break;
            case ShellShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "the SDF shell is a symmetric skin about the surface (|d| - t/2) with no B-Rep form; " +
                    "for an exact inward hollow of a polyhedral solid use Shell(thickness, openings)"));
                break;
            case SmoothShape or OffsetShape or LatticeShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "only expressible as a signed distance field, and meshes cannot be imported into B-Rep"));
                break;
            case RimShape rim:
                // Mirrored similarities included: chamfers and fillets are metric
                // features and a reflection is an isometry, so the surgery on the
                // mirrored child is the mirrored surgery. (Selectors run on the
                // LOWERED, i.e. mirrored, solid — the same contract rotations have.)
                ClassifyBrep(rim.Child, m, entries);
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "planar-face rim feature; rim shape constraints validate at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with rim features"));
                break;

            case DraftShape draft:
                // Mirrored similarities included: an angle is preserved by every isometry
                // and "narrows along the pull direction" is a metric statement, so the
                // reflected draft is the draft along the pull direction's linear image.
                // (Draft.Apply chooses the rotation SENSE by measurement rather than from
                // a handedness convention, which is what makes that true rather than
                // merely plausible.)
                ClassifyBrep(draft.Child, m, entries);
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "exact plane rotation about each face's neutral line (Draft.Apply); prism-shape constraints validate at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with a draft angle"));
                break;

            case BrepShellShape brepShell:
                // Mirrored similarities included: an inward offset by a distance is defined
                // by distance alone, and an isometry preserves distance.
                ClassifyBrep(brepShell.Child, m, entries);
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "exact inward polyhedral shelling (Shelling.Shell); polyhedron constraints validate at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with a wall thickness"));
                break;

            case DirectEditShape edit:
                // Mirrored similarities included: an offset distance and a topological
                // deletion are both preserved by every isometry, and a move reduces to the
                // dot product v.n, which an orthogonal map preserves.
                ClassifyBrep(edit.Child, m, entries);
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "face offset/move/delete on the lowered solid (DirectEdit); the corner and heal " +
                        "constraints validate at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with a face edit"));
                break;

            case RoundEdgesShape roundEdges:
                // Mirrored similarities included: the operation is the morphological
                // opening (K erode B_r) dilate B_r, and a reflection maps a ball to the
                // same ball, so it commutes with the whole construction.
                ClassifyBrep(roundEdges.Child, m, entries);
                entries.Add(TryDecomposeSimilarity(m, out _, out _, out _, out _)
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "exact morphological rounding (Filleting.FilletAllEdges); convex-edge and corner constraints validate at lowering")
                    : new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a non-uniform scale or shear does not commute with a fillet radius"));
                break;

            case HullShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "a convex hull is a mesh construction (quickhull over tessellated vertices), and meshes cannot be imported into B-Rep"));
                break;

            case RemeshShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "a remesh is defined on a triangulation, so its result is a tessellation rather than a surface, and meshes cannot be imported into B-Rep"));
                break;

            case SmoothedShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "Laplacian fairing is defined on a triangulation, so its result is a tessellation rather than a surface, and meshes cannot be imported into B-Rep"));
                break;

            case MotionSweepShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "a swept volume's outer envelope is not one of the kernel's surfaces — take the implicit route (union of the child's field over the sampled poses) or the mesh route"));
                break;

            case DrillShape drill:
                // A drill is its expansion (body minus revolved tools); the extra
                // far-face validation happens at lowering.
                ClassifyBrep(drill.Expanded, m, entries);
                break;
            case ThreadShape thread:
                // Native for the basic profile AND for its distance-field clearance offset,
                // under any similarity placement, mirrored included — a mirrored thread IS
                // the left-hand thread, and the factory builds either handedness. Only a
                // chamfer that reaches the thread depth is reported Impossible, and for a
                // reason about tangency rather than about the profile.
                if (!TryDecomposeThreadPlacement(m, out _, out _, out _, out _))
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a sheared or non-uniformly scaled placement cannot re-place a helical thread exactly"));
                else if (thread.ChamferLength >= thread.Spec.ThreadDepth)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a chamfer at or past the thread depth puts the cone's base exactly on the minor diameter, tangent to every root band along the end plane — coincident curved-surface boolean input; pass a shallower chamferLength, or use ToMesh/ToImplicit"));
                else if (thread.ProfileOffset != 0)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "boolean-free helical sweep whose clearance-eroded profile mixes straight and ARC generators (SolidFactory.OffsetPitchProfile); not STEP-exportable"));
                else if (thread.ChamferLength > 0 || thread.RunoutLength > 0)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "boolean-free helical sweep, its ends treated by a coaxial cone that cuts every band in an exact conical SpiralArc3d; not STEP-exportable"));
                else
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "boolean-free helical sweep (SolidFactory.MakeThreadedRod); not STEP-exportable"));
                break;
            case ThreadedHoleShape hole:
                // Native via ONE combined tool per point: the thread form clipped at the
                // pilot radius (the pilot volume is part of the same helical rod, so no
                // coaxial tool∩bore tangency ever reaches the boolean). A printing
                // CLEARANCE grows that same tool by the distance-field offset — the
                // mirror image of an external thread's erosion, so its crest corners round
                // where the rod's miter — and stays Native. Mirrored placements are Native
                // exactly as ThreadShape's are: the tool is the same helical rod, so the
                // FlipY identity applies per placed point with the handedness XOR'd at
                // lowering.
                ClassifyBrep(hole.Child, m, entries);
                if (!TryDecomposeThreadPlacement(m, out _, out _, out _, out _))
                    entries.Add(new ConversionEntry(hole.Describe(), NodeSupport.Impossible,
                        "a sheared or non-uniformly scaled placement cannot re-place a helical thread exactly"));
                else
                    entries.Add(new ConversionEntry(hole.Describe(), NodeSupport.Native,
                        "pilot + thread subtracted as one clipped-profile helical tool; the drilled faces split along exact spiral-arc chains"));
                break;
            case TransformShape t:
                ClassifyBrep(t.Child, m * t.Matrix, entries);
                break;
            // A tag adds no geometry, so it adds no entry: the plan a user reads should
            // describe the model, not the labels on it.
            case TagShape tag:
                ClassifyBrep(tag.Child, m, entries);
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
        // Mirrored similarities count as rigid here: implicit lowering reflects the
        // query point (exact), so a mirror never forces a bridge.
        bool rigid = TryDecomposeSimilarity(m, out _, out _, out _, out _);
        switch (shape)
        {
            case BoxShape or SphereShape or CylinderShape or TorusShape or ConeShape:
                entries.Add(rigid
                    ? new ConversionEntry(shape.Describe(), NodeSupport.Native)
                    : new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                        "sheared primitives go through a tessellated mesh SDF"));
                break;
            case ExtrudeShape { Sketch: not null } or WedgeShape:
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
            case TwistExtrudeShape { IsTwisted: true }:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "twisted-extrusion section sweep wrapped in a mesh SDF"));
                break;
            case ExtrudeShape or RevolveShape or SweepShape or RimShape or LoftShape
                or DraftShape or BrepShellShape or RoundEdgesShape or TwistExtrudeShape
                or SheetMetalShape or DirectEditShape:
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
            case HullShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "convex hull mesh (quickhull over tessellated vertices) wrapped in a mesh SDF"));
                break;

            case RemeshShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "remeshed triangles wrapped in a mesh SDF, so the field carries the tessellation's chord error rather than the child's own"));
                break;

            case SmoothedShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "faired triangles wrapped in a mesh SDF, so the field carries the tessellation's chord error rather than the child's own"));
                break;

            case MotionSweepShape sweep when rigid:
                ClassifyImplicit(sweep.Child, Matrix4d.Identity, entries);
                entries.Add(new ConversionEntry(sweep.Describe(), NodeSupport.Native,
                    "the child's field lowered once and placed per sampled pose, unioned"));
                break;
            case MotionSweepShape:
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
            case ThreadedHoleShape hole:
                ClassifyImplicit(hole.Expanded, m, entries);
                break;
            case TransformShape t:
                ClassifyImplicit(t.Child, m * t.Matrix, entries);
                break;
            case TagShape tag:
                ClassifyImplicit(tag.Child, m, entries);
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
        SmoothShape or OffsetShape or ShellShape or LatticeShape or ThreadShape
            or MotionSweepShape => true,
        SourceShape { Geometry: Sdf } => true,
        BooleanShape b => UsesImplicitOnlyOps(b.A) || UsesImplicitOnlyOps(b.B),
        DrillShape d => UsesImplicitOnlyOps(d.Expanded),
        ThreadedHoleShape h => UsesImplicitOnlyOps(h.Expanded),
        TransformShape t => UsesImplicitOnlyOps(t.Child),
        TagShape tag => UsesImplicitOnlyOps(tag.Child),
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
                    return SolidFactory.MakeBox(box.Extents);
                if (IsTranslation(m, out var offset))
                    return SolidFactory.MakeBox(new Aabb(box.Extents.Min + offset, box.Extents.Max + offset));
                var (x0, y0, z0) = box.Extents.Min;
                var (x1, y1, z1) = box.Extents.Max;
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
                DecomposeSimilarity(m, shape, out _, out var translation, out double scale);
                return SolidFactory.MakeSphere(sphere.Radius * scale, translation);
            }

            case CylinderShape cyl:
            {
                // Extruded circle/ellipse rather than MakeCylinder so any affine
                // placement bakes in exactly (circles promote back to cylinders).
                // Mirrored similarities keep the exact circle: the reflected cylinder is
                // still a true cylinder (FlipZ fixes the rim plane's x/y directions).
                var baseCenter = new Vector3d(0, 0, -cyl.Height / 2);
                Curve3d rim;
                if (TryDecomposeSimilarity(m, out var rotation, out _, out double s, out _))
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
                DecomposeSimilarity(m, shape, out _, out var translation, out double scale);
                return SolidFactory.MakeTorus(
                    torus.MajorRadius * scale, torus.MinorRadius * scale,
                    translation, m.TransformVector(Vector3d.UnitZ).Normalized());
            }

            case ConeShape cone:
            {
                // Base and top centers transform exactly; radii scale uniformly.
                // Works under mirrored similarities too (a reflected cone is a cone).
                DecomposeSimilarity(m, shape, out _, out _, out double coneScale);
                var coneBase = m.TransformPoint(new Vector3d(0, 0, -cone.Height / 2));
                var coneTop = m.TransformPoint(new Vector3d(0, 0, cone.Height / 2));
                return SolidFactory.MakeCone(
                    cone.BottomRadius * coneScale, cone.TopRadius * coneScale, cone.Height * coneScale,
                    coneBase, coneTop - coneBase);
            }

            // A wedge IS an extrusion of its trapezoidal cross-section; expanding here keeps
            // one lowering path rather than a fourth per target.
            case WedgeShape wedge:
                return LowerBrep(wedge.Expanded, m);

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

            case TwistExtrudeShape twisted:
            {
                if (twisted.IsTwisted)
                    throw new NotSupportedException(
                        "A twisted extrusion has no exact B-Rep side surface; lower to mesh or implicit instead.");
                DecomposeSimilarity(m, shape, out _, out _, out _); // similarity only (the loft rule)
                var effective = m * twisted.PlaneMatrix;
                // The top section is the base scaled per axis about the plane origin and
                // lifted by the height; a ruled loft between the two IS the linear taper
                // (scaling is linear, and a two-section loft's v is linear), and every
                // straight side sweeps an exact plane through the scaling centre. A hole
                // takes the SAME two placements, so its inner skin is the taper of the
                // hole about the same scaling centre.
                var topLocal = Matrix4d.CreateTranslation((0, 0, twisted.Height))
                             * Matrix4d.CreateScale((twisted.ScaleTop.X, twisted.ScaleTop.Y, 1));
                var (outer, holes) = twisted.Sketch.ToProfiles();
                IReadOnlyList<IReadOnlyList<Profile>>? holesPerSection = null;
                if (holes is { Count: > 0 })
                {
                    var bottomHoles = new Profile[holes.Count];
                    var topHoles = new Profile[holes.Count];
                    for (int j = 0; j < holes.Count; j++)
                    {
                        bottomHoles[j] = TransformProfile(holes[j], effective);
                        topHoles[j] = TransformProfile(holes[j], effective * topLocal);
                    }
                    holesPerSection = [bottomHoles, topHoles];
                }
                return SolidFactory.Loft(
                    [TransformProfile(outer, effective), TransformProfile(outer, effective * topLocal)],
                    holesPerSection,
                    LoftStyle.Ruled);
            }

            case RevolveShape { Sketch: { } sketch } revolve:
            {
                DecomposeSimilarity(m, shape, out _, out _, out _, out bool reflected);
                var effective = m * revolve.PlaneMatrix;
                var (outer, holes) = sketch.ToProfiles();
                // A reflection conjugates the rotation — F·Rot(d, φ)·F = Rot(F·d, −φ)
                // = Rot(−F·d, φ) — so the mirrored revolve is the SAME angular sweep
                // about the NEGATED transformed axis (the LH-thread identity; for full
                // turns the sign only keeps the orientation conventions aligned).
                var axis = effective.TransformVector((0, 1, 0));    // the plane's y axis
                return SolidFactory.Revolve(
                    TransformProfile(outer, effective),
                    effective.TransformPoint(Vector3d.Zero),
                    reflected ? -axis : axis,
                    revolve.Angle,
                    TransformProfiles(holes, effective));
            }

            case RevolveShape revolve:
            {
                DecomposeSimilarity(m, shape, out var rotation, out _, out _, out bool reflected);
                // Proper placements keep the exact spelling they always had; reflected
                // ones take the negated linear image of the axis (see the sketch case).
                var axis = reflected
                    ? -m.TransformVector(revolve.AxisDirection)
                    : rotation.Rotate(revolve.AxisDirection);
                return SolidFactory.Revolve(
                    TransformProfile(revolve.Profile!, m),
                    m.TransformPoint(revolve.AxisOrigin),
                    axis,
                    revolve.Angle,
                    TransformProfiles(revolve.Holes, m));
            }

            case SweepShape { Sketch: { } sketch } sweep:
            {
                // Mirrored similarities need no fix here: rotation-minimizing transport
                // is intrinsic (it commutes with any isometry), so sweeping the
                // reflected profile along the reflected path IS the reflected sweep.
                DecomposeSimilarity(m, shape, out _, out _, out _);
                var effective = m * sweep.PlaneMatrix;
                var (outer, holes) = sketch.ToProfiles();
                var path = IsIdentity(m) ? sweep.Path : new TransformedCurve(sweep.Path, m);
                return SolidFactory.Sweep(
                    TransformProfile(outer, effective), path, TransformProfiles(holes, effective));
            }

            case SweepShape sweep:
            {
                DecomposeSimilarity(m, shape, out _, out _, out _); // mirrored OK (see above)
                var path = IsIdentity(m) ? sweep.Path : new TransformedCurve(sweep.Path, m);
                return SolidFactory.Sweep(
                    TransformProfile(sweep.Profile!, m), path, TransformProfiles(sweep.Holes, m));
            }

            case SheetMetalShape sheet:
            {
                // Similarity, mirrored included. A reflection cannot be re-PLACED (a flange
                // tree is ordered and quoted on named edges) but it can be re-DECLARED:
                // MirroredInPlane rebuilds the tree against a reflected base sketch, and
                // the frame handed down negates its own X to match, so the sheet's +Z --
                // the face every bend line is quoted on -- never moves and Up/Down keep
                // meaning one thing.
                DecomposeSimilarity(m, shape, out _, out _, out double sheetScale, out bool sheetMirrored);
                var body = sheetMirrored ? sheet.Body.MirroredInPlane() : sheet.Body;
                // P = P'·FlipX with P' proper, so placing the REFLECTED body on P' IS
                // placing the original on P — the reflection is spent once, half in the
                // declaration and half in the frame, and never twice.
                var effective = sheetMirrored
                    ? m * body.Plane.ToMatrix() * FlipX
                    : m * body.Plane.ToMatrix();
                // FoldedOutline, not BaseSketch: a bend relief is a NOTCH in the blank rather
                // than a cut in the folded body, so the sheet is extruded from the outline
                // the reliefs left — and a LOUVRE's lanced footprint is a hole HERE and not
                // in the blank, since a lance separates material without removing any. With
                // neither declared all three are the same object.
                var (baseOuter, baseHoles) = body.FoldedOutline.ToProfiles();
                var flat = SolidFactory.Extrude(
                    TransformProfile(baseOuter, effective),
                    effective.TransformVector((0, 0, body.Spec.Thickness)),
                    TransformProfiles(baseHoles, effective));
                // The base's TOP face is where every flange's bend line is quoted from,
                // and the sheet frame's own directions come straight off the placed plane
                // so nothing is re-derived.
                return body.BuildBrep(
                    flat,
                    new SheetMetalBody.SheetFrame(
                        effective.TransformPoint((0, 0, body.Spec.Thickness)),
                        effective.TransformVector((1, 0, 0)).Normalized(),
                        effective.TransformVector((0, 1, 0)).Normalized()),
                    sheetScale);
            }

            case LoftShape loft:
            {
                // Similarity only (mirrored included) — the sections are already placed in
                // 3D, so the decomposition is a pure GATE and `m` is applied verbatim.
                DecomposeSimilarity(m, shape, out _, out _, out _);
                var placed = new Profile[loft.Sections.Count];
                for (int i = 0; i < placed.Length; i++)
                    placed[i] = TransformProfile(loft.Sections[i], m);
                IReadOnlyList<IReadOnlyList<Profile>>? loftHoles = null;
                if (loft.HolesPerSection is { } perSection)
                {
                    var placedHoles = new IReadOnlyList<Profile>[perSection.Count];
                    for (int i = 0; i < perSection.Count; i++)
                    {
                        var sectionHoles = new Profile[perSection[i].Count];
                        for (int j = 0; j < sectionHoles.Length; j++)
                            sectionHoles[j] = TransformProfile(perSection[i][j], m);
                        placedHoles[i] = sectionHoles;
                    }
                    loftHoles = placedHoles;
                }
                return SolidFactory.Loft(placed, loftHoles, loft.Style);
            }

            case BooleanShape boolean:
            {
                var a = LowerBrep(boolean.A, m);
                var b = LowerBrep(boolean.B, m);
                return WithImplicitRouteHint(() => boolean.Op switch
                {
                    BooleanOp.Union => BrepBoolean.Union(a, b),
                    BooleanOp.Intersection => BrepBoolean.Intersection(a, b),
                    _ => BrepBoolean.Difference(a, b),
                });
            }

            case RimShape rim:
            {
                // Amounts scale with the accumulated uniform factor so a feature
                // authored before a Scale behaves as if scaled with the part. Mirrored
                // similarities are fine: the surgery runs on the (mirrored) lowered
                // child, and chamfer/fillet geometry commutes with isometries.
                DecomposeSimilarity(m, shape, out _, out _, out double featureScale);
                var solid = LowerBrep(rim.Child, m);
                if (rim.EdgeSelector is { } edgeSelector)
                {
                    // Edge-set selection: the kernel groups it into complete rims plus
                    // terminated partial runs (all-or-nothing before surgery).
                    var selectedEdges = edgeSelector(solid).ToList();
                    if (selectedEdges.Count == 0)
                        throw new InvalidOperationException(
                            $"{rim.Describe()}: the edge selector matched nothing on the lowered solid.");
                    // A LAW reads corner positions on the lowered solid — the transforms are
                    // already baked into the geometry it sees — so its result is used
                    // verbatim, never multiplied by the feature scale, exactly as the
                    // face-selected law path below does.
                    if (rim.SetbackLaw is { } edgeLaw)
                        return rim.IsFillet
                            ? Filleting.FilletEdges(solid, selectedEdges, edgeLaw)
                            : Filleting.ChamferEdges(solid, selectedEdges, edgeLaw);
                    return rim.IsFillet
                        ? Filleting.FilletEdges(solid, selectedEdges, rim.Amount * featureScale)
                        : Filleting.ChamferEdges(
                            solid, selectedEdges, rim.Amount * featureScale, rim.SideAmount * featureScale);
                }
                var selected = rim.Selector(solid).ToList();
                if (selected.Count == 0)
                    throw new InvalidOperationException(
                        $"{rim.Describe()}: the face selector matched nothing on the lowered solid.");
                foreach (var target in selected)
                {
                    // A setback LAW reads corner positions on the lowered solid — the
                    // transforms are already baked into the geometry it sees — so its
                    // result is used verbatim, never multiplied by the feature scale.
                    solid = rim.SetbackLaw is { } law
                        ? rim.IsFillet
                            ? Filleting.FilletRim(solid, target, law)
                            : rim.LawAngleDegrees is { } lawAngle
                                ? Filleting.ChamferRimAtAngle(solid, target, law, lawAngle)
                                : Filleting.ChamferRim(solid, target, law)
                        : rim.IsFillet
                            ? Filleting.FilletRim(solid, target, rim.Amount * featureScale)
                            : Filleting.ChamferRim(solid, target, rim.Amount * featureScale, rim.SideAmount * featureScale);
                }
                return solid;
            }

            case DraftShape draft:
            {
                // The angle is dimensionless, so only the neutral plane and pull
                // direction bake; uniform scale leaves the taper alone (a scaled
                // frustum keeps its angles). Mirrored placements take the pull
                // direction's LINEAR IMAGE — the same asymmetry the reflected revolve
                // branch has, and for the same reason: the decomposition's rotation is
                // the proper part of m*FlipZ and would drop the reflection. Proper
                // placements keep the exact spelling they always had, so their geometry
                // stays bit-identical. No negation here (unlike a revolve's axis): a
                // pull direction is transported by the linear map, not conjugated.
                DecomposeSimilarity(m, shape, out var draftRotation, out _, out _, out bool draftReflected);
                var solid = LowerBrep(draft.Child, m);
                Func<BrepFace, bool>? selector = null;
                if (draft.Selector is not null)
                {
                    var selected = new HashSet<BrepFace>(draft.Selector(solid));
                    if (selected.Count == 0)
                        throw new InvalidOperationException(
                            $"{draft.Describe()}: the face selector matched nothing on the lowered solid.");
                    selector = selected.Contains;
                }
                return BRep.Draft.Apply(
                    solid, m.TransformPoint(draft.NeutralOrigin),
                    draftReflected
                        ? m.TransformVector(draft.PullDirection)
                        : draftRotation.Rotate(draft.PullDirection),
                    draft.AngleRadians, selector);
            }

            case BrepShellShape brepShell:
            {
                // Wall thickness is a length: it scales with the accumulated uniform
                // factor, like the rim features' amounts. Mirrored placements are fine —
                // only the scale is consumed, and an offset by a distance commutes with
                // any isometry.
                DecomposeSimilarity(m, shape, out _, out _, out double wallScale);
                var solid = LowerBrep(brepShell.Child, m);
                Func<BrepFace, bool>? openings = null;
                if (brepShell.Openings is not null)
                {
                    var selected = new HashSet<BrepFace>(brepShell.Openings(solid));
                    if (selected.Count == 0)
                        throw new InvalidOperationException(
                            $"{brepShell.Describe()}: the opening selector matched nothing on the lowered solid.");
                    openings = selected.Contains;
                }
                return Shelling.Shell(solid, brepShell.Thickness * wallScale, openings);
            }

            case RoundEdgesShape roundEdges:
            {
                // Mirrored placements included: the opening's structuring element is a
                // BALL, which every reflection maps to itself.
                DecomposeSimilarity(m, shape, out _, out _, out double radiusScale);
                return Filleting.FilletAllEdges(
                    LowerBrep(roundEdges.Child, m), roundEdges.Radius * radiusScale);
            }

            case DirectEditShape edit:
            {
                // Mirrored placements included. An offset DISTANCE only needs the uniform
                // scale (every isometry preserves distance), and a move's TRANSLATION takes
                // its full linear image — which is right for a reflection too, because the
                // operation reduces to the dot product v.n and an orthogonal map preserves
                // dot products. A deletion is purely topological and needs nothing.
                DecomposeSimilarity(m, shape, out _, out _, out double editScale);
                var solid = LowerBrep(edit.Child, m);
                var selected = new HashSet<BrepFace>(edit.Selector(solid));
                if (selected.Count == 0)
                    throw new InvalidOperationException(
                        $"{edit.Describe()}: the face selector matched nothing on the lowered solid.");
                return edit.Kind switch
                {
                    DirectEditKind.Offset =>
                        DirectEdit.OffsetFaces(solid, edit.Amount.X * editScale, selected.Contains),
                    DirectEditKind.Move =>
                        DirectEdit.MoveFaces(solid, m.TransformVector(edit.Amount), selected.Contains),
                    _ => DirectEdit.DeleteFaces(solid, selected.Contains),
                };
            }

            case DrillShape drill:
            {
                // Lower the BODY ONCE. The expansion is `((child − tool₀) − tool₁) …`, so
                // handing it to LowerBrep would lower the child a second time — on top of
                // the lowering the coplanarity validation already needed. On a drill whose
                // child is itself a drilled/filleted body that doubles the whole cost.
                if (!TryPeelDrillTools(drill, out var tools))
                {
                    ValidateDrillDepth(drill, m);
                    return LowerBrep(drill.Expanded, m);
                }
                var body = LowerBrep(drill.Child, m);
                ValidateDrillDepth(drill, body, m);
                foreach (var tool in tools)
                {
                    var cut = LowerBrep(tool, m);
                    body = WithImplicitRouteHint(() => BrepBoolean.Difference(body, cut));
                }
                return body;
            }

            // Only a SUB-DEPTH end chamfer stays clear of the root bands it would otherwise
            // be tangent to (matches the Explain classification above).
            case ThreadShape thread when thread.ChamferLength < thread.Spec.ThreadDepth:
            {
                // The ISO 68-1 basic profile, crest centered at phase 0 — the SAME
                // phase convention as Sdf.Thread (solid = {r ≤ R((z − P·θ/2π) mod P)}
                // with the crest flat at |w| ≤ P/16), so every representation of one
                // ThreadShape is the same geometry, not a rotated sibling. Corners run
                // bottom→top: crest flat (P/8 at the major radius), descending flank
                // (5P/16 axially), root flat (P/4 at the minor radius), ascending flank
                // wrapping to the next crest.
                if (!TryDecomposeThreadPlacement(
                        m, out var rotation, out var translation, out double scale, out bool reflected))
                    throw new ShapeConversionException(Classify(shape, TargetRep.Brep));
                var spec = thread.Spec;
                double pitch = spec.Pitch * scale;
                double rMajor = spec.MajorDiameter / 2 * scale;
                double rMinor = spec.MinorDiameter / 2 * scale;
                var frame = Frame3d.FromOrthonormal(
                    translation, rotation.Rotate(Vector3d.UnitX), rotation.Rotate(Vector3d.UnitY));
                double length = thread.Length * scale;
                // A printing CLEARANCE is the distance-field offset of that profile, which
                // is exact here too: it miters the crest corners and rounds the root ones
                // into arcs of the clearance radius, and an arc-generator helical band is
                // as boolean-free a sweep as a straight-generator one. The offset is a
                // length, so it rides the placement's scale with every other one.
                var rod = SolidFactory.MakeThreadedRod(
                    SolidFactory.OffsetPitchProfile(
                    [
                        new Vector2d(rMajor, -pitch / 16),
                        new Vector2d(rMajor, pitch / 16),
                        new Vector2d(rMinor, 3 * pitch / 8),
                        new Vector2d(rMinor, 5 * pitch / 8),
                    ], pitch, thread.ProfileOffset * scale),
                    pitch, length, frame, spec.LeftHand ^ reflected);
                // Deliberate exact-zero test: "no chamfer requested" is a user-parameter
                // contract, and skipping the two booleans keeps an unchamfered rod's
                // topology bit-for-bit what it has always been.
                double runout = thread.RunoutLength * scale;
                if (thread.ChamferLength <= 0 && runout <= 0)
                    return rod;
                double chamfer = thread.ChamferLength * scale;
                // Both ends, so the B-Rep is the same solid Sdf.Thread's start/end
                // treatments describe. Each tool meets the rod ONLY on its cone, which
                // cuts every helical band in an exact conical SpiralArc3d — and that is
                // true of a shallow RUNOUT cone for exactly the same reason, since the
                // family is coaxial-straight-generator carriers, not 45° ones.
                foreach (bool atMaxAxial in (ReadOnlySpan<bool>)[true, false])
                {
                    bool isRunout = !atMaxAxial && runout > 0;
                    if (!isRunout && chamfer <= 0)
                        continue;
                    double drop = isRunout ? thread.RunoutDrop * scale : chamfer;
                    double axial = isRunout ? runout : chamfer;
                    var tool = SolidFactory.MakeThreadEndConeTool(
                        rMajor, drop, axial, atMaxAxial ? length : 0, atMaxAxial, frame);
                    rod = WithImplicitRouteHint(() => BrepBoolean.Difference(rod, tool));
                }
                return rod;
            }

            case ThreadedHoleShape hole:
            {
                // ONE tool per point: the internal thread form CLIPPED at the pilot
                // radius, so the tap-drill volume is part of the same helical rod and
                // no coaxial pilot-bore∩root-band tangency (unsupported boolean input)
                // ever exists. Face-pair inventory per point, body = planar faces:
                // the tool's helical bands cross only the drilled plane(s), each in an
                // exact spiral arc (plane ⊥ helical axis, SurfaceIntersection); the
                // arcs chain into a closed loop the plane face splits along
                // (SplitByClosedCurveChain), each band splits at its own arc, and the
                // tool's flat caps sit strictly off every body face (overshoot above,
                // blind-depth validation below) — no tangent or coplanar pairs.
                if (!TryDecomposeThreadPlacement(m, out _, out _, out double scale, out bool reflected))
                    throw new ShapeConversionException(Classify(shape, TargetRep.Brep));
                var body = LowerBrep(hole.Child, m);
                var spec = hole.Spec;
                double pitch = spec.Pitch;
                double rMajor = spec.MajorDiameter / 2;
                double rMinor = spec.MinorDiameter / 2;
                double rPilot = spec.TapDrillDiameter / 2;

                // The void profile per pitch is max(basic form, pilot radius): where the
                // tap drill exceeds the thread minor radius (the usual case) the root
                // flat widens into a pilot flat, its corners on the exact flank lines.
                Vector2d[] corners;
                if (rPilot > rMinor + 1e-12)
                {
                    double flankDrop = 5 * pitch / 16 * (rMajor - rPilot) / (rMajor - rMinor);
                    corners =
                    [
                        new Vector2d(rMajor, -pitch / 16),
                        new Vector2d(rMajor, pitch / 16),
                        new Vector2d(rPilot, pitch / 16 + flankDrop),
                        new Vector2d(rPilot, 15 * pitch / 16 - flankDrop),
                    ];
                }
                else
                {
                    corners =
                    [
                        new Vector2d(rMajor, -pitch / 16),
                        new Vector2d(rMajor, pitch / 16),
                        new Vector2d(rMinor, 3 * pitch / 8),
                        new Vector2d(rMinor, 5 * pitch / 8),
                    ];
                }
                var scaledCorners = corners.Select(c => new Vector2d(c.X * scale, c.Y * scale)).ToList();
                // The clearance grows the VOID, so the tool that cuts it grows too — the
                // same distance-field offset with the opposite sign, which is why the tool's
                // crest corners round where an external thread's miter.
                var toolProfile = SolidFactory.OffsetPitchProfile(
                    scaledCorners, pitch * scale, hole.Clearance * scale);

                var effective = m * hole.PlaneMatrix;
                ValidateThreadedHoleDepth(hole, body, effective);

                // Same overshoot rule as the implicit tool; the geometry below the
                // surface — the actual hole — is identical either way.
                double overshoot = 0.05 * Math.Max(hole.Depth, spec.MajorDiameter);
                foreach (var point in hole.Points)
                {
                    // The tool advances DOWN the plane normal: frame (X, −Y, −Z), the
                    // exact π-rotation about X the implicit route uses (flipDown). A
                    // MIRRORED placement folds the FlipY factorization into the same
                    // recipe — the tool's improper placement effective∘flipDown factors
                    // as (effective∘flipDown∘FlipY)∘FlipY, whose proper half has axes
                    // (X, +Y) and Z still down the transformed normal — and FlipY∘rod
                    // is the opposite-handed rod on that proper frame (the ThreadShape
                    // identity, applied per placed point).
                    var origin = effective.TransformPoint(new Vector3d(point.X, point.Y, overshoot));
                    var xAxis = effective.TransformVector(Vector3d.UnitX).Normalized();
                    var yAxis = (reflected ? 1.0 : -1.0)
                        * effective.TransformVector(Vector3d.UnitY).Normalized();
                    var frame = Frame3d.FromOrthonormal(origin, xAxis, yAxis);
                    var tool = SolidFactory.MakeThreadedRod(
                        toolProfile, pitch * scale, (hole.Depth + overshoot) * scale, frame,
                        spec.LeftHand ^ reflected);
                    var cutting = body;
                    body = WithImplicitRouteHint(() => BrepBoolean.Difference(cutting, tool));
                }
                return body;
            }

            case TransformShape t:
                return LowerBrep(t.Child, m * t.Matrix);

            // The one place provenance is STAMPED. Every face the child's lowering
            // produced takes the tag, and inheritance carries it from there: a boolean
            // hands untouched faces through by reference and gives every fragment its
            // parent's tags (BrepFace.DescendsFrom), so a tagged boss stays named after
            // the union that swallowed half of it. Tags append rather than overwrite, so
            // nesting reads outermost-last.
            case TagShape tagged:
            {
                var solid = LowerBrep(tagged.Child, m);
                foreach (var face in solid.Faces)
                    face.AddProvenance(tagged.Label);
                return solid;
            }

            // CLONE at the source boundary. B-Rep booleans consume their inputs, and the
            // wrapped solid belongs to the caller and may be lowered any number of times
            // (a second representation, a re-render, two designs off one imported body).
            // Handing over the raw object poisons it silently: the counts survive, so the
            // solid still looks intact, but its coedges now belong to the first boolean's
            // faces and the next lowering is closed, Validate-clean and WRONG. Geometry is
            // shared by the clone, so this costs topology allocation only.
            case SourceShape { Geometry: BrepSolid solid } when IsIdentity(m):
                return solid.Clone();

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
        {
            // Mirrored similarity m = (m·FlipZ)·FlipZ: reflect the query point (exact,
            // an isometry) and place the proper remainder rigidly — never a bridge.
            if ((m * FlipZ).TryDecomposeRigidUniformScale(out var mq, out var mt, out double ms))
                return Place(
                    LowerImplicit(shape, Matrix4d.Identity, quality).Mirror(Vector3d.Zero, Vector3d.UnitZ),
                    mq, mt, ms);
            return BridgeToSdf(shape, m, quality);
        }

        switch (shape)
        {
            case BoxShape box:
                return Place(Sdf.Box(box.Extents.Size.X, box.Extents.Size.Y, box.Extents.Size.Z)
                        .Translate(box.Extents.Center),
                    rotation, translation, scale);
            case SphereShape sphere:
                return Place(Sdf.Sphere(sphere.Radius), rotation, translation, scale);
            case CylinderShape cyl:
                return Place(Sdf.Cylinder(cyl.Radius, cyl.Height), rotation, translation, scale);
            case TorusShape torus:
                return Place(Sdf.Torus(torus.MajorRadius, torus.MinorRadius), rotation, translation, scale);
            case ConeShape cone:
                return Place(Sdf.Cone(cone.BottomRadius, cone.TopRadius, cone.Height), rotation, translation, scale);

            case WedgeShape wedge:
                return LowerImplicit(wedge.Expanded, m, quality);

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

            case TwistExtrudeShape { IsTwisted: true } twisted:
                // The twist has no field form; the section-swept mesh is the geometry,
                // wrapped as an exact mesh SDF and placed rigidly (we are in the
                // decomposable branch; the sheared case bridged above).
                return Place(new MeshSdf(TwistedExtrusion.Build(twisted, quality)),
                    rotation, translation, scale);

            case ExtrudeShape or RevolveShape or SweepShape or RimShape or LoftShape
                or DraftShape or BrepShellShape or RoundEdgesShape or TwistExtrudeShape
                or SheetMetalShape or DirectEditShape:
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
                    .Intersect(Place(l.Field, rotation, translation, scale));

            case HullShape:
                // No SDF form: build the hull once in mesh land (with the transform
                // already applied — hulls commute with affine maps) and wrap it.
                return new MeshSdf(LowerMesh(shape, m, quality));

            case RemeshShape:
                // Likewise: a remesh is defined on triangles, so the field is one of the
                // remeshed mesh — deliberately NOT the child's field, which would silently
                // discard the very operation that was asked for.
                return new MeshSdf(LowerMesh(shape, m, quality));

            case SmoothedShape:
                // Same as remesh: fairing moves triangles, so the honest field is one of the
                // faired mesh, not the child's own (which carries the pre-fairing surface).
                return new MeshSdf(LowerMesh(shape, m, quality));

            case DrillShape drill:
                // Exact SDF subtraction has no coplanar-face degeneracy: no validation.
                return LowerImplicit(drill.Expanded, m, quality);

            case ThreadShape thread:
                return Place(thread.ToSdf(), rotation, translation, scale);

            case ThreadedHoleShape hole:
                // Exact SDF subtraction: pilot drill + thread tool, no coplanarity concerns.
                return LowerImplicit(hole.Expanded, m, quality);

            case MotionSweepShape sweep:
            {
                // The point of the implicit route: the child's field is lowered ONCE
                // and each sampled pose is a Rotate/Translate wrapper — N placements,
                // not N tessellations.
                var field = LowerImplicit(sweep.Child, Matrix4d.Identity, quality);
                var copies = new List<Sdf>(sweep.Poses.Count);
                foreach (var pose in sweep.Poses)
                {
                    var placed = m * pose;
                    if (!placed.TryDecomposeRigidUniformScale(out var q, out var t2, out double s2))
                        throw new NotSupportedException(
                            $"{sweep.Describe()}: a swept-volume pose must be rigid (or a uniform " +
                            "similarity) — a sheared pose cannot place a distance field.");
                    copies.Add(Place(field, q, t2, s2));
                }
                return copies.Count == 1 ? copies[0] : Sdf.Union(copies);
            }

            case TransformShape t:
                return LowerImplicit(t.Child, m * t.Matrix, quality);

            case TagShape tagged:
                return LowerImplicit(tagged.Child, m, quality);

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
        // Round-off-scale no-op elision (pure optimization: taking the wrap branch for
        // an identity transform would still be correct, just a redundant SDF node).
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
        return new MeshSdf(TransformMesh(
            SurfaceNets.Polygonize(sdf, quality.SdfResolution, options: quality.SurfaceNets), m));
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
                LowerImplicit(shape, Matrix4d.Identity, quality), quality.SdfResolution,
                options: quality.SurfaceNets);

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

            case HullShape hull:
            {
                var hullPoints = new List<Vector3d>();
                foreach (var operand in hull.Operands)
                    hullPoints.AddRange(LowerMesh(operand, m, quality).ToIndexed().Positions);
                return ConvexHull.Compute(hullPoints);
            }

            case RemeshShape remesh:
            {
                var source = LowerMesh(remesh.Child, m, quality);
                var options = remesh.Options with
                {
                    // The node's target length is in ITS OWN coordinates, so a scale above it
                    // scales the target too and the node means the same thing wherever it
                    // sits. A shear has no single factor; the volume-preserving equivalent is
                    // the honest stand-in, and remeshing a sheared body is approximate anyway.
                    TargetEdgeLength = remesh.Options.TargetEdgeLength * EquivalentScale(m),
                    // Without a target, smoothing is curvature flow and the model shrinks
                    // every pass. The child's own lowering is what "keep the shape" means here.
                    ProjectionTarget = remesh.Options.ProjectionTarget ?? new MeshProjectionTarget(source),
                };
                return Remesher.Remesh(source, options).Mesh;
            }

            case SmoothedShape smoothed:
                // TimeStep is dimensionless (λ = TimeStep·h̄²), so fairing is scale-free:
                // lowering the child with the transform baked in and smoothing gives the same
                // relative fairing at any scale, and the operator is intrinsic so it commutes
                // with any rigid placement. A closed solid has no boundary, so the whole
                // surface fairs — that is the operation (see Shape.Smoothed).
                return LaplacianMeshSmoother.Smooth(
                    LowerMesh(smoothed.Child, m, quality), smoothed.Options);

            case TwistExtrudeShape { IsTwisted: true } twisted:
            {
                // Direct section sweep (the node's plane is baked in); the accumulated
                // transform applies to the finished mesh, winding flips included.
                var swept = TwistedExtrusion.Build(twisted, quality);
                return IsIdentity(m) ? swept : TransformMesh(swept, m);
            }

            case TransformShape t:
                return LowerMesh(t.Child, m * t.Matrix, quality);

            case TagShape tagged:
                return LowerMesh(tagged.Child, m, quality);

            case SourceShape { Geometry: HalfEdgeMesh mesh }:
                return IsIdentity(m) ? mesh : TransformMesh(mesh, m);

            default:
                if (CanBrep(shape, m))
                    return Tessellate(LowerBrep(shape, m), quality);
                if (CanBrep(shape, Matrix4d.Identity))
                    return TransformMesh(Tessellate(LowerBrep(shape, Matrix4d.Identity), quality), m);
                return TransformMesh(
                    SurfaceNets.Polygonize(
                        LowerImplicit(shape, Matrix4d.Identity, quality), quality.SdfResolution,
                        options: quality.SurfaceNets),
                    m);
        }
    }

    /// <summary>
    /// Cosine threshold above which a planar face counts as parallel to a hole tool's flat
    /// bottom. <b>The angle it encodes is 0.081° (1.4142e-3 rad = acos(1 − 1e-6))</b> —
    /// worth writing down, because <c>1 − 1e-6</c> reads like a length tolerance and
    /// invites someone to "tighten" it to 1e-9 for consistency with the weld tier, which
    /// would actually narrow the guard to 0.0026° and let near-coplanar tools through into
    /// a boolean that cannot handle them. The cosine is quadratically flat near parallel,
    /// so the dot-product margin buys only its square root in angle.
    /// <para>Deliberately NOT widened, and the reason is now geometric rather than a
    /// deferral: two planes coincide iff they are parallel AND a point of one lies on the
    /// other, so this is the parallelism half of a coincidence test. Past the band the
    /// tool's flat bottom CROSSES the face in a chord instead of lying in it, which is an
    /// ordinary transversal boolean the kernel handles — widening would start refusing
    /// legal models. (The companion <see cref="CoplanarFaceDistance"/> check used to be
    /// ill-defined in exactly this band, which was the earlier reason not to touch the
    /// angle; see <see cref="BottomLiesInFacePlane"/> for what replaced it.)</para>
    /// </summary>
    private const double CoplanarFaceCosine = 1 - 1e-6;

    /// <summary>
    /// Distance within which the tool's flat bottom is treated as landing ON a face —
    /// applied to a genuine point-to-plane distance (see <see cref="BottomLiesInFacePlane"/>).
    /// Absolute by design (unlike an angle it cannot be made scale-free): it is a
    /// model-unit coincidence test against geometry the caller positioned in model units.
    /// <para>Deliberately one tier LOOSER than <c>CoplanarFaces.SamePlane</c>'s 1e-7 seam
    /// tier, which asks the same question of two faces inside the boolean. This is a
    /// refuse-EARLY guard: a conservative refusal costs the caller a nudge to the depth
    /// and names the reason, while a missed coincidence surfaces as a deep tessellation
    /// error ("Directed edge appears twice"), so the asymmetric cost justifies the
    /// asymmetric tolerance.</para>
    /// </summary>
    private const double CoplanarFaceDistance = 1e-6;

    /// <summary>
    /// Whether a hole tool's flat bottom — the disk through <paramref name="bottom"/>
    /// perpendicular to <paramref name="axis"/> — lies IN <paramref name="face"/>'s plane.
    /// The one rule both <see cref="ValidateDrillDepth(DrillShape, BrepSolid, in Matrix4d)"/>
    /// and <see cref="ValidateThreadedHoleDepth"/> ask, so the two cannot drift.
    /// <para><b>The offset is measured along the FACE's normal, never along the tool axis.</b>
    /// <c>IsPlanar</c> hands back an arbitrary in-plane point (a box cap's is a CORNER; a
    /// boolean fragment inherits its parent surface's, which can sit outside its own trim),
    /// and <c>n̂·(origin − bottom)</c> is the same number for every such point while
    /// <c>axis·(origin − bottom)</c> is not: at a tilt θ an in-plane offset L contributes
    /// L·sin θ, which reaches the whole 1e-6 threshold by L ≈ 7e-4 model units. The old
    /// axial form was therefore decided by WHERE the origin happened to be — on a 200×150
    /// plate whose bottom-cap origin is the corner (−100, −75, −10), a face tilted by
    /// 0.057° reads a 0.075 gap for a bottom sitting exactly in its plane, three decades
    /// past the threshold, so the guard silently did not fire; and symmetrically it
    /// refused a blind hole with 0.075 of real floor under it. Same shape as the recorded
    /// trap that a face must be located by <c>Bounds().Center</c>, and the same arithmetic
    /// <c>CoplanarFaces.SamePlane</c> already uses inside the boolean.</para>
    /// </summary>
    private static bool BottomLiesInFacePlane(BrepFace face, in Vector3d bottom, in Vector3d axis)
    {
        if (!face.IsPlanar(out var origin, out var normal) ||
            !normal.TryNormalize(Tolerance.Default, out var unit))
            return false;
        // Parallel to the tool's bottom disk...
        if (Math.Abs(unit.Dot(axis)) < CoplanarFaceCosine)
            return false;
        // ...and the bottom point lies on the face's plane.
        return Math.Abs(unit.Dot(origin - bottom)) <= CoplanarFaceDistance;
    }

    /// <summary>
    /// Recovers the per-point tool shapes from a drill's expansion, which
    /// <see cref="Shape.Drill"/> builds as the difference chain
    /// <c>((child − tool₀) − tool₁) …</c>. Returns false — and the caller falls back to
    /// lowering the expansion whole — if the chain is not that exact shape, so a future
    /// change to how the expansion is assembled degrades to the old behaviour instead of
    /// producing wrong geometry.
    /// </summary>
    private static bool TryPeelDrillTools(DrillShape drill, out List<Shape> tools)
    {
        tools = [];
        var node = drill.Expanded;
        while (!ReferenceEquals(node, drill.Child))
        {
            if (node is not BooleanShape { Op: BooleanOp.Difference } difference)
            {
                tools.Clear();
                return false;
            }
            tools.Add(difference.B);
            node = difference.A;
        }
        tools.Reverse(); // the chain is built outermost-last
        return true;
    }

    /// <summary>
    /// Runs an exact B-Rep boolean, replacing a <see cref="BrepBooleanException"/> with the
    /// same failure plus the implicit-route suggestion. EVERY boolean the compiler performs
    /// goes through here — a drilled hole that cannot be cut exactly is the same failure as
    /// a subtraction that cannot, and telling the caller about the escape hatch only on the
    /// operator path made the more common route the less helpful one.
    /// <para>
    /// The exact route deliberately does NOT fall back on its own: that would make
    /// <c>Explain(Representation.Brep)</c> lie (it reported Native) and would quietly
    /// downgrade an exact model to a polygonized one. The caller chooses.
    /// </para>
    /// </summary>
    private static BrepSolid WithImplicitRouteHint(Func<BrepSolid> boolean)
    {
        try
        {
            return boolean();
        }
        catch (BrepBooleanException ex)
        {
            throw new InvalidOperationException(
                $"{ex.Message} Model this shape through the implicit representation instead — " +
                "Shape.From(shape.ToImplicit()).ToMesh(quality) — which handles coplanar and " +
                "tangent configurations, at the cost of an approximated (polygonized) surface.",
                ex);
        }
    }

    /// <summary>
    /// Rejects a drill whose tool's flat bottom is coplanar with a planar face of the body,
    /// which is boolean input the exact path cannot take: without this guard the failure
    /// surfaces as a deep tessellation error ("Directed edge appears twice"). Follows the
    /// rim features' precedent of validating against the lowered solid. Only exact
    /// coplanarity (within tolerance) throws — a bottom safely short of the far face is a
    /// legitimate blind hole.
    /// <para><b>The coplanar-fusion tier does not retire this.</b> <c>CoplanarFaces</c> now
    /// fuses coincident PLANAR pairs in the boolean, so a flush cylinder tool would be fine
    /// — but a drill tool is ONE axis-touching revolve, so its flat bottom is a
    /// <c>RevolvedSurface</c> pole cap and <c>IsPlanar</c> reports false for it. The tier
    /// collects only <c>IsPlanar</c> faces (<c>CoplanarFaces.For</c>), so it structurally
    /// cannot see this pair, and the guard stays load-bearing. What DID change is the
    /// measure — see <see cref="BottomLiesInFacePlane"/>.</para>
    /// </summary>
    private static void ValidateDrillDepth(DrillShape drill, in Matrix4d m)
    {
        if (!CanBrep(drill.Child, m))
            return; // the expansion will produce its own conversion report
        ValidateDrillDepth(drill, LowerBrep(drill.Child, m), m);
    }

    /// <summary>
    /// The same check against an ALREADY LOWERED body — the form the drill lowering uses,
    /// so validation and the subtraction share one lowering instead of paying for two.
    /// </summary>
    private static void ValidateDrillDepth(DrillShape drill, BrepSolid body, in Matrix4d m)
    {
        var effective = m * drill.PlaneMatrix;
        var drillNormal = effective.TransformVector((0, 0, 1)).Normalized();

        // The shoulder plane (the deepest full-diameter point, always at −Depth) and, on a
        // tipped tool, the drill point's apex. A tool that ENDS on a face plane is
        // degenerate whichever of the two lands there: the shoulder makes the bore wall
        // and the face coincide along a circle, the apex touches it at a single point.
        double deepest = drill.ToolSilhouette[0].Axial;
        Span<double> ends = deepest < -drill.Depth ? [-drill.Depth, deepest] : [-drill.Depth];

        foreach (var point in drill.Points)
        {
            foreach (double end in ends)
            {
                var bottom = effective.TransformPoint(new Vector3d(point.X, point.Y, end));
                foreach (var face in body.Faces)
                {
                    if (BottomLiesInFacePlane(face, bottom, drillNormal))
                        throw new ArgumentException(
                            $"Drill depth {drill.Depth:g6} puts the tool's " +
                            (end < -drill.Depth ? "drill point" : "bottom") +
                            $" coplanar with a planar face of the body (hole at {point}); increase depth " +
                            "so the tool clears the far face, or reduce it for a blind hole.");
                }
            }
        }
    }

    /// <summary>
    /// Rejects a threaded hole whose tool's flat bottom is coplanar with a planar face
    /// of the body — the same guard as <see cref="ValidateDrillDepth"/> and through the
    /// same shared rule, so increase the depth past the far face for a through hole or
    /// reduce it for a blind one. (A thread tool's cap is a plane rather than a revolved
    /// pole, but the pair is still coincident-and-untraceable input here: it sits on a
    /// helical band, not on the plane-vs-plane geometry the fusion tier handles.)
    /// </summary>
    private static void ValidateThreadedHoleDepth(ThreadedHoleShape hole, BrepSolid body, in Matrix4d effective)
    {
        var drillNormal = effective.TransformVector((0, 0, 1)).Normalized();
        foreach (var point in hole.Points)
        {
            var bottom = effective.TransformPoint(new Vector3d(point.X, point.Y, -hole.Depth));
            foreach (var face in body.Faces)
            {
                if (BottomLiesInFacePlane(face, bottom, drillNormal))
                    throw new ArgumentException(
                        $"Threaded-hole depth {hole.Depth:g6} puts the tool's flat bottom coplanar with a " +
                        $"planar face of the body (hole at {point}); increase depth so the tool clears the " +
                        "far face, or reduce it for a blind hole.");
            }
        }
    }

    // ---------------------------------------------------------------------- helpers

    private static HalfEdgeMesh Tessellate(BrepSolid solid, MeshQuality quality)
    {
        var (segmentsPerCircle, curveSamples) = quality.ResolveSegments(solid);
        return BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples);
    }

    internal static HalfEdgeMesh TransformMesh(HalfEdgeMesh mesh, in Matrix4d m) =>
        mesh.Transformed(m);

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

    /// <summary>
    /// The single length scale a placement multiplies by — exact for a similarity, and the
    /// volume-preserving equivalent (cube root of |det|) otherwise, since a shear has no
    /// single factor and any answer there is a stand-in. Used where a node carries a LENGTH
    /// in its own coordinates (a remesh's target edge) and must mean the same thing wherever
    /// the graph places it.
    /// </summary>
    private static double EquivalentScale(in Matrix4d m)
    {
        if (m.TryDecomposeRigidUniformScale(out _, out _, out double scale))
            return scale;
        double determinant = Math.Abs(m.Determinant);
        return determinant > 0 ? Math.Cbrt(determinant) : 1.0;
    }

    private static readonly Matrix4d FlipZ = Matrix4d.CreateScale(new Vector3d(1, 1, -1));

    /// <summary>
    /// Like <see cref="Matrix4d.TryDecomposeRigidUniformScale"/> but also accepts
    /// improper (mirrored) similarities: m = T·R·S, or m = T·R·S·FlipZ when
    /// <paramref name="reflected"/>. Translation and scale are m's own; for reflected
    /// maps the rotation is the proper part of m·FlipZ (whose linear action matches m
    /// on the XY plane and negates Z).
    /// </summary>
    private static bool TryDecomposeSimilarity(
        in Matrix4d m, out Quaterniond rotation, out Vector3d translation, out double scale, out bool reflected)
    {
        if (m.TryDecomposeRigidUniformScale(out rotation, out translation, out scale))
        {
            reflected = false;
            return true;
        }
        reflected = true;
        return (m * FlipZ).TryDecomposeRigidUniformScale(out rotation, out translation, out scale);
    }

    private static readonly Matrix4d FlipY = Matrix4d.CreateScale(new Vector3d(1, -1, 1));

    /// <summary>Reflection in a sketch plane's own x — the half of a mirrored SHEET
    /// placement that the frame carries, the other half being the flange tree rebuilt the
    /// other way round. FlipX rather than <see cref="FlipZ"/> deliberately: it leaves the
    /// sheet's +Z, the face every bend line is quoted on, exactly where it was, so
    /// <c>SheetBendDirection</c> keeps meaning one thing.</summary>
    private static readonly Matrix4d FlipX = Matrix4d.CreateScale(new Vector3d(-1, 1, 1));

    /// <summary>
    /// Decomposes a thread's placement, mirrored placements included:
    /// m = T·R·S, or m = T·R·S·FlipY when <paramref name="reflected"/>.
    /// </summary>
    /// <remarks>
    /// <para>The reflected branch is what makes <c>Mirror(thread)</c> exact rather than
    /// Impossible, and it rests on one identity: <b>reflecting a right-hand thread across
    /// a plane CONTAINING its axis gives the left-hand thread with the SAME profile on
    /// the SAME frame</b> (the reflection maps the helical phase θ to −θ, which is
    /// exactly what negating the axial rate does). So writing m = (m·FlipY)·FlipY —
    /// where FlipY is that axis-containing reflection in the rod's own local frame, and
    /// m·FlipY is proper because both determinants are negative — leaves a plain rigid
    /// similarity to place a rod of the opposite handedness. Measured: the factory's
    /// left-hand rod matches the reflected right-hand one to 0 on the band surfaces and
    /// 9e-15 at the vertices (helix trigonometry).</para>
    /// <para>FlipY, not the <see cref="FlipZ"/> the implicit path uses: FlipZ reverses
    /// the rod's own axis, which would move the caps and reverse the profile's axial
    /// order, where FlipY leaves the axis and the profile alone. Any two reflections
    /// differ by a rotation, so choosing the convenient one costs nothing.</para>
    /// </remarks>
    private static bool TryDecomposeThreadPlacement(
        in Matrix4d m, out Quaterniond rotation, out Vector3d translation, out double scale, out bool reflected)
    {
        if (m.TryDecomposeRigidUniformScale(out rotation, out translation, out scale))
        {
            reflected = false;
            return true;
        }
        reflected = true;
        return (m * FlipY).TryDecomposeRigidUniformScale(out rotation, out translation, out scale);
    }

    private static void DecomposeSimilarity(
        in Matrix4d m, Shape shape,
        out Quaterniond rotation, out Vector3d translation, out double scale)
    {
        if (!TryDecomposeSimilarity(m, out rotation, out translation, out scale, out _))
            throw new ShapeConversionException(Classify(shape, TargetRep.Brep));
    }

    /// <summary>As above, but also reporting whether the similarity is improper
    /// (mirrored) — the nodes that must negate an axis need to know.</summary>
    private static void DecomposeSimilarity(
        in Matrix4d m, Shape shape,
        out Quaterniond rotation, out Vector3d translation, out double scale, out bool reflected)
    {
        if (!TryDecomposeSimilarity(m, out rotation, out translation, out scale, out reflected))
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
        // Deliberate bit-exact test: pure translations built by CreateTranslation have
        // exactly these entries; a near-translation correctly falls to the general path.
        return m.M11 == 1 && m.M12 == 0 && m.M13 == 0
            && m.M21 == 0 && m.M22 == 1 && m.M23 == 0
            && m.M31 == 0 && m.M32 == 0 && m.M33 == 1
            && m.M41 == 0 && m.M42 == 0 && m.M43 == 0 && m.M44 == 1;
    }
}
