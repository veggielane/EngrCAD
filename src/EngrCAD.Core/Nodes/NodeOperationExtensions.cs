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
    public static Node Union(this Node node, params Node[] others) => new Union(node, others);
    public static Node Union(this IEnumerable<Node> nodes) => new Union(nodes);
    public static Node Union(this ValueTuple<Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node, Node, Node> tuple) => new Union(tuple.Merge());
    public static Node Union(this ValueTuple<Node, Node, Node, Node, Node, Node, Node> tuple) => new Union(tuple.Merge());

    public static Node Subtract(this Node node, params Node[] others) => new Subtract(node, others);
    public static Node Subtract(this Node node, IEnumerable<Node> others) => new Subtract(node, others);
    public static Node Intersect(this Node node, params Node[] others) => new Intersect(node, others);
    public static Node Intersect(this Node node, IEnumerable<Node> others) => new Intersect(node, others);

    public static Node Extrude(this ClosedSketch sketch, float distance) => new Extrude(sketch, distance);

    public static Node Revolve(this ClosedSketch sketch, IAxis axis) => new Revolve(sketch, axis);
    public static Node Revolve(this ClosedSketch sketch, IAxis axis, float angle) => new RevolveAngle(sketch, axis, angle);

    /// <summary>
    /// Round All Edges
    /// </summary>
    /// <param name="node">Node to Round</param>
    /// <param name="radius">Round Radius</param>
    /// <example>
    /// node.Round(5f);
    /// </example>
    /// <returns>Node with all edges rounded</returns>
    public static Node Round(this Node node, float radius) => new Round(node, new RadiusDefinition(radius, node.Edges.Select(edge => edge.Wrapper).ToList()));
   /// <summary>
   /// Round edges filtered by predicate
   /// </summary>
   /// <param name="node">Node to Round</param>
   /// <param name="predicate">Predicate </param>
   /// <returns></returns>
    public static Node Round(this Node node, Func<IEdge, float?> predicate) => new Round(node, node.Edges.Select(edge => (edge, predicate(edge))).Where(tuple => tuple.Item2.HasValue).Select(tuple => new RadiusDefinition(tuple.Item2.Value, new List<EdgeWrapper>() { tuple.edge.Wrapper })).ToList());
    public static Node Round(this Node node, float radius, Func<IEdge[], IEnumerable<IEdge>> filter) => new Round(node, new RadiusDefinition(radius, filter(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList()));
    public static Node Round(this Node node, params ValueTuple<float, Func<IEdge[], IEnumerable<IEdge>>>[] filter) => new Round(node, filter.Select(tuple => new RadiusDefinition(tuple.Item1, tuple.Item2(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList())).ToList());
}