
#include "pch.h"

#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRepOffsetAPI_MakeThickSolid.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <StlAPI_Writer.hxx>
#include "TopoDS_Solid.hxx"
#include <TopExp_Explorer.hxx>

#include "EngrCAD.OCWrapper.h"

#include <vcclr.h>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakeCone.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>



#pragma comment(lib, "TKernel.lib")
#pragma comment(lib, "TKMath.lib")
#pragma comment(lib, "TKBRep.lib")
#pragma comment(lib, "TKXSBase.lib")
#pragma comment(lib, "TKService.lib")
#pragma comment(lib, "TKTopAlgo.lib")
#pragma comment(lib, "TKMesh.lib")
#pragma comment(lib, "TKBO.lib")
#pragma comment(lib, "TKG3d.lib")
#pragma comment(lib, "TKOffset.lib")
#pragma comment(lib, "TKPrim.lib")
#pragma comment(lib, "TKV3d.lib")
#pragma comment(lib, "TKIGES.lib")
#pragma comment(lib, "TKSTEP.lib")
#pragma comment(lib, "TKSTEP.lib")
#pragma comment(lib, "TKStl.lib")
#pragma comment(lib, "TKVrml.lib")
#pragma comment(lib, "TKLCAF.lib")
#pragma comment(lib, "TKFillet.lib")

static TCollection_AsciiString toAsciiString(System::String^ theString)
{
    if (theString == nullptr)
    {
        return TCollection_AsciiString();
    }
    
    pin_ptr<const wchar_t> aPinChars = PtrToStringChars(theString);
    const wchar_t* aWCharPtr = aPinChars;
    if (aWCharPtr == NULL
        || *aWCharPtr == L'\0')
    {
        return TCollection_AsciiString();
    }
    return TCollection_AsciiString(aWCharPtr);
}

namespace EngrCADOCWrapper {

