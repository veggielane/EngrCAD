﻿using System.Collections.Generic;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Operations;

public class Round : BaseNode
{
    public INode Child { get; init; }

    //public float Radius { get; init; } = 0.1f;

    public List<RadiusDefinition> Definitions = new();

    public override ShapeWrapper Generate() => Child.Wrapper.Round(Definitions);
}