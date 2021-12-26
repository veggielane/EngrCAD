
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
#include <BRepBndLib.hxx>

#include "EngrCAD.OCWrapper.h"

#include <vcclr.h>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakeCone.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <gp_GTrsf.hxx>
#include <BRepBuilderAPI_GTransform.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <Geom_Line.hxx>
#include <Geom_BezierCurve.hxx>

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

static gp_Pnt ToPnt(System::Numerics::Vector3^ v) {
    return gp_Pnt(v->X, v->Y, v->Z);
}

static gp_Dir ToDir(System::Numerics::Vector3^ v) {
    return gp_Dir(v->X, v->Y, v->Z);
}
static gp_Vec ToVec(System::Numerics::Vector3^ v) {
    return gp_Vec(v->X, v->Y, v->Z);
}

namespace EngrCADOCWrapper {

    ShapeWrapper^ ShapeWrapper::Sphere(float radius)
    {
        gp_Ax2 anAxis;
        anAxis.SetLocation(gp_Pnt(0, 0, 0));
        TopoDS_Shape shape = BRepPrimAPI_MakeSphere(anAxis, radius).Shape();
        TopoDS_Shape* retVal = new TopoDS_Shape(shape);
        ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
        return f;
    }

    ShapeWrapper^ ShapeWrapper::Box(float x, float y, float z, bool centered)
    {
        TopoDS_Shape shape = BRepPrimAPI_MakeBox(x, y, z).Shape();

        if (centered) {
            gp_Trsf transformation = gp_Trsf();
            transformation.SetTranslation(gp_Vec(-x / 2.0f, -y / 2.0f, -z / 2.0f));
            TopLoc_Location translation = TopLoc_Location(transformation);
            TopoDS_Shape* retVal = new TopoDS_Shape(shape.Moved(translation));
            ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
            return f;
        }
        else {
            TopoDS_Shape* retVal = new TopoDS_Shape(shape);
            ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
            return f;
        }
    }

    ShapeWrapper^ ShapeWrapper::Cylinder(float radius, float height, bool centered)
    {
        gp_Ax2 pos = gp_Ax2(gp_Pnt(0, 0, centered ? -height / 2.0 : 0), gp_Dir(0, 0, 1));
        TopoDS_Shape shape = BRepPrimAPI_MakeCylinder(pos, radius, height).Shape();

        TopoDS_Shape* retVal = new TopoDS_Shape(shape);
        ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
        return f;
    }

    ShapeWrapper^ ShapeWrapper::Cone(float bottomRadius, float topRadius, float height, bool centered)
    {
        gp_Ax2 pos = gp_Ax2(gp_Pnt(0, 0, centered ? -height / 2.0 : 0), gp_Dir(0, 0, 1));
        TopoDS_Shape shape = BRepPrimAPI_MakeCone(pos, bottomRadius, topRadius, height).Shape();

        TopoDS_Shape* retVal = new TopoDS_Shape(shape);
        ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
        return f;
    }

    ShapeWrapper^ ShapeWrapper::Translate(float x, float y, float z)
    {
        TopoDS_Shape shape = *m_Impl;

        gp_Trsf transformation = gp_Trsf();
        transformation.SetTranslation(gp_Vec(x, y, z));
        TopLoc_Location translation = TopLoc_Location(transformation);

        TopoDS_Shape* retVal = new TopoDS_Shape(shape.Moved(translation));
        ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
        return f;

    }

    ShapeWrapper^ ShapeWrapper::Rotate(float radians, System::Numerics::Vector3^ origin, System::Numerics::Vector3^ direction)
    {
        TopoDS_Shape shape = *m_Impl;
        gp_Dir dir = ToDir(direction);
        gp_Pnt ori = ToPnt(origin);
        gp_Ax1 axis = gp_Ax1(ori, dir);
        gp_Trsf transformation = gp_Trsf();
        transformation.SetRotation(axis, radians);
        TopLoc_Location translation = TopLoc_Location(transformation);
        TopoDS_Shape* retVal = new TopoDS_Shape(shape.Moved(translation));
        ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
        return f;
    }