    NativeWrapper^ EngrCADOCWrapper::NativeWrapper::Sphere(float radius)
    {
        gp_Ax2 anAxis;
        anAxis.SetLocation(gp_Pnt(0.0, 0, 0.0));
        TopoDS_Shape shape = BRepPrimAPI_MakeSphere(anAxis, radius).Shape();
        TopoDS_Shape* retVal = new TopoDS_Shape(shape);
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    NativeWrapper^ NativeWrapper::Box(float x, float y, float z, bool centered)
    {
        TopoDS_Shape shape = BRepPrimAPI_MakeBox(x, y, z).Shape();

        if (centered) {
            gp_Trsf transformation = gp_Trsf();
            transformation.SetTranslation(gp_Vec(-x / 2.0f, -y / 2.0f, -z / 2.0f));
            TopLoc_Location translation = TopLoc_Location(transformation);
            TopoDS_Shape* retVal = new TopoDS_Shape(shape.Moved(translation));
            NativeWrapper^ f = gcnew NativeWrapper(retVal);
            return f;
        }
        else {
            TopoDS_Shape* retVal = new TopoDS_Shape(shape);
            NativeWrapper^ f = gcnew NativeWrapper(retVal);
            return f;
        }
    }

    NativeWrapper^ NativeWrapper::Cylinder(float radius, float height, bool centered)
    {
        gp_Ax2 pos = gp_Ax2(gp_Pnt(0, 0, centered ? -height / 2.0 : 0), gp_Dir(0, 0, 1));
        TopoDS_Shape shape = BRepPrimAPI_MakeCylinder(pos, radius, height).Shape();

        TopoDS_Shape* retVal = new TopoDS_Shape(shape);
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    NativeWrapper^ NativeWrapper::Cone(float bottomRadius, float topRadius, float height, bool centered)
    {
        gp_Ax2 pos = gp_Ax2(gp_Pnt(0, 0, centered ? -height / 2.0 : 0), gp_Dir(0, 0, 1));
        TopoDS_Shape shape = BRepPrimAPI_MakeCone(pos, bottomRadius, topRadius, height).Shape();

        TopoDS_Shape* retVal = new TopoDS_Shape(shape);
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    NativeWrapper^ NativeWrapper::Translate(float x, float y, float z)
    {
        TopoDS_Shape shape = *m_Impl;

        gp_Trsf transformation = gp_Trsf();
        transformation.SetTranslation(gp_Vec(x, y, z));
        TopLoc_Location translation = TopLoc_Location(transformation);

        TopoDS_Shape* retVal = new TopoDS_Shape(shape.Moved(translation));
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;

    }

    System::Collections::Generic::List<FaceWrapper^>^ NativeWrapper::GetFaces()
    {
        TopoDS_Shape shape = *m_Impl;
        int t = 0;
        TopExp_Explorer Ex;
        System::Collections::Generic::List<FaceWrapper^>^ list = gcnew System::Collections::Generic::List<FaceWrapper^>();
        for (Ex.Init(shape, TopAbs_FACE); Ex.More(); Ex.Next()) {

            TopoDS_Shape* t = new TopoDS_Shape(Ex.Current());
            FaceWrapper^ face = gcnew FaceWrapper(t);
            list->Add(face);
        }
        return list;
    }

    System::Collections::Generic::List<EdgeWrapper^>^ NativeWrapper::GetEdges()
    {
        TopoDS_Shape shape = *m_Impl;
        int t = 0;
        TopExp_Explorer Ex;
        System::Collections::Generic::List<EdgeWrapper^>^ list = gcnew System::Collections::Generic::List<EdgeWrapper^>();
        for (Ex.Init(shape, TopAbs_EDGE); Ex.More(); Ex.Next()) {

            TopoDS_Edge* t = static_cast<TopoDS_Edge*>(new TopoDS_Shape(Ex.Current()));
            EdgeWrapper^ face = gcnew EdgeWrapper(t);
            list->Add(face);
        }
        return list;
    }

    NativeWrapper^ NativeWrapper::Subtract(NativeWrapper^ other)
    {
        TopoDS_Shape difference = *m_Impl;
        TopoDS_Shape other_shape = *other->m_Impl;

        BRepAlgoAPI_Cut algo = BRepAlgoAPI_Cut(difference, other_shape);
        //differenceCut.SetFuzzyValue(0.1);
        algo.Build();
        algo.SimplifyResult(true, true, 1e-3);

        TopoDS_Shape* retVal = new TopoDS_Shape(algo.Shape());
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;

    }

    NativeWrapper^ NativeWrapper::Union(NativeWrapper^ other)
    {
        TopoDS_Shape first = *m_Impl;
        TopoDS_Shape other_shape = *other->m_Impl;

        BRepAlgoAPI_Fuse algo = BRepAlgoAPI_Fuse(first, other_shape);
        //combinedFuse.SetFuzzyValue(0.1);
        algo.Build();
        algo.SimplifyResult(true, true, 1e-3);
        TopoDS_Shape* retVal = new TopoDS_Shape(algo.Shape());
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    NativeWrapper^ NativeWrapper::Intersect(NativeWrapper^ other)
    {
        TopoDS_Shape first = *m_Impl;
        TopoDS_Shape other_shape = *other->m_Impl;


        BRepAlgoAPI_Common algo = BRepAlgoAPI_Common(first, other_shape);
        //.SetFuzzyValue(0.1);
        algo.Build();
        algo.SimplifyResult(true, true, 1e-3);
        TopoDS_Shape* retVal = new TopoDS_Shape(algo.Shape());
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    NativeWrapper^ NativeWrapper::Shell(double thickness)
    {
        TopoDS_Shape shape = *m_Impl;

        TopTools_ListOfShape facesToRemove = TopTools_ListOfShape();
        BRepOffsetAPI_MakeThickSolid builder = BRepOffsetAPI_MakeThickSolid();
        builder.MakeThickSolidByJoin(shape, facesToRemove, thickness, 1e-4);


        TopoDS_Shape* retVal = new TopoDS_Shape(builder.Shape());
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    NativeWrapper^ NativeWrapper::Round(System::Collections::Generic::List<RadiusDefinition^>^ definitions)
    {
        TopoDS_Shape shape = *m_Impl;
        BRepFilletAPI_MakeFillet fillet = BRepFilletAPI_MakeFillet(shape);

        for each (RadiusDefinition^ def in definitions) {
            for each (EdgeWrapper^ edge in def->Edges) {
                TopoDS_Edge* edge_pointer = edge->GetPointer();
                fillet.Add(def->Radius, *edge_pointer);
            }
        }
        TopoDS_Shape* retVal = new TopoDS_Shape(fillet.Shape());
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    void NativeWrapper::SaveSTP(System::String^ path)
    {
        STEPControl_Writer writer;

        TopoDS_Shape shape = *m_Impl;

        writer.Transfer(shape, STEPControl_AsIs);
        TCollection_AsciiString temp = toAsciiString(path);
        writer.Write(temp.ToCString());
    }

    void NativeWrapper::SaveSTL(System::String^ path)
    {
        StlAPI_Writer writer;
        writer.ASCIIMode() = true;
        TopoDS_Shape shape = *m_Impl;

        BRepMesh_IncrementalMesh mesh(shape, 0.001f, true);
        mesh.Perform();

        TCollection_AsciiString temp = toAsciiString(path);

        bool t = writer.Write(shape, temp.ToCString());
    }

    float NativeWrapper::CalculateVolume()
    {
        TopoDS_Shape shape = *m_Impl;

        GProp_GProps gprops;


        BRepGProp::VolumeProperties(shape, gprops);

        Standard_Real vol = gprops.Mass();
        return vol;
    }

}
