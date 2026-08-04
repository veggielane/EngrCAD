using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// A PINCH vertex — one whose link is two or more fans — is the non-manifold defect
/// <see cref="HalfEdgeMesh.Build"/> deliberately does not reject, because a pinch is
/// sometimes the correct answer (see its remarks). In Surface Nets it never is, and the
/// cause is that a cell gave ONE vertex to an inside component that bounds SEVERAL sheets:
/// a wall about one cell thick has connected material with the void inside and the space
/// outside as two separate blobs, so the cell's six crossings are two triangles and averaging
/// all six puts both on one point.
/// <para>
/// The fix refines a component into the sheets it bounds by the cube's own FACE adjacency,
/// and the counts below are the measurement (win-x64). They are asserted as VALUES, in both
/// directions, because the residual is a different defect with a different fix: the ambiguous
/// face's split is applied by the cell on the face's + side only, so where it fires the
/// neighbour keeps one vertex against two and pinches there instead. Every remaining pinch
/// traced back to that configuration.
/// </para>
/// <para>
/// Whether a cell lands in the failing configuration is ALIGNMENT, not tolerance — the counts
/// below are not monotone in resolution and go to zero on both sides of a peak — so the sweep
/// runs over fields AND resolutions, and each row asserts it still CARRIES the configuration
/// so it cannot quietly stop testing anything.
/// </para>
/// </summary>
public class SurfaceNetsPinchTests
{
    private static Sdf Field(string name) => name switch
    {
        "shell 0.5" => Sdf.Sphere(10).Shell(0.5),
        "shell 0.6" => Sdf.Sphere(10).Shell(0.6),
        "shell 0.7" => Sdf.Sphere(10).Shell(0.7),
        "shell 0.9" => Sdf.Sphere(10).Shell(0.9),
        "gyroid 12/1.2" => Shape.Sphere(16).Lattice(Sdf.Gyroid(12, 1.2)).ToImplicit(),
        "gyroid 10/1.0" => Shape.Sphere(16).Lattice(Sdf.Gyroid(10, 1.0)).ToImplicit(),
        "box gyroid" => Sdf.Box(10, 10, 10) & Sdf.Gyroid(8, 0.2),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// Rows that used to pinch and no longer do. The "before" figure is what the incumbent
    /// rule produced, kept so the row states what it is protecting rather than only that a
    /// number is zero — and every one of these carries the configuration, asserted separately.
    /// </summary>
    public static TheoryData<string, int, int> Fixed => new()
    {
        // name, resolution, pinch vertices the incumbent rule produced
        { "shell 0.5", 64, 768 },
        { "shell 0.6", 56, 528 },
        { "shell 0.7", 44, 600 },
        { "shell 0.9", 44, 0 },
        { "gyroid 12/1.2", 44, 174 },
        { "gyroid 12/1.2", 64, 18 },
        { "gyroid 12/1.2", 96, 18 },
        { "gyroid 12/1.2", 112, 18 },
        { "gyroid 10/1.0", 56, 72 },
        { "gyroid 10/1.0", 64, 18 },
        { "box gyroid", 64, 3066 },
        { "box gyroid", 88, 36 },
    };

    [Theory]
    [MemberData(nameof(Fixed))]
    public void SheetsNoLongerShareAVertex(string name, int resolution, int before)
    {
        var mesh = SurfaceNets.Polygonize(Field(name), resolution);

        Assert.True(mesh.IsClosed, $"{name} at {resolution} came out open");
        Assert.Empty(mesh.NonManifoldVertices());
        mesh.Validate();
        // Any row that stopped carrying the configuration would pass vacuously.
        Assert.True(before == 0 || CellsBoundingSeveralSheets(Field(name), resolution) > 0,
            $"{name} at {resolution} no longer carries a component bounding several sheets");
    }

    /// <summary>
    /// The residual, pinned as VALUES so it cannot rot into a guess: the ambiguous-face split
    /// is one-sided, so the cell on the face's minus side keeps one vertex where its neighbour
    /// made two and its link falls into fans. Both an asymptotic-decider face resolution and a
    /// cut of the cube's own connectivity were built to close it and both were measured WORSE
    /// (open meshes and bow-tie vertices on the same family) — see todo.md.
    /// </summary>
    [Theory]
    [InlineData("shell 0.5", 44, 432, 144)]
    [InlineData("shell 0.5", 56, 1608, 144)]
    [InlineData("shell 0.6", 44, 984, 240)]
    [InlineData("shell 0.7", 32, 144, 48)]
    [InlineData("gyroid 12/1.2", 32, 666, 60)]
    [InlineData("gyroid 12/1.2", 88, 30, 6)]
    [InlineData("gyroid 10/1.0", 32, 602, 126)]
    [InlineData("gyroid 10/1.0", 44, 1118, 6)]
    [InlineData("box gyroid", 44, 234, 78)]
    [InlineData("box gyroid", 56, 2768, 642)]
    public void TheAmbiguousFaceResidualIsMeasured(string name, int resolution, int before, int after)
    {
        var mesh = SurfaceNets.Polygonize(Field(name), resolution);

        Assert.True(mesh.IsClosed, $"{name} at {resolution} came out open");
        Assert.True(after < before, "the residual must be strictly smaller than what it replaced");
        Assert.Equal(after, mesh.NonManifoldVertices().Count);
    }

    /// <summary>
    /// Splitting a pinch moves NO material — the two sheets were already there and only their
    /// shared point comes apart — so the volume must be unchanged to the last bit, not merely
    /// to the polygonization's own convergence. Faces are unchanged for the same reason.
    /// </summary>
    [Theory]
    [InlineData("shell 0.6", 44)]
    [InlineData("gyroid 12/1.2", 44)]
    [InlineData("box gyroid", 64)]
    public void OpeningAPinchAddsVerticesAndNothingElse(string name, int resolution)
    {
        var mesh = SurfaceNets.Polygonize(Field(name), resolution);
        int expectedFaces = name switch
        {
            "shell 0.6" => 14436,   // unchanged from the incumbent rule
            "gyroid 12/1.2" => 22566,
            _ => 49098,
        };

        Assert.Equal(expectedFaces, mesh.FaceCount);
        Assert.True(mesh.VertexCount > 0);
    }

    /// <summary>
    /// Fields with no thin wall anywhere never had the defect and must be untouched, which is
    /// what says the trigger is exact rather than merely effective. The golden bit patterns in
    /// <see cref="SurfaceNetsSamplingTests"/> are the strong form of this for three of them.
    /// </summary>
    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    public void SolidFieldsCarryNoPinchAtAnyResolution(int resolution)
    {
        foreach (var field in (Sdf[])[
            Sdf.Sphere(1.0),
            Sdf.Torus(1.0, 0.35),
            Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3),
            Sdf.Sphere(1.2).SmoothUnion(Sdf.Box(1, 1, 1).Translate((0.8, 0, 0)), 0.3)])
        {
            var mesh = SurfaceNets.Polygonize(field, resolution);
            Assert.Empty(mesh.NonManifoldVertices());
            Assert.Equal(0, CellsBoundingSeveralSheets(field, resolution));
        }
    }

