using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes;
using EngrCAD.Core.Numerics;
using EngrCAD.Core.Sketcher.Edges;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher;

public class Polygon : ClosedSketch
{
    public Polygon(ICSYS csys, IEnumerable<Vector2> points) : base(csys)
    {
        var sketch = new Sketch(csys);
        sketch.MoveTo(points.First());
        foreach (var point in points.Skip(1))
        {
            sketch.LineTo(point);
        }
        sketch.Close();
        Edges = sketch.Edges.ToList();
    }
}

public class Text : IClosedSketch
{
    private readonly CoordMapper _mapper;

    public Text(ICSYS csys, string text)
    {
        CSYS = csys;

        _mapper = new CoordMapper(csys.Origin, csys.Normal, csys.XDirection);
        //var sketch = new Sketch(csys);
#pragma warning disable CA1416 // Validate platform compatibility
        var gp = new GraphicsPath();
        var edges = new List<ISketchEdge>();

        using (var f = new Font("Tahoma", 40f))
        {

            gp.AddString(text, f.FontFamily, 0, 40f, new Point(0, 0), StringFormat.GenericDefault);

            GraphicsPathIterator pathIterator = new GraphicsPathIterator(gp);
            for (int i = 0; i < pathIterator.SubpathCount; i++)
            {
                int strIdx, endIdx;
                bool bClosedCurve;

                pathIterator.NextSubpath(out strIdx, out endIdx, out bClosedCurve);
                for (int j = strIdx; j < endIdx - 1; j++)
                {
                    edges.Add(new SketchEdgeLine()
                    {
                        Start = ToWorldCoords(new Vector2(gp.PathPoints[j].X, gp.PathPoints[j].Y)),
                        End = ToWorldCoords(new Vector2(gp.PathPoints[j + 1].X, gp.PathPoints[j + 1].Y))
                    });

                    if (bClosedCurve)
                    {
                        
                        edges.Add(new SketchEdgeLine()
                        {
                            Start = ToWorldCoords(new Vector2(gp.PathPoints[endIdx - 1].X, gp.PathPoints[endIdx - 1].Y)),
                            End = ToWorldCoords(new Vector2(gp.PathPoints[strIdx].X, gp.PathPoints[strIdx].Y))
                        });

                    }

                }

            }
        }
#pragma warning restore CA1416 // Validate platform compatibility
        Edges = edges;
    }

    private Vector3 ToWorldCoords(Vector2 v) => (Vector3)_mapper.ToWorldCoords(v);
    private Vector2 ToLocalCoords(Vector3 v) => (Vector2)_mapper.ToLocalCoords(v);
    public ICSYS CSYS { get; }
    public IReadOnlyList<ISketchEdge> Edges { get; }
    public Node Extrude(float distance)
    {
        throw new System.NotImplementedException();
    }

    public Node Revolve(IAxis axis)
    {
        throw new System.NotImplementedException();
    }

    public Node Revolve(IAxis axis, Angle angle)
    {
        throw new System.NotImplementedException();
    }

    public Node Loft(params IClosedSketch[] sketches)
    {
        throw new System.NotImplementedException();
    }

    public Node Loft(IEnumerable<IClosedSketch> sketches)
    {
        throw new System.NotImplementedException();
    }
}