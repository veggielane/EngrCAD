using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;

namespace EngrCAD.Core;

public abstract class Part<T> : IPart where T : class, IPartMetadata, new()
{
    public T Metadata { get; init; } = new T();
    public abstract Body Generate();

}

public interface IPart
{
    public Body Generate();
}