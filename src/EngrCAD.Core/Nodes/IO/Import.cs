using System;
using System.IO;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.IO;

public class Import : Node
{
    public string FilePath { get; init; }

    public override ShapeWrapper Generate()
    {
        return Path.GetExtension(FilePath)?.ToLowerInvariant() switch
        {
            ".stp" or ".step" => ShapeWrapper.ImportSTP(FilePath),
            _ => throw new NotImplementedException("Extension not supported")
        };
    }
}