    ShapeWrapper^ ShapeWrapper::Transform(System::Numerics::Matrix4x4^ matrix)
    {
        TopoDS_Shape shape = *m_Impl;


        gp_Mat mat = gp_Mat(matrix->M11, matrix->M21, matrix->M31,
            matrix->M12, matrix->M22, matrix->M32,
            matrix->M13, matrix->M23, matrix->M33);


        gp_XYZ pos = gp_XYZ(matrix->M41, matrix->M42, matrix->M43);
        gp_GTrsf transformation = gp_GTrsf(mat, pos);

        BRepBuilderAPI_GTransform brep = BRepBuilderAPI_GTransform(shape, transformation);
        brep.Build();
        TopoDS_Shape * retVal = new TopoDS_Shape(brep.Shape());
        ShapeWrapper^ f = gcnew ShapeWrapper(retVal);
        return f;

    }

    System::Collections::Generic::List<FaceWrapper^>^ ShapeWrapper::GetFaces()
    {
        TopoDS_Shape shape = *m_Impl;
        TopExp_Explorer Ex;
        System::Collections::Generic::List<FaceWrapper^>^ list = gcnew System::Collections::Generic::List<FaceWrapper^>();
        for (Ex.Init(shape, TopAbs_FACE); Ex.More(); Ex.Next()) 
        {
            TopoDS_Face* t = static_cast<TopoDS_Face*>(new TopoDS_Shape(Ex.Current()));
            FaceWrapper^ face = gcnew FaceWrapper(t);
            list->Add(face);
        }
        return list;
    }

    System::Collections::Generic::List<EdgeWrapper^>^ ShapeWrapper::GetEdges()
    {
        TopoDS_Shape shape = *m_Impl;
        TopExp_Explorer Ex;
        System::Collections::Generic::List<EdgeWrapper^>^ list = gcnew System::Collections::Generic::List<EdgeWrapper^>();
        for (Ex.Init(shape, TopAbs_EDGE); Ex.More(); Ex.Next()) {

            TopoDS_Edge* t = static_cast<TopoDS_Edge*>(new TopoDS_Shape(Ex.Current()));
            EdgeWrapper^ face = gcnew EdgeWrapper(t);
            list->Add(face);
        }
        return list;
    }

    int ShapeWrapper::ShapeType()
    {
        TopoDS_Shape shape = *m_Impl;
        return shape.ShapeType();
    }

    ShapeWrapper^ ShapeWrapper::Subtract(ShapeWrapper^ other)
    {
        TopoDS_Shape difference = *m_Impl;
        TopoDS_Shape other_shape = *other->m_Impl;
        BRepAlgoAPI_Cut algo = BRepAlgoAPI_Cut(difference, other_shape);
        algo.Build();
        algo.SimplifyResult(true, true, 1e-3);
        return gcnew ShapeWrapper(new TopoDS_Shape(algo.Shape()));
    }

    ShapeWrapper^ ShapeWrapper::Union(System::Collections::Generic::List<ShapeWrapper^>^ others)
    {
        TopoDS_Shape combined = TopoDS_Shape(*m_Impl) ;

        for each (ShapeWrapper^ otherWrapper in others) {

            TopoDS_Shape other_shape = *otherWrapper->m_Impl;
            BRepAlgoAPI_Fuse algo = BRepAlgoAPI_Fuse(combined, other_shape);
            algo.Build();
            algo.SimplifyResult(true, true, 1e-3);
            combined = TopoDS_Shape(algo.Shape());
        }
        return gcnew ShapeWrapper(new TopoDS_Shape(combined));
    }

    ShapeWrapper^ ShapeWrapper::Intersect(ShapeWrapper^ other)
    {
        TopoDS_Shape first = *m_Impl;
        TopoDS_Shape other_shape = *other->m_Impl;
        BRepAlgoAPI_Common algo = BRepAlgoAPI_Common(first, other_shape);
        algo.Build();
        algo.SimplifyResult(true, true, 1e-3);
        return gcnew ShapeWrapper(new TopoDS_Shape(algo.Shape()));
    }

    ShapeWrapper^ ShapeWrapper::Shell(double thickness)
    {
        TopoDS_Shape shape = *m_Impl;

        TopTools_ListOfShape facesToRemove = TopTools_ListOfShape();
        BRepOffsetAPI_MakeThickSolid builder = BRepOffsetAPI_MakeThickSolid();
        builder.MakeThickSolidByJoin(shape, facesToRemove, thickness, 1e-4);

        return gcnew ShapeWrapper(new TopoDS_Shape(builder.Shape()));
    }

