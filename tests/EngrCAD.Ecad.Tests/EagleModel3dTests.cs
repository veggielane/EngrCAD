using System;
using System.IO;
using System.Linq;
using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The managed-library (Eagle 9 / Fusion) <c>&lt;packages3d&gt;</c> 3D-package BINDINGS, and the
/// newer XML's tolerance generally.
///
/// <para><b>The honesty rule is the whole feature</b>: a managed library binds a package to a 3D
/// package by URN, and that 3D package's model FILE is Fusion cloud content the <c>.lbr</c> does
/// not carry. So the reader surfaces the BINDING as data, attaches a <c>ComponentModel3D</c> only
/// when a caller-supplied resolver returns a LOCAL file that exists, and records the URN in the
/// diagnostics BY NAME otherwise — never guessing a path into geometry.</para>
///
/// <para>Everything is verified against SYNTHETIC fixtures built from the Eagle 9 XML's shape
/// (urn attributes, <c>packages3d</c>/<c>packageinstances</c>/<c>package3dinstances</c>); what
/// cannot be verified is diagnosed rather than assumed.</para>
/// </summary>
public sealed class EagleModel3dTests
{
    // A 2 x 3 x 4 box as OBJ — the smallest local model file whose BOUNDS discriminate all three
    // axes, so a body loaded through the binding cannot be confused with any other.
    private const string BoxObj = """
        v 0 0 0
        v 2 0 0
        v 2 3 0
        v 0 3 0
        v 0 0 4
        v 2 0 4
        v 2 3 4
        v 0 3 4
        f 1 4 3 2
        f 5 6 7 8
        f 1 2 6 5
        f 2 3 7 6
        f 3 4 8 7
        f 4 1 5 8
        """;

    private static string WriteBoxObj()
    {
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-eagle3d-{Guid.NewGuid():N}.obj");
        File.WriteAllText(path, BoxObj);
        return path;
    }

    // ==== 1. the bindings are read as DATA ====================================

    [Fact]
    public void AManagedLibrary_ListsItsPackages3dBindings()
    {
        var lib = EagleLibraryReader.Read(EagleFixtures.ManagedLibrary);

        // Both well-formed 3D packages are carried, in file order; the urn-less one is not.
        Assert.Equal(
            new[] { "RESC2012X70N", "RESC2012X70N-BOX" },
            lib.Packages3d.Select(p => p.Name));

        var model = lib.Packages3d[0];
        Assert.Equal("urn:adsk.eagle:package:23123/2", model.Urn);
        Assert.Equal("model", model.Type);                          // the file's own token, verbatim
        Assert.Equal(new[] { "R0805" }, model.PackageNames);

        // A "box" 3D package is an auto-generated extruded body; the type is DATA, not a refusal.
        Assert.Equal("box", lib.Packages3d[1].Type);
    }

    [Fact]
    public void ADevice_CarriesTheUrnItBinds()
    {
        var lib = EagleLibraryReader.Read(EagleFixtures.ManagedLibrary);

        var device = lib.Devices.Single(d => d.Name == "R-EU_R0805");
        Assert.Equal("urn:adsk.eagle:package:23123/2", device.Package3dUrn);

        // ... and the whole classic listing still reads the same way.
        Assert.Equal("R0805", device.Package);
        Assert.Equal("R", device.Prefix);
    }

    // ==== 2. a resolved LOCAL file becomes a ComponentModel3D =================

