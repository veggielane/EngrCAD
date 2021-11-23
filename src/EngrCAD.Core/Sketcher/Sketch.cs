using System.Collections.Generic;
using EngrCAD.Core.Nodes.Operations;

namespace EngrCAD.Core.Sketcher
{
    public class Sketch:ISketch
    {
        public IPlane Plane { get; init; }
        public List<ISketchObject> Objects { get; }

        public ISketch HorizontalLine(float distance)
        {
            throw new System.NotImplementedException();
        }

        public ISketch VerticalLine(float distance)
        {
            throw new System.NotImplementedException();
        }

        public IClosedSketch Close()
        {
            throw new System.NotImplementedException();
        }

        public Sketch(IPlane plane)
        {
            Plane = plane;
            Objects = new List<ISketchObject>();
        }

        protected Sketch(IPlane plane, IEnumerable<ISketchObject> sketchObjects, ISketchObject next)
        {
            Plane = plane;
            Objects = new List<ISketchObject>(sketchObjects);
            Objects.Add(next);
        }
    }
}
