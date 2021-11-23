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
            return new Sketch(Plane, Objects, new LineSketchObject());
        }

        public ISketch VerticalLine(float distance)
        {
            return new Sketch(Plane, Objects, new LineSketchObject());
        }

        public IClosedSketch Close()
        {
            return new ClosedSketch(Plane, Objects, new LineSketchObject());
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