    [Fact]
    public void WhenTheResolverFindsALocalFile_TheModelIsAttachedAndLoads()
    {
        string path = WriteBoxObj();
        try
        {
            string? asked = null;
            var part = EagleLibraryReader.Load(
                EagleFixtures.ManagedLibrary, "R-EU_R0805",
                p3d => { asked = p3d.Urn; return path; });

            // The resolver is asked about the 3D package the DEVICE binds, by urn.
            Assert.Equal("urn:adsk.eagle:package:23123/2", asked);

            var model = part.Definition.Model;
            Assert.NotNull(model);
            Assert.True(model!.IsFileReferenced);
            Assert.Equal(path, model.FilePath);

            // An Eagle package3d carries no placement, so the body seats at the IDENTITY.
            Assert.True(model.Placement.IsIdentity);

            // It really loads, and it is the file's own body (2 x 3 x 4, all three axes distinct).
            var bounds = model.Load().Bounds();
            Assert.Equal(2.0, bounds.Size.X, 6);
            Assert.Equal(3.0, bounds.Size.Y, 6);
            Assert.Equal(4.0, bounds.Size.Z, 6);

            // The classic three representations are untouched by the binding.
            Assert.True(part.Identity.Ok, part.Identity.ToString());
            Assert.Equal(2, part.Definition.Pins.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ==== 3. what stays DIAGNOSED-ONLY, and why ===============================

    [Fact]
    public void WithNoResolver_TheBindingIsRecordedByName_AndNoModelIsAttached()
    {
        // The model file is Fusion cloud content: the binding is data the .lbr carries, the
        // geometry is not. So the URN is named and nothing is invented.
        var part = EagleLibraryReader.Load(EagleFixtures.ManagedLibrary, "R-EU_R0805");

        Assert.Null(part.Definition.Model);
        Assert.Contains(part.Diagnostics, d =>
            d.Contains("urn:adsk.eagle:package:23123/2")
            && d.Contains("RESC2012X70N")
            && d.Contains("modelResolver"));
    }

    [Fact]
    public void WhenTheResolverFindsNoLocalCopy_TheBindingIsRecordedByName()
    {
        var part = EagleLibraryReader.Load(EagleFixtures.ManagedLibrary, "R-EU_R0805", _ => null);

        Assert.Null(part.Definition.Model);
        Assert.Contains(part.Diagnostics, d =>
            d.Contains("urn:adsk.eagle:package:23123/2") && d.Contains("no local copy"));
    }

    [Fact]
    public void WhenTheResolverNamesAMissingFile_TheBindingIsRecordedByName()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"engrcad-absent-{Guid.NewGuid():N}.obj");
        var part = EagleLibraryReader.Load(EagleFixtures.ManagedLibrary, "R-EU_R0805", _ => missing);

        // A path that does not exist is NOT attached — a ComponentModel3D whose file is absent
        // would only fail later, further from the cause.
        Assert.Null(part.Definition.Model);
        Assert.Contains(part.Diagnostics, d => d.Contains(missing) && d.Contains("no such file"));
    }

    [Fact]
    public void ADeviceBindingAnUndeclaredUrn_IsRecordedByName()
    {
        string path = WriteBoxObj();
        try
        {
            var part = EagleLibraryReader.Load(
                EagleFixtures.ManagedLibrary, "R-EU_R0805X", _ => path);

            // The resolver is never even asked: the library declares no such 3D package.
            Assert.Null(part.Definition.Model);
            Assert.Contains(part.Diagnostics, d =>
                d.Contains("urn:adsk.eagle:package:99999/9") && d.Contains("does not declare"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APackage3dNotListingTheDevicesPackage_IsRecorded_AndTheBindingIsHonoured()
    {
        string path = WriteBoxObj();
        try
        {
            var part = EagleLibraryReader.Load(
                EagleFixtures.ManagedLibrary, "R-EU_R0805B", _ => path);

            // The DEVICE's own binding is the statement of intent, so the model is attached...
            Assert.NotNull(part.Definition.Model);
            // ... and the disagreement is reported rather than silently resolved either way.
            Assert.Contains(part.Diagnostics, d =>
                d.Contains("RESC2012X70N-BOX") && d.Contains("R0805"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASecondBinding_IsRecordedAndNotAttached()
    {
        string path = WriteBoxObj();
        try
        {
            var part = EagleLibraryReader.Load(
                EagleFixtures.ManagedLibrary, "R-EU_R0805T", _ => path);

            // A PartDefinition carries ONE model, so the first binding wins and the rest are named.
            Assert.NotNull(part.Definition.Model);
            Assert.Contains(part.Diagnostics, d =>
                d.Contains("further 3D package") && d.Contains("urn:adsk.eagle:package:23124/1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APackage3dWithNoUrn_IsIgnoredByName()
    {
        // The urn IS a package3d's identity — it is what a device's binding references — so one
        // without it cannot be bound and is dropped rather than half-kept.
        var lib = EagleLibraryReader.Read(EagleFixtures.ManagedLibrary);

        Assert.DoesNotContain(lib.Packages3d, p => p.Name == "NAMELESS");
        Assert.Contains(lib.Diagnostics, d => d.Contains("NAMELESS") && d.Contains("no urn"));
    }

    [Fact]
    public void APackage3dBindingAMissingPackage_IsRecordedByName()
    {
        var lib = EagleLibraryReader.Read(EagleFixtures.ManagedLibrary);
        Assert.Contains(lib.Diagnostics, d =>
            d.Contains("RESC2012X70N-BOX") && d.Contains("R0805_NOT_HERE"));
    }

    // ==== 4. the newer format is TOLERATED, and says so =======================

    [Fact]
    public void AnEagle9File_NamesItsManagedFormat()
    {
        var lib = EagleLibraryReader.Read(EagleFixtures.ManagedLibrary);
        Assert.Contains(lib.Diagnostics, d => d.Contains("9.6.2") && d.Contains("managed"));
    }

    [Fact]
    public void AClassicLibrary_CarriesNoBindingsAndNoVersionNote()
    {
        // The classic path is unchanged: no 3D packages, no urn on any device, and none of the
        // managed format's diagnostics — so a pre-9 import reads exactly as it always did.
        var lib = EagleLibraryReader.Read(EagleFixtures.Library);

        Assert.Empty(lib.Packages3d);
        Assert.All(lib.Devices, d => Assert.Null(d.Package3dUrn));
        Assert.DoesNotContain(lib.Diagnostics, d => d.Contains("managed"));

        // ... and a device loads with no model, whether or not a resolver is supplied.
        Assert.Null(EagleLibraryReader.Load(EagleFixtures.Library, "R-EU_R0805").Definition.Model);
        Assert.Null(EagleLibraryReader
            .Load(EagleFixtures.Library, "R-EU_R0805", _ => "anything.obj").Definition.Model);
    }

    [Fact]
    public void TheManagedLibrarysUrnAttributesDoNotDisturbTheClassicSubset()
    {
        // urn / library_version are additive attributes a reader that asks for its attributes BY
        // NAME never sees — so the managed library's pads, pins and connect map read exactly as
        // the classic fixture's do.
        var part = EagleLibraryReader.Load(EagleFixtures.ManagedLibrary, "R-EU_R0805");

        Assert.Equal(new[] { "1", "2" }, part.Definition.Pins.Select(p => p.Number));
        Assert.Equal(-0.9125, part.Definition.Footprint!.Pads[0].Center.X);
        Assert.Equal(PadShape.RoundedRectangle, part.Definition.Footprint.Pads[0].Shape);
        Assert.Equal(
            new Vector2d(0, 3.81), part.Definition.Symbol!.PinNumbered("1").Anchor);
        Assert.True(part.Identity.Ok, part.Identity.ToString());
    }

    // ==== 5. determinism + persistence ========================================

    [Fact]
    public void AResolvedModel_RoundTripsThroughTheSchematicFile_AndIsDeterministic()
    {
        string path = WriteBoxObj();
        try
        {
            static Schematic With(PartDefinition definition)
            {
                var sch = new Schematic("eagle-3d");
                sch.Add("R1", definition, value: "10k");
                return sch;
            }

            var a = EagleLibraryReader.Load(EagleFixtures.ManagedLibrary, "R-EU_R0805", _ => path);
            var b = EagleLibraryReader.Load(EagleFixtures.ManagedLibrary, "R-EU_R0805", _ => path);

            string json = With(a.Definition).Save();
            Assert.Equal(json, With(b.Definition).Save());            // deterministic

            // A file-referenced model travels as DATA, so save -> load -> save is a fixed point
            // and the reloaded definition still names the same file.
            var reloaded = Schematic.Load(json);
            Assert.Equal(json, reloaded.Save());
            Assert.Equal(path, reloaded.Find("R1")!.Definition.Model!.FilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
