#include "pch.h"
#include "STEPControl_Reader.hxx"
#include "TopoDS_Solid.hxx"

#include "EngrCAD.OCWrapper.h"
#include <vcclr.h>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <STEPControl_Writer.hxx>


#pragma comment(lib, "TKernel.lib")
#pragma comment(lib, "TKMath.lib")
#pragma comment(lib, "TKBRep.lib")
#pragma comment(lib, "TKXSBase.lib")
#pragma comment(lib, "TKService.lib")
#pragma comment(lib, "TKTopAlgo.lib")


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


int EngrCADOCWrapper::Wrapper::Test(String^ filename)
{
    STEPControl_Reader aReader;
    IFSelect_ReturnStatus aStatus = aReader.ReadFile(toAsciiString(filename).ToCString());
    if (aStatus == IFSelect_RetDone)
    {
        bool isFailsonly = false;
        aReader.PrintCheckLoad(isFailsonly, IFSelect_ItemsByEntity);

        int aNbRoot = aReader.NbRootsForTransfer();
        //aReader.PrintCheckTransfer(isFailsonly, IFSelect_ItemsByEntity);
        for (Standard_Integer n = 1; n <= aNbRoot; n++)
        {
            Standard_Boolean ok = aReader.TransferRoot(n);
            int aNbShap = aReader.NbShapes();
            if (aNbShap > 0)
            {
                for (int i = 1; i <= aNbShap; i++)
                {
                    TopoDS_Shape aShape = aReader.Shape(i);
                    //TopoDS_Solid
                    Console::WriteLine(i);
                    //myAISContext()->Display(new AIS_Shape(aShape), Standard_False);
                }
                //myAISContext()->UpdateCurrentViewer();
            }
        }
    }
    else
    {
        return 0;
    }

    return 1;
}

EngrCADOCWrapper::ShapeWrapper::ShapeWrapper()
{
    gp_Ax2 anAxis;
    anAxis.SetLocation(gp_Pnt(0.0, 0, 0.0));
    TopoDS_Shape fshape = BRepPrimAPI_MakeSphere(anAxis, 50).Shape();
    shape = &fshape;

    STEPControl_Writer writer;
    writer.Transfer(fshape, STEPControl_AsIs);
    TCollection_AsciiString temp = toAsciiString("output.stp");
    writer.Write(temp.ToCString());
}


