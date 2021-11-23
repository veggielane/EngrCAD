using System.Collections.Generic;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public abstract class BaseNode : INode
    {
        private NativeWrapper _cached;
        public NativeWrapper Wrapper => _cached ??= Generate();
        public abstract NativeWrapper Generate();

        public float CalculateVolume() => Wrapper.CalculateVolume();

    }
}
