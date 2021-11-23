using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Transformations
{
    public class Translate: BaseNode
    {
        public Vector3 Position { get; init; } = Vector3.Zero;

        public override NativeWrapper Generate()
        {
            return Children.First().Generate().Translate(Position.X, Position.Y, Position.Z);
        }
    }
}
