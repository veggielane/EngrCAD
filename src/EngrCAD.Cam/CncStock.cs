using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>One recorded machining state: the fraction of the total cut length completed,
/// the length machined so far (mm), and the stock with that prefix of the toolpath
/// removed. State 0 is the untouched stock BY REFERENCE.</summary>
public sealed record CncStockState(double Fraction, double CutLength, Shape Shape);

/// <summary>
/// The machined-stock simulation — the material-removal record the CAM campaign filed:
/// stock minus the toolpath's swept volume at N cut-length fractions, each state an
/// ordinary <see cref="Shape"/> a scene can show, export or measure.
///
/// <para><b>The swept volume of a 2.5D pass is CLOSED FORM</b>: a flat tool at constant z
/// occupies its stroked footprint (<see cref="Region2dOffset.Stroke"/> at the tool
/// diameter — exactly the region the stage-2 opening oracle already measures) from the
/// cut level up through the stock, and a vertical descent bores a disc (a 32-gon here,
/// so the drilled volume is the inscribed n-gon's EXACT prism — polyhedral booleans are
/// exact, which is what makes the drill test an identity rather than a tolerance).
/// Footprints are unioned per level in 2D FIRST, so each state subtracts one prism per
/// level rather than one per pass. A pass that moves in XY and Z at once (a surfacing
/// raster row) is refused BY NAME — the 3-axis swept volume is not a prism, and its
/// simulation is filed with the campaign.</para>
///
/// <para><b>What each state is FOR is a still or an export, not a live clip</b>: a
/// changing-geometry animation has no matrices-only form (a pose track's contract is
/// that only matrices move), so the states are recorded data — the transient-thermal
/// precedent, where a per-step colour animation was scoped separately rather than
/// squeezed through the deformation uniform. The tool itself animates along its path as
/// an ordinary pose track (<c>PathTracks.Follow</c> in the viewer), which IS
/// matrices-only.</para>
/// </summary>
public static class CncStock
{
    /// <summary>Simulates machining <paramref name="operations"/> (in order) out of
    /// <paramref name="stock"/>, recording <paramref name="states"/> stock states at
    /// evenly spaced fractions of the total cut length (state 0 = the stock untouched,
    /// the last = fully machined).</summary>
    public static IReadOnlyList<CncStockState> Simulate(
        Shape stock, IReadOnlyList<MillOperation> operations, int states = 8)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(operations);
        if (states < 2)
            throw new ArgumentException($"A simulation needs at least 2 states; got {states}.");

        double stockTop = stock.Bounds().Max.Z;
        var atoms = CollectAtoms(operations, stockTop);
        double total = atoms.Sum(a => a.Length);
        double top = stockTop + 1; // the Drill overshoot doctrine
        var stockMesh = stock.ToMesh();

