using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The shared per-part upload builder. These assert the CONTRACT the three front ends
/// now share — which pieces a request produces, and the content rules that used to be
/// written out once per pass.
/// </summary>
public class PartUploadTests
{
    private static Part Plate(Action<Part>? configure = null)
    {
        var part = new Part("plate", Shape.Box(20, 10, 2));
        var mesh = part.GetMesh();
        part.AddResult(MeshField.Sample(mesh, "stress", "MPa", p => p.Z));
        part.AddResult(MeshField.SampleVector(mesh, "u", "mm", p => new Vector3d(0, 0, p.X)));
        configure?.Invoke(part);
        return part;
    }

    [Fact]
    public void Build_All_ProducesEveryPiece()
    {
        var upload = PartUploads.Build(Plate(), PartUploadRequest.All);

        Assert.Same(upload.Part.GetMesh(), upload.Mesh);
        Assert.Equal(upload.Mesh.VertexCount, upload.Render.SourceVertices.Distinct().Count());
        Assert.Equal(upload.Render.Indices.Length, upload.IndexCount);
        Assert.NotEmpty(upload.FeatureEdges);
        Assert.NotEmpty(upload.WireEdges);
        Assert.Equal(upload.FeatureEdges.Count * 2, upload.FeatureEdgeVertexCount);
        Assert.Equal(upload.WireEdges.Count * 2, upload.WireEdgeVertexCount);
        Assert.NotNull(upload.Pick);
        // No occlusion delegate supplied: no array, which the attribute reads as the
        // constant 1.0 -- exactly the AO-off shading.
        Assert.Null(upload.Occlusion);
    }

    [Fact]
    public void Build_NothingRequested_StillMeshesButProducesNoPieces()
    {
        // The render mesh is unconditional (every pass needs it, and the field build and
        // pick BVH are derived from it); everything else is the caller's to ask for.
        var upload = PartUploads.Build(Plate(), new PartUploadRequest());

        Assert.NotNull(upload.Render);
        Assert.Empty(upload.FeatureEdges);
        Assert.Empty(upload.WireEdges);
        Assert.Null(upload.Pick);
        Assert.Null(upload.Field);
        Assert.Null(upload.FieldError);
    }

    [Fact]
    public void RequirePick_WithoutOne_NamesTheRequestRatherThanReturningNull()
    {
        var upload = PartUploads.Build(Plate(), PartUploadRequest.All with { Pick = false });
        var thrown = Assert.Throws<InvalidOperationException>(() => upload.RequirePick);
        Assert.Contains("plate", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Pick", thrown.Message, StringComparison.Ordinal);
    }

    // ---- The rule that used to be stated three times -------------------------------

    [Fact]
    public void Build_ADeformedPart_GetsNoFeatureEdgesEvenThoughOneWasAskedFor()
    {
        // Those edges describe geometry that has moved, so drawing them over the
        // displaced shape would be a WRONG outline rather than a coarse one. Before the
        // extraction this rule lived in ViewportControl, OffscreenRenderer AND the Blazor
        // viewport; here it is asserted once, on the one implementation.
        var part = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 2,
        });
        var upload = PartUploads.Build(part, PartUploadRequest.All);

