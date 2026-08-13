using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The 3D model as a first-class peer of the symbol and footprint: backward compatibility with the
/// legacy <see cref="PartDefinition.Body"/>, the seating identities (offset shift, exact quarter
/// turn, exact scale), the side reflection, file loading and refusals, persistence, and the KiCad
/// model reference. ECAD fails plausibly, so these are closed forms and identities, not pictures.
/// </summary>
public class ComponentModelTests
{
    // ---- 1. backward compatibility: a Body-only part is bit-identical -------

    [Fact]
    public void BodyOnlyDefinition_SeatsBitIdentically_ToTheRawBody()
    {
        // A definition with only the legacy Body (no Model) must produce an assembly whose body
        // part's mesh is bit-for-bit the raw body's — no transform crept into the legacy path.
        var layout = OneComponentLayout(WithBody(), CopperSide.Top);
        var seated = SeatedInstance(layout);

        var raw = new Part("x", WithBody().Body!()).GetMesh();
        AssertMeshBitIdentical(raw, seated.Part.GetMesh());
    }

    [Fact]
    public void CodeModel_WithIdentityPlacement_IsTheLegacyBody_SeatedBitIdentically()
    {
        // "Body is the legacy spelling of a code model with the identity placement": the two seat
        // identically, so ToAssembly seats Model ?? (Body as identity-placed model).
        var bodyLayout = OneComponentLayout(WithBody(), CopperSide.Top);
        var codeModelLayout = OneComponentLayout(WithCodeModel(ModelPlacement.Identity), CopperSide.Top);

        AssertMeshBitIdentical(
            SeatedInstance(bodyLayout).Part.GetMesh(),
            SeatedInstance(codeModelLayout).Part.GetMesh());
    }

    // ---- 2. seating identities (closed form) --------------------------------

    [Fact]
    public void ModelOffset_ShiftsTheSeatedBounds_ByExactlyThatOffset()
    {
        var offset = new Vector3d(3.5, -2.25, 1.75);
        var baseBounds = SeatedBounds(OneComponentLayout(WithCodeModel(ModelPlacement.Identity), CopperSide.Top));
        var shifted = SeatedBounds(OneComponentLayout(WithCodeModel(new ModelPlacement(offset)), CopperSide.Top));

        // A pure translation in the footprint frame, seated on a top face at rotation 0, is exactly
        // that translation in world — the shift is additive and exact.
        Assert.Equal(offset.X, shifted.Min.X - baseBounds.Min.X, 12);
        Assert.Equal(offset.Y, shifted.Min.Y - baseBounds.Min.Y, 12);
        Assert.Equal(offset.Z, shifted.Min.Z - baseBounds.Min.Z, 12);
        Assert.Equal(offset.X, shifted.Max.X - baseBounds.Max.X, 12);
        Assert.Equal(offset.Y, shifted.Max.Y - baseBounds.Max.Y, 12);
        Assert.Equal(offset.Z, shifted.Max.Z - baseBounds.Max.Z, 12);
    }

    [Fact]
    public void QuarterTurnAboutZ_TransposesTheFootprintPlaneBounds_Exactly()
    {
        // The box body is 4 × 2 × 1; a 90° rotate about Z is a sign swap (x, y) -> (-y, x), NOT a
        // cos, so the transpose is exact to the last bit.
        var id = SeatedBounds(OneComponentLayout(WithCodeModel(ModelPlacement.Identity), CopperSide.Top));
        var turned = SeatedBounds(OneComponentLayout(
            WithCodeModel(new ModelPlacement(Vector3d.Zero, new Vector3d(0, 0, 90))), CopperSide.Top));

        Assert.Equal(4.0, id.Size.X);      // the body's own extents
        Assert.Equal(2.0, id.Size.Y);
        Assert.Equal(id.Size.Y, turned.Size.X);   // transposed EXACTLY
        Assert.Equal(id.Size.X, turned.Size.Y);
        Assert.Equal(id.Size.Z, turned.Size.Z);   // the out-of-plane extent is unchanged
    }

    [Fact]
    public void Scale_ScalesTheSeatedBounds_ByExactlyTheFactor()
    {
        var factors = new Vector3d(2, 3, 4);
        var id = SeatedBounds(OneComponentLayout(WithCodeModel(ModelPlacement.Identity), CopperSide.Top));
        var scaled = SeatedBounds(OneComponentLayout(
            WithCodeModel(new ModelPlacement(Vector3d.Zero, Vector3d.Zero, factors)), CopperSide.Top));

        Assert.Equal(id.Size.X * factors.X, scaled.Size.X, 12);
        Assert.Equal(id.Size.Y * factors.Y, scaled.Size.Y, 12);
        Assert.Equal(id.Size.Z * factors.Z, scaled.Size.Z, 12);
    }

