using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EngrCAD.Core;

public abstract class Part<T> where T : class, IPartMetadata, new()
{
    public string Name
    {
        get => Metadata.Name;
        set => Metadata.Name = value;
    }

    public T Metadata { get; init; } = new T();
    public abstract IEnumerable<Body> Generate();
}