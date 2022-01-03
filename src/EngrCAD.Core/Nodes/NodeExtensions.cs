using System;
using System.Collections.Generic;
using System.Drawing;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Numerics;
using EngrCAD.Core.Sketcher;

namespace EngrCAD.Core.Nodes;

public static class NodeExtensions
{
    public static Node UnionAll(this IEnumerable<Node> nodes) => new Union(nodes);
    public static Node Union(this ValueTuple<Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node, Node, Node, Node> tuple) => new Union(tuple.Merge());

    public static Node Extrude(this ClosedSketch sketch, float distance) => new Extrude(sketch, distance);
    public static Node Revolve(this ClosedSketch sketch, IAxis axis) => new Revolve(sketch, axis);
    public static Node Revolve(this ClosedSketch sketch, IAxis axis, Angle angle) => new RevolveAngle(sketch, axis, angle);


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