    ShapeWrapper^ ShapeWrapper::Extrude(System::Collections::Generic::List<EdgeWrapper^>^ edges, System::Numerics::Vector3^ direction)
    {
        BRepBuilderAPI_MakeWire wireBuilder = BRepBuilderAPI_MakeWire();
        for each (EdgeWrapper^ edge in edges) {
            TopoDS_Edge* edge_pointer = edge->GetPointer();
            wireBuilder.Add(*edge_pointer);
        }
        wireBuilder.Build();
        BRepBuilderAPI_MakeFace faceBuilder = BRepBuilderAPI_MakeFace(wireBuilder.Wire(), false);
        TopoDS_Face face = faceBuilder.Face();
        BRepPrimAPI_MakePrism builder = BRepPrimAPI_MakePrism(face, ToVec(direction),false, true);
        return gcnew ShapeWrapper(new TopoDS_Shape(builder.Shape()));
    }

    ShapeWrapper^ ShapeWrapper::ImportSTP(System::String^ path)
    {
        STEPControl_Reader reader = STEPControl_Reader();
        reader.ReadFile(toAsciiString(path).ToCString());
        reader.TransferRoots();
        return gcnew ShapeWrapper(new TopoDS_Shape(reader.OneShape()));
    }

    ShapeWrapper^ ShapeWrapper::Round(System::Collections::Generic::List<RadiusDefinition^>^ definitions)
    {
        TopoDS_Shape shape = *m_Impl;
        BRepFilletAPI_MakeFillet fillet = BRepFilletAPI_MakeFillet(shape);

        for each (RadiusDefinition^ def in definitions) {
            for each (EdgeWrapper^ edge in def->Edges) {
                TopoDS_Edge* edge_pointer = edge->GetPointer();
                fillet.Add(def->Radius, *edge_pointer);
            }
        }
        return gcnew ShapeWrapper(new TopoDS_Shape(fillet.Shape()));
    }

    void ShapeWrapper::SaveSTP(System::String^ path)
    {
        STEPControl_Writer writer;

        TopoDS_Shape shape = *m_Impl;

        writer.Transfer(shape, STEPControl_AsIs);
        TCollection_AsciiString temp = toAsciiString(path);
        writer.Write(temp.ToCString());
    }

    void ShapeWrapper::SaveSTL(System::String^ path)
    {
        StlAPI_Writer writer;
        writer.ASCIIMode() = true;
        TopoDS_Shape shape = *m_Impl;

        BRepMesh_IncrementalMesh mesh(shape, 0.001f, true);
        mesh.Perform();

        TCollection_AsciiString temp = toAsciiString(path);

        bool t = writer.Write(shape, temp.ToCString());
    }

    System::Tuple<System::Numerics::Vector3, System::Numerics::Vector3>^ ShapeWrapper::CalculateBoundingBox()
    {
        Bnd_Box box;
        BRepBndLib::Add(*m_Impl, box);

        Standard_Real xmind, ymind, zmind, xmaxd, ymaxd, zmaxd;
        box.Get(xmind, ymind, zmind, xmaxd, ymaxd, zmaxd);

        System::Numerics::Vector3 min = System::Numerics::Vector3(xmind, ymind, zmind);
        System::Numerics::Vector3 max = System::Numerics::Vector3(xmaxd, ymaxd, zmaxd);

        return gcnew System::Tuple<System::Numerics::Vector3, System::Numerics::Vector3>(min, max);

    }

    float ShapeWrapper::CalculateVolume()
    {
        TopoDS_Shape shape = *m_Impl;

        GProp_GProps gprops;


        BRepGProp::VolumeProperties(shape, gprops);

        Standard_Real vol = gprops.Mass();
        return vol;
    }

    CoordMapper::CoordMapper(System::Numerics::Vector3^ origin, System::Numerics::Vector3^ normal, System::Numerics::Vector3^ xDirection)
    {
        gp_Ax3 globalCoordSystem = gp_Ax3();

        gp_Pnt origin_pnt = ToPnt(origin);
        gp_Dir normal_dir = ToDir(normal);
        gp_Dir xDirection_dir = ToDir(xDirection);

        gp_Ax3 localCoordSystem = gp_Ax3(origin_pnt, normal_dir, xDirection_dir);

        gp_Trsf globalToLocal = gp_Trsf();
        globalToLocal.SetTransformation(globalCoordSystem, localCoordSystem);
        _globalToLocal = new gp_Trsf(globalToLocal);

        gp_Trsf localToGlobal = gp_Trsf();
        localToGlobal.SetTransformation(localCoordSystem, globalCoordSystem);
        _localToGlobal = new gp_Trsf(localToGlobal);
    }

