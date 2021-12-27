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


    public static void SaveSTL(this Node node, string path) => node.Generate().SaveSTL(path);
    public static void SaveSTP(this Node node, string path) => node.Generate().SaveSTP(path);

    public static Node Transform(this Node node, Matrix4x4 matrix) => new Transform
    {
        Child = node,
        Matrix = matrix
    };

    public static Node Translate(this Node node, Vector3 position) => new Translate
    {
        Child = node,
        Position = position
    };

    public static Node Centered(this Node node)
    {
        var aabb = node.BoundingBox;
        return new Translate
        {
            Child = node,
            Position = -aabb.Center
        };
    }

    public static Node Translate(this Node node, float x, float y, float z) => node.Translate(new Vector3(x, y, z));

    public static Node Rotate(this Node node, float radians, Vector3 origin, Vector3 direction) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Origin = origin,
        Direction = direction
    };

    public static Node RotateX(this Node node, float radians) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Direction = Vector3.UnitX
    };
    public static Node RotateY(this Node node, float radians) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Direction = Vector3.UnitY
    };
    public static Node RotateZ(this Node node, float radians) => new Rotate()
    {
        Child = node,
        Radians = radians,
        Direction = Vector3.UnitZ
    };


    public static Node Shell(this Node node, float thickness) => new Shell()
    {
        Child = node,
        Thickness = thickness
    };


    public static Body ToBody(this Node node, Color color) => new()
    {
        Node = node,
        Color = color
    };

    public static Body ToBody(this Node node) => new()
    {
        Node = node,
        Color = Color.DimGray
    };

    public static IEnumerable<Node> Merge(Node node, IEnumerable<Node> others)
    {
        yield return node;
        foreach (var child in others)
        {
            yield return child;
        }
    }

    public static IEnumerable<Node> Merge(this ValueTuple<Node> tuple)
    {
        yield return tuple.Item1;
    }

    public static IEnumerable<Node> Merge(this ValueTuple<Node, Node> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
    }

    public static IEnumerable<Node> Merge(this ValueTuple<Node, Node, Node> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
    }

    public static IEnumerable<Node> Merge(this ValueTuple<Node, Node, Node, Node> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
    }

    public static IEnumerable<Node> Merge(this ValueTuple<Node, Node, Node, Node, Node> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
        yield return tuple.Item5;
    }

    public static IEnumerable<Node> Merge(this ValueTuple<Node, Node, Node, Node, Node, Node> tuple)
    {
        yield return tuple.Item1;
        yield return tuple.Item2;
        yield return tuple.Item3;
        yield return tuple.Item4;
        yield return tuple.Item5;
        yield return tuple.Item6;
    }
    public static IEnumerable<Node> Merge(this ValueTuple<Node, Node, Node, Node, Node, Node, Node> tuple)
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