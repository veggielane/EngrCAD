using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Part.Material"/> and the three things that read it: mass properties, the bill
/// of materials, and the default display color.
///
/// <para>The one <see cref="Material"/> type serves the document model and the FEA solvers,
/// so the density here is the same number a structural solve integrates — tonne/mm³ — and a
/// mass that comes back is in tonnes. That is what makes
/// <c>scene.AllInstances.MassProperties()</c> a one-liner instead of a question about whose
/// units it is written in.</para>
/// </summary>
public class PartMaterialTests
{
    // A 100 x 20 x 5 mm plate: 10 000 mm3.
    private static Part Plate(string name = "plate") => new(name, Shape.Box(100, 20, 5));

    [Fact]
    public void MassProperties_TakeTheirDensityFromTheMaterial()
    {
        var part = Plate().Of(Materials.Aluminium6061);

        var mp = part.MassProperties();

        Assert.Equal(10_000, mp.Volume, 6);
        // 10 000 mm3 x 2.70e-9 t/mm3 = 2.7e-5 t = 27 g.
        Assert.Equal(2.7e-5, mp.Mass, 12);
        Assert.Equal(27.0, ModelUnits.MassToGrams(mp.Mass), 6);
        Assert.Equal(27.0, part.MassGrams()!.Value, 6);
    }

    [Fact]
    public void NoMaterial_LeavesMassEqualToVolume_AndTheGramsAccessorUnknown()
    {
        // The honest answer when nobody has said what the part is made of: density 1, so
        // mass IS volume -- and MassGrams is null rather than zero, because an unstated
        // mass is unknown, not light.
        var part = Plate();

        Assert.Equal(part.MassProperties().Volume, part.MassProperties().Mass, 9);
        Assert.Null(part.MassGrams());
        Assert.Null(part.DisplayMassGrams());
    }

    [Fact]
    public void AnExplicitDensity_StillOverridesTheMaterial()
    {
        // The overload the task exists to keep: a part whose material is not modelled, or a
        // one-off "what would this weigh in brass" question.
        var part = Plate().Of(Materials.Aluminium6061);

        Assert.Equal(
            10_000 * Materials.Brass.Density,
            part.MassProperties(Materials.Brass.Density).Mass,
            12);
    }

    [Fact]
    public void AnAssemblyOfMixedMaterials_IsAOneLiner()
    {
        var steel = new Part("bracket", Shape.Box(10, 10, 10)).Of(Materials.Steel);
        var aluminium = new Part("cover", Shape.Box(10, 10, 10)).Of(Materials.Aluminium6061);
        var unstated = new Part("shim", Shape.Box(10, 10, 10));

        var assembly = new Assembly("mixed");
        assembly.Add(steel, Frame3d.WorldXY);
        assembly.Add(aluminium, Frame3d.FromXY((20, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        assembly.Add(unstated, Frame3d.FromXY((40, 0, 0), Vector3d.UnitX, Vector3d.UnitY));

        var total = assembly.Flatten().MassProperties();

        Assert.Equal(3000, total.Volume, 6);
        // The two stated parts contribute their real masses; the unstated one contributes
        // density 1 x its volume, which is the documented fallback and NOT silently zero.
        double expected = 1000 * Materials.Steel.Density
                        + 1000 * Materials.Aluminium6061.Density
                        + 1000 * 1.0;
        Assert.Equal(expected, total.Mass, 9);

        // The per-part override is still available and wins everywhere.
        var overridden = assembly.Flatten().MassProperties(_ => Materials.Steel.Density);
        Assert.Equal(3000 * Materials.Steel.Density, overridden.Mass, 12);
    }

    [Fact]
    public void MaterialColor_IsTheDefault_AndDoesNotConsumeAPaletteSlot()
    {
        // The stability rule: giving ONE part a colored material must not shift any other
        // part's palette color, so the cursor advances only when the palette is read.
        var anodised = Materials.Aluminium6061.WithColor(new PartColor(0.2f, 0.2f, 0.25f));
        var scene = new Scene();
        var painted = scene.Add(Plate("painted").Of(anodised));
        var first = scene.Add(Plate("first"));
        var second = scene.Add(Plate("second"));

        Assert.Equal(new PartColor(0.2f, 0.2f, 0.25f), painted.Color);
        Assert.Equal(Palette.Cycle[0], first.Color);
        Assert.Equal(Palette.Cycle[1], second.Color);
    }

    [Fact]
    public void AnExplicitColorStillWins_OverTheMaterialsOwn()
    {
        var anodised = Materials.Steel.WithColor(new PartColor(0.1f, 0.1f, 0.1f));
        var part = new Part("plate", Shape.Box(1, 1, 1), color: Palette.Rose).Of(anodised);
        new Scene().Add(part);

        Assert.Equal(Palette.Rose, part.Color);
    }

    [Fact]
    public void ACatalogueMaterialMovesNoPixels()
    {
        // No entry in Materials carries a color (appearance is a finish), so assigning one
        // leaves the part exactly where the palette put it -- which is what keeps every
        // committed docs render byte-identical across this feature.
        var scene = new Scene();
        var withMaterial = scene.Add(Plate("a").Of(Materials.Steel));
        var without = scene.Add(Plate("b"));

        Assert.Equal(Palette.Cycle[0], withMaterial.Color);
        Assert.Equal(Palette.Cycle[1], without.Color);
    }

    [Fact]
    public void DisplayMassGrams_ReadsTheDisplayMeshAndAgreesWithTheVolumeBesideIt()
    {
        // The properties-panel rule: never lower a B-Rep on the UI thread, and always agree
        // with the Volume row printed above it.
        var part = Plate().Of(Materials.Steel);
        var mesh = part.GetMesh();

        Assert.Equal(
            ModelUnits.MassToGrams(mesh.Volume() * Materials.Steel.Density),
            part.DisplayMassGrams()!.Value,
            9);
        // A box is planar-faced, so the display mesh and the exact solid agree exactly and
        // the two accessors give the same answer -- the difference only shows on curvature.
        Assert.Equal(part.MassGrams()!.Value, part.DisplayMassGrams()!.Value, 6);
    }

    [Fact]
    public void AnOpenMeshPartHasNoMass_JustAsItHasNoVolume()
    {
        var sheet = HalfEdgeMesh.Build([(0, 0, 0), (10, 0, 0), (10, 10, 0)], [new[] { 0, 1, 2 }]);

        var part = new Part("shell", sheet).Of(Materials.Steel);
        Assert.False(part.GetMesh().IsClosed);
        Assert.Null(part.DisplayMassGrams());
    }
}
