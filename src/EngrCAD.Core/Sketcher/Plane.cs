namespace EngrCAD.Core.Sketcher
{
    public class Plane : IPlane
    {
        public static IPlane XY => new Plane();
    }
}