#pragma once


namespace EngrCADOCWrapper 
{



	public ref class FaceWrapper
	{
	public:
		FaceWrapper(TopoDS_Face* face) : m_Impl(face)
		{
		}

		~FaceWrapper() 
		{
			this->!FaceWrapper();
		}

		void* GetPointer() 
		{ 
			return m_Impl; 
		}
	protected:
		!FaceWrapper() 
		{
			delete m_Impl;
		}
	private:
		TopoDS_Face* m_Impl;
	};


	public ref class EdgeWrapper
	{
	public:
		EdgeWrapper(TopoDS_Edge* shape) : m_Impl(shape) {
		}

		~EdgeWrapper() {
			this->!EdgeWrapper();
		}
	protected:
		!EdgeWrapper() {
			delete m_Impl;
		}

	public:
		TopoDS_Edge* GetPointer() { return m_Impl; }


	private:
		TopoDS_Edge* m_Impl;
	};

	public ref class RadiusDefinition
	{
	public:
		explicit RadiusDefinition(float radius, System::Collections::Generic::List<EdgeWrapper^>^ edges) : Radius(radius), Edges(edges)
		{
		}
		System::Collections::Generic::List<EdgeWrapper^>^ Edges;
		float Radius;
	};

	public ref class NativeWrapper
	{
	public:
		NativeWrapper(TopoDS_Shape* shape) : m_Impl(shape) {
		}

		~NativeWrapper() {
			this->!NativeWrapper();
		}
	protected:
		!NativeWrapper() {
			delete m_Impl;
		}

	public:
		void* GetPointer() { return m_Impl; }

		//c
		static NativeWrapper^ Sphere(float radius);
		static NativeWrapper^ Box(float x, float y, float z, bool centered);
		static NativeWrapper^ Cylinder(float radius, float height, bool centered);
		static NativeWrapper^ Cone(float bottomRadius, float topRadius, float height, bool centered);


		NativeWrapper^ Translate(float x, float y, float z);
		NativeWrapper^ Rotate(float radians, System::Numerics::Vector3^ position, System::Numerics::Vector3^ direction);
		NativeWrapper^ Transform(System::Numerics::Matrix4x4^ matrix);


		System::Collections::Generic::List<FaceWrapper^>^ GetFaces();
		System::Collections::Generic::List<EdgeWrapper^>^ GetEdges();

		//Operations
		NativeWrapper^ Subtract(NativeWrapper^ other);
		NativeWrapper^ Union(NativeWrapper^ other);
		NativeWrapper^ Intersect(NativeWrapper^ other);
		NativeWrapper^ Shell(double thickness);
		
		NativeWrapper^ Round(System::Collections::Generic::List<RadiusDefinition^>^ definitions);

		void SaveSTP(System::String^ path);
		void SaveSTL(System::String^ path);

		float CalculateVolume();

	private:
		TopoDS_Shape* m_Impl;
	};



}
