﻿using System;
using System.Collections.Generic;
using System.Linq;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public abstract class BaseNode : INode
{
    private ShapeWrapper _cached;
    public ShapeWrapper Wrapper => _cached ??= Generate();
    public abstract ShapeWrapper Generate();
    public float Volume => Wrapper.CalculateVolume();
    public ShapeType ShapeType => (ShapeType)Wrapper.ShapeType();
    public IEnumerable<Face> Faces => Wrapper.GetFaces().Select(wrapper => new Face(wrapper));
    public IEnumerable<Edge> Edges => Wrapper.GetEdges().Select(wrapper =>
    {
        var edgeType = (EdgeType)wrapper.CurveType();
        switch (edgeType)
        {
            case EdgeType.GeomAbs_Line:
                return new EdgeLine(wrapper);
            case EdgeType.GeomAbs_Circle:
                break;
            case EdgeType.GeomAbs_Ellipse:
                break;
            case EdgeType.GeomAbs_Hyperbola:
                break;
            case EdgeType.GeomAbs_Parabola:
                break;
            case EdgeType.GeomAbs_BezierCurve:
                break;
            case EdgeType.GeomAbs_BSplineCurve:
                break;
            case EdgeType.GeomAbs_OffsetCurve:
                break;
            case EdgeType.GeomAbs_OtherCurve:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        return new EdgeLine(wrapper);


    });
}

public enum EdgeType
{
    GeomAbs_Line,
    GeomAbs_Circle,
    GeomAbs_Ellipse,
    GeomAbs_Hyperbola,
    GeomAbs_Parabola,
    GeomAbs_BezierCurve,
    GeomAbs_BSplineCurve,
    GeomAbs_OffsetCurve,
    GeomAbs_OtherCurve, }