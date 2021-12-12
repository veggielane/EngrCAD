using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core
{
    public class Face
    {
        private readonly FaceWrapper _wrapper;

        internal Face(FaceWrapper wrapper)
        {
            _wrapper = wrapper;
        }
    }

    public class Edge
    {
        public EdgeWrapper Wrapper { get; }

        internal Edge(EdgeWrapper wrapper)
        {
            Wrapper = wrapper;
        }
    }
}
