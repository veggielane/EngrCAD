#pragma once


namespace EngrCADOCWrapper {
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


		//Operations
		NativeWrapper^ Subtract(NativeWrapper^ other);
		NativeWrapper^ Union(NativeWrapper^ other);
		NativeWrapper^ Intersect(NativeWrapper^ other);



		void SaveSTP(System::String^ path);
		void SaveSTL(System::String^ path);

		float CalculateVolume();

	private:
		void* m_Impl;
	};

}
