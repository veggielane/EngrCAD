using System.Collections.Generic;
using System.Numerics;
using EngrCAD.Core.Nodes.Operations;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher;

public class Sketch:ISketch
{
    public IPlane Plane { get; init; }
    public List<ISketchObject> Objects { get; protected set; }

    protected Vector3 _firstPoint;
    protected Vector3 _pointer;

    private readonly CoordMapper _mapper;

    private void UpdatePointer(Vector3 v)
    {
        _pointer = v;
    }

    public ISketch LineTo(Vector2 v)
    {
        var endPoint = (Vector3)_mapper.ToWorldCoords(v);
        Objects.Add(new LineSketchObject
        {
            Start = _pointer,
            End = endPoint
        });
        UpdatePointer(endPoint);
        return this;
    }

    public ISketch LineTo(float x, float y)
    {
        return LineTo(new Vector2(x, y));
    }

    public ISketch Line(float x, float y)
    {
        var pointer = (Vector2)_mapper.ToLocalCoords(_pointer);
        return LineTo(x + pointer.X, y + pointer.Y);
    }

    public ISketch HorizontalLine(float distance)
    {
        return Line(distance, 0);
    }

    public ISketch VerticalLine(float distance)
    {
        return Line(0, distance);
    }

    public IClosedSketch Close()
    {

        Objects.Add(new LineSketchObject
        {
            Start = _pointer,
            End = _firstPoint
        });
        return new ClosedSketch(Plane, Objects);
    }

    public Sketch(IPlane plane)
    {
        Plane = plane;
        Objects = new List<ISketchObject>();
        _mapper = new CoordMapper(Plane.Origin, Plane.Normal, Plane.XDirection);

        //_firstPoint = Plane.Origin.
    }


}