using EngrCADOCWrapper;

namespace EngrCAD.Core;

public class Face
{
    private readonly FaceWrapper _wrapper;

    internal Face(FaceWrapper wrapper)
    {
        _wrapper = wrapper;
    }
}