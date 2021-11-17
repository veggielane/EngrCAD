#include "pch.h"
#include "STEPControl_Reader.hxx"

#include "EngrCAD.OCWrapper.h"
#include <vcclr.h>

#pragma comment(lib, "TKernel.lib")
#pragma comment(lib, "TKMath.lib")
#pragma comment(lib, "TKBRep.lib")
#pragma comment(lib, "TKXSBase.lib")
#pragma comment(lib, "TKService.lib")
#pragma comment(lib, "TKV3d.lib")
#pragma comment(lib, "TKOpenGl.lib")
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