    System::Numerics::Vector2^ CoordMapper::ToLocalCoords(System::Numerics::Vector3^ vec)
    {
        gp_Pnt point = ToPnt(vec);
        gp_Trsf trsf = *_globalToLocal;
        gp_Pnt transformed = point.Transformed(trsf);

        return gcnew System::Numerics::Vector2(transformed.X(), transformed.Y());
    }

    System::Numerics::Vector3^ CoordMapper::ToWorldCoords(System::Numerics::Vector2^ vec)
    {
        gp_Pnt point = gp_Pnt(vec->X, vec->Y, 0);
        gp_Trsf trsf = *_localToGlobal;
        gp_Pnt transformed = point.Transformed(trsf);
        return gcnew System::Numerics::Vector3(transformed.X(), transformed.Y(), transformed.Z());
    }

    EdgeWrapper^ EdgeWrapper::Line(System::Numerics::Vector3^ v1, System::Numerics::Vector3^ v2)
    {
        BRepBuilderAPI_MakeEdge brep = BRepBuilderAPI_MakeEdge(gp_Pnt(v1->X, v1->Y, v1->Z), gp_Pnt(v2->X, v2->Y, v2->Z));
        TopoDS_Edge* edge = new TopoDS_Edge(brep.Edge());
        EdgeWrapper^ edgeWrapper = gcnew EdgeWrapper(edge);
        return edgeWrapper;
    }

    EdgeWrapper^ EdgeWrapper::BezierCurve(System::Collections::Generic::List<System::Numerics::Vector3>^ points)
    {
        TColgp_Array1OfPnt pointArray = TColgp_Array1OfPnt(1, points->Count);
        int i = 1;
        for each (System::Numerics::Vector3^ p in points) {
            pointArray.SetValue(i++, gp_Pnt(p->X, p->Y, p->Z));
        }

        Handle_Geom_Curve bezierCurve = new Geom_BezierCurve(pointArray);

        BRepBuilderAPI_MakeEdge edgeMaker;
        edgeMaker.Init(bezierCurve);
        TopoDS_Edge* edge = new TopoDS_Edge(edgeMaker.Edge());

        EdgeWrapper^ edgeWrapper = gcnew EdgeWrapper(edge);
        return edgeWrapper;
    }

    System::Numerics::Vector3^ EdgeWrapper::Normal()
    {
        TopoDS_Edge edge = *m_Impl;

        Standard_Real first, last;
        //BRep_Tool::Curve
        //Geom_Curve aCurve = BRep_Tool::Curve(anEdge, first, last);
        //Handle(Geom_Curve) curve = BRep_Tool::Curve(edge, first, last);

        BRepAdaptor_Curve curve(edge);
        GeomAbs_CurveType theTyp = curve.GetType();

        if (theTyp == GeomAbs_Line)
        {
            gp_Lin line = curve.Line();

            gp_Dir dir = line.Direction();

            return System::Numerics::Vector3::Normalize(System::Numerics::Vector3(dir.X(), dir.Y(), dir.Z()));
        }



        //if (curve->IsKind(STANDARD_TYPE(Geom_Line)))
        {
            // do something with line
        }
        //tangentAt
        throw gcnew System::NotImplementedException();
        // TODO: insert return statement here
    }

    int EdgeWrapper::CurveType()
    {
        TopoDS_Edge edge = *m_Impl;
        BRepAdaptor_Curve theCrv(edge);
        return theCrv.GetType();
    }

    WireWrapper^ WireWrapper::FromEdges(System::Collections::Generic::List<EdgeWrapper^>^ edges)
    {
        BRepBuilderAPI_MakeWire wireBuilder = BRepBuilderAPI_MakeWire();

        for each (EdgeWrapper^ edge in edges) {
            TopoDS_Edge* edge_pointer = edge->GetPointer();
            wireBuilder.Add(*edge_pointer);
        }
        wireBuilder.Build();
        TopoDS_Wire* wire = new TopoDS_Wire(wireBuilder.Wire());
        WireWrapper^ wrapper = gcnew WireWrapper(wire);
        return wrapper;
    }

}
