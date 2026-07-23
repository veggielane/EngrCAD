using System.Reflection.Metadata;
using EngrCAD.Viewer;

[assembly: MetadataUpdateHandler(typeof(HotReloadHandler))]

namespace EngrCAD.Viewer;

/// <summary>
/// Invoked by the hot-reload runtime (dotnet watch) after code patches are applied.
/// Patched method bodies don't re-execute by themselves — this is the hook that makes
/// <see cref="EngrCad.ShowLive"/> re-run the scene factory and swap the result in.
/// </summary>
internal static class HotReloadHandler
{
    public static void UpdateApplication(Type[]? updatedTypes) => EngrCad.OnHotReload();
}
