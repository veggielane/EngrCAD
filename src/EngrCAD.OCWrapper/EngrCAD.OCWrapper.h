#pragma once

using namespace System;

namespace EngrCADOCWrapper {
	public ref class Wrapper
	{
	public:
		
		static int Test(System::String^ filename);

		// TODO: Add your methods for this class here.
	};


	public ref class ShapeWrapper
	{
	private:
		TopoDS_Shape* shape;

	public:
		ShapeWrapper();
		~ShapeWrapper()
		{
			delete shape;
		}
	};
}