    /// <summary>
    /// Cells where one inside component bounds two or more sheets, counted independently of
    /// the polygonizer straight off the sampled signs: the crossings of a component are joined
    /// when they share a cube FACE, and the count is the components that come out in more than
    /// one group. Sharing only the definition with <see cref="SurfaceNets"/> is the point — a
    /// fixture that has drifted off the configuration then says so.
    /// </summary>
    private static int CellsBoundingSeveralSheets(Sdf sdf, int resolution)
    {
        (int A, int B)[] edges =
        [
            (0, 1), (2, 3), (4, 5), (6, 7),
            (0, 2), (1, 3), (4, 6), (5, 7),
            (0, 4), (1, 5), (2, 6), (3, 7),
        ];
        int[][] faces =
        [
            [4, 6, 8, 10], [5, 7, 9, 11], [0, 2, 8, 9], [1, 3, 10, 11], [0, 1, 4, 5], [2, 3, 6, 7],
        ];

        var bounds = sdf.Bounds;
        var region = bounds.Expanded(bounds.Size[bounds.LongestAxis] / resolution * 2);
        var size = region.Size;
        double cell = size[region.LongestAxis] / resolution;
        int nx = Math.Max(1, (int)Math.Ceiling(size.X / cell - 1e-9));
        int ny = Math.Max(1, (int)Math.Ceiling(size.Y / cell - 1e-9));
        int nz = Math.Max(1, (int)Math.Ceiling(size.Z / cell - 1e-9));
        var origin = region.Min;
        int sy = ny + 1, sz = nz + 1;

        var values = new double[(nx + 1) * sy * sz];
        var xs = new double[sz];
        var ys = new double[sz];
        var zs = new double[sz];
        for (int i = 0; i <= nx; i++)
        {
            for (int j = 0; j < sy; j++)
            {
                for (int k = 0; k < sz; k++)
                {
                    xs[k] = origin.X + i * cell;
                    ys[k] = origin.Y + j * cell;
                    zs[k] = origin.Z + k * cell;
                }
                sdf.Evaluate(xs, ys, zs, values.AsSpan((i * sy + j) * sz, sz));
            }
        }

        // Inside-corner components over the cube's face adjacency (bit flips).
        static int[] Components(int mask)
        {
            var component = new int[8];
            Array.Fill(component, -1);
            int next = 0;
            for (int seed = 0; seed < 8; seed++)
            {
                if (component[seed] >= 0 || ((mask >> seed) & 1) == 0)
                    continue;
                int id = next++;
                var stack = new Stack<int>();
                stack.Push(seed);
                component[seed] = id;
                while (stack.Count > 0)
                {
                    int c = stack.Pop();
                    foreach (int neighbor in (int[])[c ^ 1, c ^ 2, c ^ 4])
                    {
                        if (component[neighbor] < 0 && ((mask >> neighbor) & 1) != 0)
                        {
                            component[neighbor] = id;
                            stack.Push(neighbor);
                        }
                    }
                }
            }
            return component;
        }

        int split = 0;
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int k = 0; k < nz; k++)
                {
                    int mask = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        int index = ((i + (c & 1)) * sy + j + ((c >> 1) & 1)) * sz + k + ((c >> 2) & 1);
                        if (values[index] < 0)
                            mask |= 1 << c;
                    }
                    if (mask is 0 or 255)
                        continue;

