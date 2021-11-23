using System.Collections.Generic;

namespace EngrCAD.Core.Sketcher
{
    public class ClosedSketch : Sketch, IClosedSketch
    {
        public ClosedSketch(IPlane plane) : base(plane)
        {
        }

        internal ClosedSketch(IPlane plane, IEnumerable<ISketchObject> sketchObjects, ISketchObject next) : base(plane, sketchObjects, next)
        {
        }
    }
}