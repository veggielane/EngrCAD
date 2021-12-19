using System.Collections.Generic;
using System.Linq;

namespace EngrCAD.Core.Sketcher;

public class ClosedSketch : Sketch, IClosedSketch
{
    internal ClosedSketch(IPlane plane, IEnumerable<ISketchObject> sketchObjects) : base(plane)
    {
        Objects = sketchObjects.ToList();
    }
}