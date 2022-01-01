using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Features;

public class Hole : Node
{
    private readonly IClosedSketch _sketch;

    public Hole(float dia, float depth, ICSYS csys)
    {
        _sketch = new Sketch(csys).Close();
    }

    public override ShapeWrapper Generate()
    {
        return ShapeWrapper.Revolve(_sketch.Edges.Select(o => o.ToEdge()).ToList(), _sketch.CSYS.Origin, _sketch.CSYS.XDirection);
    }
}