    // ---- 3. the bottom-side reflection --------------------------------------

    [Fact]
    public void SideReflection_IsAMirror_AndTheModelHangsBelow()
    {
        // The reflection lives on the part transform; its square is the identity (Mirror(Mirror(x)) == x).
        var m = PcbLayout.PartTransform(CopperSide.Bottom);
        Assert.Equal(Matrix4d.Identity, m * m);

        // A body sitting at z in [0, 0.5] (proud of its seat) hangs BELOW the board on the bottom.
        var bounds = SeatedBounds(OneComponentLayout(WithProudBody(), CopperSide.Bottom));
        Assert.True(bounds.Max.Z <= 1e-12, $"the bottom-side body should hang below z = 0; max z = {bounds.Max.Z}");
        Assert.Equal(-0.5, bounds.Min.Z, 12);
    }

    [Fact]
    public void ModelPlacement_IsAppliedInTheFootprintFrame_BeforeTheSideReflection()
    {
        // On the bottom, the reflection flips z AFTER the model placement, so a model offset
        // (dx, dy, dz) moves the seated body by (dx, dy, -dz): the placement is in the footprint
        // frame, applied before the reflection.
        var offset = new Vector3d(1.5, -0.75, 0.5);
        var baseC = SeatedBounds(OneComponentLayout(WithCodeModel(ModelPlacement.Identity), CopperSide.Bottom)).Center;
        var offC = SeatedBounds(OneComponentLayout(WithCodeModel(new ModelPlacement(offset)), CopperSide.Bottom)).Center;

        Assert.Equal(offset.X, offC.X - baseC.X, 12);
        Assert.Equal(offset.Y, offC.Y - baseC.Y, 12);
        Assert.Equal(-offset.Z, offC.Z - baseC.Z, 12);   // z is reflected
    }

    // ---- 4. file loading + refusals -----------------------------------------

