using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Interop;

/// <summary>
/// Feature-edge extraction from the ACTUAL B-Rep edges of a solid — the exact-geometry
/// alternative to mesh-dihedral extraction (<c>MeshFeatureEdges</c> in EngrCAD.Mesh)
/// for display overlays: a rim circle sampled here stays a smooth circle at any mesh
/// tessellation, because the segments come from the edge's curve, not from the coarse
/// triangle mesh. Edges are classified sharp/smooth by comparing the adjacent faces'
/// outward normals at probe points along the edge (same 30-degree default as the mesh
/// extractor), so smooth seams — the wrap-split sub-band junctions of a bore wall,
/// tangent fillet junction arcs, sphere generator seams — are correctly omitted while
/// real creases are kept.
/// </summary>
public static class BrepFeatureEdges
{
    /// <summary>
    /// Segments (endpoint pairs, in the solid's own coordinates) of every sharp or
    /// boundary edge, each edge sampled by the tessellator's own edge-sampling rules
    /// (<paramref name="segmentsPerCircle"/> for circles, angular for helices,
    /// exact vertices for tracer polylines, 2 points for lines,
    /// <paramref name="curveSamples"/> otherwise). Defaults are display resolution —
    /// deliberately finer than typical mesh quality, which is the point. An edge is
    /// smooth (omitted) when the two adjacent faces' outward normals agree within
    /// <paramref name="sharpAngleRadians"/> at all probe points, or when both uses
    /// belong to one periodic face (a seam). Boundary/non-manifold edges and edges
    /// whose normals cannot be probed are kept — draw rather than hide.
    /// </summary>
    public static List<(Vector3d A, Vector3d B)> Extract(
        BrepSolid solid, int segmentsPerCircle = 96, int curveSamples = 48,
        double sharpAngleRadians = Math.PI / 6)
    {
        // One pass over the topology: every face using each edge (a face can appear
        // twice — a periodic face's seam edge).
        var facesOf = new Dictionary<BrepEdge, List<BrepFace>>();
        foreach (var face in solid.Faces)
        {
            foreach (var loop in face.Loops)
            {
                foreach (var coedge in loop.Coedges)
                {
                    if (!facesOf.TryGetValue(coedge.Edge, out var faces))
                        facesOf[coedge.Edge] = faces = new List<BrepFace>(2);
                    faces.Add(face);
                }
            }
        }

        double smoothDot = Math.Cos(sharpAngleRadians);
        var segments = new List<(Vector3d A, Vector3d B)>();
        foreach (var (edge, faces) in facesOf)
        {
            if (!IsSharp(edge, faces, smoothDot))
                continue;
            var polyline = BRepTessellator.SampleEdge(edge, segmentsPerCircle, curveSamples);
            for (int i = 0; i + 1 < polyline.Count; i++)
                segments.Add((polyline[i], polyline[i + 1]));
            // Closed-edge polylines omit the duplicate closing point; close the loop.
            if (edge.IsClosedEdge && polyline.Count > 1)
                segments.Add((polyline[^1], polyline[0]));
        }
        return segments;
    }

    /// <summary>Sharpness of one edge from its using faces' outward normals.</summary>
    private static bool IsSharp(BrepEdge edge, List<BrepFace> faces, double smoothDot)
    {
        if (faces.Count != 2)
            return true;                        // boundary or non-manifold: draw it
        if (ReferenceEquals(faces[0], faces[1]))
            return false;                       // seam of one periodic face: smooth by construction

        // Probe interior points (never the endpoints — those are shared corner
        // vertices where inverse evaluation is ambiguous). Multiple probes catch
        // edges whose dihedral varies along their length.
        ReadOnlySpan<double> fractions = [0.25, 0.5, 0.75];
        foreach (double f in fractions)
        {
            var point = edge.Curve.PointAt(edge.Domain.ParameterAt(f));
            try
            {
                if (faces[0].NormalAt(point).Dot(faces[1].NormalAt(point)) < smoothDot)
                    return true;
            }
            catch (ArgumentException)
            {
                // Inverse evaluation rejected the probe (e.g. a tracer polyline's
                // mid-chord point sits off-surface — they are exact only at their
                // vertices). Boolean intersection edges are almost always sharp;
                // draw rather than hide.
                return true;
            }
        }
        return false;
    }
}
