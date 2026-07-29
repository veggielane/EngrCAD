using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class BomTests
{
    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(1, 1, 1));

    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    [Fact]
    public void CountsOccurrencesPerDistinctPart()
    {
        var plate = BoxPart("plate");
        var bolt = BoxPart("bolt");
        var clamp = new Assembly("clamp");
        clamp.Add(plate);
        for (int i = 0; i < 4; i++)
            clamp.Add(bolt, At(i, 0, 0));

        var bom = Bom.For(clamp);

        Assert.Equal(2, bom.LineCount);
        Assert.Equal(5, bom.TotalQuantity);
        Assert.Equal(4, bom.Lines.Single(l => l.Item == "bolt").Quantity);
        Assert.Equal(1, bom.Lines.Single(l => l.Item == "plate").Quantity);
    }

    [Fact]
    public void LinesCarryTheOccurrencePaths()
    {
        var bolt = BoxPart("bolt");
        var clamp = new Assembly("clamp");
        clamp.Add(bolt);
        clamp.Add(bolt, At(1, 0, 0));

        var line = Bom.For(clamp).Lines.Single();

        Assert.Equal(["clamp/bolt", "clamp/bolt.2"], line.Paths);
    }

    [Fact]
    public void NestedSubAssembliesRollUp()
    {
        var plate = BoxPart("plate");
        var bolt = BoxPart("bolt");
        var clamp = new Assembly("clamp");
        clamp.Add(plate);
        clamp.Add(bolt);
        clamp.Add(bolt, At(1, 0, 0));

        var stack = new Assembly("stack");
        stack.Add(clamp);
        stack.Add(clamp, At(0, 0, 5));
        stack.Add(bolt, At(9, 9, 9));   // one loose bolt at the top level too

        var bom = Bom.For(stack);

        // 2 clamps x 2 bolts + 1 loose = 5 bolts; 2 clamps x 1 plate = 2 plates.
        Assert.Equal(5, bom.Lines.Single(l => l.Item == "bolt").Quantity);
        Assert.Equal(2, bom.Lines.Single(l => l.Item == "plate").Quantity);
        Assert.Equal(7, bom.TotalQuantity);
    }

    [Fact]
    public void StructuredBomTotalsAgreeWithTheFlatList()
    {
        var plate = BoxPart("plate");
        var bolt = BoxPart("bolt");
        var clamp = new Assembly("clamp");
        clamp.Add(plate);
        clamp.Add(bolt);
        clamp.Add(bolt, At(1, 0, 0));
        clamp.Add(bolt, At(2, 0, 0));

        var stack = new Assembly("stack");
        stack.Add(clamp);
        stack.Add(clamp, At(0, 0, 5));

        var flat = Bom.For(stack);
        var root = Bom.Structured(stack);

        // Per-level quantity: the clamp appears twice, each clamp holds three bolts.
        var clampNode = root.Children.Single();
        Assert.Equal(2, clampNode.Quantity);
        Assert.Equal(2, clampNode.TotalQuantity);
        var boltNode = clampNode.Children.Single(c => c.Name == "bolt");
        Assert.Equal(3, boltNode.Quantity);
        Assert.Equal(6, boltNode.TotalQuantity);

        // ...and the leaf totals are exactly the flat list.
        foreach (var (node, _) in root.Flatten())
        {
            if (node.Part is { } part)
                Assert.Equal(flat.Lines.Single(l => ReferenceEquals(l.Part, part)).Quantity, node.TotalQuantity);
        }
    }

    [Fact]
    public void HardwareLinesCarryTheirCatalogueItem()
    {
        var screw = StandardComponents.CapScrew(4, 16);
        var assembly = new Assembly("rig");
        assembly.Add(BoxPart("housing"));
        var part = screw.ToPart();
        assembly.Add(part);
        assembly.Add(part, At(10, 0, 0));

        var bom = Bom.For(assembly);
        var line = bom.Hardware.Single();

        Assert.Same(screw, line.Hardware);
        Assert.Equal(screw.Designation, line.Item);
        Assert.Equal(2, line.Quantity);
        Assert.Equal("housing", bom.Manufactured.Single().Item);
    }

    [Fact]
    public void ComponentAssemblyPlacementsBecomeABom()
    {
        var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        var screw = StandardComponents.CapScrew(4, 16);
        build.Place(screw, [new(-20, 0), new(20, 0), new(0, 12)], top);

        var bom = Bom.For(build.ToAssembly());

        Assert.Equal(3, bom.Hardware.Single().Quantity);
        Assert.Same(screw, bom.Hardware.Single().Hardware);
        Assert.Equal("plate", bom.Manufactured.Single().Item);
        // Suppressing the placement removes the bore AND the BOM line.
        build.Suppress(build.Placements.First());
        Assert.Empty(Bom.For(build.ToAssembly()).Hardware);
    }

    [Fact]
    public void ByItemRollsUpSeparatelyBuiltButIdenticallyDesignatedComponents()
    {
        var assembly = new Assembly("rig");
        assembly.Add(StandardComponents.CapScrew(4, 16).ToPart());
        assembly.Add(StandardComponents.CapScrew(4, 16).ToPart(), At(10, 0, 0));

        var bom = Bom.For(assembly);

        Assert.Equal(2, bom.LineCount);              // two distinct parts, honestly
        var rolled = bom.ByItem().Single();          // ...one purchasing line
        Assert.Equal(2, rolled.Quantity);
    }

    [Fact]
    public void TabAndSceneBomsIncludeLoosePartsAndAssemblies()
    {
        var scene = new Scene();
        var tab = scene.AddTab("model");
        tab.Add(BoxPart("jig"));
        var assembly = new Assembly("clamp");
        var bolt = BoxPart("bolt");
        assembly.Add(bolt);
        assembly.Add(bolt, At(1, 0, 0));
        tab.Add(assembly);

        var bom = Bom.For(tab);
        Assert.Equal(2, bom.LineCount);
        Assert.Equal(3, bom.TotalQuantity);
        Assert.Equal(bom.TotalQuantity, Bom.For(scene).TotalQuantity);
    }

    [Fact]
    public void CsvQuotesFieldsAndListsEveryPath()
    {
        var part = BoxPart("bracket, left");
        var assembly = new Assembly("rig");
        assembly.Add(part);
        assembly.Add(part, At(1, 0, 0));

        string csv = Bom.For(assembly).ToCsv();

        Assert.StartsWith("Quantity,Item,Kind,Paths\n", csv);
        Assert.Contains("\"bracket, left\"", csv);
        Assert.Contains("\"rig/bracket, left;rig/bracket, left.2\"", csv);
    }

    [Fact]
    public void TextRendersAlignedAndSummarizes()
    {
        var assembly = new Assembly("rig");
        var bolt = BoxPart("bolt");
        for (int i = 0; i < 5; i++)
            assembly.Add(bolt, At(i, 0, 0));

        string text = Bom.For(assembly).ToText();

        Assert.Contains("QTY", text);
        Assert.Contains("+2 more", text);        // 5 paths, 3 shown
        Assert.Contains("1 item, 5 occurrences", text);
    }

    [Fact]
    public void EmptyAssemblyRendersHonestly()
    {
        Assert.Equal("(empty bill of materials)", Bom.For(new Assembly("empty")).ToText());
        Assert.Equal(0, Bom.For(new Assembly("empty")).TotalQuantity);
    }

    // ---- materials and mass --------------------------------------------------------

    /// <summary>A 100 x 20 x 5 mm plate — 10 000 mm3, so 27 g in aluminium.</summary>
    private static Part PlatePart(string name, Material? material = null) =>
        new Part(name, Shape.Box(100, 20, 5)).Of(material);

    [Fact]
    public void NoMaterialsAnywhere_LeavesTheReportsExactlyAsTheyWere()
    {
        // The rule that keeps every existing caller byte-identical: a column that would be
        // empty on every row is not printed.
        var assembly = new Assembly("rig");
        assembly.Add(BoxPart("bolt"));
        var bom = Bom.For(assembly);

        Assert.False(bom.HasMaterials);
        Assert.DoesNotContain("MATERIAL", bom.ToText());
        Assert.StartsWith("Quantity,Item,Kind,Paths\n", bom.ToCsv());
    }

    [Fact]
    public void OneMaterialAnywhere_AddsTheColumnToEveryRow()
    {
        var assembly = new Assembly("rig");
        assembly.Add(PlatePart("plate", Materials.Aluminium6061));
        assembly.Add(BoxPart("shim"));
        var bom = Bom.For(assembly);

        Assert.True(bom.HasMaterials);
        string text = bom.ToText();
        Assert.Contains("MATERIAL", text);
        Assert.Contains("Aluminium 6061-T6", text);
        // A part with no material says so rather than looking like the row above it.
        Assert.Contains("-", text);
        Assert.StartsWith("Quantity,Item,Kind,Material,Paths\n", bom.ToCsv());
    }

    [Fact]
    public void MassIsOptIn_AndTotalsInGrams()
    {
        var plate = PlatePart("plate", Materials.Aluminium6061);
        var assembly = new Assembly("rig");
        assembly.Add(plate);
        assembly.Add(plate, At(0, 40, 0));
        var bom = Bom.For(assembly);

        Assert.Equal(27.0, bom.Lines[0].UnitMassGrams!.Value, 6);
        Assert.Equal(54.0, bom.Lines[0].TotalMassGrams!.Value, 6);

        // Opt-in, because it is the only part of a BOM that evaluates geometry.
        Assert.DoesNotContain("MASS", bom.ToText());
        string text = bom.ToText(mass: true);
        Assert.Contains("MASS (g)", text);
        Assert.Contains("TOTAL (g)", text);
        Assert.Contains("27", text);
        Assert.Contains("54 g", text);            // the footer total
        Assert.Contains("UnitMassGrams,TotalMassGrams", bom.ToCsv(mass: true));
    }

    [Fact]
    public void AnUnknownMassIsEmpty_NotZero_AndTheTotalSaysWhatItCovers()
    {
        // A spreadsheet sums zeros silently, so an unstated mass must not look like one --
        // and a total that quietly skipped those parts would read as the assembly's weight.
        var assembly = new Assembly("rig");
        assembly.Add(PlatePart("plate", Materials.Aluminium6061));
        assembly.Add(PlatePart("mystery"));
        var bom = Bom.For(assembly);

        Assert.Null(bom.Lines.Single(l => l.Item == "mystery").UnitMassGrams);
        Assert.Contains("over the 1 of 2 items stating a material", bom.ToText(mass: true));

        string csv = bom.ToCsv(mass: true);
        var mysteryRow = csv.Split('\n').Single(l => l.StartsWith("1,mystery,"));
        Assert.Contains(",,,", mysteryRow);       // two empty mass cells, not two zeros
    }
}
