using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using EngrCAD.Core.Edges;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Nodes.Transformations;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public static class NodeExtensions
{


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


    public static INode Shell(this INode node, float thickness) => new Shell()
    {
        Child = node,
        Thickness = thickness
    };


    public static Body ToBody(this INode node, Color color) => new()
    {
        Node = node,
        Color = color
    };

    public static Body ToBody(this INode node) => new()
    {
        Node = node,
        Color = Color.DimGray
    };

    public static IEnumerable<INode> Merge(INode node, IEnumerable<INode> others)
    {
        yield return node;
        foreach (var child in others)
        {
            yield return child;
        }
    }

    public static IEnumerable<INode> Merge(this ValueTuple<INode> tuple)
    {
        yield return tuple.Item1;
    }

    public static IEnumerable<INode> Merge(this ValueTuple<INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
    }

    public static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
    }

    public static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
    }

    public static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
        yield return tuple.Item5;
    }

    public static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode, INode, INode> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
        yield return tuple.Item5;
        yield return tuple.Item6;
    }
    public static IEnumerable<INode> Merge(this ValueTuple<INode, INode, INode, INode, INode, INode, INode> tuple)
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