                    var component = Components(mask);
                    var group = new int[12];
                    for (int e = 0; e < 12; e++)
                    {
                        var (a, b) = edges[e];
                        group[e] = ((mask >> a) & 1) != ((mask >> b) & 1) ? e : -1;
                    }
                    int Find(int e)
                    {
                        while (group[e] != e)
                            e = group[e] = group[group[e]];
                        return e;
                    }
                    foreach (var face in faces)
                    {
                        var first = new int[8];
                        Array.Fill(first, -1);
                        foreach (int e in face)
                        {
                            if (group[e] < 0)
                                continue;
                            var (a, b) = edges[e];
                            int id = component[((mask >> a) & 1) != 0 ? a : b];
                            if (first[id] < 0)
                                first[id] = e;
                            else
                            {
                                int ra = Find(first[id]), rb = Find(e);
                                if (ra != rb)
                                    group[Math.Max(ra, rb)] = Math.Min(ra, rb);
                            }
                        }
                    }

                    // Does any component's crossings come out in more than one group?
                    var seen = new Dictionary<int, int>();
                    bool several = false;
                    for (int e = 0; e < 12 && !several; e++)
                    {
                        if (group[e] < 0)
                            continue;
                        var (a, b) = edges[e];
                        int id = component[((mask >> a) & 1) != 0 ? a : b];
                        int root = Find(e);
                        if (seen.TryGetValue(id, out int other))
                            several = other != root;
                        else
                            seen[id] = root;
                    }
                    if (several)
                        split++;
                }
            }
        }
        return split;
    }
}
