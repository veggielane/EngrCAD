﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations
{
    public class Round : BaseNode
    {
        public INode Child { get; init; }

        public float Radius { get; init; } = 0.1f;

        public override NativeWrapper Generate() => Child.Wrapper.Round(Radius);
    }
}
