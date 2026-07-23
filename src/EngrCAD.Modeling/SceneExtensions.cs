using EngrCAD.Core;
using EngrCAD.Interop;

namespace EngrCAD.Modeling;

public static class SceneExtensions
{
    /// <summary>
    /// Adds a <see cref="Shape"/> to a scene, meshed via its highest-fidelity route
    /// (exact B-Rep tessellation when possible, SDF polygonization otherwise) at the
    /// scene's quality settings. The part's <c>Source</c> is the shape itself.
    /// </summary>
    public static Part Add(
        this Scene scene, string name, Shape shape, PartColor? color = null, Matrix4d? transform = null)
    {
        var quality = new MeshQuality
        {
            SegmentsPerCircle = scene.Options.SegmentsPerCircle,
            CurveSamples = scene.Options.CurveSamples,
            SdfResolution = scene.Options.SdfResolution,
        };
        return scene.AddPart(name, shape.ToMesh(quality), color, transform, shape);
    }
}
