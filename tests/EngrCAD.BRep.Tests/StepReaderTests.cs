using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>Part 21 tokenizer/parser unit tests (internal <see cref="StepParser"/>).</summary>
public class StepParserTests
{
    [Fact]
    public void Strings_DecodeDoubledApostrophes()
    {
        var file = StepParser.Parse("#1=CARTESIAN_POINT('it''s a point',(1.,2.,3.));");
        var record = file.Entity(1).Records[0];
        Assert.Equal("CARTESIAN_POINT", record.Keyword);
        Assert.Equal("it's a point", record.Args[0].Text);
    }

    [Theory]
    [InlineData("1.E-6", 1e-6)]
    [InlineData("-2.5E+3", -2500)]
    [InlineData("0.", 0)]
    [InlineData("4", 4)]
    [InlineData("1.5e2", 150)]
    [InlineData("-0.125", -0.125)]
    [InlineData("6.283185307179586", 6.283185307179586)]
    public void Numbers_ParseAllPart21Forms(string literal, double expected)
    {
        var file = StepParser.Parse($"#1=X(({literal}));");
        Assert.Equal(expected, file.Entity(1).Records[0].Args[0].AsList()[0].AsNumber(), 15);
    }

    [Fact]
    public void EnumsNullsDerivedAndTypedValues_Parse()
    {
        var file = StepParser.Parse(
            "#1=WIDGET(4,.T.,$,*,.UNSPECIFIED.,LENGTH_MEASURE(1.E-6)); /* a comment */");
        var args = file.Entity(1).Records[0].Args;
        Assert.Equal(4, args[0].AsInt());
        Assert.True(args[1].AsBool());
        Assert.Equal(StepValueKind.Null, args[2].Kind);
        Assert.Equal(StepValueKind.Derived, args[3].Kind);
        Assert.Equal("UNSPECIFIED", args[4].Text);
        Assert.Equal(StepValueKind.Typed, args[5].Kind);
        Assert.Equal("LENGTH_MEASURE", args[5].Text);
        Assert.Equal(1e-6, args[5].AsNumber(), 15);
    }

    [Fact]
    public void ComplexInstance_SplitsIntoRecords()
    {
        // The exact rational B-spline shape StepWriter emits.
        var file = StepParser.Parse(
            "#5=(BOUNDED_CURVE()B_SPLINE_CURVE(2,(#1,#2,#3),.UNSPECIFIED.,.F.,.F.)" +
            "B_SPLINE_CURVE_WITH_KNOTS((3,3),(0.,1.),.UNSPECIFIED.)" +
            "CURVE()GEOMETRIC_REPRESENTATION_ITEM()" +
            "RATIONAL_B_SPLINE_CURVE((1.,0.7071067811865476,1.))" +
            "REPRESENTATION_ITEM(''));");
        var entity = file.Entity(5);
        Assert.True(entity.IsComplex);
        Assert.Equal(7, entity.Records.Count);
        Assert.Equal(2, entity.Find("B_SPLINE_CURVE")!.Args[0].AsInt());
        Assert.Equal(3, entity.Find("RATIONAL_B_SPLINE_CURVE")!.Args[0].AsList().Count);
        Assert.Equal([1, 2, 3], entity.Find("B_SPLINE_CURVE")!.Args[1].AsList().Select(v => v.AsReference()));
    }

    [Fact]
    public void ForwardReferences_Resolve()
    {
        var file = StepParser.Parse(
            "#1=VERTEX_POINT('',#2);\n#2=CARTESIAN_POINT('',(7.,8.,9.));");
        int target = file.Entity(1).Records[0].Args[1].AsReference();
        Assert.Equal(2, target);
        Assert.Equal(8.0, file.Entity(target).Records[0].Args[1].AsList()[1].AsNumber());
    }

