using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EngrCAD.Core.Sketcher.Edges;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Sketcher;

public class Sketch:ISketch
{
    private Vector3 _firstPoint;
    private Vector3 _pointer;
    private readonly CoordMapper _mapper;

    public IPlane Plane { get; init; }
    public List<ISketchEdge> Edges { get; protected init; }


    public ISketch MoveTo(Vector2 v)
    {
        if (Edges.Any())
        {
            throw new Exception("MoveTo can only be used at the start");
        }
        var point = ToWorldCoords(v);
        _pointer = point;
        _firstPoint = point;
        return this;
    }

    public ISketch MoveTo(float x, float y) => MoveTo(new Vector2(x, y));


    private void UpdatePointer(Vector3 v)
    {
        _pointer = v;
    }


    private Vector3 ToWorldCoords(Vector2 v)
    {
        return (Vector3)_mapper.ToWorldCoords(v);
    }

    private Vector2 ToLocalCoords(Vector3 v)
    {
        return (Vector2)_mapper.ToLocalCoords(v);
    }

    public ISketch LineTo(Vector2 v)
    {
        var endPoint = ToWorldCoords(v);
        Edges.Add(new SketchEdgeLine
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

    public ISketch Line(Vector2 v)
    {
        var pointer = ToLocalCoords(_pointer);
        return LineTo(v.X + pointer.X, v.Y + pointer.Y);
    }

    public ISketch Line(float x, float y) => Line(new Vector2(x, y));
    public ISketch BezierCurveTo(Vector2 end, params Vector2[] controlPoints)
    {
        var endPoint = ToWorldCoords(end);
        Edges.Add(new SketchEdgeBezierCurve
        {
            Start = _pointer,
            End = endPoint,
            ControlPoints = controlPoints.Select(ToWorldCoords).ToList()
        });

        UpdatePointer(endPoint);
        return this;
    }

    public ISketch BezierCurve(Vector2 end, params Vector2[] controlPoints)
    {
        var pointer = ToLocalCoords(_pointer);

        return BezierCurveTo(end + pointer, controlPoints.Select(v => v + pointer).ToArray());

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
        if (_firstPoint != _pointer)
        {
            Edges.Add(new SketchEdgeLine
            {
                Start = _pointer,
                End = _firstPoint
            });
        }

        return new ClosedSketch(Plane, Edges);
    }

    public Sketch(IPlane plane)
    {
        Plane = plane;
        Edges = new List<ISketchEdge>();
        _mapper = new CoordMapper(Plane.Origin, Plane.Normal, Plane.XDirection);

        _firstPoint = Plane.Origin;
    }


}