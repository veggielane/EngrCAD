using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EngrCAD.Core.Nodes
{
    public static class NodeExtensions
    {

        public static void SaveSTP(this INode node, string path) => node.Wrap().Translate(10,10,10).SaveSTP(path);


    }
}