        var result = new List<CncStockState>(states);
        for (int k = 0; k < states; k++)
        {
            double fraction = (double)k / (states - 1);
            if (k == 0 || total <= 0)
            {
                result.Add(new CncStockState(fraction, 0, stock));
                continue;
            }
            double s = fraction * total;
            result.Add(new CncStockState(
                fraction, s, Machined(stock, stockMesh, atoms, s, top)));
        }
        return result;
    }

    /// <summary>A removal atom: one constant-z stroked run or one vertical bore, with the
    /// cut length it contributes to the schedule.</summary>
    private sealed record Atom(
        double Length, double Z, double Diameter,
        IReadOnlyList<Vector2d>? Run, bool Closed, Vector2d Point);

    private static List<Atom> CollectAtoms(IReadOnlyList<MillOperation> operations, double stockTop)
    {
        var atoms = new List<Atom>();
        foreach (var op in operations)
        {
            double d = op.Tool.Diameter;
            foreach (var pass in op.Passes)
            {
                var points = pass.Points;
                // Every pass is ENTERED by a plunge from above (the writer's own rule), so
                // a first point below the stock top bores its disc — which is what makes a
                // single-point drill pass remove material at all, and is contained in the
                // run's own round start cap for every stroked pass.
                if (points[0].Z < stockTop - 1e-12)
                    atoms.Add(new Atom(stockTop - points[0].Z, points[0].Z, d,
                        null, false, new Vector2d(points[0].X, points[0].Y)));
                int count = points.Count + (pass.IsClosed ? 1 : 0);
                var run = new List<Vector2d> { new(points[0].X, points[0].Y) };
                double runZ = points[0].Z;
                for (int i = 1; i < count; i++)
                {
                    var a = points[i - 1];
                    var b = points[i % points.Count];
                    bool movedXy = a.X != b.X || a.Y != b.Y;
                    bool movedZ = a.Z != b.Z;
                    if (movedXy && movedZ)
                        throw new ArgumentException(
                            $"Pass in '{op.Name}' moves in XY and Z simultaneously "
                            + $"(({a.X:0.###}, {a.Y:0.###}, {a.Z:0.###}) → ({b.X:0.###}, "
                            + $"{b.Y:0.###}, {b.Z:0.###})): the 3-axis swept volume is not "
                            + "a prism — surfacing stock simulation is filed with the campaign.");
                    if (movedXy)
                    {
                        run.Add(new Vector2d(b.X, b.Y));
                        continue;
                    }
                    // A z move ends the current run and, when descending, bores a disc.
                    FlushRun(atoms, run, runZ, d, pass, wholePass: false);
                    if (b.Z < a.Z)
                        atoms.Add(new Atom(a.Z - b.Z, b.Z, d, null, false, new Vector2d(b.X, b.Y)));
                    run = [new Vector2d(b.X, b.Y)];
                    runZ = b.Z;
                }
                FlushRun(atoms, run, runZ, d, pass,
                    wholePass: run.Count == count); // one unbroken run = the whole loop
            }
        }
        return atoms;

        static void FlushRun(
            List<Atom> atoms, List<Vector2d> run, double z, double d, MillPass pass, bool wholePass)
        {
            if (run.Count < 2)
                return;
            double length = 0;
            for (int i = 1; i < run.Count; i++)
                length += (run[i] - run[i - 1]).Length;
            // Only a pass that is one unbroken constant-z loop strokes as a circuit; a
            // tabbed loop's runs stroke open, their shared endpoints covered by the
            // round caps.
            atoms.Add(new Atom(length, z, d, run.ToArray(), pass.IsClosed && wholePass, default));
        }
    }

    private static Shape Machined(
        Shape stock, HalfEdgeMesh stockMesh, List<Atom> atoms, double s, double top)
    {
        // The footprints per cut level (exact z equality — levels repeat the same double).
        var byLevel = new Dictionary<double, List<Region2d>>();
        double done = 0;
        foreach (var atom in atoms)
        {
            if (done >= s)
                break;
            double take = Math.Min(atom.Length, s - done);
            AddFootprint(byLevel, atom, take);
            done += atom.Length;
        }
        if (byLevel.Count == 0)
            return stock;

        // Subtraction runs through the MESH imprint boolean deliberately: the swept
        // prisms are polyhedral (a stroked footprint is a polygon), so the mesh boolean
        // is exact there, and the B-Rep route — which the Shape compiler would otherwise
        // prefer — has nothing better to offer a chorded profile. The removal is cut into
        // z BANDS rather than one prism-to-the-top per level, because successive levels
        // repeat the same footprint and their full-height prisms would share their entire
        // side walls; a band spans one level to the next, so vertical walls never overlap
        // between subtractions and the only coincidence left is the horizontal
        // stacked-plates case the imprint boolean's coplanar tier is built for. A band's
        // cross-section is the union of every level AT OR BELOW it (a level's tool
        // occupies its footprint from its own z up through the stock).
        var levels = byLevel.Keys.OrderByDescending(z => z).ToList();
        var cumulative = new IReadOnlyList<Region2d>[levels.Count];
        for (int j = levels.Count - 1; j >= 0; j--)
        {
            var own = Region2dBoolean.UnionAll(byLevel[levels[j]]);
            cumulative[j] = j == levels.Count - 1
                ? own
                : Region2dBoolean.Union(cumulative[j + 1], own);
        }

        var result = stockMesh;
        for (int j = 0; j < levels.Count; j++)
        {
            double zLow = levels[j];
            double zHigh = j == 0 ? top : levels[j - 1];
            var plane = SketchPlane.At(new Vector3d(0, 0, zLow), Vector3d.UnitX, Vector3d.UnitY);
            foreach (var region in cumulative[j])
            {
                var sketch = Sketch.Polygon(region.Outer);
                foreach (var hole in region.Holes)
                    sketch = sketch.WithHole(Sketch.Polygon(hole));
                var tool = Shape.Extrude(sketch, zHigh - zLow, plane).ToMesh();
                result = MeshBoolean.Difference(result, tool);
            }
        }
        return Shape.From(result);
    }

    private static void AddFootprint(
        Dictionary<double, List<Region2d>> byLevel, Atom atom, double take)
    {
        bool partial = take < atom.Length - 1e-12;
        if (atom.Run is null)
        {
            // A vertical bore: a partial descent has reached only partway down.
            double z = partial ? atom.Z + (atom.Length - take) : atom.Z;
            Level(z).Add(Disc(atom.Point, atom.Diameter / 2));
            return;
        }

        IReadOnlyList<Vector2d> path = atom.Run;
        bool closed = atom.Closed;
        if (partial)
        {
            var cut = new List<Vector2d> { path[0] };
            double remaining = take;
            for (int i = 1; i < path.Count && remaining > 0; i++)
            {
                var a = path[i - 1];
                var b = path[i];
                double len = (b - a).Length;
                if (len <= remaining)
                {
                    cut.Add(b);
                    remaining -= len;
                }
                else
                {
                    cut.Add(a + (b - a) * (remaining / len));
                    remaining = 0;
                }
            }
            if (cut.Count < 2)
                return;
            path = cut;
            closed = false;
        }
        Level(atom.Z).AddRange(Region2dOffset.Stroke(path, atom.Diameter, closed: closed));

        List<Region2d> Level(double z)
        {
            if (!byLevel.TryGetValue(z, out var list))
                byLevel[z] = list = [];
            return list;
        }
    }

    /// <summary>The drill footprint: an inscribed 32-gon, so a drilled state's volume is
    /// the n-gon prism's EXACT closed form ((n/2)·r²·sin(2π/n) per unit depth).</summary>
    private static Region2d Disc(in Vector2d centre, double radius)
    {
        const int n = 32;
        var points = new Vector2d[n];
        for (int i = 0; i < n; i++)
        {
            double angle = 2 * Math.PI * i / n;
            points[i] = centre + new Vector2d(radius * Math.Cos(angle), radius * Math.Sin(angle));
        }
        return new Region2d(points);
    }
}
