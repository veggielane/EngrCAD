using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;

namespace EngrCAD.Mcp.Tests;

/// <summary>Scene fixtures shared by the tool tests — deliberately coarse and small,
/// because these tests mesh for real.</summary>
internal static class TestScenes
{
    internal static MeshQuality Coarse => new() { SegmentsPerCircle = 16, CurveSamples = 12, SdfResolution = 16 };

    /// <summary>Pin radius/height — the analytic ground truth for the volume assertions.</summary>
    internal const double PinRadius = 2;
    internal const double PinHeight = 14;

    /// <summary>Two tabs: a Shape union plus a placed cylinder, and an SDF sphere.</summary>
    internal static Scene Basic(Sdf? blob = null)
    {
        var scene = new Scene(Coarse);
        var model = scene.AddTab("Model");
        model.Add(new Part("bracket", Shape.Box(20, 12, 6) | Shape.Cylinder(3, 10).Translate(6, 0, 3)));
        model.Add(new Part("pin", Shape.Cylinder(PinRadius, PinHeight),
            transform: Matrix4d.CreateTranslation(new Vector3d(-6, 0, 7))));
        var field = scene.AddTab("field");
        field.Add(new Part("blob", blob ?? Sdf.Sphere(5)));
        return scene;
    }

    /// <summary>An SDF that counts evaluations — the laziness probe. If a tool that
    /// should not touch geometry has touched it, <see cref="Evaluations"/> is non-zero.</summary>
    internal sealed class CountingSdf : Sdf
    {
        private int _evaluations;

        public int Evaluations => Volatile.Read(ref _evaluations);

        public override double Evaluate(in Vector3d point)
        {
            Interlocked.Increment(ref _evaluations);
            return point.Length - 5;
        }

        public override Aabb Bounds => new(new Vector3d(-5, -5, -5), new Vector3d(5, 5, 5));
    }

    /// <summary>An SDF that cannot be meshed — the failure path a tool must report as a
    /// message rather than an exception.</summary>
    internal sealed class ThrowingSdf : Sdf
    {
        public override double Evaluate(in Vector3d point) =>
            throw new InvalidOperationException("this field is deliberately broken");

        public override Aabb Bounds => new(new Vector3d(-1, -1, -1), new Vector3d(1, 1, 1));
    }
}