    [Fact]
    public void FullFileSkeleton_HeaderAndSectionsHandled()
    {
        var file = StepParser.Parse(
            "ISO-10303-21;\nHEADER;\n" +
            "FILE_DESCRIPTION(('EngrCAD B-Rep export'),'2;1');\n" +
            "FILE_NAME('part.step','2026-01-01T00:00:00',('EngrCAD'),('EngrCAD'),'EngrCAD','EngrCAD','');\n" +
            "FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\n" +
            "ENDSEC;\nDATA;\n#1=CARTESIAN_POINT('',(0.,0.,0.));\nENDSEC;\nEND-ISO-10303-21;\n");
        Assert.Equal(3, file.Header.Count);
        Assert.Single(file.Entities);
    }

    [Theory]
    [InlineData("#1=FOO(")]                    // truncated argument list
    [InlineData("#1=FOO('unterminated);")]     // unterminated string
    [InlineData("#1=FOO(1.,.BAD);")]           // unterminated enumeration
    [InlineData("#1=FOO(1.E-);")]              // malformed exponent
    [InlineData("#1=")]                        // EOF where an entity body starts
    [InlineData("#1=FOO(BAR")]                 // EOF after a bare identifier argument
    public void Malformed_ThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => StepParser.Parse(text));
    }
}

/// <summary>Structural StepReader tests (no tessellation — see Interop.Tests for volumes).</summary>
public class StepReaderTests
{
    private static BrepSolid ReadSingle(string step, out IReadOnlyList<string> diagnostics)
    {
        var result = StepReader.Read(step);
        diagnostics = result.Diagnostics;
        return Assert.Single(result.Solids);
    }

    private static void AssertSameCounts(BrepSolid original, BrepSolid read)
    {
        Assert.Equal(original.Faces.Count(), read.Faces.Count());
        Assert.Equal(original.Loops.Count(), read.Loops.Count());
        Assert.Equal(original.Edges.Count(), read.Edges.Count());
        Assert.Equal(original.Vertices.Count(), read.Vertices.Count());
    }

