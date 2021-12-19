using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core.Sketcher;

public class ClosedSketch : Sketch, IClosedSketch
{
    internal ClosedSketch(IPlane plane, IEnumerable<ISketchEdge> sketchObjects) : base(plane)
    {
        Edges = sketchObjects.ToList();
    }
}