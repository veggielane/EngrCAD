using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// <see cref="SurfaceNets.Polygonize"/> is documented as the MANIFOLD variant, so manifold
/// output is its contract rather than a best effort. <see cref="HalfEdgeMesh.Build"/> is the
/// enforcement — it refuses a duplicated directed edge — which is why most assertions here
/// are simply that polygonizing returns at all.
/// <para>
/// The configuration that broke the contract is a grid FACE whose four corners alternate in
/// sign, the two inside ones diagonally opposite: Marching Cubes' <em>ambiguous</em> face.
/// All four of its edges cross, so it carries two quad edges between the same pair of cells,
/// and when BOTH cells join its two inside corners into one component those two quads land
/// on the same directed edge. It appears wherever a wall is about one cell thick — a gyroid
/// lattice found it first, and a plain thin spherical shell reproduces it just as well, so
/// the gap was never about lattices.
/// </para>
/// </summary>
public class SurfaceNetsManifoldTests
{
    /// <summary>The exact case that was reported: it threw "Directed edge 4954 → 4967 appears twice".</summary>
    private static Sdf ReportedLattice() => Shape.Sphere(16).Lattice(Sdf.Gyroid(12, 1.2)).ToImplicit();

    [Fact]
    public void TheReportedGyroidLattice_Polygonizes()
    {
        var mesh = SurfaceNets.Polygonize(ReportedLattice(), 88);

        Assert.True(mesh.IsClosed);
        // 99 722 before an inside component was given one vertex per SHEET it bounds: 24 of
        // its vertices were shared by two sheets and became 48. The FACE count is untouched,
        // which is the shape of that fix — a pinch is a point, so opening it moves no material.
        Assert.Equal(99746, mesh.VertexCount);
        Assert.Equal(99846, mesh.FaceCount);
    }

    /// <summary>
    /// The other fields that reproduced the same duplicated directed edge before the fix.
    /// Every one of these threw; the thin shell is the important entry, because it is not a
    /// lattice and shows the gap belonged to the rule, not to the input.
    /// </summary>
    public static TheoryData<string, int> Hostile => new()
    {
        { "gyroid 12/1.2", 88 },   // the reported case
        { "gyroid 10/1.0", 88 },
        { "gyroid 6/1.5", 88 },
        { "thin shell", 44 },
    };

