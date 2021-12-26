#pragma once


namespace EngrCADOCWrapper 
{
	public ref class CoordMapper
	{
	public:
		CoordMapper(System::Numerics::Vector3^ origin, System::Numerics::Vector3^ normal, System::Numerics::Vector3^ xDirection);

		System::Numerics::Vector2^ ToLocalCoords(System::Numerics::Vector3^ vec);
		System::Numerics::Vector3^ ToWorldCoords(System::Numerics::Vector2^ vec);
	private:
		gp_Trsf* _globalToLocal;
		gp_Trsf* _localToGlobal;
	};


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

		static EdgeWrapper^ Line(System::Numerics::Vector3^ v1, System::Numerics::Vector3^ v2);
		static EdgeWrapper^ BezierCurve(System::Collections::Generic::List<System::Numerics::Vector3>^ points);

		System::Numerics::Vector3^ Normal();
		int CurveType();


	private:
		TopoDS_Edge* m_Impl;
	};

	public ref class WireWrapper
	{
	public:
		WireWrapper(TopoDS_Wire* wire) : m_Impl(wire) {
		}

		~WireWrapper() {
			this->!WireWrapper();
		}
	protected:
		!WireWrapper() {
			delete m_Impl;
		}

	public:
		TopoDS_Wire* GetPointer() { return m_Impl; }

		static WireWrapper^ FromEdges(System::Collections::Generic::List<EdgeWrapper^>^ edges);

	private:
		TopoDS_Wire* m_Impl;
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

	public ref class ShapeWrapper
	{
	public:
		ShapeWrapper(TopoDS_Shape* shape) : m_Impl(shape) {
		}

		~ShapeWrapper() {
			this->!ShapeWrapper();
		}
	protected:
		!ShapeWrapper() {
			delete m_Impl;
		}

	public:
		void* GetPointer() { return m_Impl; }

		//c
		static ShapeWrapper^ Sphere(float radius);
		static ShapeWrapper^ Box(float x, float y, float z, bool centered);
		static ShapeWrapper^ Cylinder(float radius, float height, bool centered);
		static ShapeWrapper^ Cone(float bottomRadius, float topRadius, float height, bool centered);
		static ShapeWrapper^ Extrude(System::Collections::Generic::List<EdgeWrapper^>^ edges, System::Numerics::Vector3^ direction);
		static ShapeWrapper^ ImportSTP(System::String^ filenpathame);

		ShapeWrapper^ Translate(float x, float y, float z);
		ShapeWrapper^ Rotate(float radians, System::Numerics::Vector3^ position, System::Numerics::Vector3^ direction);
		ShapeWrapper^ Transform(System::Numerics::Matrix4x4^ matrix);


		System::Collections::Generic::List<FaceWrapper^>^ GetFaces();

		System::Collections::Generic::List<EdgeWrapper^>^ GetEdges();

		int ShapeType();

		//Operations
		ShapeWrapper^ Subtract(ShapeWrapper^ other);
		ShapeWrapper^ Union(System::Collections::Generic::List<ShapeWrapper^>^ others);
		ShapeWrapper^ Intersect(ShapeWrapper^ other);
		ShapeWrapper^ Shell(double thickness);

		ShapeWrapper^ Round(System::Collections::Generic::List<RadiusDefinition^>^ definitions);

		void SaveSTP(System::String^ path);
		void SaveSTL(System::String^ path);
		System::Tuple<System::Numerics::Vector3, System::Numerics::Vector3>^ CalculateBoundingBox();
		float CalculateVolume();

	private:
		TopoDS_Shape* m_Impl;
	};



}