    [Fact]
    public void FileReferencedStepModel_LoadsAndSeats()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ecad-model-{Guid.NewGuid():N}.step");
        StepWriter.WriteFile(Shape.Box(4, 2, 1).ToBrep(), path);
        try
        {
            var model = ComponentModel3D.FromFile(path);
            Assert.True(model.IsFileReferenced);
            Assert.True(model.CanLoad);

            // The loaded body has the box's own extents.
            var bounds = model.Load().Bounds();
            Assert.Equal(4.0, bounds.Size.X, 6);
            Assert.Equal(2.0, bounds.Size.Y, 6);
            Assert.Equal(1.0, bounds.Size.Z, 6);

            // ...and it seats into an assembly (a real 3D occurrence, plus the board).
            var def = new PartDefinition("R_STEP", "R",
                [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
                new Footprint("F", [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)]),
                model: model);
            var seated = SeatedBounds(OneComponentLayout(def, CopperSide.Top));
            Assert.Equal(4.0, seated.Size.X, 6);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_IsANotLoadedReference_NotAThrow()
    {
        var model = ComponentModel3D.FromFile("does-not-exist.stl");
        Assert.True(model.CanLoad);   // the extension is supported; existence is checked at load
        var shape = model.TryLoad(out var error);
        Assert.Null(shape);
        Assert.NotNull(error);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void VrmlModel_Loads_WithTheKiCadUnitConvention()
    {
        // KiCad's default 3D format. VrmlReader reads coordinates VERBATIM (VRML is unitless);
        // the KiCad convention — 1 VRML unit = 0.1 inch = 2.54 mm — is applied HERE, at the
        // consumer that knows a component model's .wrl is KiCad's. So a unit cube in the file
        // seats as a 2.54 mm body.
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.wrl");
        System.IO.File.WriteAllText(path, """
            #VRML V2.0 utf8
            Shape {
              geometry IndexedFaceSet {
                coord Coordinate { point [ 0 0 0, 1 0 0, 1 1 0, 0 1 0, 0 0 1, 1 0 1, 1 1 1, 0 1 1 ] }
                coordIndex [ 0 3 2 1 -1, 4 5 6 7 -1, 0 1 5 4 -1, 1 2 6 5 -1, 2 3 7 6 -1, 3 0 4 7 -1 ]
              }
            }
            """);
        try
        {
            var model = ComponentModel3D.FromFile(path);
            Assert.True(model.CanLoad);
            var shape = model.TryLoad(out var error);
            Assert.NotNull(shape);
            Assert.Null(error);
            var bounds = shape!.Bounds();
            Assert.Equal(2.54, bounds.Size.X, 9);
            Assert.Equal(2.54, bounds.Size.Y, 9);
            Assert.Equal(2.54, bounds.Size.Z, 9);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void IgesModel_IsRecordedButRefusedByName()
    {
        var model = ComponentModel3D.FromFile("/models/part.igs");
        Assert.False(model.CanLoad);
        Assert.Null(model.TryLoad(out var error));
        Assert.Contains("IGES", error);
    }

    [Fact]
    public void UnloadableModel_LeavesTheAssemblyWithoutA3dOccurrence_ButPadsArePlaced()
    {
        var def = new PartDefinition("R_IGS", "R",
            [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
            new Footprint("F", [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)]),
            model: ComponentModel3D.FromFile("/models/R_0805.igs"));
        var layout = OneComponentLayout(def, CopperSide.Top);

        // No 3D occurrence for the .igs part — only the board — but the pads are still placed.
        var instances = layout.ToAssembly().Flatten();
        Assert.Single(instances);
        Assert.Equal("board", instances[0].Part.Name);
        Assert.Equal(2, layout.PlacedPads().Count);
        Assert.True(layout.Check().Ok);
    }

    // ---- 5. the model is geometry, not connectivity -------------------------

    [Fact]
    public void PinIdentity_IsUnaffectedByTheModel()
    {
        var pins = new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) };
        var footprint = new Footprint("F",
            [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)]);
        var symbol = new Symbol("S",
            [new SymbolPin("1", "", new Vector2d(0, 2), SymbolPinDirection.Down, 1, PinType.Passive),
             new SymbolPin("2", "", new Vector2d(0, -2), SymbolPinDirection.Up, 1, PinType.Passive)]);

        var without = new PartDefinition("R", "R", pins, footprint, symbol: symbol);
        // A geometrically nonsensical model must not touch pin identity — the model is geometry.
        var wrongModel = ComponentModel3D.FromShape(() => Shape.Box(999, 999, 999));
        var with = new PartDefinition("R", "R", pins, footprint, symbol: symbol, model: wrongModel);

        Assert.True(PinIdentity.Check(without).Ok);
        Assert.True(PinIdentity.Check(with).Ok);
    }

    // ---- 6. persistence -----------------------------------------------------

    [Fact]
    public void FileReferencedModel_RoundTrips_AsAByteIdenticalFixedPoint()
    {
        var sch = SchematicWithFileModel();
        var s1 = sch.Save();
        var loaded = Schematic.Load(s1);
        var s2 = loaded.Save();
        Assert.Equal(s1, s2);

        // The model came back as a file reference with the same path and placement.
        var model = loaded.Find("R1")!.Definition.Model;
        Assert.NotNull(model);
        Assert.True(model!.IsFileReferenced);
        Assert.Equal("models/R_0805.step", model.FilePath);
        Assert.Equal(new Vector3d(0.1, 0.2, 0.3), model.Placement.Offset);
        Assert.Equal(new Vector3d(0, 0, 90), model.Placement.RotationDegrees);
        Assert.Equal(new Vector3d(1, 1, 2), model.Placement.Scale);
    }

    [Fact]
    public void ModelLessDefinition_WritesNoModelKey()
    {
        // A definition with no model saves byte-identically to a pre-model file: no "model" key.
        Assert.DoesNotContain("\"model\"", Fixtures.LedIndicator().Save());
    }

    [Fact]
    public void CodeModel_IsOpaque_AndNotSerialized()
    {
        var def = new PartDefinition("R", "R",
            [new Pin("1", PinType.Passive)],
            model: ComponentModel3D.FromShape(() => Shape.Box(1, 1, 1)));
        var sch = new Schematic("code");
        sch.Add("R1", def);
        sch.Stub("T1", sch.Find("R1")!.Pin("1"));

        // A code model does not travel (it is opaque, like the legacy Body).
        Assert.DoesNotContain("\"model\"", sch.Save());
        var reloaded = Schematic.Load(sch.Save());
        Assert.Null(reloaded.Find("R1")!.Definition.Model);
    }

    [Fact]
    public void BoardWithAFileModel_RoundTrips_AsAByteIdenticalFixedPoint()
    {
        var sch = SchematicWithFileModel();
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("R1", 4, 3, 90, CopperSide.Top);

        var s1 = layout.Save();
        var s2 = PcbLayout.Load(s1).Save();
        Assert.Equal(s1, s2);

        var model = PcbLayout.Load(s1).Schematic.Find("R1")!.Definition.Model;
        Assert.Equal("models/R_0805.step", model!.FilePath);
    }

    // ---- 7. the KiCad model reference ---------------------------------------

    [Fact]
    public void KiCadFootprint_CarriesItsModelReference()
    {
        var footprint = KiCadFootprintReader.Read(ModWithModel);

        // The component reader stays otherwise bit-identical: the pads are unchanged.
        Assert.Equal(2, footprint.Footprint.Pads.Count);
        Assert.Equal(-0.9125, footprint.Footprint.Pads[0].Center.X);

        var model = footprint.Model;
        Assert.NotNull(model);
        Assert.True(model!.IsFileReferenced);
        Assert.EndsWith("R_0805_2012Metric.step", model.FilePath);
        Assert.Equal(new Vector3d(0.25, -0.5, 0.75), model.Placement.Offset);
        Assert.Equal(new Vector3d(0, 0, 90), model.Placement.RotationDegrees);
        Assert.Equal(new Vector3d(1, 1, 1), model.Placement.Scale);
    }

    [Fact]
    public void ComponentLibrary_LoadedPart_CarriesTheModelReference()
    {
        var part = ComponentLibrary.Read(KiCadFixtures.ResistorSym, ModWithModel);
        var model = part.Definition.Model;
        Assert.NotNull(model);
        Assert.EndsWith("R_0805_2012Metric.step", model!.FilePath);
        // The symbol and footprint identity still holds — the model changed nothing.
        Assert.True(part.Identity.Ok);
    }

    // ---- fixtures + helpers -------------------------------------------------

    // A distinctive body: a 4 × 2 × 1 box CENTERED on the footprint origin, so its extents read
    // 4/2/1 and a quarter turn transposes them.
    private static Func<Shape> BoxBody => () => Shape.Box(4, 2, 1);

    private static PartDefinition WithBody() => new(
        "R_BODY", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("F", [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)]),
        body: BoxBody);

