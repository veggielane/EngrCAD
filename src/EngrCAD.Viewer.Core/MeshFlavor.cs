using EngrCAD.BRep;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The secondary progress line: what the kernel is actually doing to the part being
/// meshed. The primary line stays the honest count ("meshing 'sketch' - 2 of 3 parts");
/// this one names the route, chosen from the part's geometry rather than rotated at
/// random, so every message is true of the part it appears under.
/// </summary>
/// <remarks>
/// Yes, one of them is the SimCity 2000 joke — and it is the literal truth here: a
/// sketch's lines, arcs and Beziers become exact <c>NurbsCurve</c> profiles on the way
/// to a B-Rep (every glyph outline too), so a part built from sketches really does
/// have splines to reticulate. It is only ever shown for those parts.
/// </remarks>
public static class MeshFlavor
{
    /// <summary>Sketch-derived geometry: profiles lower to exact NURBS curves.</summary>
    public const string Splines = "Reticulating splines...";

    /// <summary>An SDF part: Surface Nets over the distance field.</summary>
    public const string Field = "Polygonizing the field...";

    /// <summary>A Shape whose display mesh comes from an exact solid.</summary>
    public const string Lowering = "Lowering to B-Rep...";

    /// <summary>A solid that only needs tessellating.</summary>
    public const string Tessellating = "Tessellating surfaces...";

    /// <summary>Mesh geometry: nothing to compute but the display buffers.</summary>
    public const string Mesh = "Preparing the mesh...";

    /// <summary>The line to show while <paramref name="part"/> is being meshed. Cheap
    /// (graph inspection only — no lowering, no tessellation); called on the worker
    /// thread just before the part's own work starts.</summary>
    public static string For(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Geometry switch
        {
            Sdf => Field,
            HalfEdgeMesh => Mesh,
            BrepSolid solid => HasNurbs(solid) ? Splines : Tessellating,
            Shape shape => BuiltFromSketches(part) ? Splines
                : shape.CanConvertTo(TargetRep.Brep) ? Lowering
                : Field,
            _ => Tessellating,
        };
    }

    /// <summary>Whether the part's construction tree contains a sketch — the honest test
    /// for "there are splines in here", since sketch segments become NURBS profiles.
    /// The tree is built once and cached per part, and takes its own lock, so this never
    /// waits on the mesh being produced.</summary>
    private static bool BuiltFromSketches(Part part) =>
        part.ConstructionTree() is { } tree
        && tree.Flatten().Any(node => node.Kind == ConstructionNodeKind.Sketch);

    /// <summary>Whether an exact solid carries NURBS geometry: a NURBS surface, a
    /// generator/path curve that is one (extrusions, revolves and sweeps are built on
    /// their profile curves), or an edge that stayed a spline.</summary>
    private static bool HasNurbs(BrepSolid solid)
    {
        foreach (var face in solid.Faces)
        {
            if (IsSpline(face.Surface))
                return true;
            foreach (var loop in face.Loops)
            {
                foreach (var coedge in loop.Coedges)
                {
                    if (Underlying(coedge.Edge.Curve) is NurbsCurve)
                        return true;
                }
            }
        }
        return false;
    }

    private static bool IsSpline(Surface surface) => surface switch
    {
        NurbsSurface => true,
        ExtrudedSurface extruded => Underlying(extruded.Generator) is NurbsCurve,
        RevolvedSurface revolved => Underlying(revolved.Generator) is NurbsCurve,
        SweptSurface swept => Underlying(swept.Generator) is NurbsCurve
                           || Underlying(swept.Path) is NurbsCurve,
        _ => false,
    };

    /// <summary>The curve under any reverse/transform wrappers (the wrappers keep the
    /// spline a spline).</summary>
    private static Curve3d Underlying(Curve3d curve) => curve switch
    {
        ReversedCurve reversed => Underlying(reversed.Underlying),
        TransformedCurve transformed => Underlying(transformed.Underlying),
        _ => curve,
    };
}
