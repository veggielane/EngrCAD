using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// Locates the built <c>EngrCAD.Mcp.TestModel</c> program — a real design program the
/// tests drive as a child process, over stdio (<c>--mcp</c>) or over its remote-control
/// socket (<c>--view --rpc</c>). The tests project references it with
/// <c>ReferenceOutputAssembly="false"</c>, so it is built beside us but not copied in.
/// </summary>
internal static class TestModelProgram
{
    public static string Executable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "tests")
            directory = directory.Parent;
        Assert.NotNull(directory);

        string name = OperatingSystem.IsWindows() ? "EngrCAD.Mcp.TestModel.exe" : "EngrCAD.Mcp.TestModel";
        var candidates = Directory.GetFiles(
            Path.Combine(directory.FullName, "EngrCAD.Mcp.TestModel"), name, SearchOption.AllDirectories);
        Assert.True(candidates.Length > 0, $"'{name}' was not built under {directory.FullName}");
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }
}
