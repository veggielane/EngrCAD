using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public class ClosedSketch : Sketch
{
    internal ClosedSketch(ICSYS csys, IEnumerable<ISketchEdge> sketchObjects) : base(csys)
    {
        Edges = sketchObjects.ToList();
    }
}