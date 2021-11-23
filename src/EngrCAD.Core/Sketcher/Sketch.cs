namespace EngrCAD.Core.Sketcher
{
    public class Sketch:ISketch
    {
        public IPlane Plane { get; init; }

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
        }
    }

    public interface ISketch
    {
        IPlane Plane { get; }

        ISketch HorizontalLine(float distance);
        ISketch VerticalLine(float distance);
        IClosedSketch Close();
    }

    public interface IClosedSketch:ISketch
    {

    }

    public interface IPlane
    {

    }

    public class Plane : IPlane
    {
        public static IPlane XY => new Plane();
    }
}