    private static PartDefinition WithCodeModel(ModelPlacement placement) => new(
        "R_MODEL", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("F", [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)]),
        model: ComponentModel3D.FromShape(BoxBody, placement));

    // A body sitting proud of its seat (bottom at z = 0, top at z = 0.5) so the reflection is visible.
    private static PartDefinition WithProudBody() => new(
        "R_PROUD", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("F", [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)]),
        model: ComponentModel3D.FromShape(() => Shape.Box(2, 1, 0.5).Translate(0, 0, 0.25)));

    private static PcbLayout OneComponentLayout(PartDefinition def, CopperSide side)
    {
        var sch = new Schematic("one");
        var c = sch.Add("R1", def);
        // Give the pins a home so the schematic is well-formed (not required by ToAssembly).
        sch.Stub("T1", c.Pin("1"));
        sch.Stub("T2", c.Pin("2"));
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("R1", 0, 0, 0, side);
        return layout;
    }

    private static PartInstance SeatedInstance(PcbLayout layout) =>
        layout.ToAssembly().Flatten().Single(i => i.Part.Name != "board");

    private static Aabb SeatedBounds(PcbLayout layout)
    {
        var inst = SeatedInstance(layout);
        return inst.Part.Bounds(inst.World);
    }

    private static Schematic SchematicWithFileModel()
    {
        var def = new PartDefinition("R_0805", "R",
            [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
            new Footprint("F", [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)]),
            model: ComponentModel3D.FromFile(
                "models/R_0805.step",
                new ModelPlacement(new Vector3d(0.1, 0.2, 0.3), new Vector3d(0, 0, 90), new Vector3d(1, 1, 2))));
        var sch = new Schematic("modelled");
        var c = sch.Add("R1", def, "330");
        sch.Stub("T1", c.Pin("1"));
        sch.Stub("T2", c.Pin("2"));
        return sch;
    }

    private static void AssertMeshBitIdentical(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        Assert.Equal(a.VertexCount, b.VertexCount);
        for (int i = 0; i < a.VertexCount; i++)
        {
            var pa = a.GetPosition(i);
            var pb = b.GetPosition(i);
            Assert.Equal(BitConverter.DoubleToInt64Bits(pa.X), BitConverter.DoubleToInt64Bits(pb.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(pa.Y), BitConverter.DoubleToInt64Bits(pb.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(pa.Z), BitConverter.DoubleToInt64Bits(pb.Z));
        }
    }

    // A KiCad footprint with a (model …) block (a .step so it is loadable; the placement is
    // non-trivial so the offset/rotate/scale mapping is exercised).
    private const string ModWithModel = """
(footprint "R_0805_2012Metric"
  (layer "F.Cu")
  (attr smd)
  (pad "1" smd roundrect (at -0.9125 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask"))
  (pad "2" smd roundrect (at 0.9125 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask"))
  (model "${KICAD6_3DMODEL_DIR}/Resistor_SMD.3dshapes/R_0805_2012Metric.step"
    (offset (xyz 0.25 -0.5 0.75))
    (scale (xyz 1 1 1))
    (rotate (xyz 0 0 90))))
""";
}
