using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using EngrCAD.Core.Edges;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Nodes.Transformations;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public static class NodeExtensions
{


    public static INode Union(this INode node, params INode[] others) => new Union(Merge(node, others));
    public static INode Union(this IEnumerable<INode> nodes) => new Union(nodes);
    public static INode Union(this ValueTuple<INode> tuple) => new Union(Merge(tuple));
    public static INode Union(this ValueTuple<INode, INode> tuple) => new Union(Merge(tuple));
    public static INode Union(this ValueTuple<INode, INode, INode> tuple) => new Union(Merge(tuple));
    public static INode Union(this ValueTuple<INode, INode, INode, INode> tuple) => new Union(Merge(tuple));
    public static INode Union(this ValueTuple<INode, INode, INode, INode, INode> tuple) => new Union(Merge(tuple));
    public static INode Union(this ValueTuple<INode, INode, INode, INode, INode, INode> tuple) => new Union(Merge(tuple));
    public static INode Union(this ValueTuple<INode, INode, INode, INode, INode, INode, INode> tuple) => new Union(Merge(tuple));

    public static INode Subtract(this INode node, params INode[] others) => new Subtract(Merge(node, others));
    public static INode Subtract(this IEnumerable<INode> nodes) => new Subtract(nodes);



    public static void SaveSTL(this INode node, string path) => node.Generate().SaveSTL(path);
    public static void SaveSTP(this INode node, string path) => node.Generate().SaveSTP(path);

    public static INode Transform(this INode node, Matrix4x4 matrix) => new Transform
    {
        Child = node,
        Matrix = matrix
    };

    public static INode Translate(this INode node, Vector3 position) => new Translate
    {
        Child = node,
        Position = position
    };

    public static INode Centered(this INode node)
    {
        var aabb = node.BoundingBox;
        return new Translate
        {
            Child = node,
            Position = -aabb.Center
        };
    }

    public static INode Translate(this INode node, float x, float y, float z) => node.Translate(new Vector3(x, y, z));

    public static INode Rotate(this INode node, float radians, Vector3 origin, Vector3 direction) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Origin = origin,
        Direction = direction
    };

    public static INode RotateX(this INode node, float radians) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Direction = Vector3.UnitX
    };
    public static INode RotateY(this INode node, float radians) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Direction = Vector3.UnitY
    };
    public static INode RotateZ(this INode node, float radians) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Direction = Vector3.UnitZ
    };


    public static INode Intersect(this INode node, INode other) => new Intersect()
    {
        Children = { node, other }
    };

    public static INode Shell(this INode node, float thickness) => new Shell()
    {
        Child = node,
        Thickness = thickness
    };

    public static INode Round(this INode node, float radius) => new Round()
    {
        Child = node,
        Definitions = new List<RadiusDefinition>()
        {
            new(radius,node.Edges.Select(edge => edge.Wrapper).ToList())
        }

    };

    public static INode Round(this INode node, Func<IEdge, float?> predicate) => new Round()
    {
        Child = node,
        Definitions = node.Edges.Select(edge => (edge,predicate(edge))).Where(tuple => tuple.Item2.HasValue).Select(tuple => new RadiusDefinition(tuple.Item2.Value, new List<EdgeWrapper>(){ tuple.edge.Wrapper})).ToList()
    };

    public static INode Round(this INode node, float radius, Func<IEdge[], IEnumerable<IEdge>> filter) => new Round()
    {
        Child = node,
        Definitions = new List<RadiusDefinition>
        {
            new(radius,filter(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList())
        }
    };

    public static INode Round(this INode node, params ValueTuple<float, Func<IEdge[], IEnumerable<IEdge>>>[] filter) => new Round()
    {
        Child = node,
        Definitions = filter.Select(tuple => new RadiusDefinition(tuple.Item1, tuple.Item2(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList())).ToList()
    };

    public static Extrude Extrude(this IClosedSketch sketch, float distance) => new Extrude(sketch, distance);

    public static Body ToBody(this INode node, Color color) => new Body()
    {
        Node = node,
        Color = color
    };

    public static Body ToBody(this INode node) => new Body()
    {
        Node = node,
        Color = Color.DimGray
    };

    private static IEnumerable<INode> Merge(INode node, IEnumerable<INode> others)
    {
        yield return node;
        foreach (var child in others)
        {
            yield return child;
        }
    }

    private static IEnumerable<INode> Merge(this ValueTuple<INode> tuple)
    {
        yield return tuple.Item1;
    }

    private static IEnumerable<INode> Merge(this ValueTuple<INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
    }

    private static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
    }

    private static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
    }

    private static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
        yield return tuple.Item5;
    }

    private static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
        yield return tuple.Item5;
        yield return tuple.Item6;
    }
    private static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode, INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
        yield return tuple.Item5;
        yield return tuple.Item6;
        yield return tuple.Item7;
    }

}