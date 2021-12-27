using System;
using System.Collections.Generic;
using System.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Edges;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes;

public static class NodeOperationExtensions
{
    public static INode Union(this INode node, params INode[] others) => new Union(node, others);
    public static INode Union(this IEnumerable<INode> nodes) => new Union(nodes);
    public static INode Union(this ValueTuple<INode> tuple) => new Union(tuple.Merge());
    public static INode Union(this ValueTuple<INode, INode> tuple) => new Union(tuple.Merge());
    public static INode Union(this ValueTuple<INode, INode, INode> tuple) => new Union(tuple.Merge());
    public static INode Union(this ValueTuple<INode, INode, INode, INode> tuple) => new Union(tuple.Merge());
    public static INode Union(this ValueTuple<INode, INode, INode, INode, INode> tuple) => new Union(tuple.Merge());
    public static INode Union(this ValueTuple<INode, INode, INode, INode, INode, INode> tuple) => new Union(tuple.Merge());
    public static INode Union(this ValueTuple<INode, INode, INode, INode, INode, INode, INode> tuple) => new Union(tuple.Merge());

    public static INode Subtract(this INode node, params INode[] others) => new Subtract(node, others);
    public static INode Subtract(this INode node, IEnumerable<INode> others) => new Subtract(node, others);
    public static INode Intersect(this INode node, params INode[] others) => new Intersect(node, others);
    public static INode Intersect(this INode node, IEnumerable<INode> others) => new Intersect(node, others);

    public static INode Extrude(this ClosedSketch sketch, float distance) => new Extrude(sketch, distance);

    public static INode Revolve(this ClosedSketch sketch, IAxis axis, float angle) => new RevolveAngle(sketch, axis, angle);
    public static INode Revolve(this ClosedSketch sketch, IAxis axis) => new Revolve(sketch, axis);

    public static INode Round(this INode node, float radius) => new Round(node, new RadiusDefinition(radius, node.Edges.Select(edge => edge.Wrapper).ToList()));
    public static INode Round(this INode node, Func<IEdge, float?> predicate) => new Round(node, node.Edges.Select(edge => (edge, predicate(edge))).Where(tuple => tuple.Item2.HasValue).Select(tuple => new RadiusDefinition(tuple.Item2.Value, new List<EdgeWrapper>() { tuple.edge.Wrapper })).ToList());
    public static INode Round(this INode node, float radius, Func<IEdge[], IEnumerable<IEdge>> filter) => new Round(node, new RadiusDefinition(radius, filter(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList()));
    public static INode Round(this INode node, params ValueTuple<float, Func<IEdge[], IEnumerable<IEdge>>>[] filter) => new Round(node, filter.Select(tuple => new RadiusDefinition(tuple.Item1, tuple.Item2(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList())).ToList());
}