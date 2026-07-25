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
                            // Hull nodes bridge as a mesh construction, not through the SDF.
                            Detail = entries[i].Node.StartsWith("Hull(", StringComparison.Ordinal)
                                ? "quickhull over the operands' tessellated mesh vertices"
                                : "polygonized from the signed distance field (Surface Nets)",
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

            case HullShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                    "a convex hull is a mesh construction (quickhull over tessellated vertices), and meshes cannot be imported into B-Rep"));
                break;

            case DrillShape drill:
                // A drill is its expansion (body minus revolved tools); the extra
                // far-face validation happens at lowering.
                ClassifyBrep(drill.Expanded, m, entries);
                break;
            case ThreadShape thread:
                // Native only for the unmodified basic profile under proper similarity
                // placements: the boolean-free helical sweep is exact, but chamfer
                // cones and distance-field profile offsets have no B-Rep counterpart
                // yet, and a mirrored thread is left-handed — each reported truthfully
                // rather than lowered to silently different geometry.
                if (!m.TryDecomposeRigidUniformScale(out _, out _, out _))
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "a mirrored, sheared, or non-uniformly scaled placement cannot re-place a helical thread exactly (a mirrored thread is left-handed)"));
                else if (thread.ChamferLength > 0)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "end chamfers have no B-Rep form yet (the 45° cone ∩ helical-band cut is future surface-intersection work) — pass chamferEnds: false, or use ToMesh/ToImplicit"));
                // Deliberate exact-zero test: "no clearance requested" is a user-parameter
                // contract (any nonzero offset means distance-field clearance), not a
                // geometric comparison.
                else if (thread.ProfileOffset != 0)
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Impossible,
                        "printing clearance offsets the profile as a distance field (reflex corners round into arcs) with no exact B-Rep counterpart — model clearance via ToMesh/ToImplicit"));
                else
                    entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Native,
                        "boolean-free helical sweep (SolidFactory.MakeThreadedRod); not STEP-exportable"));
                break;
            case ThreadedHoleShape hole:
                // Native via ONE combined tool per point: the thread form clipped at the
                // pilot radius (the pilot volume is part of the same helical rod, so no
                // coaxial tool∩bore tangency ever reaches the boolean). Clearance stays
                // Impossible for the same distance-field reason as ExternalThread.
                ClassifyBrep(hole.Child, m, entries);
                if (!m.TryDecomposeRigidUniformScale(out _, out _, out _))
                    entries.Add(new ConversionEntry(hole.Describe(), NodeSupport.Impossible,
                        "a mirrored, sheared, or non-uniformly scaled placement cannot re-place a helical thread exactly (a mirrored thread is left-handed)"));
                // Deliberate exact-zero test (see ThreadShape above).
                else if (hole.Clearance != 0)
                    entries.Add(new ConversionEntry(hole.Describe(), NodeSupport.Impossible,
                        "printing clearance offsets the profile as a distance field (reflex corners round into arcs) with no exact B-Rep counterpart — model clearance via ToMesh/ToImplicit"));
                else
                    entries.Add(new ConversionEntry(hole.Describe(), NodeSupport.Native,
                        "pilot + thread subtracted as one clipped-profile helical tool; the drilled faces split along exact spiral-arc chains"));
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
            case HullShape:
                entries.Add(new ConversionEntry(shape.Describe(), NodeSupport.Bridged,
                    "convex hull mesh (quickhull over tessellated vertices) wrapped in a mesh SDF"));
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
        ThreadedHoleShape h => UsesImplicitOnlyOps(h.Expanded),
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
                try
                {
                    return boolean.Op switch
                    {
                        BooleanOp.Union => BrepBoolean.Union(a, b),
                        BooleanOp.Intersection => BrepBoolean.Intersection(a, b),
                        _ => BrepBoolean.Difference(a, b),
                    };
                }
                catch (BrepBooleanException ex)
                {
                    // The exact route failed; hand the caller the approximate one rather
                    // than silently taking it for them. Falling back automatically would
                    // make Explain(Representation.Brep) lie (it reported Native) and would
                    // quietly downgrade an exact model to a polygonized one.
                    throw new InvalidOperationException(
                        $"{ex.Message} Model this shape through the implicit representation instead — " +
                        "Shape.From(shape.ToImplicit()).ToMesh(quality) — which handles coplanar and " +
                        "tangent configurations, at the cost of an approximated (polygonized) surface.",
                        ex);
                }
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
                    body = BrepBoolean.Difference(body, LowerBrep(tool, m));
                return body;
            }

            // Exact-zero user-parameter gate (matches the Explain classification above):
            // only the unmodified basic profile has an exact B-Rep form.
            case ThreadShape thread when thread.ProfileOffset == 0 && thread.ChamferLength <= 0:
            {
                // The ISO 68-1 basic profile, crest centered at phase 0 — the SAME
                // phase convention as Sdf.Thread (solid = {r ≤ R((z − P·θ/2π) mod P)}
                // with the crest flat at |w| ≤ P/16), so every representation of one
                // ThreadShape is the same geometry, not a rotated sibling. Corners run
                // bottom→top: crest flat (P/8 at the major radius), descending flank
                // (5P/16 axially), root flat (P/4 at the minor radius), ascending flank
                // wrapping to the next crest.
                Decompose(m, shape, out var rotation, out var translation, out double scale);
                var spec = thread.Spec;
                double pitch = spec.Pitch * scale;
                double rMajor = spec.MajorDiameter / 2 * scale;
                double rMinor = spec.MinorDiameter / 2 * scale;
                var frame = Frame3d.FromOrthonormal(
                    translation, rotation.Rotate(Vector3d.UnitX), rotation.Rotate(Vector3d.UnitY));
                return SolidFactory.MakeThreadedRod(
                [
                    new Vector2d(rMajor, -pitch / 16),
                    new Vector2d(rMajor, pitch / 16),
                    new Vector2d(rMinor, 3 * pitch / 8),
                    new Vector2d(rMinor, 5 * pitch / 8),
                ], pitch, thread.Length * scale, frame);
            }

            case ThreadedHoleShape hole when hole.Clearance == 0:
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
                Decompose(m, shape, out _, out _, out double scale);
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

                var effective = m * hole.PlaneMatrix;
                ValidateThreadedHoleDepth(hole, body, effective);

                // Same overshoot rule as the implicit tool; the geometry below the
                // surface — the actual hole — is identical either way.
                double overshoot = 0.05 * Math.Max(hole.Depth, spec.MajorDiameter);
                foreach (var point in hole.Points)
                {
                    // The tool advances DOWN the plane normal: frame (X, −Y, −Z), the
                    // exact π-rotation about X the implicit route uses (flipDown).
                    var origin = effective.TransformPoint(new Vector3d(point.X, point.Y, overshoot));
                    var xAxis = effective.TransformVector(Vector3d.UnitX).Normalized();
                    var yAxis = -effective.TransformVector(Vector3d.UnitY).Normalized();
                    var frame = Frame3d.FromOrthonormal(origin, xAxis, yAxis);
                    var tool = SolidFactory.MakeThreadedRod(
                        scaledCorners, pitch * scale, (hole.Depth + overshoot) * scale, frame);
                    body = BrepBoolean.Difference(body, tool);
                }
                return body;
            }

            case TransformShape t:
                return LowerBrep(t.Child, m * t.Matrix);

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
                return Place(Sdf.Box(box.Bounds.Size.X, box.Bounds.Size.Y, box.Bounds.Size.Z)
                        .Translate(box.Bounds.Center),
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

            case HullShape:
                // No SDF form: build the hull once in mesh land (with the transform
                // already applied — hulls commute with affine maps) and wrap it.
                return new MeshSdf(LowerMesh(shape, m, quality));

            case DrillShape drill:
                // Exact SDF subtraction has no coplanar-face degeneracy: no validation.
                return LowerImplicit(drill.Expanded, m, quality);

            case ThreadShape thread:
                return Place(thread.ToSdf(), rotation, translation, scale);

            case ThreadedHoleShape hole:
                // Exact SDF subtraction: pilot drill + thread tool, no coplanarity concerns.
                return LowerImplicit(hole.Expanded, m, quality);

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

            case HullShape hull:
            {
                var hullPoints = new List<Vector3d>();
                foreach (var operand in hull.Operands)
                    hullPoints.AddRange(LowerMesh(operand, m, quality).ToIndexed().Positions);
                return ConvexHull.Compute(hullPoints);
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
    /// Cosine threshold above which a planar face counts as parallel to a hole tool's flat
    /// bottom. <b>The angle it encodes is 0.081° (1.4142e-3 rad = acos(1 − 1e-6))</b> —
    /// worth writing down, because <c>1 − 1e-6</c> reads like a length tolerance and
    /// invites someone to "tighten" it to 1e-9 for consistency with the weld tier, which
    /// would actually narrow the guard to 0.0026° and let near-coplanar tools through into
    /// a boolean that cannot handle them. The cosine is quadratically flat near parallel,
    /// so the dot-product margin buys only its square root in angle.
    /// <para>Deliberately NOT widened while naming it: this test only decides whether the
    /// companion <see cref="CoplanarFaceDistance"/> check is meaningful, and for a face
    /// that is genuinely tilted relative to the axis that check measures the axial gap to
    /// an ARBITRARY point of the face's plane (whatever <c>IsPlanar</c> reports as the
    /// origin), so it is ill-defined exactly in the band a wider angle would admit.
    /// Widening needs its own coplanar-boolean evidence — see the backlog note.</para>
    /// </summary>
    private const double CoplanarFaceCosine = 1 - 1e-6;

    /// <summary>
    /// Distance within which the tool's flat bottom is treated as landing ON a face.
    /// Absolute by design (unlike an angle it cannot be made scale-free): it is a
    /// model-unit coincidence test against geometry the caller positioned in model units,
    /// and it sits at the inverse-evaluation tier because <c>origin</c> comes from the
    /// lowered solid's face geometry.
    /// </summary>
    private const double CoplanarFaceDistance = 1e-6;

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

        foreach (var point in drill.Points)
        {
            var bottom = effective.TransformPoint(new Vector3d(point.X, point.Y, -drill.Depth));
            foreach (var face in body.Faces)
            {
                if (!face.IsPlanar(out var origin, out var normal))
                    continue;
                if (Math.Abs(normal.Normalized().Dot(drillNormal)) < CoplanarFaceCosine)
                    continue;
                if (Math.Abs(drillNormal.Dot(origin - bottom)) <= CoplanarFaceDistance)
                    throw new ArgumentException(
                        $"Drill depth {drill.Depth:g6} puts the tool's flat bottom coplanar with a planar " +
                        $"face of the body (hole at {point}); increase depth so the tool clears the far " +
                        "face, or reduce it for a blind hole.");
            }
        }
    }

    /// <summary>
    /// Rejects a threaded hole whose tool's flat bottom is coplanar with a planar face
    /// of the body — the same v1 transversality contract as
    /// <see cref="ValidateDrillDepth"/>: coplanar face pairs are unsupported boolean
    /// input, so increase the depth past the far face for a through hole or reduce it
    /// for a blind one.
    /// </summary>
    private static void ValidateThreadedHoleDepth(ThreadedHoleShape hole, BrepSolid body, in Matrix4d effective)
    {
        var drillNormal = effective.TransformVector((0, 0, 1)).Normalized();
        foreach (var point in hole.Points)
        {
            var bottom = effective.TransformPoint(new Vector3d(point.X, point.Y, -hole.Depth));
            foreach (var face in body.Faces)
            {
                if (!face.IsPlanar(out var origin, out var normal))
                    continue;
                if (Math.Abs(normal.Normalized().Dot(drillNormal)) < CoplanarFaceCosine)
                    continue;
                if (Math.Abs(drillNormal.Dot(origin - bottom)) <= CoplanarFaceDistance)
                    throw new ArgumentException(
                        $"Threaded-hole depth {hole.Depth:g6} puts the tool's flat bottom coplanar with a " +
                        $"planar face of the body (hole at {point}); increase depth so the tool clears the " +
                        "far face, or reduce it for a blind hole.");
            }
        }
    }

    // ---------------------------------------------------------------------- helpers

    private static HalfEdgeMesh Tessellate(BrepSolid solid, MeshQuality quality) =>
        BRepTessellator.Tessellate(solid, quality.SegmentsPerCircle, quality.CurveSamples);

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

    private static void DecomposeSimilarity(
        in Matrix4d m, Shape shape,
        out Quaterniond rotation, out Vector3d translation, out double scale)
    {
        if (!TryDecomposeSimilarity(m, out rotation, out translation, out scale, out _))
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