    private static Sdf Field(string name) => name switch
    {
        "gyroid 12/1.2" => ReportedLattice(),
        "gyroid 10/1.0" => Shape.Sphere(16).Lattice(Sdf.Gyroid(10, 1.0)).ToImplicit(),
        "gyroid 6/1.5" => Shape.Sphere(16).Lattice(Sdf.Gyroid(6, 1.5)).ToImplicit(),
        // A wall about one cell thick: r = 10 with a 0.6 wall at resolution 44 makes the
        // sampled shell straddle single cells, which is exactly what puts inside corners on
        // both sides of an ambiguous face.
        "thin shell" => Sdf.Sphere(10).Shell(0.6),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void HostileFields_PolygonizeIntoClosedManifoldMeshes(string name, int resolution)
    {
        var mesh = SurfaceNets.Polygonize(Field(name), resolution);

        Assert.True(mesh.IsClosed, $"{name} came out open");
        Assert.True(mesh.FaceCount > 0);
    }

    /// <summary>
    /// The sweep that would have caught this originally: whether an ambiguous face lands with
    /// both cells joined is an ALIGNMENT question, not a tolerance one, so it appears and
    /// disappears as the grid moves under the surface. The reported lattice only fails at
    /// resolution 88 of the ten tried — which is why one fixture at one resolution is not
    /// evidence about a contract, and a family is.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.6)]
    [InlineData(0.7)]
    [InlineData(0.9)]
    public void ThinShells_AreManifoldAtEveryResolution(double thickness)
    {
        var field = Sdf.Sphere(10).Shell(thickness);
        for (int resolution = 24; resolution <= 52; resolution += 4)
        {
            var mesh = SurfaceNets.Polygonize(field, resolution);
            Assert.True(mesh.IsClosed, $"thickness {thickness} at resolution {resolution} came out open");
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(56)]
    [InlineData(72)]
    [InlineData(88)]
    [InlineData(96)]
    public void TheReportedLattice_IsManifoldAtEveryResolution(int resolution)
    {
        var mesh = SurfaceNets.Polygonize(ReportedLattice(), resolution);

        Assert.True(mesh.IsClosed);
    }

    /// <summary>
    /// A fixture that has stopped reproducing its bug passes for the wrong reason, so the two
    /// headline cases are checked to still CARRY the configuration: an ambiguous grid face
    /// whose two adjacent cells both join its diagonal inside pair. Counted independently of
    /// the polygonizer, straight off the sampled signs.
    /// </summary>
    [Theory]
    [InlineData("gyroid 12/1.2", 88, 6)]
    [InlineData("thin shell", 44, 240)]
    public void TheFixturesStillCarryTheFailingConfiguration(string name, int resolution, int expected) =>
        Assert.Equal(expected, JoinedAmbiguousFaces(Field(name), resolution));

    /// <summary>
    /// Interior grid faces whose inside corners are exactly a diagonal pair AND whose two
    /// adjacent cells each join that pair over the cube's face adjacency — the configuration
    /// that duplicates a directed edge. Each face is counted once, from its + side.
    /// </summary>
    private static int JoinedAmbiguousFaces(Sdf sdf, int resolution)
    {
        // (face corner mask, the diagonal pair), matching SurfaceNets' own table.
        (int Face, int A, int B)[] diagonals =
        [
            (0x55, 0, 6), (0x55, 2, 4), (0x33, 0, 5), (0x33, 1, 4), (0x0F, 0, 3), (0x0F, 1, 2),
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

        int Mask(int i, int j, int k)
        {
            int mask = 0;
            for (int c = 0; c < 8; c++)
            {
                int index = ((i + (c & 1)) * sy + j + ((c >> 1) & 1)) * sz + k + ((c >> 2) & 1);
                if (values[index] < 0)
                    mask |= 1 << c;
            }
            return mask;
        }

        static bool Joins(int mask, int a, int b)
        {
            int side = (mask >> a) & 1;
            int reached = 1 << a;
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int c = 0; c < 8; c++)
                {
                    if ((reached & (1 << c)) == 0)
                        continue;
                    foreach (int neighbor in (ReadOnlySpan<int>)[c ^ 1, c ^ 2, c ^ 4])
                    {
                        if ((reached & (1 << neighbor)) != 0 || ((mask >> neighbor) & 1) != side)
                            continue;
                        reached |= 1 << neighbor;
                        grew = true;
                    }
                }
            }
            return (reached & (1 << b)) != 0;
        }

        int joined = 0;
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int k = 0; k < nz; k++)
                {
                    int mask = Mask(i, j, k);
                    if (mask is 0 or 255)
                        continue;
                    foreach (var (face, a, b) in diagonals)
                    {
                        if ((mask & face) != ((1 << a) | (1 << b)) || !Joins(mask, a, b))
                            continue;
                        // The neighbour across this low face, and the pair in ITS numbering.
                        int flip = face == 0x55 ? 1 : face == 0x33 ? 2 : 4;
                        int ni = i - (flip == 1 ? 1 : 0);
                        int nj = j - (flip == 2 ? 1 : 0);
                        int nk = k - (flip == 4 ? 1 : 0);
                        if (ni < 0 || nj < 0 || nk < 0)
                            continue;
                        int neighbor = Mask(ni, nj, nk);
                        if (neighbor is not (0 or 255) && Joins(neighbor, a ^ flip, b ^ flip))
                            joined++;
                    }
                }
            }
        }
        return joined;
    }
}
