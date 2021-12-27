﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCAD.Core.Edges;
using EngrCAD.Core.Nodes.Operations;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public abstract partial class Node
    {
        public static Node operator +(Node a, Node b) => a.Union(b);
        public static Node operator -(Node a, Node b) => a.Subtract(b);

        public static Node operator &(Node a, Node b) => a.Intersect(b);
        public static Node operator |(Node a, Node b) => a.Union(b);
        public static Node operator ^(Node a, Node b) => a.Subtract(b);

        public Node Union(params Node[] others) => new Union(this, others);
        public Node Union(IEnumerable<Node> nodes) => new Union(nodes);
        public Node Subtract(params Node[] others) => new Subtract(this, others);
        public Node Subtract(IEnumerable<Node> others) => new Subtract(this, others);
        public Node Intersect(params Node[] others) => new Intersect(this, others);
        public Node Intersect(IEnumerable<Node> others) => new Intersect(this, others);
        
        /// <summary>
        /// Round All Edges
        /// </summary>
        /// <example>
        /// node.Round(5f);
        /// </example>
        /// <param name="radius">Round Radius</param>
        /// <returns>Node with all edges rounded</returns>
        public Node Round(float radius) => new Round(this, new RadiusDefinition(radius, Edges.Select(edge => edge.Wrapper).ToList()));

        /// <summary>
        /// Round edges filtered by predicate
        /// </summary>
        /// <example>
        /// To select all edges that are parallel to Z:
        /// node.Round(5f,e => edges.OfType&lt;EdgeLine&gt;().Where(line => line.ParallelTo(Vector3.UnitZ)));
        /// </example>
        /// <param name="predicate">Predicate to select Edge</param>
        /// <returns></returns>
        public Node Round(Func<IEdge, float?> predicate) => new Round(this, Edges.Select(edge => (edge, predicate(edge))).Where(tuple => tuple.Item2.HasValue).Select(tuple => new RadiusDefinition(tuple.Item2.Value, new List<EdgeWrapper>() { tuple.edge.Wrapper })).ToList());
        public Node Round(float radius, Func<IEdge[], IEnumerable<IEdge>> filter) => new Round(this, new RadiusDefinition(radius, filter(Edges.ToArray()).Select(edge => edge.Wrapper).ToList()));
        public Node Round(params ValueTuple<float, Func<IEdge[], IEnumerable<IEdge>>>[] filter) => new Round(this, filter.Select(tuple => new RadiusDefinition(tuple.Item1, tuple.Item2(Edges.ToArray()).Select(edge => edge.Wrapper).ToList())).ToList());
    }
}
