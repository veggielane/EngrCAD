using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Nodes.Transformations;
using EngrCAD.Core.Sketcher;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public static class NodeExtensions
    {
        public static void SaveSTL(this INode node, string path) => node.Generate().SaveSTL(path);
        public static void SaveSTP(this INode node, string path) => node.Generate().SaveSTP(path);

        public static INode Translate(this INode node, Vector3 position) => new Translate
        {
            Child = node,
            Position = position
        };

        public static INode Translate(this INode node, float x, float y, float z) => node.Translate(new Vector3(x, y, z));

        public static INode Subtract(this INode node, INode other) => new Subtract()
        {
            Children = {node, other}
        };

        public static INode Union(this INode node, INode other) => new Union()
        {
            Children = { node, other }
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

        public static INode Round(this INode node, float radius, Func<Edge[], IEnumerable<Edge>> predicate) => new Round()
        {
            Child = node,
            Definitions = new List<RadiusDefinition>()
            {
                new(radius,predicate(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList())
            }
        };

        public static INode Round(this INode node, params ValueTuple<float, Func<Edge[], IEnumerable<Edge>>>[] predicates) => new Round()
        {
            Child = node,
            Definitions = predicates.Select(tuple => new RadiusDefinition(tuple.Item1, tuple.Item2(node.Edges.ToArray()).Select(edge => edge.Wrapper).ToList())).ToList()
        };

        public static Extrude Extrude(this IClosedSketch sketch, float distance) => new Extrude(sketch, distance);

    }
}
