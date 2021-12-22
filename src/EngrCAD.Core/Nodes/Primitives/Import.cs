using System;
using System.IO;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes.Primitives;

public class Import : BaseNode
{
    public string FilePath { get; init; }

    public override NativeWrapper Generate()
    {
        return Path.GetExtension(FilePath)?.ToLowerInvariant() switch
        {
            ".stp" or ".step" => NativeWrapper.ImportSTP(FilePath),
            _ => throw new NotImplementedException("Extension not supported")
        };
    }
}