using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using EngrCAD.Core.Nodes.Transformations;

namespace EngrCAD.Core.Nodes
{
    public static class NodeExtensions
    {
        public static void SaveSTL(this INode node, string path) => node.Generate().SaveSTL(path);
        public static void SaveSTP(this INode node, string path) => node.Generate().SaveSTP(path);

        public static INode Translate(this INode node, Vector3 position) => new Translate
        {
            Children = { node },
            Position = position
        };

        public static INode Subtract(this INode node, INode other) => new Subtract()
        {
            Children = {node, other}
        };
    }
}
