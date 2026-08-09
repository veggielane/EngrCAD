using EngrCAD.Core;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Fixtures for topology optimisation, on the structured Kuhn meshes every solver
/// verification here uses.
///
/// <para><b>Poisson's ratio is ZERO in the bar fixtures, and that is the device that makes a
/// closed form exist.</b> With a fully fixed end and no lateral coupling the exact solution is
/// a uniform uniaxial strain, so each element's strain energy is its volume times one energy
/// density and the optimisation reduces to a one-dimensional problem with a known answer. A
/// nonzero ratio would put a boundary layer at the clamp — the structural fixtures' recorded
/// lesson — and leave nothing to compare against.</para>
/// </summary>
internal static class TopologyFixtures
{
    /// <summary>A material with no Poisson coupling — see the type's remarks.</summary>
    public static Material Poissonless(double youngsModulus) =>
        new("test", youngsModulus, 0.0, 7.85e-9);

    /// <summary>Bar length.</summary>
    public const double BarLength = 40;

    /// <summary>Bar cross-section side.</summary>
    public const double BarSide = 10;

    /// <summary>Bar Young's modulus.</summary>
    public const double BarModulus = 70_000;

    /// <summary>
    /// A prismatic bar fixed over its whole <c>x = 0</c> face and pulled axially by
    /// <paramref name="endForce"/> spread over its whole far face — so with
    /// <see cref="Poissonless"/> material the stress is exactly uniform.
    /// </summary>
    public static StructuralModel Bar(double endForce = 1000, int nx = 8)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(BarLength, BarSide, BarSide), nx, 2, 2);
        var model = new StructuralModel(AnalysisMesh.Of(tets), Poissonless(BarModulus));
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(endForce, 0, 0));
        return model;
    }

    /// <summary>
    /// The same bar with an extra axial force at MID-LENGTH, so the internal force is
    /// <c>F1 + F2</c> over the first half and <c>F2</c> over the second — a two-step series
    /// chain whose optimum is the classical fully-stressed design.
    /// </summary>
    public static StructuralModel SteppedBar(double midForce, double endForce)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(BarLength, BarSide, BarSide), 8, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Poissonless(BarModulus));
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(endForce, 0, 0));

        // Mid-length is a node plane by construction (8 cells over 40, so x = 20 is a node
        // plane exactly), and the load is spread equally over its nodes. A consistent
        // traction has no meaning on an interior plane — there are no facets there — so this
        // is a self-equilibrated nodal load, which is why the closed form below is compared
        // as an ACCURACY rather than as an identity.
        var plane = new List<int>();
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (Math.Abs(mesh.Position(v).X - BarLength / 2) < 1e-9)
                plane.Add(v);
        }
        foreach (int node in plane)
            model.NodalForce(node, new Vector3d(midForce / plane.Count, 0, 0));
        return model;
    }

    /// <summary>The mean density over the elements whose centroid lies in the first or second
    /// half of a <see cref="SteppedBar"/>.</summary>
    public static (double First, double Second) HalfMeans(
        AnalysisMesh mesh, IReadOnlyList<double> density)
    {
        double first = 0, second = 0;
        int nFirst = 0, nSecond = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            double x = 0.25 * (mesh.Position(nodes[0]).X + mesh.Position(nodes[1]).X
                + mesh.Position(nodes[2]).X + mesh.Position(nodes[3]).X);
            if (x < BarLength / 2)
            {
                first += density[e];
                nFirst++;
            }
            else
            {
                second += density[e];
                nSecond++;
            }
        }
        return (first / nFirst, second / nSecond);
    }

    /// <summary>Cantilever length.</summary>
    public const double CantileverLength = 60;

    /// <summary>Cantilever depth.</summary>
    public const double CantileverDepth = 20;

    /// <summary>Cantilever thickness.</summary>
    public const double CantileverThickness = 5;

    /// <summary>
    /// The standard cantilever: fixed over its whole root face, a downward tip load spread
    /// over its whole end face, refined by <paramref name="level"/> in the two long
    /// directions and one cell thick.
    /// <para>Thin in z deliberately: the mesh-refinement study needs the SAME structure at
    /// three resolutions, and refining the thickness as well would multiply the element count
    /// without adding anything the answer depends on.</para>
    /// </summary>
    public static StructuralModel Cantilever(int level, out AnalysisMesh mesh)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero,
            new Vector3d(CantileverLength, CantileverDepth, CantileverThickness),
            12 + 6 * level, 4 + 2 * level, 1);
        mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Materials.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -2000));
        return model;
    }

    /// <summary>MBB beam span (X).</summary>
    public const double MbbSpan = 80;

    /// <summary>MBB beam thickness (Y, out-of-plane).</summary>
    public const double MbbThickness = 12;

    /// <summary>MBB beam depth (Z, the vertical the arch stands in — so it renders upright in
    /// the Z-up viewer).</summary>
    public const double MbbDepth = 24;

    /// <summary>
    /// The MBB (Messerschmitt-Bölkow-Blohm) beam — the canonical topology-optimisation problem:
    /// a simply-supported beam loaded downward at the top centre, which optimises into the
    /// iconic arch with a bottom tension chord and web members.
    ///
    /// <para><b>The supports are symmetric on purpose</b>, so the optimum is left-right
    /// symmetric and a wrong boundary condition breaks that symmetry visibly (the mutation the
    /// symmetry test relies on). Two bottom rollers carry the vertical reaction and pin the
    /// out-of-plane translation; a single base-centre X pin removes the remaining horizontal
    /// slide without breaking symmetry, because under a purely vertical load the horizontal
    /// reaction is zero anyway (equilibrium), so the structure still forms its own tension tie
    /// rather than leaning on a support for thrust.</para>
    ///
    /// <para>Refined by <paramref name="level"/> in the two long directions only, one fixed
    /// thickness — the same device the cantilever uses so a refinement study compares the SAME
    /// structure at three resolutions.</para>
    /// </summary>
    public static StructuralModel MbbBeam(int level, out AnalysisMesh mesh)
    {
        // Span along X, thickness along Y, DEPTH along Z — so the arch stands in the world's
        // own vertical and renders upright, and the load points down the world's -Z.
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(MbbSpan, MbbThickness, MbbDepth),
            20 + 10 * level, 3, 6 + 3 * level);
        mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Materials.Steel);

        double support = 0.12 * MbbSpan, load = 0.10 * MbbSpan, yHi = MbbThickness + 1;
        // Bottom rollers, left and right: resist vertical (Z) and out-of-plane (Y).
        model.Fix(Facets.And(Facets.Tag(StructuredTetMesh.ZMin),
            Facets.InBox(new Aabb((-1, -1, -1), (support, yHi, 1)))), Dof.Y | Dof.Z);
        model.Fix(Facets.And(Facets.Tag(StructuredTetMesh.ZMin),
            Facets.InBox(new Aabb((MbbSpan - support, -1, -1), (MbbSpan + 1, yHi, 1)))), Dof.Y | Dof.Z);
        // Base-centre X pin: removes horizontal drift symmetrically.
        model.Fix(Facets.And(Facets.Tag(StructuredTetMesh.ZMin),
            Facets.InBox(new Aabb((MbbSpan / 2 - load, -1, -1), (MbbSpan / 2 + load, yHi, 1)))), Dof.X);
        // Downward load at the top centre.
        model.Force(Facets.And(Facets.Tag(StructuredTetMesh.ZMax),
            Facets.InBox(new Aabb((MbbSpan / 2 - load, -1, MbbDepth - 1), (MbbSpan / 2 + load, yHi, MbbDepth + 1)))),
            new Vector3d(0, 0, -3000));
        return model;
    }

    /// <summary>The MBB density averaged into a fixed grid of bins over span (X) × depth (Z), so
    /// two meshes (and a field and its mirror) are compared in one representation.</summary>
    public static double[] MbbBinned(AnalysisMesh mesh, IReadOnlyList<double> density, int bx = 16, int by = 6)
    {
        var sum = new double[bx * by];
        var weight = new double[bx * by];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            var centre = 0.25 * (mesh.Position(nodes[0]) + mesh.Position(nodes[1])
                + mesh.Position(nodes[2]) + mesh.Position(nodes[3]));
            int i = Math.Clamp((int)(centre.X / MbbSpan * bx), 0, bx - 1);
            int j = Math.Clamp((int)(centre.Z / MbbDepth * by), 0, by - 1);
            double v = mesh.ElementVolume(e);
            sum[i + j * bx] += v * density[e];
            weight[i + j * bx] += v;
        }
        for (int k = 0; k < sum.Length; k++)
        {
            if (weight[k] > 0)
                sum[k] /= weight[k];
        }
        return sum;
    }

    /// <summary>A binned field mirrored left-to-right (about the mid-span plane).</summary>
    public static double[] MirrorX(double[] binned, int bx = 16, int by = 6)
    {
        var mirror = new double[binned.Length];
        for (int j = 0; j < by; j++)
        {
            for (int i = 0; i < bx; i++)
                mirror[i + j * bx] = binned[(bx - 1 - i) + j * bx];
        }
        return mirror;
    }

    /// <summary>
    /// The density field averaged into a fixed grid of bins over the two long directions —
    /// the ONE representation two different meshes can be compared in.
    /// <para>Volume-weighted, so a bin holding elements of several sizes reports what the bin
    /// contains rather than what its elements average to.</para>
    /// </summary>
    public static double[] Binned(
        AnalysisMesh mesh, IReadOnlyList<double> density, int bx = 12, int by = 4)
    {
        var sum = new double[bx * by];
        var weight = new double[bx * by];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            var centre = 0.25 * (mesh.Position(nodes[0]) + mesh.Position(nodes[1])
                + mesh.Position(nodes[2]) + mesh.Position(nodes[3]));
            int i = Math.Clamp((int)(centre.X / CantileverLength * bx), 0, bx - 1);
            int j = Math.Clamp((int)(centre.Y / CantileverDepth * by), 0, by - 1);
            double v = mesh.ElementVolume(e);
            sum[i + j * bx] += v * density[e];
            weight[i + j * bx] += v;
        }
        for (int k = 0; k < sum.Length; k++)
        {
            if (weight[k] > 0)
                sum[k] /= weight[k];
        }
        return sum;
    }

    /// <summary>Mean absolute difference between two binned fields.</summary>
    public static double MeanAbsoluteDifference(double[] a, double[] b)
    {
        double sum = 0;
        for (int k = 0; k < a.Length; k++)
            sum += Math.Abs(a[k] - b[k]);
        return sum / a.Length;
    }

    /// <summary>
    /// How far each element's density sits from the mean of the elements sharing a FACE with
    /// it — the direct measurement of checkerboarding.
    /// <para>Against the neighbour MEAN rather than against each neighbour: a genuine
    /// structural boundary puts a solid element next to a void one and a pairwise measure
    /// would count that as oscillation, while an element alternating with ALL of its
    /// neighbours is the artefact and only this form sees it.</para>
    /// </summary>
    public static double NeighbourVariation(AnalysisMesh mesh, IReadOnlyList<double> density)
    {
        int[][] faces = [[1, 2, 3], [0, 3, 2], [0, 1, 3], [0, 2, 1]];
        var byFace = new Dictionary<(int, int, int), List<int>>();
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            foreach (var face in faces)
            {
                int[] key = [nodes[face[0]], nodes[face[1]], nodes[face[2]]];
                Array.Sort(key);
                var k = (key[0], key[1], key[2]);
                if (!byFace.TryGetValue(k, out var list))
                    byFace[k] = list = [];
                list.Add(e);
            }
        }

        var neighbours = new List<int>[mesh.ElementCount];
        for (int e = 0; e < neighbours.Length; e++)
            neighbours[e] = [];
        foreach (var list in byFace.Values)
        {
            if (list.Count != 2)
                continue;
            neighbours[list[0]].Add(list[1]);
            neighbours[list[1]].Add(list[0]);
        }

        double total = 0;
        int counted = 0;
        for (int e = 0; e < neighbours.Length; e++)
        {
            if (neighbours[e].Count == 0)
                continue;
            double mean = 0;
            foreach (int j in neighbours[e])
                mean += density[j];
            mean /= neighbours[e].Count;
            total += Math.Abs(density[e] - mean);
            counted++;
        }
        return counted > 0 ? total / counted : 0;
    }
}