    [Fact]
    public void Box_ReadsBackCompleteSharedTopology()
    {
        var original = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4)));
        var read = ReadSingle(StepWriter.Write(original, "box"), out var diagnostics);
        Assert.Empty(diagnostics);

        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        AssertSameCounts(original, read);

        // Vertex identity is shared through entity ids, and positions survive exactly.
        var originalCorners = original.Vertices.Select(v => v.Position).OrderBy(p => (p.X, p.Y, p.Z)).ToList();
        var readCorners = read.Vertices.Select(v => v.Position).OrderBy(p => (p.X, p.Y, p.Z)).ToList();
        for (int i = 0; i < originalCorners.Count; i++)
            Assert.True(originalCorners[i].AreEqual(readCorners[i], Tolerance.Default));
    }

    [Fact]
    public void Cylinder_ClosedRimEdgesKeepFullCircleDomains()
    {
        var original = SolidFactory.MakeCylinder(1.5, 4);
        var read = ReadSingle(StepWriter.Write(original), out var diagnostics);
        Assert.Empty(diagnostics);
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        AssertSameCounts(original, read);

        var side = read.Faces.Single(f => f.Surface is CylinderSurface);
        Assert.Equal(2, side.Loops.Count);
        foreach (var loop in side.Loops)
        {
            var edge = Assert.Single(loop.Coedges).Edge;
            Assert.True(edge.IsClosedEdge);
            Assert.IsType<Circle3d>(edge.Curve);
            Assert.Equal(0, edge.Domain.Start, 12);
            Assert.Equal(2 * Math.PI, edge.Domain.End, 12);
        }
    }

    [Fact]
    public void PartialRevolve_RecoversSweptAngleFromRailArcs()
    {
        var original = SolidFactory.Revolve(
            Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]),
            Vector3d.Zero, Vector3d.UnitZ, Math.PI / 2);
        var read = ReadSingle(StepWriter.Write(original), out var diagnostics);
        Assert.Empty(diagnostics);
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        AssertSameCounts(original, read);

        var bands = read.Faces.Where(f => f.Surface is RevolvedSurface).ToList();
        Assert.Equal(4, bands.Count);
        foreach (var band in bands)
            Assert.Equal(Math.PI / 2, ((RevolvedSurface)band.Surface).Angle, 12);
    }

    [Fact]
    public void Sphere_CircularGeneratorsSurviveAsExactCircles()
    {
        // The sphere's meridian generators are CurveSegments over a Circle3d (the
        // angular-density rule reads Underlying — the measured Shape.Sphere defect's
        // fix), so they export as trimmed CIRCLE entities and come back as exact
        // circles: strictly better than the rational B-spline the old construction
        // carried through the complex-instance form.
        var original = SolidFactory.MakeSphere(1.5);
        var read = ReadSingle(StepWriter.Write(original), out var diagnostics);
        Assert.Empty(diagnostics);
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        AssertSameCounts(original, read);

        var originalFaces = original.Faces.ToList();
        var readFaces = read.Faces.ToList();
        for (int i = 0; i < originalFaces.Count; i++)
        {
            var readSurface = Assert.IsType<RevolvedSurface>(readFaces[i].Surface);
            // The generator comes back as the SEGMENT it is — a TRIMMED_CURVE over the
            // exact circle, span verbatim — not the rational B-spline of old.
            var generator = Assert.IsType<CurveSegment>(readSurface.Generator);
            Assert.IsType<Circle3d>(generator.Base);

            var originalSurface = (RevolvedSurface)originalFaces[i].Surface;
            Assert.Equal(originalSurface.Angle, readSurface.Angle, 12);
            for (int a = 0; a <= 4; a++)
            {
                for (int b = 0; b <= 4; b++)
                {
                    double u = originalSurface.DomainU.ParameterAt(a / 4.0);
                    double v = originalSurface.DomainV.ParameterAt(b / 4.0);
                    double ur = readSurface.DomainU.ParameterAt(a / 4.0);
                    double vr = readSurface.DomainV.ParameterAt(b / 4.0);
                    Assert.True(originalSurface.PointAt(u, v).DistanceTo(readSurface.PointAt(ur, vr)) < 1e-12);
                }
            }
        }
    }

    [Fact]
    public void NurbsProfileExtrusion_ReadsStructurally()
    {
        // Rational quadratic half-circle + base line, extruded: the side surface is an
        // extruded NURBS emitted through the rational complex-instance form, and the
        // TOP edge — a TransformedCurve(NurbsCurve) — exports EXACTLY by transforming
        // control points (weights and knots untouched), not as a sampled polyline.
        double w = Math.Sqrt(2) / 2;
        var halfCircle = new NurbsCurve(2,
            [(1, 0, 0), (1, 1, 0), (0, 1, 0), (-1, 1, 0), (-1, 0, 0)],
            [1, w, 1, w, 1],
            [0, 0, 0, 0.5, 0.5, 1, 1, 1]);
        var profile = new Profile([halfCircle, new Line3d((-1, 0, 0), (1, 0, 0))]);
        var original = SolidFactory.Extrude(profile, (0, 0, 1));

        var read = ReadSingle(StepWriter.Write(original), out var diagnostics);
        Assert.Empty(diagnostics);
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        AssertSameCounts(original, read);

        var band = read.Faces.Single(f => f.Surface is ExtrudedSurface { Generator: NurbsCurve });
        var generator = (NurbsCurve)((ExtrudedSurface)band.Surface).Generator;
        Assert.Contains(generator.Weights, weight => Math.Abs(weight - 1) > 1e-9);
        Assert.True(generator.PointAt(0.25).DistanceTo(halfCircle.PointAt(0.25)) < 1e-12);

        // The top edge comes back as the exact 5-control-point rational arc translated
        // to z = 1 — a sampled export would have produced a 33-point degree-1 spline.
        var top = read.Edges.Single(e =>
            e.Curve is NurbsCurve && Math.Abs(e.StartVertex.Position.Z - 1) < 1e-12);
        var topCurve = (NurbsCurve)top.Curve;
        Assert.Equal(5, topCurve.ControlPoints.Count);
        Assert.True(topCurve.PointAt(0.25).DistanceTo(halfCircle.PointAt(0.25) + (0, 0, 1)) < 1e-12);
    }

    [Fact]
    public void UnsupportedSurface_SkipsTheFaceWithADiagnostic()
    {
        // TOROIDAL_SURFACE used to be the unsupported specimen here; it imports now, so
        // a keyword no reader will ever learn stands in.
        string step = StepWriter.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))));
        int index = step.IndexOf("PLANE(", StringComparison.Ordinal);
        step = string.Concat(step.AsSpan(0, index), "IMAGINARY_SURFACE(", step.AsSpan(index + "PLANE(".Length));

        var result = StepReader.Read(step);
        var solid = Assert.Single(result.Solids);
        Assert.Equal(5, solid.Faces.Count());
        Assert.Contains(result.Diagnostics, d => d.Contains("IMAGINARY_SURFACE"));
    }

    [Fact]
    public void ConicalSurface_SynthesizesARevolvedFrustumBand()
    {
        // Swap the frustum side's SURFACE_OF_REVOLUTION for the CONICAL_SURFACE a
        // foreign writer would emit: radius 1 at the base plane growing at tan(semi
        // angle) = 0.5 along +Z. The reader must rebuild the slanted-line revolve and
        // trim it to the face's rims.
        var original = SolidFactory.MakeCone(1, 2, 2);
        string step = System.Text.RegularExpressions.Regex.Replace(
            StepWriter.Write(original),
            @"SURFACE_OF_REVOLUTION\('',#\d+,#\d+\)",
            "CONICAL_SURFACE('',#9001,1.,0.4636476090008061)")
            .Replace(
                "ENDSEC;\nEND-ISO-10303-21;",
                "#9001=AXIS2_PLACEMENT_3D('',#9002,#9003,#9004);\n" +
                "#9002=CARTESIAN_POINT('',(0.,0.,0.));\n" +
                "#9003=DIRECTION('',(0.,0.,1.));\n" +
                "#9004=DIRECTION('',(1.,0.,0.));\n" +
                "ENDSEC;\nEND-ISO-10303-21;");

        var result = StepReader.Read(step);
        var read = Assert.Single(result.Solids);
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(original.Faces.Count(), read.Faces.Count());

        var side = Assert.IsType<RevolvedSurface>(
            read.Faces.Single(f => f.Surface is RevolvedSurface).Surface);
        for (int a = 0; a <= 4; a++)
        {
            for (int b = 0; b <= 4; b++)
            {
                double u = side.DomainU.ParameterAt(a / 4.0);
                double v = side.DomainV.ParameterAt(b / 4.0);
                var p = side.PointAt(u, v);
                double radial = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                Assert.InRange(p.Z, -1e-9, 2 + 1e-9); // trimmed to the face's extent
                Assert.Equal(1 + 0.5 * p.Z, radial, 9); // on the cone
            }
        }
    }

    [Fact]
    public void ConicalSurface_ApexConeRecoversThePole()
    {
        // Apex-down declaration: the CONICAL_SURFACE frame sits at the apex (0,0,2)
        // pointing −Z with radius 0, semi-angle 45°. The face's only vertex is the base
        // rim's seam, so the generator's reach to the apex must come from the surface's
        // own natural boundary — the pole-snapping path.
        var original = SolidFactory.MakeCone(2, 0, 2);
        string step = System.Text.RegularExpressions.Regex.Replace(
            StepWriter.Write(original),
            @"SURFACE_OF_REVOLUTION\('',#\d+,#\d+\)",
            "CONICAL_SURFACE('',#9001,0.,0.7853981633974483)")
            .Replace(
                "ENDSEC;\nEND-ISO-10303-21;",
                "#9001=AXIS2_PLACEMENT_3D('',#9002,#9003,#9004);\n" +
                "#9002=CARTESIAN_POINT('',(0.,0.,2.));\n" +
                "#9003=DIRECTION('',(0.,0.,-1.));\n" +
                "#9004=DIRECTION('',(1.,0.,0.));\n" +
                "ENDSEC;\nEND-ISO-10303-21;");

        var result = StepReader.Read(step);
        var read = Assert.Single(result.Solids);
        read.Validate();
        Assert.Equal(original.Faces.Count(), read.Faces.Count());

        var side = Assert.IsType<RevolvedSurface>(
            read.Faces.Single(f => f.Surface is RevolvedSurface).Surface);
        // The generator must reach the apex EXACTLY (pole at radius 0) and the base rim.
        var generator = side.Generator;
        var ends = new[] { generator.PointAt(generator.Domain.Start), generator.PointAt(generator.Domain.End) };
        Assert.Contains(ends, p => p.DistanceTo((0, 0, 2)) < 1e-9);
        Assert.Contains(ends, p => Math.Abs(p.Z) < 1e-9 && Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 2) < 1e-9);
        for (int b = 0; b <= 4; b++)
        {
            var p = side.PointAt(side.DomainU.Mid, side.DomainV.ParameterAt(b / 4.0));
            double radial = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.Equal(2 - p.Z, radial, 9); // on the cone between apex and base
        }
    }

    [Fact]
    public void ToroidalSurface_SynthesizesRevolvedBandsWithTheRightHalves()
    {
        // A torus is two revolved bands sharing the top/bottom junction circles; both
        // faces see the SAME two rims on a CLOSED minor-circle generator, so recovering
        // each band's half is exactly the interval ambiguity the rim senses resolve —
        // a min/max trim would hand both faces the same half.
        var original = SolidFactory.MakeTorus(10, 3);
        string step = System.Text.RegularExpressions.Regex.Replace(
            StepWriter.Write(original),
            @"SURFACE_OF_REVOLUTION\('',#\d+,#\d+\)",
            "TOROIDAL_SURFACE('',#9001,10.,3.)")
            .Replace(
                "ENDSEC;\nEND-ISO-10303-21;",
                "#9001=AXIS2_PLACEMENT_3D('',#9002,#9003,#9004);\n" +
                "#9002=CARTESIAN_POINT('',(0.,0.,0.));\n" +
                "#9003=DIRECTION('',(0.,0.,1.));\n" +
                "#9004=DIRECTION('',(1.,0.,0.));\n" +
                "ENDSEC;\nEND-ISO-10303-21;");

        var result = StepReader.Read(step);
        var read = Assert.Single(result.Solids);
        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 1));
        Assert.Equal(original.Faces.Count(), read.Faces.Count());

        var originalFaces = original.Faces.ToList();
        var readFaces = read.Faces.ToList();
        for (int i = 0; i < readFaces.Count; i++)
        {
            var surface = Assert.IsType<RevolvedSurface>(readFaces[i].Surface);
            var originalSurface = (RevolvedSurface)originalFaces[i].Surface;
            // The original generator is a rational arc (angle-nonlinear parameters), so
            // compare by geometry: every sample lies on the torus, and the band's
            // mid-generator point sits on the same side (outer/inner) as the original's.
            for (int a = 0; a <= 4; a++)
            {
                for (int b = 0; b <= 4; b++)
                {
                    var p = surface.PointAt(
                        surface.DomainU.ParameterAt(a / 4.0), surface.DomainV.ParameterAt(b / 4.0));
                    double radial = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                    double torus = Math.Sqrt((radial - 10) * (radial - 10) + p.Z * p.Z);
                    Assert.Equal(3, torus, 9);
                }
            }
            var mid = surface.PointAt(surface.DomainU.Mid, surface.DomainV.Mid);
            var originalMid = originalSurface.PointAt(originalSurface.DomainU.Mid, originalSurface.DomainV.Mid);
            double midRadial = Math.Sqrt(mid.X * mid.X + mid.Y * mid.Y);
            double originalMidRadial = Math.Sqrt(originalMid.X * originalMid.X + originalMid.Y * originalMid.Y);
            Assert.True((midRadial > 10) == (originalMidRadial > 10),
                $"face {i}: read band mid radial {midRadial:G6} vs original {originalMidRadial:G6} — wrong half");
        }
    }

    [Fact]
    public void BsplineSurface_ParsesIntoNurbsSurface()
    {
        // A hand-written bilinear B-spline patch bounded by four line edges (not a valid
        // closed solid — the reader maps geometry without validating).
        const string step =
            "#1=CARTESIAN_POINT('',(0.,0.,0.));\n" +
            "#2=CARTESIAN_POINT('',(1.,0.,0.));\n" +
            "#3=CARTESIAN_POINT('',(1.,1.,0.));\n" +
            "#4=CARTESIAN_POINT('',(0.,1.,0.));\n" +
            "#5=VERTEX_POINT('',#1);\n" +
            "#6=VERTEX_POINT('',#2);\n" +
            "#7=VERTEX_POINT('',#3);\n" +
            "#8=VERTEX_POINT('',#4);\n" +
            "#9=DIRECTION('',(1.,0.,0.));\n" +
            "#10=VECTOR('',#9,1.);\n" +
            "#11=LINE('',#1,#10);\n" +
            "#12=EDGE_CURVE('',#5,#6,#11,.T.);\n" +
            "#13=DIRECTION('',(0.,1.,0.));\n" +
            "#14=VECTOR('',#13,1.);\n" +
            "#15=LINE('',#2,#14);\n" +
            "#16=EDGE_CURVE('',#6,#7,#15,.T.);\n" +
            "#17=DIRECTION('',(-1.,0.,0.));\n" +
            "#18=VECTOR('',#17,1.);\n" +
            "#19=LINE('',#3,#18);\n" +
            "#20=EDGE_CURVE('',#7,#8,#19,.T.);\n" +
            "#21=DIRECTION('',(0.,-1.,0.));\n" +
            "#22=VECTOR('',#21,1.);\n" +
            "#23=LINE('',#4,#22);\n" +
            "#24=EDGE_CURVE('',#8,#5,#23,.T.);\n" +
            "#25=ORIENTED_EDGE('',*,*,#12,.T.);\n" +
            "#26=ORIENTED_EDGE('',*,*,#16,.T.);\n" +
            "#27=ORIENTED_EDGE('',*,*,#20,.T.);\n" +
            "#28=ORIENTED_EDGE('',*,*,#24,.T.);\n" +
            "#29=EDGE_LOOP('',(#25,#26,#27,#28));\n" +
            "#30=FACE_OUTER_BOUND('',#29,.T.);\n" +
            "#31=B_SPLINE_SURFACE_WITH_KNOTS('',1,1,((#1,#2),(#4,#3)),.UNSPECIFIED.,.F.,.F.,.F.,(2,2),(2,2),(0.,1.),(0.,1.),.UNSPECIFIED.);\n" +
            "#32=ADVANCED_FACE('',(#30),#31,.T.);\n" +
            "#33=CLOSED_SHELL('',(#32));\n" +
            "#34=MANIFOLD_SOLID_BREP('patch',#33);\n";

        var result = StepReader.Read(step);
        var solid = Assert.Single(result.Solids);
        var face = Assert.Single(solid.Faces);
        var surface = Assert.IsType<NurbsSurface>(face.Surface);
        Assert.Equal(1, surface.DegreeU);
        Assert.Equal(1, surface.DegreeV);
        Assert.True(surface.PointAt(0.5, 0.5).AreEqual((0.5, 0.5, 0), Tolerance.Default));
        Assert.Equal(4, Assert.Single(face.Loops).Coedges.Count);
    }

    [Fact]
    public void MetreLengthUnit_ScalesCoordinatesToMillimetres()
    {
        // Deliberate behavior change (STEP unit scaling): a plain-metre file used to be
        // read unscaled with a "millimetres assumed" warning; it now scales by 1000.
        string step = StepWriter.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))))
            .Replace(".MILLI.,.METRE.", "$,.METRE.");
        var result = StepReader.Read(step);
        Assert.Contains(result.Diagnostics, d => d.Contains("scaled by 1000"));
        var read = Assert.Single(result.Solids);
        read.Validate();
        Assert.Equal(1000.0, read.Vertices.Max(v => v.Position.X), 9);
        Assert.Equal(1000.0, read.Vertices.Max(v => v.Position.Z), 9);
    }

    [Fact]
    public void InchConversionBasedUnit_ScalesBy25Point4()
    {
        // Swap the writer's mm unit for CONVERSION_BASED_UNIT('INCH', 25.4 mm) — the
        // standard imperial declaration. The metric BASE unit (#9002) that the chain
        // references must NOT be mistaken for the file's unit: the geometric context
        // references only the inch entity.
        string step = StepWriter.Write(SolidFactory.MakeCylinder(1, 2))
            .Replace(
                "(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))",
                "(CONVERSION_BASED_UNIT('INCH',#9001)LENGTH_UNIT()NAMED_UNIT(*))")
            .Replace(
                "ENDSEC;\nEND-ISO-10303-21;",
                "#9001=LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#9002);\n" +
                "#9002=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n" +
                "ENDSEC;\nEND-ISO-10303-21;");
        var result = StepReader.Read(step);
        Assert.Contains(result.Diagnostics, d => d.Contains("INCH") && d.Contains("scaled"));
        var read = Assert.Single(result.Solids);
        read.Validate();
        var side = (CylinderSurface)read.Faces.Single(f => f.Surface is CylinderSurface).Surface;
        Assert.Equal(25.4, side.Radius, 12);
        Assert.Equal(2 * 25.4, read.Vertices.Max(v => v.Position.Z), 9);
    }

    [Fact]
    public void UnresolvableLengthUnit_FallsBackToMillimetresAssumed()
    {
        string step = StepWriter.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1))))
            .Replace(".MILLI.,.METRE.", "$,.CUBIT.");
        var result = StepReader.Read(step);
        Assert.Contains(result.Diagnostics, d => d.Contains("millimetres assumed"));
        var read = Assert.Single(result.Solids);
        Assert.Equal(1.0, read.Vertices.Max(v => v.Position.X), 12); // unscaled
    }

    [Fact]
    public void EmptyDataSection_ReportsNoSolids()
    {
        var result = StepReader.Read("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
        Assert.Empty(result.Solids);
        Assert.Contains(result.Diagnostics, d => d.Contains("No MANIFOLD_SOLID_BREP"));
    }

    /// <summary>
    /// A partial revolve of a SINGLE closed profile curve — an elbow with a one-curve
    /// tube section. The profile has no segment junctions, so the sweep traces no
    /// axis-centered arc anywhere and nothing on the boundary carries the angle the way
    /// multi-segment profiles' rail arcs do; the whole boundary is the generator plus its
    /// rotated copy. This used to come back SILENTLY as a FULL TURN (angle 2π for a
    /// 1.2 rad sweep, zero diagnostics), which the tessellator's full-domain gate then
    /// refused three stages later — the exact silent-wrong-surface shape the reader's
    /// culture forbids. The angle is now read in closed form as the azimuthal rotation
    /// between corresponding samples of the two congruent boundary curves.
    /// </summary>
    [Fact]
    public void PartialRevolveOfASingleClosedNurbsProfile_RecoversItsAngle()
    {
        var points = new List<Vector3d>();
        for (int i = 0; i < 8; i++)
        {
            double a = 2 * Math.PI * i / 8;
            double r = 2 + 0.3 * Math.Cos(3 * a); // non-circular, so no meridian rule applies
            points.Add(new Vector3d(10 + r * Math.Cos(a), 0, r * Math.Sin(a)));
        }
        var profile = NurbsCurve.InterpolatePoints(points, closed: true);
        var original = SolidFactory.Revolve(
            new Profile([profile]), Vector3d.Zero, Vector3d.UnitZ, 1.2);
        original.Validate();

        var read = ReadSingle(StepWriter.Write(original, "elbow"), out var diagnostics);
        Assert.Empty(diagnostics);
        read.Validate();
        AssertSameCounts(original, read);

        var band = (RevolvedSurface)read.Faces.Single(f => f.Surface is RevolvedSurface).Surface;
        Assert.Equal(1.2, band.Angle, 9);
        // The generator legitimately spans its whole closed period (the tube section),
        // so no trim may be invented and no closed-generator diagnostic may fire.
        Assert.True(band.Generator.IsClosed);
        Assert.Equal(profile.Domain.Length, band.Generator.Domain.Length, 9);
    }
}
