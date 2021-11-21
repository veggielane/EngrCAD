using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations
{
    public class Translate: INodeWithChild
    {
        public INode Child { get; init; }

        public Vector3 Position { get; init; } = Vector3.Zero;

        public NativeWrapper Generate()
        {
            return Child.Generate().Translate(Position.X, Position.Y, Position.Z);
        }


    }
}
