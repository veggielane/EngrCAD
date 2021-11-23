using System.Numerics;
using EngrCAD.Core.Nodes.Operations;
using EngrCAD.Core.Nodes.Transformations;
using EngrCAD.Core.Sketcher;

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

        public static Extrude Extrude(this IClosedSketch sketch, float distance) => new Extrude(sketch, distance);
    }
}
