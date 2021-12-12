#pragma once


namespace EngrCADOCWrapper {



	public ref class FaceWrapper
	{
	public:
		FaceWrapper(void* shape) : m_Impl(shape) {
		}

		~FaceWrapper() {
			this->!FaceWrapper();
		}
	protected:
		!FaceWrapper() {
			delete m_Impl;
		}

	public:
		void* GetPointer() { return m_Impl; }


	private:
		void* m_Impl;
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
		NativeWrapper(void* shape) : m_Impl(shape) {
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
		void* m_Impl;
	};



}