        Assert.True(upload.Field!.Value.Deformed);
        Assert.Empty(upload.FeatureEdges);
        Assert.Equal(0, upload.FeatureEdgeVertexCount);
        // The wireframe is every mesh edge of the UNDEFORMED mesh and the shader displaces
        // it with the fill, so it is unaffected -- only the exact-B-Rep overlay is dropped.
        Assert.NotEmpty(upload.WireEdges);
    }

    [Fact]
    public void Build_AFieldColouredButUndeformedPart_KeepsItsFeatureEdges()
    {
        var coloured = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" });
        var plain = Plate();

        Assert.Equal(
            PartUploads.Build(plain, PartUploadRequest.All).FeatureEdges.Count,
            PartUploads.Build(coloured, PartUploadRequest.All).FeatureEdges.Count);
    }

    [Fact]
    public void Build_ADisplacementAtScaleZero_KeepsItsFeatureEdges()
    {
        // The test is whether the part CARRIES a displacement, and a zero scale gets no
        // displacement buffer at all -- so the drawn shape is the undeformed one and its
        // outline is the right outline. (The rule must not read an animation's factor:
        // the draw list may not depend on t, which is what lets a clip reuse one upload.)
        var part = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 0,
        });
        var upload = PartUploads.Build(part, PartUploadRequest.All);

        Assert.False(upload.Field!.Value.Deformed);
        Assert.Equal(0, upload.DeformScale);
        Assert.NotEmpty(upload.FeatureEdges);
    }

    [Fact]
    public void Build_FeatureEdgesNotRequested_AreEmptyForADeformedAndAnUndeformedPartAlike()
    {
        var request = PartUploadRequest.All with { FeatureEdges = false };
        Assert.Empty(PartUploads.Build(Plate(), request).FeatureEdges);
        Assert.Empty(PartUploads.Build(
            Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress", Deform = "u", DeformScale = 2 }),
            request).FeatureEdges);
    }

    // ---- Fields ---------------------------------------------------------------------

    [Fact]
    public void Build_FieldsOff_ResolvesNothingEvenWhenThePartShowsOne()
    {
        // RenderToImage(fields: false) is how a geometry figure is taken of a model that
        // carries results.
        var part = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" });
        var upload = PartUploads.Build(part, PartUploadRequest.All with { Fields = false });

        Assert.Null(upload.Field);
        Assert.False(upload.FieldColored);
        Assert.Null(upload.FieldError);
    }

    [Fact]
    public void Build_ADisplayNamingAMissingResult_ReportsWithoutThrowing()
    {
        var part = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "not a result" });
        var upload = PartUploads.Build(part, PartUploadRequest.All);

        Assert.Null(upload.Field);
        Assert.NotNull(upload.FieldError);
        Assert.Contains("not a result", upload.FieldError, StringComparison.Ordinal);
        // A failed display must not cost the rest of the upload.
        Assert.NotEmpty(upload.FeatureEdges);
        Assert.NotNull(upload.Pick);
    }

    [Fact]
    public void Build_NoDisplayAtAll_IsNotAnError()
    {
        // "nothing to show" versus "it went wrong" -- the distinction FieldRendering draws
        // and every front end relies on to avoid a status message per plain part.
        Assert.Null(PartUploads.Build(Plate(), PartUploadRequest.All).FieldError);
    }

    [Fact]
    public void Build_ADeformedPart_CarriesTheScaleAndTheGhostDecision()
    {
        var part = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 3,
        });
        var upload = PartUploads.Build(part, PartUploadRequest.All);

        Assert.True(upload.FieldColored);
        Assert.Equal(3, upload.DeformScale);
        Assert.True(upload.ShowGhost);   // ShowUndeformed defaults on

        var noGhost = PartUploads.Build(
            Plate(p => p.FieldDisplay = new FieldDisplay
            {
                Field = "stress",
                Deform = "u",
                DeformScale = 3,
                ShowUndeformed = false,
            }),
            PartUploadRequest.All);
        Assert.False(noGhost.ShowGhost);
    }

    [Fact]
    public void Build_PickGeometryFollowsTheDisplacement()
    {
        // A BVH is a spatial index and cannot be a uniform, so it indexes the DISPLACED
        // triangles at the part's own scale (FieldRendering.PickShape). Deforming along +Z
        // by the x coordinate moves the far end well outside the undeformed bounds.
        var part = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 5,
        });
        var upload = PartUploads.Build(part, PartUploadRequest.All);
        var plain = PartUploads.Build(Plate(), PartUploadRequest.All);

        Assert.True(TopZ(upload.RequirePick.Mesh) > TopZ(plain.RequirePick.Mesh) + 1);

        static float TopZ(RenderMesh mesh)
        {
            float top = float.NegativeInfinity;
            for (int v = 0; v < mesh.VertexCount; v++)
                top = Math.Max(top, mesh.Positions[v * 3 + 2]);
            return top;
        }
    }

    // ---- Occlusion ------------------------------------------------------------------

    [Fact]
    public void Build_TheOcclusionDelegateSeesTheMeshAndTheRenderMesh()
    {
        // A delegate rather than a flag because the window asks a never-bake cache read
        // and the offscreen pass bakes inline -- two different questions, one seam.
        HalfEdgeMesh? sawMesh = null;
        RenderMesh? sawRender = null;
        var part = Plate();
        var upload = PartUploads.Build(part, PartUploadRequest.All with
        {
            Occlusion = (m, r) =>
            {
                sawMesh = m;
                sawRender = r;
                return new float[r.VertexCount];
            },
        });

        Assert.Same(part.GetMesh(), sawMesh);
        Assert.Same(upload.Render, sawRender);
        Assert.NotNull(upload.Occlusion);
        Assert.Equal(upload.Render.VertexCount, upload.Occlusion!.Length);
    }

    [Fact]
    public void Build_AnOcclusionSourceThatHasNothingYet_LeavesTheArrayNull()
    {
        // The window's TryGet never bakes, so an uncached part goes up flat-lit and the
        // backfill attaches the array on a later frame. Null must travel, not throw.
        var upload = PartUploads.Build(Plate(), PartUploadRequest.All with
        {
            Occlusion = static (_, _) => null,
        });
        Assert.Null(upload.Occlusion);
    }

    // ---- Quality --------------------------------------------------------------------

    [Fact]
    public void Build_OneQualityDrivesBothTheMeshAndTheFeatureEdges()
    {
        // ONE quality reaches BOTH Part.GetMesh and Part.GetFeatureEdges, which is what
        // stops an adaptive criterion tessellating the fill and the exact edge overlay at
        // different densities. Counts above the overlay's own 96-segment display floor,
        // so the edges are free to follow the request rather than sitting on the floor.
        // Two Parts, not one: Part caches its mesh and the FIRST caller's quality wins.
        var coarse = Build(128);
        var fine = Build(256);

        Assert.True(fine.Mesh.VertexCount > coarse.Mesh.VertexCount);
        Assert.True(fine.FeatureEdges.Count > coarse.FeatureEdges.Count);

        static PartUpload Build(int segments) => PartUploads.Build(
            new Part("cyl", Shape.Cylinder(10, 20)),
            PartUploadRequest.All with
            {
                Quality = new MeshQuality { SegmentsPerCircle = segments, CurveSamples = segments / 2 },
            });
    }
}
