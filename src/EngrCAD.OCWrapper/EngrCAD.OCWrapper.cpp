#include "pch.h"
#include "STEPControl_Reader.hxx"
#include "TopoDS_Solid.hxx"

#include "EngrCAD.OCWrapper.h"
#include <vcclr.h>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <STEPControl_Writer.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>

#pragma comment(lib, "TKernel.lib")
#pragma comment(lib, "TKMath.lib")
#pragma comment(lib, "TKBRep.lib")
#pragma comment(lib, "TKXSBase.lib")
#pragma comment(lib, "TKService.lib")
#pragma comment(lib, "TKTopAlgo.lib")
#pragma comment(lib, "TKBO.lib")
#pragma comment(lib, "TKG3d.lib")

#pragma comment(lib, "TKPrim.lib")
#pragma comment(lib, "TKV3d.lib")
#pragma comment(lib, "TKIGES.lib")
#pragma comment(lib, "TKSTEP.lib")
#pragma comment(lib, "TKStl.lib")
#pragma comment(lib, "TKVrml.lib")
#pragma comment(lib, "TKLCAF.lib")

static TCollection_AsciiString toAsciiString(String^ theString)
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
        TopoDS_Shape fshape = BRepPrimAPI_MakeSphere(anAxis, radius).Shape();
        TopoDS_Shape* retVal = new TopoDS_Shape(fshape);
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;
    }

    NativeWrapper^ NativeWrapper::Translate(float x, float y, float z)
    {
        TopoDS_Shape* shape_pointer = static_cast<TopoDS_Shape*>(m_Impl);
        TopoDS_Shape shape = *shape_pointer;

        gp_Trsf transformation = gp_Trsf();
        transformation.SetTranslation(gp_Vec(x, y, z));
        TopLoc_Location translation = TopLoc_Location(transformation);

        TopoDS_Shape* retVal = new TopoDS_Shape(shape.Moved(translation));
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;

    }

    NativeWrapper^ NativeWrapper::Subtract(NativeWrapper^ other)
    {
        TopoDS_Shape* shape_pointer = static_cast<TopoDS_Shape*>(m_Impl);
        TopoDS_Shape difference = *shape_pointer;

        TopoDS_Shape* other_pointer = static_cast<TopoDS_Shape*>(other->m_Impl);
        TopoDS_Shape other_shape = *other_pointer;

        BRepAlgoAPI_Cut differenceCut = BRepAlgoAPI_Cut(difference, other_shape);
        differenceCut.SetFuzzyValue(0.1);
        differenceCut.Build();
        difference = differenceCut.Shape();

        TopoDS_Shape* retVal = new TopoDS_Shape(difference);
        NativeWrapper^ f = gcnew NativeWrapper(retVal);
        return f;

    }

    void NativeWrapper::SaveSTP(String^ path)
    {
        STEPControl_Writer writer;

        TopoDS_Shape* shape_pointer = static_cast<TopoDS_Shape*>(m_Impl);
        TopoDS_Shape shape = *shape_pointer;

        writer.Transfer(shape, STEPControl_AsIs);
        TCollection_AsciiString temp = toAsciiString(path);
        writer.Write(temp.ToCString());
    }

    float NativeWrapper::CalculateVolume()
    {
        TopoDS_Shape* shape_pointer = static_cast<TopoDS_Shape*>(m_Impl);
        TopoDS_Shape shape = *shape_pointer;

        GProp_GProps gprops;


        BRepGProp::VolumeProperties(shape, gprops);

        Standard_Real vol = gprops.Mass();
        return vol;
    }

}
