using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Edges;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public abstract partial class Node
{
    private ShapeWrapper _cached;
    public ShapeWrapper Wrapper => _cached ??= Generate();
    public abstract ShapeWrapper Generate();

    /// <summary>
    /// Calculate the Volume of the Node
    /// </summary>
    public float Volume => Wrapper.CalculateVolume();

    /// <summary>
    /// Calculate the AABB of the Node
    /// </summary>
    public IAABB BoundingBox => new AABB(Wrapper.CalculateBoundingBox());

    /// <summary>
    /// Gets the <see cref="ShapeType"/> of the Node
    /// </summary>
    public ShapeType ShapeType => (ShapeType)Wrapper.ShapeType();

    /// <summary>
    /// Gets the Faces of the Node
    /// </summary>
    public IEnumerable<Face> Faces => Wrapper.GetFaces().Select(wrapper => new Face(wrapper));

    /// <summary>
    /// Gets the Edges of the Node
    /// </summary>
    public IEnumerable<IEdge> Edges => Wrapper.GetEdges().Select(wrapper =>
    {
        switch ((EdgeType)wrapper.CurveType())
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

        throw new NotImplementedException();
    });
}