using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// One edge-connected face component of a mesh: its face selection plus aggregate metrics.
/// Produced by <see cref="MeshConnectedComponents.Find"/>.
/// </summary>
public sealed class MeshComponent
{
    /// <summary>The component's faces (every face reachable through shared edges).</summary>
    public MeshFaceSelection Faces { get; }

    public int FaceCount => Faces.Count;

    /// <summary>Sum of the component's face areas.</summary>
    public double Area { get; }

    /// <summary>
    /// Signed volume of the component's faces by the divergence theorem (same convention
    /// as <see cref="HalfEdgeMesh.SignedVolume"/>); the enclosed volume when
    /// <see cref="IsClosed"/> and outward-wound.
    /// </summary>
    public double SignedVolume { get; }

    /// <summary>True when no face of the component borders the void (the component is watertight).</summary>
    public bool IsClosed { get; }

    internal MeshComponent(MeshFaceSelection faces, double area, double signedVolume, bool isClosed)
    {
        Faces = faces;
        Area = area;
        SignedVolume = signedVolume;
        IsClosed = isClosed;
    }

    /// <summary>
    /// Extracts the component as its own mesh (<see cref="MeshFaceSelection.ToMesh"/>).
    /// Always manifold: the source mesh forbids bow-tie vertices, so all faces around any
    /// vertex are edge-connected and land in one component — components never share vertices.
    /// </summary>
    public HalfEdgeMesh ToMesh() => Faces.ToMesh();
}

/// <summary>
/// Edge-connected face components (g3 <c>MeshConnectedComponents</c>): flood fill over
/// face adjacency, seeded at ascending face index (so component order is deterministic —
/// the component containing face 0 comes first). The shape-of-API the mesh-repair wave
/// builds on: count/areas/volumes per component and per-component extraction.
/// </summary>
public static class MeshConnectedComponents
{
    /// <summary>Finds all edge-connected face components with their metrics.</summary>
    public static IReadOnlyList<MeshComponent> Find(HalfEdgeMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var componentOf = new int[mesh.FaceCount];
        Array.Fill(componentOf, -1);
        var components = new List<MeshComponent>();
        var stack = new Stack<int>();

        for (int seed = 0; seed < mesh.FaceCount; seed++)
        {
            if (componentOf[seed] >= 0)
                continue;
            int id = components.Count;
            var faces = new List<int>();
            double area = 0;
            double signedVolume = 0;
            bool closed = true;

            stack.Push(seed);
            componentOf[seed] = id;
            while (stack.Count > 0)
            {
                int f = stack.Pop();
                faces.Add(f);
                var face = mesh.GetFace(f);
                area += face.Area;
                signedVolume += FaceDivergenceVolume(mesh, f);
                foreach (var he in face.HalfEdges())
                {
                    var twin = he.Twin;
                    if (twin.IsBoundary)
                    {
                        closed = false;
                        continue;
                    }
                    int neighbor = twin.Face.Index;
                    if (componentOf[neighbor] < 0)
                    {
                        componentOf[neighbor] = id;
                        stack.Push(neighbor);
                    }
                }
            }

            components.Add(new MeshComponent(
                MeshFaceSelection.FromIndices(mesh, faces), area, signedVolume, closed));
        }
        return components;
    }

    /// <summary>Extracts every component as its own mesh, in <see cref="Find"/> order.</summary>
    public static List<HalfEdgeMesh> Separate(HalfEdgeMesh mesh) =>
        [.. Find(mesh).Select(c => c.ToMesh())];

    /// <summary>One face's contribution to the divergence-theorem signed volume
    /// (fan-triangulated, matching <see cref="HalfEdgeMesh.SignedVolume"/>).</summary>
    private static double FaceDivergenceVolume(HalfEdgeMesh mesh, int faceIndex)
    {
        var vertices = mesh.GetFace(faceIndex).Vertices().Select(v => v.Position).ToList();
        double volume = 0;
        int apex = PolygonFan.Apex(vertices);
        var p0 = vertices[apex];
        for (int i = 1; i < vertices.Count - 1; i++)
        {
            volume += p0.Dot(vertices[PolygonFan.Corner(apex, vertices.Count, i)]
                .Cross(vertices[PolygonFan.Corner(apex, vertices.Count, i + 1)]));
        }
        return volume / 6.0;
    }
}
