using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using EngrCAD.Core.Datums;
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